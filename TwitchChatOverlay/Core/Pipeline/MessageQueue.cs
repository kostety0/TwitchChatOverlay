using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.Core.Pipeline;

/// <summary>
/// Bounded queue in front of the dispatcher. Bounded is the point: under a 10+ messages/second
/// flood the queue must stay at its configured size instead of growing without limit.
/// </summary>
public sealed class MessageQueue
{
    /// <summary>Hard ceiling matching the settings slider's maximum. The channel is created
    /// once at this size and the user's limit is enforced logically, so changing the limit
    /// takes effect immediately without discarding what is already queued.</summary>
    private const int HardCapacity = 50;

    private readonly SettingsService _settingsService;
    private readonly ILogger<MessageQueue> _logger;
    private readonly Channel<ChatMessage> _channel;

    public MessageQueue(SettingsService settingsService, ILogger<MessageQueue> logger)
    {
        _settingsService = settingsService;
        _logger = logger;

        _channel = Channel.CreateBounded<ChatMessage>(new BoundedChannelOptions(HardCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public ChannelReader<ChatMessage> Reader => _channel.Reader;

    public int Count => _channel.Reader.Count;

    public void Enqueue(ChatMessage message)
    {
        var settings = _settingsService.Current.Queue;
        var limit = Math.Clamp(settings.MaxQueueSize, 1, HardCapacity);

        if (_channel.Reader.Count >= limit)
        {
            if (settings.OverflowPolicy == QueueOverflowPolicy.DropNewest)
            {
                _logger.LogDebug("Сообщение от {User} отброшено: очередь заполнена", message.UserLogin);
                return;
            }

            // EvictOldest: make room by discarding the stalest message. Reading in a loop
            // because the limit may have just been lowered below the current count.
            while (_channel.Reader.Count >= limit && _channel.Reader.TryRead(out var evicted))
            {
                _logger.LogDebug("Сообщение от {User} вытеснено из очереди", evicted.UserLogin);
            }
        }

        if (!_channel.Writer.TryWrite(message))
        {
            _logger.LogDebug("Сообщение от {User} отброшено: канал заполнен", message.UserLogin);
        }
    }

    /// <summary>True when the message sat in the queue longer than the configured limit.</summary>
    public bool IsStale(ChatMessage message)
    {
        var settings = _settingsService.Current.Queue;
        if (!settings.SkipOldMessages)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - message.ReceivedAt > TimeSpan.FromSeconds(settings.SkipOlderThanSeconds);
    }

    /// <summary>Drains everything currently queued — the "очистить очередь" hotkey.</summary>
    public void Clear()
    {
        while (_channel.Reader.TryRead(out _))
        {
        }
    }
}
