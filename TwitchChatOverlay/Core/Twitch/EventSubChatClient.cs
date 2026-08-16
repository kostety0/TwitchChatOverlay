using Microsoft.Extensions.Logging;
using TwitchChatOverlay.Core.Pipeline;
using TwitchChatOverlay.Core.Settings;
using TwitchLib.EventSub.Core.EventArgs.Channel;
using TwitchLib.EventSub.Websockets;
using TwitchLib.EventSub.Websockets.Core.EventArgs;

namespace TwitchChatOverlay.Core.Twitch;

public sealed class EventSubChatClient : ITwitchChatClient, IAsyncDisposable
{
    /// <summary>How often the access token is checked; the auth service refreshes it once it
    /// is within its own lead time of expiring.</summary>
    private static readonly TimeSpan TokenCheckInterval = TimeSpan.FromMinutes(5);

    private readonly EventSubWebsocketClient _client;
    private readonly TwitchAuthService _authService;
    private readonly TwitchHelixClient _helixClient;
    private readonly SettingsService _settingsService;
    private readonly ILogger<EventSubChatClient> _logger;

    private readonly ReconnectBackoff _backoff = new();
    private readonly object _retryLock = new();

    private CancellationTokenSource? _lifetimeCts;
    private Task? _retryTask;
    private Task? _tokenRefreshTask;
    private bool _stopRequested;

    public ConnectionStatus Status { get; private set; } = new(TwitchConnectionState.Disconnected);

    public event EventHandler<ConnectionStatus>? StatusChanged;
    public event EventHandler<ChatMessage>? MessageReceived;

    public EventSubChatClient(
        EventSubWebsocketClient client,
        TwitchAuthService authService,
        TwitchHelixClient helixClient,
        SettingsService settingsService,
        ILogger<EventSubChatClient> logger)
    {
        _client = client;
        _authService = authService;
        _helixClient = helixClient;
        _settingsService = settingsService;
        _logger = logger;

        _client.WebsocketConnected += OnWebsocketConnected;
        _client.WebsocketDisconnected += OnWebsocketDisconnected;
        _client.WebsocketReconnected += OnWebsocketReconnected;
        _client.ErrorOccurred += OnErrorOccurred;
        _client.ChannelChatMessage += OnChannelChatMessage;
        _client.ChannelPointsCustomRewardRedemptionAdd += OnRewardRedemption;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _stopRequested = false;
        _lifetimeCts?.Dispose();
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var connection = _settingsService.Current.Connection;
        if (string.IsNullOrWhiteSpace(connection.ChannelName))
        {
            SetStatus(TwitchConnectionState.Disconnected, "Не указано имя канала");
            return;
        }

        if (!_authService.HasClientId)
        {
            SetStatus(TwitchConnectionState.Error, "Не задан Client ID приложения Twitch");
            return;
        }

        if (!_authService.IsLoggedIn)
        {
            SetStatus(TwitchConnectionState.Disconnected, "Требуется вход через Twitch");
            return;
        }

        SetStatus(TwitchConnectionState.Connecting);
        _backoff.Reset();

        // Connecting runs in its own retry loop rather than inline: with no network at
        // startup the first attempt throws, and the app must keep trying instead of sitting
        // disconnected until the user touches a setting.
        StartRetryLoop(useReconnect: false);
        StartTokenRefreshLoop();

        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stopRequested = true;
        if (_lifetimeCts is not null)
        {
            await _lifetimeCts.CancelAsync();
        }

        await _client.DisconnectAsync();
        SetStatus(TwitchConnectionState.Disconnected);
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        await StopAsync(ct);
        await StartAsync(ct);
    }

