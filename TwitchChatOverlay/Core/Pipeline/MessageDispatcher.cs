using Microsoft.Extensions.Logging;
using TwitchChatOverlay.Core.Audio;
using TwitchChatOverlay.Core.Settings;
using TwitchChatOverlay.Core.Speech;

namespace TwitchChatOverlay.Core.Pipeline;

/// <summary>Everything the UI layer needs to show a card, without the UI knowing about audio.</summary>
public sealed record CardPresentation(ChatMessage Message);

/// <summary>
/// Drains the queue strictly one message at a time, but synthesizes the *next* message while
/// the current one is still playing. That prefetch removes the dead air between messages
/// without ever overlapping two voices.
/// </summary>
public sealed class MessageDispatcher : IAsyncDisposable
{
    private readonly MessageQueue _queue;
    private readonly SpeechService _speechService;
    private readonly AudioPlaybackService _playbackService;
    private readonly SettingsService _settingsService;
    private readonly ILogger<MessageDispatcher> _logger;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private CancellationTokenSource? _currentPlaybackCts;

    /// <summary>Raised when a card should appear; the UI subscribes and marshals to its thread.</summary>
    public event EventHandler<CardPresentation>? CardShowRequested;
    public event EventHandler? CardHideRequested;

    public bool IsMuted { get; set; }

    public MessageDispatcher(
        MessageQueue queue,
        SpeechService speechService,
        AudioPlaybackService playbackService,
        SettingsService settingsService,
        ILogger<MessageDispatcher> logger)
    {
        _queue = queue;
        _speechService = speechService;
        _playbackService = playbackService;
        _settingsService = settingsService;
        _logger = logger;
    }

    public void Start(CancellationToken ct)
    {
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = Task.Run(() => RunAsync(_loopCts.Token), CancellationToken.None);
    }

    /// <summary>Aborts the message currently being spoken and moves on to the next one.</summary>
    public void SkipCurrent()
    {
        _currentPlaybackCts?.Cancel();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Holds the message whose audio is already being synthesized ahead of time.
        Task<SynthesisResult>? prefetched = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pending = prefetched;
                prefetched = null;

                SynthesisResult current;
                if (pending is not null)
                {
                    current = await pending;
                }
                else
                {
                    var message = await _queue.Reader.ReadAsync(ct);
                    if (_queue.IsStale(message))
                    {
                        _logger.LogDebug("Сообщение от {User} пропущено: устарело", message.UserLogin);
                        continue;
                    }

                    current = await SynthesizeSafelyAsync(message, ct);
                }

                if (current.Audio is null)
                {
                    continue;
                }

                CardShowRequested?.Invoke(this, new CardPresentation(current.Message));

                // Kick off synthesis of the next queued message so it is ready the moment
                // playback of this one finishes.
                if (_queue.Reader.TryRead(out var next))
                {
                    prefetched = _queue.IsStale(next)
                        ? null
                        : SynthesizeSafelyAsync(next, ct);
                }

                await PlayAsync(current, ct);

                CardHideRequested?.Invoke(this, EventArgs.Empty);

                var pause = _settingsService.Current.Voice.PauseBetweenMessagesSeconds;
                if (pause > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pause), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Цикл диспетчера завершился с ошибкой");
        }
    }

    private async Task PlayAsync(SynthesisResult current, CancellationToken ct)
    {
        _currentPlaybackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            var voiceSettings = _settingsService.Current.Voice;
            await _playbackService.PlayAsync(
                current.Audio!,
                voiceSettings.OutputDeviceId,
                voiceSettings.Volume,
                voiceSettings.Pitch,
                _currentPlaybackCts.Token);

            // The card stays up for the configured tail after speech actually ends, rather
            // than for a fixed duration guessed in advance.
            var tail = _settingsService.Current.Overlay.PostSpeechTailSeconds;
            if (tail > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(tail), _currentPlaybackCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Skipped by hotkey — fall through so the loop continues with the next message.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка воспроизведения сообщения от {User}", current.Message.UserLogin);
        }
        finally
        {
            _currentPlaybackCts.Dispose();
            _currentPlaybackCts = null;
        }
    }

    /// <summary>
    /// Synthesis failures must never take the app down or stall the queue: log, return no
    /// audio, and the loop moves to the next message.
    /// </summary>
    private async Task<SynthesisResult> SynthesizeSafelyAsync(ChatMessage message, CancellationToken ct)
    {
        if (IsMuted)
        {
            return new SynthesisResult(message, null);
        }

        try
        {
            var profile = _speechService.BuildProfile(message.UserId);
            if (profile is null)
            {
                _logger.LogWarning("Нет доступных голосов для синтеза, сообщение пропущено");
                return new SynthesisResult(message, null);
            }

            var text = BuildSpeechText(message);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new SynthesisResult(message, null);
            }

            var audio = await _speechService.SynthesizeAsync(text, profile, ct);
            return new SynthesisResult(message, audio);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Синтез сообщения от {User} не удался, сообщение пропущено", message.UserLogin);
            return new SynthesisResult(message, null);
        }
    }

    private string BuildSpeechText(ChatMessage message)
    {
        var voiceSettings = _settingsService.Current.Voice;

        var userName = voiceSettings.TransliterateUsernames
            ? Transliterator.LatinToCyrillic(message.DisplayName)
            : message.DisplayName;

        if (voiceSettings.DontReadUsername)
        {
            return message.Text;
        }

        return voiceSettings.PronunciationTemplate
            .Replace("{user}", userName, StringComparison.OrdinalIgnoreCase)
            .Replace("{message}", message.Text, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync();
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        _loopCts?.Dispose();
        _currentPlaybackCts?.Dispose();
    }

    private sealed record SynthesisResult(ChatMessage Message, byte[]? Audio);
}
