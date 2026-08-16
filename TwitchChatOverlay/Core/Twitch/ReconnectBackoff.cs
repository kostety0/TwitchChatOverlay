namespace TwitchChatOverlay.Core.Twitch;

/// <summary>
/// Exponential backoff schedule for reconnect attempts: 1s → 2s → 4s → … → 60s, then 60s
/// forever. Kept as its own type so the schedule can be tested without a websocket.
/// </summary>
public sealed class ReconnectBackoff
{
    private readonly TimeSpan _initial;
    private readonly TimeSpan _max;
    private TimeSpan _current;

    public ReconnectBackoff(TimeSpan? initial = null, TimeSpan? max = null)
    {
        _initial = initial ?? TimeSpan.FromSeconds(1);
        _max = max ?? TimeSpan.FromSeconds(60);
        _current = _initial;
    }

    public TimeSpan Current => _current;

    /// <summary>Returns the delay to wait now, then doubles it for the next call.</summary>
    public TimeSpan Next()
    {
        var delay = _current;
        var doubled = _current.TotalSeconds * 2;
        _current = TimeSpan.FromSeconds(Math.Min(doubled, _max.TotalSeconds));
        return delay;
    }

    /// <summary>Called after a successful connection so the next outage starts from 1s again.</summary>
    public void Reset() => _current = _initial;
}
