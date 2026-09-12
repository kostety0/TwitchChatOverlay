using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.Core.Twitch;

public sealed class TwitchAuthService
{
    /// <summary>
    /// Twitch Application Client ID. Register an app at https://dev.twitch.tv/console/apps
    /// and paste its Client ID here so end users never have to. Authorization Code Flow for a
    /// public client involves no client secret, so shipping this ID in the binary is expected
    /// and safe. Users can also override it on the Connection tab if this is left empty.
    /// </summary>
    private const string BuiltInClientId = "";

    private const string RequiredScopes = "user:read:chat channel:read:redemptions";

    /// <summary>
    /// Port the app listens on while the browser completes sign-in. A fixed port is required
    /// because Twitch matches redirect_uri exactly against the value registered in the
    /// developer console — it cannot be chosen at random per run.
    /// </summary>
    public const int CallbackPort = 47990;

    public static string RedirectUri => $"http://localhost:{CallbackPort}/";

    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Refresh this early so a long synthesis/playback cycle never runs into a dead token.</summary>
    private static readonly TimeSpan RefreshLeadTime = TimeSpan.FromMinutes(10);

    private readonly SettingsService _settingsService;
    private readonly ILogger<TwitchAuthService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public TwitchAuthService(SettingsService settingsService, ILogger<TwitchAuthService> logger, IHttpClientFactory httpClientFactory)
    {
        _settingsService = settingsService;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public string ClientId
    {
        get
        {
            var configured = _settingsService.Current.Connection.ClientId;
            return !string.IsNullOrWhiteSpace(configured) ? configured.Trim() : BuiltInClientId;
        }
    }

    public bool HasClientId => !string.IsNullOrWhiteSpace(ClientId);

    public bool IsLoggedIn => TokenProtector.Unprotect(_settingsService.Current.Connection.ProtectedAccessToken) is not null;

    /// <summary>
    /// Starts Authorization Code Flow. Returns the URL to open in the browser and a task that
    /// completes once Twitch redirects back to the local listener.
    ///
    /// This replaced Device Code Flow, where the user had to retype an 8-character code on
    /// twitch.tv for every sign-in. Here the browser returns the authorization code straight
    /// to the app, so signing in is a single "Authorize" click.
    /// </summary>
    public (string AuthorizeUrl, Task<bool> Completion) StartSignIn(CancellationToken ct)
    {
        if (!HasClientId)
        {
            throw new InvalidOperationException("Не задан Client ID приложения Twitch.");
        }

        // state ties the browser response back to this request and is what makes a forged
        // callback from another page harmless.
        var state = Guid.NewGuid().ToString("N");

        var url = "https://id.twitch.tv/oauth2/authorize" +
                  "?response_type=code" +
                  $"&client_id={Uri.EscapeDataString(ClientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                  $"&scope={Uri.EscapeDataString(RequiredScopes)}" +
                  $"&state={state}";

        return (url, WaitForCallbackAsync(state, ct));
    }

    private async Task<bool> WaitForCallbackAsync(string expectedState, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _logger.LogError(ex, "Не удалось занять локальный порт {Port} для приёма ответа Twitch", CallbackPort);
            throw new InvalidOperationException(
                $"Не удалось открыть локальный порт {CallbackPort}. Возможно, его занимает другая программа.", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);

        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var contextTask = listener.GetContextAsync();
                var finished = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeout.Token));
                if (finished != contextTask)
                {
                    _logger.LogWarning("Ожидание ответа Twitch прервано или истекло");
                    return false;
                }

                var context = await contextTask;
                var query = context.Request.QueryString;
                var code = query["code"];
                var state = query["state"];
                var error = query["error"];

                if (!string.IsNullOrEmpty(error))
                {
                    var description = query["error_description"] ?? error;
                    _logger.LogWarning("Twitch отклонил авторизацию: {Error}", description);
                    await RespondAsync(context, "Вход не выполнен", description, success: false);
                    return false;
                }

