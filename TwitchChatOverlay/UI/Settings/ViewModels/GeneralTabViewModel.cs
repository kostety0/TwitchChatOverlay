using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TwitchChatOverlay.Core.Settings;
using TwitchChatOverlay.UI.Localization;

namespace TwitchChatOverlay.UI.Settings.ViewModels;

public sealed record LanguageOption(AppLanguage Value, string DisplayName);

public sealed partial class GeneralTabViewModel : SettingsTabViewModel
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "TwitchChatOverlay";

    /// <summary>The About row stays hidden while this is blank, so a build without a public
    /// home never shows a dead link.</summary>
    public const string RepositoryUrl = "https://github.com/kostety0/TwitchChatOverlay";

    private readonly IUserDialogs _dialogs;
    private readonly ILogger<GeneralTabViewModel> _logger;

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _minimizeToTrayOnClose;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private LanguageOption? _selectedLanguage;

    /// <summary>Raised after a reset/import swaps the whole settings object so the other tabs
    /// can reload themselves.</summary>
    public event EventHandler? SettingsReplaced;

    public IReadOnlyList<LanguageOption> Languages { get; } = new[]
    {
        new LanguageOption(AppLanguage.Russian, "Русский"),
        new LanguageOption(AppLanguage.English, "English")
    };

    /// <summary>
    /// Read from the assembly rather than hardcoded, so the number shown in the UI can never
    /// drift from the one stamped into the binary. InformationalVersion is preferred because
    /// it survives suffixes like "-beta"; anything after a '+' is build metadata and is cut.
    /// </summary>
    public string VersionText { get; } = ReadVersion();

    public string AuthorText { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCompanyAttribute>()?.Company is { Length: > 0 } company
            ? company
            : "—";

    public string CopyrightText { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;

    private static string ReadVersion()
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    }

    public string SettingsFilePath => SettingsService.SettingsFilePath;

    public bool HasRepositoryLink => !string.IsNullOrWhiteSpace(RepositoryUrl);

    public GeneralTabViewModel(
        SettingsService settingsService,
        SettingsApplier applier,
        IUserDialogs dialogs,
        ILogger<GeneralTabViewModel> logger)
        : base(settingsService, applier)
    {
        _dialogs = dialogs;
        _logger = logger;

        Refresh();
    }

    public sealed override void Refresh()
    {
        LoadFromSettings(() =>
        {
            var g = Settings.General;

            // Read the actual registry state rather than trusting the stored flag: the user
            // may have removed the entry outside the app.
            StartWithWindows = IsAutostartEnabled();
            MinimizeToTrayOnClose = g.MinimizeToTrayOnClose;
            StartMinimized = g.StartMinimized;
            SelectedLanguage = Languages.FirstOrDefault(l => l.Value == g.Language) ?? Languages[0];
        });
    }

    partial void OnStartWithWindowsChanged(bool value) => Apply(nameof(StartWithWindows), () =>
    {
        Settings.General.StartWithWindows = value;
        SetAutostart(value);
    });

    partial void OnMinimizeToTrayOnCloseChanged(bool value) => Apply(nameof(MinimizeToTrayOnClose), () =>
        Settings.General.MinimizeToTrayOnClose = value);

    partial void OnStartMinimizedChanged(bool value) => Apply(nameof(StartMinimized), () =>
        Settings.General.StartMinimized = value);

    partial void OnSelectedLanguageChanged(LanguageOption? value) => Apply(nameof(SelectedLanguage), () =>
    {
        if (value is null)
        {
            return;
        }

        Settings.General.Language = value.Value;
        Localization.Localization.Current.SetLanguage(value.Value);
    });

    [RelayCommand]
    private void OpenConfigFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(SettingsService.SettingsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось открыть папку конфигурации");
        }
    }

    [RelayCommand]
    private void OpenRepository()
    {
        if (!HasRepositoryLink)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось открыть ссылку на репозиторий");
        }
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        var loc = Localization.Localization.Current;
        if (!_dialogs.Confirm(loc["G_ResetConfirm"], loc["G_ResetTitle"]))
        {
            return;
        }

        // Keep the Connection section so a reset doesn't sign the user out.
        var fresh = new AppSettings { Connection = Settings.Connection };
        await SettingsService.ReplaceAsync(fresh);

        Localization.Localization.Current.SetLanguage(fresh.General.Language);
        SettingsReplaced?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        var loc = Localization.Localization.Current;
        var path = _dialogs.AskSavePath(loc["G_Export"], "twitchchatoverlay-settings.json");
        if (path is null)
        {
            return;
        }

        try
        {
            await SettingsService.ExportAsync(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось экспортировать настройки в {Path}", path);
            _dialogs.Error(ex.Message, loc["G_Export"]);
        }
    }

    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        var loc = Localization.Localization.Current;
        var path = _dialogs.AskOpenPath(loc["G_ImportTitle"]);
        if (path is null)
        {
            return;
        }

        try
        {
            await SettingsService.ImportAsync(path);
            Localization.Localization.Current.SetLanguage(Settings.General.Language);
            SettingsReplaced?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            _logger.LogError(ex, "Не удалось импортировать настройки из {Path}", path);
            _dialogs.Error($"{loc["G_ImportError"]}: {ex.Message}", loc["G_Import"]);
        }
    }

    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is not null;
    }

    private void SetAutostart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    _logger.LogWarning("Не удалось определить путь к исполняемому файлу для автозапуска");
                    return;
                }

                key.SetValue(RunValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось изменить автозапуск с Windows");
        }
    }
}
