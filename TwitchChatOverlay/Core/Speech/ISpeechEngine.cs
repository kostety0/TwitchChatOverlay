using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.Core.Speech;

public interface ISpeechEngine
{
    SpeechEngineType EngineType { get; }

    /// <summary>True when the engine can actually run — Piper reports false when the
    /// executable or voice models are missing, which drives the automatic SAPI fallback.</summary>
    bool IsAvailable { get; }

    /// <summary>Reason shown in the UI when <see cref="IsAvailable"/> is false.</summary>
    string? UnavailableReason { get; }

    /// <summary>Synthesizes a complete RIFF/WAV buffer. Throws on synthesis failure —
    /// the dispatcher logs, skips the message and continues the queue.</summary>
    Task<byte[]> SynthesizeAsync(string text, VoiceProfile profile, CancellationToken ct);
}