                if (string.IsNullOrEmpty(code))
                {
                    // Браузер попутно просит favicon.ico и подобное — это не ответ Twitch.
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                if (!string.Equals(state, expectedState, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Получен ответ с несовпадающим state — запрос отклонён");
                    await RespondAsync(context, "Вход не выполнен", "Ответ не соответствует запросу.", success: false);
                    return false;
                }

                var ok = await ExchangeCodeAsync(code, ct);
                await RespondAsync(
                    context,
                    ok ? "Готово" : "Вход не выполнен",
                    ok ? "Вернитесь в TwitchChatOverlay — окно браузера можно закрыть." : "Не удалось обменять код на токен. Подробности в журнале приложения.",
                    ok);
                return ok;
            }

            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<bool> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        using var http = CreateHttp();
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            // Twitch требует, чтобы redirect_uri здесь совпадал с тем, что был в /authorize.
            ["redirect_uri"] = RedirectUri
        });

        var response = await http.PostAsync("https://id.twitch.tv/oauth2/token", body, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Не удалось обменять код на токен: {Status}. Ответ Twitch: {Detail}",
                response.StatusCode, detail);
            return false;
        }

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
        if (token is null)
        {
            return false;
        }

        await StoreTokensAsync(token, ct);
        await FetchAndStoreUserIdentityAsync(ct);

        _logger.LogInformation("Авторизация Twitch выполнена успешно, refresh token {State}",
            string.IsNullOrEmpty(token.RefreshToken) ? "НЕ получен" : "получен");
        return true;
    }

    private static async Task RespondAsync(HttpListenerContext context, string title, string message, bool success)
    {
        var accent = success ? "#3FB950" : "#F85149";
        var html = $"""
            <!doctype html>
            <html lang="ru"><head><meta charset="utf-8"><title>{title}</title></head>
            <body style="margin:0;display:flex;align-items:center;justify-content:center;height:100vh;
                         background:#0E0E14;color:#F2F2F7;font-family:Segoe UI,system-ui,sans-serif">
              <div style="text-align:center;max-width:30rem;padding:2rem">
                <div style="font-size:1.6rem;font-weight:600;color:{accent};margin-bottom:.75rem">{title}</div>
                <div style="color:#9A9AAE;line-height:1.5">{message}</div>
              </div>
            </body></html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    /// <summary>
    /// Returns a valid access token, refreshing it ahead of expiry. Null means the user
    /// must sign in again.
    /// </summary>
    public async Task<string?> GetValidAccessTokenAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var connection = _settingsService.Current.Connection;
            var accessToken = TokenProtector.Unprotect(connection.ProtectedAccessToken);
            if (accessToken is null)
            {
                return null;
            }

            var expiresAt = connection.TokenExpiresAt;
            if (expiresAt is null || expiresAt - RefreshLeadTime > DateTimeOffset.UtcNow)
            {
                return accessToken;
            }

            var refreshToken = TokenProtector.Unprotect(connection.ProtectedRefreshToken);
            if (refreshToken is null)
            {
                _logger.LogWarning("Токен истёк, но refresh token отсутствует — требуется повторный вход");
                return null;
            }

            try
            {
                using var http = CreateHttp();
                var body = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken
                });

                var response = await http.PostAsync("https://id.twitch.tv/oauth2/token", body, ct);
                if (!response.IsSuccessStatusCode)
                {
                    // Twitch объясняет причину в теле ответа ("Invalid refresh token",
                    // "missing client secret" и т.п.). Без него по одному коду 400
                    // невозможно отличить протухший токен от неверного типа приложения.
                    var detail = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning("Не удалось обновить токен: {Status}. Ответ Twitch: {Detail}",
                        response.StatusCode, detail);
                    return null;
                }

                var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
                if (token is null)
                {
                    return null;
                }

                await StoreTokensAsync(token, ct);
                _logger.LogInformation("Токен Twitch обновлён");
                return token.AccessToken;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Сетевая ошибка при обновлении токена");
                return null;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var connection = _settingsService.Current.Connection;
        connection.ProtectedAccessToken = null;
        connection.ProtectedRefreshToken = null;
        connection.TokenExpiresAt = null;
        connection.TwitchUserId = string.Empty;
        connection.TwitchLogin = string.Empty;
        await _settingsService.SaveAsync(ct);
        _logger.LogInformation("Выполнен выход из аккаунта Twitch");
    }

    private async Task StoreTokensAsync(TokenResponse token, CancellationToken ct)
    {
        var connection = _settingsService.Current.Connection;
        connection.ProtectedAccessToken = TokenProtector.Protect(token.AccessToken);
        if (!string.IsNullOrEmpty(token.RefreshToken))
        {
            connection.ProtectedRefreshToken = TokenProtector.Protect(token.RefreshToken);
        }
        connection.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
        await _settingsService.SaveAsync(ct);
    }

    /// <summary>Stores the authenticated user's id — EventSub subscriptions are keyed by it.</summary>
    private async Task FetchAndStoreUserIdentityAsync(CancellationToken ct)
    {
        var accessToken = TokenProtector.Unprotect(_settingsService.Current.Connection.ProtectedAccessToken);
        if (accessToken is null)
        {
            return;
        }

        using var http = CreateHttp();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.twitch.tv/helix/users");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Client-Id", ClientId);

        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Не удалось получить данные пользователя: {Status}", response.StatusCode);
            return;
        }

        var users = await response.Content.ReadFromJsonAsync<HelixUsersResponse>(cancellationToken: ct);
        var user = users?.Data.FirstOrDefault();
        if (user is null)
        {
            return;
        }

        var connection = _settingsService.Current.Connection;
        connection.TwitchUserId = user.Id;
        connection.TwitchLogin = user.Login;
        if (string.IsNullOrWhiteSpace(connection.ChannelName))
        {
            connection.ChannelName = user.Login;
        }
        await _settingsService.SaveAsync(ct);
    }

    private HttpClient CreateHttp()
    {
        var http = _httpClientFactory.CreateClient();
        http.Timeout = HttpTimeout;
        return http;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record HelixUsersResponse(
        [property: JsonPropertyName("data")] List<HelixUser> Data);

    private sealed record HelixUser(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("login")] string Login,
        [property: JsonPropertyName("display_name")] string DisplayName);
}
