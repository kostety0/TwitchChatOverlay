using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace TwitchChatOverlay.Core.Twitch;

/// <summary>
/// Minimal Helix surface: resolving a channel login to a user id, and creating the EventSub
/// subscriptions for a websocket session. TwitchLib.EventSub.Websockets only transports
/// notifications — subscriptions themselves must be created over HTTP.
/// </summary>
public sealed class TwitchHelixClient
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TwitchHelixClient> _logger;

    public TwitchHelixClient(IHttpClientFactory httpClientFactory, ILogger<TwitchHelixClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<string?> GetUserIdByLoginAsync(string login, string accessToken, string clientId, CancellationToken ct)
    {
        using var http = CreateHttp();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(login)}");
        AddAuth(request, accessToken, clientId);

        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Не удалось найти канал {Login}: {Status}", login, response.StatusCode);
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<UsersResponse>(cancellationToken: ct);
        return payload?.Data.FirstOrDefault()?.Id;
    }

    public async Task<bool> CreateSubscriptionAsync(
        string type,
        string version,
        Dictionary<string, string> condition,
        string sessionId,
        string accessToken,
        string clientId,
        CancellationToken ct)
    {
        using var http = CreateHttp();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.twitch.tv/helix/eventsub/subscriptions");
        AddAuth(request, accessToken, clientId);
        request.Content = JsonContent.Create(new
        {
            type,
            version,
            condition,
            transport = new { method = "websocket", session_id = sessionId }
        });

        var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Подписка EventSub создана: {Type}", type);
            return true;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogError("Не удалось создать подписку {Type}: {Status} {Body}", type, response.StatusCode, body);
        return false;
    }

    private static void AddAuth(HttpRequestMessage request, string accessToken, string clientId)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Client-Id", clientId);
    }

    private HttpClient CreateHttp()
    {
        var http = _httpClientFactory.CreateClient();
        http.Timeout = HttpTimeout;
        return http;
    }

    private sealed record UsersResponse([property: JsonPropertyName("data")] List<UserData> Data);

    private sealed record UserData([property: JsonPropertyName("id")] string Id);
}