    /// <summary>
    /// Subscriptions are created only on a fresh connect. On a Twitch-requested reconnect
    /// (session_reconnect) the existing subscriptions carry over to the new session, so
    /// re-creating them would produce duplicate notifications.
    /// </summary>
    private async Task OnWebsocketConnected(object? sender, WebsocketConnectedArgs args)
    {
        _backoff.Reset();

        if (args.IsRequestedReconnect)
        {
            _logger.LogInformation("Websocket переподключён по запросу Twitch, подписки сохранены");
            SetStatus(TwitchConnectionState.Connected);
            return;
        }

        var ct = _lifetimeCts?.Token ?? CancellationToken.None;

        try
        {
            var accessToken = await _authService.GetValidAccessTokenAsync(ct);
            if (accessToken is null)
            {
                SetStatus(TwitchConnectionState.Error, "Токен недействителен, войдите заново");
                return;
            }

            var connection = _settingsService.Current.Connection;
            var clientId = _authService.ClientId;

            var broadcasterId = await _helixClient.GetUserIdByLoginAsync(connection.ChannelName, accessToken, clientId, ct);
            if (broadcasterId is null)
            {
                SetStatus(TwitchConnectionState.Error, $"Канал \"{connection.ChannelName}\" не найден");
                return;
            }

            var chatSubscribed = await _helixClient.CreateSubscriptionAsync(
                "channel.chat.message", "1",
                new Dictionary<string, string>
                {
                    ["broadcaster_user_id"] = broadcasterId,
                    ["user_id"] = connection.TwitchUserId
                },
                _client.SessionId, accessToken, clientId, ct);

            if (!chatSubscribed)
            {
                SetStatus(TwitchConnectionState.Error, "Не удалось подписаться на сообщения чата");
                return;
            }

            if (_settingsService.Current.Filters.AccessLevel == ChatAccessLevel.ChannelPointsOnly)
            {
                await _helixClient.CreateSubscriptionAsync(
                    "channel.channel_points_custom_reward_redemption.add", "1",
                    new Dictionary<string, string> { ["broadcaster_user_id"] = broadcasterId },
                    _client.SessionId, accessToken, clientId, ct);
            }

            SetStatus(TwitchConnectionState.Connected);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при создании подписок EventSub");
            SetStatus(TwitchConnectionState.Error, ex.Message);
        }
    }

