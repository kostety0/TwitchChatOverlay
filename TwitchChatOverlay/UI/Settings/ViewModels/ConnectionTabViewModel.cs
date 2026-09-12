using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TwitchChatOverlay.Core.Settings;
using TwitchChatOverlay.Core.Twitch;

namespace TwitchChatOverlay.UI.Settings.ViewModels;

public sealed partial class ConnectionTabViewModel : SettingsTabViewModel, IDisposable
{
    /// <summary>The channel box updates on every keystroke so validation is live. Reconnecting
    /// on every keystroke, though, would tear down and rebuild the EventSub session once per
    /// character — this waits until typing settles.</summary>
    private static readonly TimeSpan ReconnectDebounce = TimeSpan.FromMilliseconds(1200);

    private readonly TwitchAuthService _authService;
    private readonly ITwitchChatClient _chatClient;
    private readonly ILogger<ConnectionTabViewModel> _logger;
    private readonly DispatcherTimer _reconnectTimer;
    private CancellationTokenSource? _loginCts;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Укажите имя канала")]
    [RegularExpression("^[A-Za-z0-9_]{3,25}$", ErrorMessage = "Имя канала: 3–25 символов, латиница, цифры и _")]
    private string _channelName = string.Empty;

    [ObservableProperty]
    private string _clientId = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private TwitchConnectionState _connectionState;

    [ObservableProperty]
    private bool _isLoggedIn;

    [ObservableProperty]
    private bool _isLoginInProgress;

    /// <summary>Shown while the browser tab is open and we are waiting for Twitch to redirect back.</summary>
    [ObservableProperty]
    private bool _isWaitingForBrowser;

    [ObservableProperty]
    private string? _loginError;

    public ConnectionTabViewModel(
        SettingsService settingsService,
        SettingsApplier applier,
        TwitchAuthService authService,
        ITwitchChatClient chatClient,
        ILogger<ConnectionTabViewModel> logger)
        : base(settingsService, applier)
    {
        _authService = authService;
        _chatClient = chatClient;
        _logger = logger;

        _reconnectTimer = new DispatcherTimer { Interval = ReconnectDebounce };
        _reconnectTimer.Tick += (_, _) =>
        {
            _reconnectTimer.Stop();
            RestartChatClient();
        };

        Refresh();

        _chatClient.StatusChanged += OnStatusChanged;
        UpdateStatus(_chatClient.Status);
    }

    public sealed override void Refresh()
    {
        LoadFromSettings(() =>
        {
            ChannelName = Settings.Connection.ChannelName;
            ClientId = Settings.Connection.ClientId;
        });

        IsLoggedIn = _authService.IsLoggedIn;
    }

    partial void OnChannelNameChanged(string value) => Apply(nameof(ChannelName), () =>
    {
        Settings.Connection.ChannelName = value.Trim();

        // Reconnect once the user stops typing, not once per character.
        _reconnectTimer.Stop();
        _reconnectTimer.Start();
    });

    partial void OnClientIdChanged(string value) => Apply(nameof(ClientId), () =>
    {
        Settings.Connection.ClientId = value.Trim();
    });

    private void OnStatusChanged(object? sender, ConnectionStatus status)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => UpdateStatus(status));
    }

    private void UpdateStatus(ConnectionStatus status)
    {
        ConnectionState = status.State;
        StatusText = status.State switch
        {
            TwitchConnectionState.Disconnected => "Отключено",
            TwitchConnectionState.Connecting => "Подключение…",
            TwitchConnectionState.Connected => "Подключено",
            TwitchConnectionState.Error => string.IsNullOrWhiteSpace(status.Detail) ? "Ошибка" : $"Ошибка: {status.Detail}",
            _ => string.Empty
        };
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        LoginError = null;

        if (!_authService.HasClientId)
        {
            LoginError = "Сначала укажите Client ID приложения Twitch.";
            return;
        }

        // Persist the channel/client id the user just typed before the flow starts.
        await Applier.SaveNowAsync();

        IsLoginInProgress = true;
        _loginCts?.Cancel();
        _loginCts?.Dispose();
        _loginCts = new CancellationTokenSource();

        try
        {
            var (authorizeUrl, completion) = _authService.StartSignIn(_loginCts.Token);
            IsWaitingForBrowser = true;
            OpenBrowser(authorizeUrl);

            var success = await completion;
            if (success)
            {
                IsLoggedIn = true;
                Refresh();
                RestartChatClient();
            }
            else
            {
                LoginError = "Авторизация не завершена. Попробуйте ещё раз.";
            }
        }
        catch (OperationCanceledException)
        {
            // window closed or a second login started — nothing to report
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка входа через Twitch");
            LoginError = $"Не удалось войти: {ex.Message}";
        }
        finally
        {
            IsLoginInProgress = false;
            IsWaitingForBrowser = false;
        }
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await _authService.LogoutAsync();
        IsLoggedIn = false;
        LoginError = null;
        Refresh();

        try
        {
            await _chatClient.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка при отключении после выхода");
        }
    }

    [RelayCommand]
    private void OpenDeveloperConsole() => OpenBrowser("https://dev.twitch.tv/console/apps");

    private void RestartChatClient()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _chatClient.RestartAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось переподключиться после изменения настроек подключения");
            }
        });
    }

    private void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось открыть браузер по адресу {Url}", url);
        }
    }

    public void Dispose()
    {
        _reconnectTimer.Stop();
        _chatClient.StatusChanged -= OnStatusChanged;
        _loginCts?.Cancel();
        _loginCts?.Dispose();
    }
}
