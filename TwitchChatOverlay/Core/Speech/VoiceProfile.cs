using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.Core.Speech;

/// <summary>Quality tier shown next to a voice name in the settings dropdown.</summary>
public enum VoiceQuality
{
    Low,
    Medium,
    High,
    Unknown
}

/// <summary>
/// A voice as offered to the user, independent of engine. <see cref="Id"/> is what gets
/// persisted: a Piper model file path, or a SAPI voice name.
/// </summary>
public sealed record VoiceDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required SpeechEngineType Engine { get; init; }
    public required VoiceGender Gender { get; init; }
    public string Language { get; init; } = string.Empty;
    public VoiceQuality Quality { get; init; } = VoiceQuality.Unknown;

    public string QualityLabel => Quality switch
    {
        VoiceQuality.High => "высокое качество",
        VoiceQuality.Medium => "среднее качество",
        VoiceQuality.Low => "низкое качество",
        _ => string.Empty
    };
}

/// <summary>Synthesis parameters for a single utterance.</summary>
public sealed record VoiceProfile
{
    public required VoiceDescriptor Voice { get; init; }

    /// <summary>0.5–2.0, where 1.0 is the voice's natural speed.</summary>
    public double Rate { get; init; } = 1.0;

    /// <summary>−10…+10. Applied in the playback chain so it works for every engine.</summary>
    public int Pitch { get; init; }
}
