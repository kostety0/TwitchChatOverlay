using TwitchChatOverlay.Core.Pipeline;

namespace TwitchChatOverlay.Core.Twitch;

public enum TwitchConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

public sealed record ConnectionStatus(TwitchConnectionState State, string? Detail = null);

public interface ITwitchChatClient
{
    ConnectionStatus Status { get; }

    event EventHandler<ConnectionStatus>? StatusChanged;
    event EventHandler<ChatMessage>? MessageReceived;

    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);

    /// <summary>Re-reads channel/token settings and reconnects from scratch.</summary>
    Task RestartAsync(CancellationToken ct);
}
