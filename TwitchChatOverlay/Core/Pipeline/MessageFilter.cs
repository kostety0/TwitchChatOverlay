using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.Core.Pipeline;

/// <summary>Outcome of filtering: either a (possibly rewritten) message, or a reason it was dropped.</summary>
public sealed record FilterResult(ChatMessage? Message, string? RejectionReason)
{
    public bool Allowed => Message is not null;

    public static FilterResult Accept(ChatMessage message) => new(message, null);
    public static FilterResult Reject(string reason) => new(null, reason);
}

/// <summary>
/// All chat filtering rules in one place, applied between EventSub and the queue.
/// Settings are read per message, so every checkbox on the "Фильтры и доступ" tab takes
/// effect on the very next message without a restart.
/// </summary>
public sealed partial class MessageFilter
{
    private readonly SettingsService _settingsService;
    private readonly ILogger<MessageFilter> _logger;

    /// <summary>Last accepted message per user id, for the per-user cooldown.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAccepted = new();

    /// <summary>Cheap guard against the dictionary growing without bound in a big chat.</summary>
    private const int CooldownPruneThreshold = 512;

    public MessageFilter(SettingsService settingsService, ILogger<MessageFilter> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    [GeneratedRegex(@"(https?://\S+)|(www\.\S+)|(\b[\w-]+\.(com|net|org|ru|io|tv|gg|me|dev|xyz)\b\S*)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    /// <summary>Three or more of the same character collapse to two ("ааааа" → "аа").</summary>
    [GeneratedRegex(@"(.)\1{2,}")]
    private static partial Regex RepeatedCharRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex ExtraWhitespaceRegex();

    public FilterResult Apply(ChatMessage message)
    {
        var settings = _settingsService.Current.Filters;

        if (IsUserBlacklisted(settings, message))
        {
            return FilterResult.Reject("пользователь в чёрном списке");
        }

        if (!HasAccess(settings, message))
        {
            return FilterResult.Reject("недостаточно прав для озвучки");
        }

        var text = message.Text.Trim();

        if (settings.IgnoreCommands && text.StartsWith('!'))
        {
            return FilterResult.Reject("команда чата");
        }

        // Cooldown is checked before any rewriting, but only stamped once the message is
        // actually accepted — a dropped message must not start someone's cooldown.
        if (IsOnCooldown(settings, message, out var remaining))
        {
            return FilterResult.Reject($"кулдаун пользователя, осталось {remaining.TotalSeconds:0.#} с");
        }

        text = ApplyTextRules(settings, message, text);

        if (string.IsNullOrWhiteSpace(text))
        {
            return FilterResult.Reject("пустой текст после фильтрации");
        }

        if (ContainsBlacklistedWord(settings, text))
        {
            return FilterResult.Reject("сообщение содержит слово из чёрного списка");
        }

        text = Truncate(text, settings.MaxMessageLength);

        StampCooldown(settings, message);

        return FilterResult.Accept(message with { Text = text });
    }

    private static bool IsUserBlacklisted(FiltersSettings settings, ChatMessage message) =>
        settings.UserBlacklist.Any(entry =>
            entry.Equals(message.UserLogin, StringComparison.OrdinalIgnoreCase) ||
            entry.Equals(message.DisplayName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Higher roles always satisfy a lower requirement — a moderator who is not
    /// subscribed still passes the "Подписчики" gate.</summary>
    private static bool HasAccess(FiltersSettings settings, ChatMessage message) => settings.AccessLevel switch
    {
        ChatAccessLevel.Everyone => true,
        ChatAccessLevel.Subscribers => message.IsSubscriber || message.IsVip || message.IsModerator || message.IsBroadcaster,
        ChatAccessLevel.VipAndAbove => message.IsVip || message.IsModerator || message.IsBroadcaster,
        ChatAccessLevel.ModeratorsAndAbove => message.IsModerator || message.IsBroadcaster,
        ChatAccessLevel.ChannelPointsOnly => MatchesReward(settings, message),
        _ => true
    };

    private static bool MatchesReward(FiltersSettings settings, ChatMessage message)
    {
        if (message.RedeemedRewardTitle is null)
        {
            return false;
        }

        // An empty reward name means "any redemption counts".
        return string.IsNullOrWhiteSpace(settings.ChannelPointsRewardName) ||
               settings.ChannelPointsRewardName.Trim().Equals(message.RedeemedRewardTitle.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string ApplyTextRules(FiltersSettings settings, ChatMessage message, string text)
    {
        if (settings.StripEmotes && message.EmoteTexts.Count > 0)
        {
            foreach (var emote in message.EmoteTexts.Distinct(StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(emote))
                {
                    text = text.Replace(emote, " ", StringComparison.Ordinal);
                }
            }
        }

        if (settings.StripLinks)
        {
            text = LinkRegex().Replace(text, " ");
        }

        if (settings.CollapseRepeatedChars)
        {
            text = RepeatedCharRegex().Replace(text, "$1$1");
        }

        return ExtraWhitespaceRegex().Replace(text, " ").Trim();
    }

    private static bool ContainsBlacklistedWord(FiltersSettings settings, string text) =>
        settings.WordBlacklist.Any(word =>
            !string.IsNullOrWhiteSpace(word) &&
            text.Contains(word.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string text, int maxLength)
    {
        var limit = Math.Clamp(maxLength, 20, 500);
        if (text.Length <= limit)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, limit - 1).TrimEnd(), "…");
    }

    private bool IsOnCooldown(FiltersSettings settings, ChatMessage message, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;

        if (settings.UserCooldownSeconds <= 0)
        {
            return false;
        }

        if (!_lastAccepted.TryGetValue(message.UserId, out var last))
        {
            return false;
        }

        var cooldown = TimeSpan.FromSeconds(settings.UserCooldownSeconds);
        var elapsed = DateTimeOffset.UtcNow - last;
        if (elapsed >= cooldown)
        {
            return false;
        }

        remaining = cooldown - elapsed;
        return true;
    }

    private void StampCooldown(FiltersSettings settings, ChatMessage message)
    {
        if (settings.UserCooldownSeconds <= 0)
        {
            return;
        }

        _lastAccepted[message.UserId] = DateTimeOffset.UtcNow;

        if (_lastAccepted.Count > CooldownPruneThreshold)
        {
            PruneCooldowns(TimeSpan.FromSeconds(settings.UserCooldownSeconds));
        }
    }

    private void PruneCooldowns(TimeSpan cooldown)
    {
        var cutoff = DateTimeOffset.UtcNow - cooldown;
        foreach (var pair in _lastAccepted)
        {
            if (pair.Value < cutoff)
            {
                _lastAccepted.TryRemove(pair.Key, out _);
            }
        }

        _logger.LogDebug("Кэш кулдаунов очищен, осталось записей: {Count}", _lastAccepted.Count);
    }
}
