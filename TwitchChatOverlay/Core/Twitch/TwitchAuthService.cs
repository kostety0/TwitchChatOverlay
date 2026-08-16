using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.Core.Twitch;

public sealed record DeviceCodePrompt(string UserCode, string VerificationUri, int ExpiresInSeconds);

public sealed class TwitchAuthService
{
    /// <summary>
    /// Twitch Application Client ID. Register an app at https://dev.twitch.tv/console/apps
    /// with OAuth Redirect URL "http://localhost" and paste its Client ID here so end users
    /// never have to. Device Code Flow is a public-client flow: no client secret is involved,
    /// so shipping this ID in the binary is expected and safe.
    /// Users can also override it on the Connection tab if this is left empty.
    /// </summary>
    private const string BuiltInClientId = "";

    private const string RequiredScopes = "user:read:chat channel:read:redemptions";

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
    /// Starts Device Code Flow: returns the code/URL to show the user, and a task that
    /// completes once they finish authorizing in the browser (or the code expires).
    /// </summary>
    public async Task<(DeviceCodePrompt Prompt, Task<bool> Completion)> StartDeviceCodeFlowAsync(CancellationToken ct)
    {
        if (!HasClientId)
        {
            throw new InvalidOperationException("Не задан Client ID приложения Twitch.");
        }

        using var http = CreateHttp();
        var response = await http.PostAsync(
            $"https://id.twitch.tv/oauth2/device?client_id={Uri.EscapeDataString(ClientId)}&scopes={Uri.EscapeDataString(RequiredScopes)}",
            content: null,
            ct);

        response.EnsureSuccessStatusCode();
        var device = await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken: ct)
                     ?? throw new InvalidOperationException("Twitch вернул пустой ответ на запрос device code.");

        var prompt = new DeviceCodePrompt(device.UserCode, device.VerificationUri, device.ExpiresIn);
        var completion = PollForTokenAsync(device, ct);
        return (prompt, completion);
    }

    private async Task<bool> PollForTokenAsync(DeviceCodeResponse device, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(device.Interval, 1));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);

        using var http = CreateHttp();

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);

            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = device.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });

            var response = await http.PostAsync("https://id.twitch.tv/oauth2/token", body, ct);
            if (response.IsSuccessStatusCode)
            {
                var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
                if (token is null)
                {
                    return false;
                }

                await StoreTokensAsync(token, ct);
                await FetchAndStoreUserIdentityAsync(ct);
                _logger.LogInformation("Авторизация Twitch выполнена успешно");
                return true;
            }

            // "authorization_pending" is the normal case while the user is still in the browser.
            var error = await response.Content.ReadAsStringAsync(ct);
            if (!error.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Device Code Flow прерван, ответ Twitch: {Error}", error);
                return false;
            }
        }

        _logger.LogWarning("Срок действия кода авторизации истёк");
        return false;
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
                    _logger.LogWarning("Не удалось обновить токен: {Status}", response.StatusCode);
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

    private sealed record DeviceCodeResponse(
        [property: JsonPropertyName("device_code")] string DeviceCode,
        [property: JsonPropertyName("user_code")] string UserCode,
        [property: JsonPropertyName("verification_uri")] string VerificationUri,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval);

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
