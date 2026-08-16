namespace TwitchChatOverlay.Core.Pipeline;

/// <summary>
/// Transport-agnostic chat message. Decouples the pipeline (filter → queue → dispatcher)
/// from TwitchLib's EventSub payload types.
/// </summary>
public sealed record ChatMessage
{
    public required string MessageId { get; init; }
    public required string UserId { get; init; }
    public required string UserLogin { get; init; }
    public required string DisplayName { get; init; }
    public required string Text { get; init; }
    public string? ColorHex { get; init; }
    public IReadOnlyList<string> BadgeSetIds { get; init; } = Array.Empty<string>();

    public bool IsSubscriber { get; init; }
    public bool IsVip { get; init; }
    public bool IsModerator { get; init; }
    public bool IsBroadcaster { get; init; }

    /// <summary>Set when the message arrived through a channel points reward redemption.</summary>
    public string? RedeemedRewardTitle { get; init; }

    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Emote text fragments, used by the "вырезать эмоуты" filter.</summary>
    public IReadOnlyList<string> EmoteTexts { get; init; } = Array.Empty<string>();
}
