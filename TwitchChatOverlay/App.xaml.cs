using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using TwitchChatOverlay.Core.Audio;
using TwitchChatOverlay.Core.Pipeline;
using TwitchChatOverlay.Core.Settings;
using TwitchChatOverlay.Core.Speech;
using TwitchChatOverlay.Core.Twitch;
using TwitchChatOverlay.UI.Hotkeys;
using TwitchChatOverlay.UI.Overlay;
using TwitchChatOverlay.UI.Settings;
using TwitchChatOverlay.UI.Settings.ViewModels;
using TwitchChatOverlay.UI.Tray;
using TwitchLib.EventSub.Websockets.Extensions;

namespace TwitchChatOverlay;

public partial class App : Application
{
    /// <summary>
    /// Only one copy may run. Several instances would fight over settings.json (last writer
    /// wins, so a token saved by one is erased by another) and over the log file, which the
    /// file sink holds per process — the second copy then logs nowhere and the user is left
    /// re-authorising against a config that keeps reverting.
    /// </summary>
    private const string SingleInstanceMutexName = @"Global\TwitchChatOverlay.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "TwitchChatOverlay уже запущен. Откройте настройки через иконку в системном трее.",
                "TwitchChatOverlay",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        // Log to %APPDATA%\TwitchChatOverlay\logs before the host builds the DI logger,
        // so startup failures land somewhere the user can find without a debugger attached.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logsDirectory = Path.Combine(appData, "TwitchChatOverlay", "logs");
        Directory.CreateDirectory(logsDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logsDirectory, "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                // Without this the sink opens the file exclusively, so a second process
                // silently loses every log line it writes.
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton<SettingsService>();
                services.AddSingleton<TrayIconService>();
                services.AddSingleton<OverlayWindow>();

                services.AddHttpClient();
                services.AddTwitchLibEventSubWebsockets();
                services.AddSingleton<TwitchAuthService>();
                services.AddSingleton<TwitchHelixClient>();
                services.AddSingleton<ITwitchChatClient, EventSubChatClient>();

                services.AddSingleton<VoiceCatalog>();
                services.AddSingleton<PiperSpeechEngine>();
                services.AddSingleton<SapiSpeechEngine>();
                services.AddSingleton<SpeechService>();
                services.AddSingleton<AudioPlaybackService>();
                services.AddSingleton<MessageFilter>();
                services.AddSingleton<MessageQueue>();
                services.AddSingleton<MessageDispatcher>();
                services.AddSingleton<GlobalHotkeyService>();

                services.AddSingleton<SettingsApplier>();
                services.AddSingleton<IUserDialogs, UserDialogs>();
                services.AddSingleton<ConnectionTabViewModel>();
                services.AddSingleton<OverlayTabViewModel>();
                services.AddSingleton<VoiceTabViewModel>();
                services.AddSingleton<FiltersTabViewModel>();
                services.AddSingleton<QueueTabViewModel>();
                services.AddSingleton<HotkeysTabViewModel>();
                services.AddSingleton<GeneralTabViewModel>();
                services.AddSingleton<SettingsWindowViewModel>();
                services.AddSingleton<SettingsWindow>();
            })
            .Build();

        await _host.StartAsync();

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        var settingsService = _host.Services.GetRequiredService<SettingsService>();

        try
        {
            await settingsService.LoadAsync();
            logger.LogInformation("Настройки загружены из {Path}", settingsService.SettingsFilePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось загрузить настройки при старте");
        }

        var overlay = _host.Services.GetRequiredService<OverlayWindow>();
        overlay.Show();

        // EventSub → queue → dispatcher → (synthesis, card, playback). The message filter
        // joins this chain in этап 6.
        var queue = _host.Services.GetRequiredService<MessageQueue>();
        var dispatcher = _host.Services.GetRequiredService<MessageDispatcher>();

        dispatcher.CardShowRequested += (_, presentation) =>
            overlay.Dispatcher.BeginInvoke(() => overlay.Display(
                presentation.Message.DisplayName,
                presentation.Message.ColorHex,
                presentation.Message.Text,
                presentation.Message.BadgeSetIds));
        dispatcher.CardHideRequested += (_, _) =>
            overlay.Dispatcher.BeginInvoke(overlay.HideCard);

        dispatcher.Start(CancellationToken.None);

        var filter = _host.Services.GetRequiredService<MessageFilter>();

        var chatClient = _host.Services.GetRequiredService<ITwitchChatClient>();
        chatClient.MessageReceived += (_, message) =>
        {
            var result = filter.Apply(message);
            if (result.Message is not null)
            {
                queue.Enqueue(result.Message);
            }
            else
            {
                logger.LogDebug("Сообщение от {User} отфильтровано: {Reason}", message.UserLogin, result.RejectionReason);
            }
        };
        chatClient.StatusChanged += (_, status) =>
            logger.LogInformation("Статус подключения к Twitch: {State} {Detail}", status.State, status.Detail);

        try
        {
            await chatClient.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось подключиться к Twitch при старте");
        }

        UI.Localization.Localization.Current.SetLanguage(settingsService.Current.General.Language);

        var tray = _host.Services.GetRequiredService<TrayIconService>();
        tray.OpenSettingsRequested += (_, _) => ShowSettingsWindow();
        tray.OpenConfigFolderRequested += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(settingsService.SettingsDirectory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Не удалось открыть папку конфигурации");
            }
        };
        tray.ExitRequested += (_, _) =>
        {
            if (_host.Services.GetService<SettingsWindow>() is { } settingsWindow)
            {
                settingsWindow.AllowClose = true;
            }

            Shutdown();
        };

        var hotkeys = _host.Services.GetRequiredService<GlobalHotkeyService>();
        hotkeys.HotkeyPressed += (_, action) => Dispatcher.BeginInvoke(() =>
        {
            switch (action)
            {
                case HotkeyAction.ToggleMute:
                    dispatcher.IsMuted = !dispatcher.IsMuted;
                    tray.ShowBalloon("TwitchChatOverlay", dispatcher.IsMuted ? "Озвучка выключена" : "Озвучка включена");
                    logger.LogInformation("Озвучка {State} горячей клавишей", dispatcher.IsMuted ? "выключена" : "включена");
                    break;

                case HotkeyAction.SkipCurrent:
                    dispatcher.SkipCurrent();
                    break;

                case HotkeyAction.ClearQueue:
                    queue.Clear();
                    tray.ShowBalloon("TwitchChatOverlay", "Очередь сообщений очищена");
                    break;

                case HotkeyAction.ToggleOverlayVisibility:
                    var visible = overlay.ToggleOverlayVisibility();
                    logger.LogInformation("Оверлей {State} горячей клавишей", visible ? "показан" : "скрыт");
                    break;

                case HotkeyAction.OpenSettings:
                    ShowSettingsWindow();
                    break;
            }
        });

        if (hotkeys.FailedRegistrations.Count > 0)
        {
            tray.ShowBalloon(
                "TwitchChatOverlay",
                $"Не удалось занять горячие клавиши: {string.Join(", ", hotkeys.FailedRegistrations)}. Они уже используются другим приложением.",
                System.Windows.Forms.ToolTipIcon.Warning);
        }

        // A first run has no channel and no token, so landing straight in the settings window
        // is what makes "запустил и всё работает без правки файлов" true.
        if (!settingsService.Current.General.StartMinimized)
        {
            ShowSettingsWindow();
        }

        logger.LogInformation("TwitchChatOverlay запущен");

        base.OnStartup(e);
    }

    private void ShowSettingsWindow()
    {
        if (_host is null)
        {
            return;
        }

        var window = _host.Services.GetRequiredService<SettingsWindow>();

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.Services.GetService<GlobalHotkeyService>()?.Dispose();
            _host.Services.GetService<TrayIconService>()?.Dispose();
            _host.Services.GetService<SettingsWindowViewModel>()?.Dispose();

            if (_host.Services.GetService<MessageDispatcher>() is { } dispatcher)
            {
                await dispatcher.DisposeAsync();
            }

            if (_host.Services.GetService<ITwitchChatClient>() is IAsyncDisposable chatClient)
            {
                await chatClient.DisposeAsync();
            }

            await _host.StopAsync();
            _host.Dispose();
        }

        Log.CloseAndFlush();

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Необработанное исключение в UI-потоке");
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error(e.ExceptionObject as Exception, "Необработанное исключение в AppDomain");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Необработанное исключение в фоновой задаче");
        e.SetObserved();
    }
}