    private Task OnWebsocketDisconnected(object? sender, WebsocketDisconnectedArgs args)
    {
        if (!_stopRequested)
        {
            _logger.LogWarning("Websocket отключён, запускается переподключение");
            StartRetryLoop(useReconnect: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts the retry loop unless one is already running — the disconnect event can fire
    /// more than once for a single outage, and two competing loops would double the traffic.
    /// </summary>
    private void StartRetryLoop(bool useReconnect)
    {
        lock (_retryLock)
        {
            if (_retryTask is { IsCompleted: false })
            {
                return;
            }

            var ct = _lifetimeCts?.Token ?? CancellationToken.None;
            _retryTask = Task.Run(() => RetryLoopAsync(useReconnect, ct), CancellationToken.None);
        }
    }

    /// <summary>
    /// Keeps trying until it succeeds or the app shuts down, backing off 1s → 2s → … → 60s.
    /// A single attempt is not enough: the whole point is surviving an outage that outlasts it.
    /// </summary>
    private async Task RetryLoopAsync(bool useReconnect, CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested && !_stopRequested)
        {
            attempt++;

            try
            {
                var connected = useReconnect
                    ? await _client.ReconnectAsync()
                    : await _client.ConnectAsync();

                if (connected)
                {
                    _logger.LogInformation("Подключение установлено с попытки {Attempt}", attempt);
                    return;
                }

                _logger.LogWarning("Попытка подключения {Attempt} не удалась", attempt);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка при попытке подключения {Attempt}", attempt);
            }

            // A failed ReconnectAsync means the old session is gone for good; every further
            // attempt has to be a fresh connect.
            useReconnect = false;

            var delay = _backoff.Next();
            SetStatus(TwitchConnectionState.Connecting, $"Переподключение через {delay.TotalSeconds:0} с");

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Refreshes the access token ahead of expiry so a long stream never drops because the
    /// token quietly died. GetValidAccessTokenAsync is a no-op until the lead time is reached.
    /// </summary>
    private void StartTokenRefreshLoop()
    {
        lock (_retryLock)
        {
            if (_tokenRefreshTask is { IsCompleted: false })
            {
                return;
            }

            var ct = _lifetimeCts?.Token ?? CancellationToken.None;
            _tokenRefreshTask = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TokenCheckInterval);

                try
                {
                    while (await timer.WaitForNextTickAsync(ct))
                    {
                        if (_stopRequested)
                        {
                            return;
                        }

                        var token = await _authService.GetValidAccessTokenAsync(ct);
                        if (token is null)
                        {
                            _logger.LogWarning("Токен недействителен и не обновляется — требуется повторный вход");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // shutting down
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Цикл обновления токена завершился с ошибкой");
                }
            }, CancellationToken.None);
        }
    }

    private Task OnWebsocketReconnected(object? sender, WebsocketReconnectedArgs args)
    {
        _logger.LogInformation("Websocket переподключён");
        SetStatus(TwitchConnectionState.Connected);
        return Task.CompletedTask;
    }

    private Task OnErrorOccurred(object? sender, ErrorOccuredArgs args)
    {
        _logger.LogError(args.Exception, "Ошибка websocket EventSub: {Message}", args.Message);
        SetStatus(TwitchConnectionState.Error, args.Message);
        return Task.CompletedTask;
    }

    private Task OnChannelChatMessage(object? sender, ChannelChatMessageArgs args)
    {
        var payload = args.Payload.Event;

        var emoteTexts = payload.Message.Fragments
            .Where(f => string.Equals(f.Type, "emote", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToArray();

        var message = new ChatMessage
        {
            MessageId = payload.MessageId,
            UserId = payload.ChatterUserId,
            UserLogin = payload.ChatterUserLogin,
            DisplayName = payload.ChatterUserName,
            Text = payload.Message.Text,
            ColorHex = string.IsNullOrWhiteSpace(payload.Color) ? null : payload.Color,
            BadgeSetIds = payload.Badges.Select(b => b.SetId).ToArray(),
            IsSubscriber = payload.IsSubscriber,
            IsVip = payload.IsVip,
            IsModerator = payload.IsModerator,
            IsBroadcaster = payload.IsBroadcaster,
            EmoteTexts = emoteTexts
        };

        MessageReceived?.Invoke(this, message);
        return Task.CompletedTask;
    }

    private Task OnRewardRedemption(object? sender, ChannelPointsCustomRewardRedemptionArgs args)
    {
        var payload = args.Payload.Event;

        // Only meaningful when the reward carries user text; a reward without input has
        // nothing to read out.
        if (string.IsNullOrWhiteSpace(payload.UserInput))
        {
            return Task.CompletedTask;
        }

        var message = new ChatMessage
        {
            MessageId = payload.Id,
            UserId = payload.UserId,
            UserLogin = payload.UserLogin,
            DisplayName = payload.UserName,
            Text = payload.UserInput,
            RedeemedRewardTitle = payload.Reward.Title
        };

        MessageReceived?.Invoke(this, message);
        return Task.CompletedTask;
    }

    private void SetStatus(TwitchConnectionState state, string? detail = null)
    {
        Status = new ConnectionStatus(state, detail);
        StatusChanged?.Invoke(this, Status);
    }

    public async ValueTask DisposeAsync()
    {
        _client.WebsocketConnected -= OnWebsocketConnected;
        _client.WebsocketDisconnected -= OnWebsocketDisconnected;
        _client.WebsocketReconnected -= OnWebsocketReconnected;
        _client.ErrorOccurred -= OnErrorOccurred;
        _client.ChannelChatMessage -= OnChannelChatMessage;
        _client.ChannelPointsCustomRewardRedemptionAdd -= OnRewardRedemption;

        if (_lifetimeCts is not null)
        {
            await _lifetimeCts.CancelAsync();
            _lifetimeCts.Dispose();
        }
    }
}
