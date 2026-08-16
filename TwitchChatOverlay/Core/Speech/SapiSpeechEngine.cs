using System.IO;
using System.Speech.Synthesis;
using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.Core.Speech;

/// <summary>
/// Windows SAPI5 fallback. Always available on Windows, so it is what the app falls back to
/// when piper.exe or its models are missing.
/// </summary>
public sealed class SapiSpeechEngine : ISpeechEngine
{
    public SpeechEngineType EngineType => SpeechEngineType.Sapi;

    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public Task<byte[]> SynthesizeAsync(string text, VoiceProfile profile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // System.Speech is synchronous and CPU-bound; keep it off the caller's thread so the
        // UI thread and the dispatcher loop are never blocked by synthesis.
        return Task.Run(() =>
        {
            using var synthesizer = new SpeechSynthesizer();

            try
            {
                synthesizer.SelectVoice(profile.Voice.Id);
            }
            catch (ArgumentException)
            {
                // Voice disappeared since the catalog was built — the default voice still speaks.
            }

            // SAPI's Rate is -10…10 on a roughly exponential scale; map the 0.5–2.0 multiplier
            // onto it so the slider means the same thing as it does for Piper.
            synthesizer.Rate = (int)Math.Round(Math.Clamp(Math.Log2(Math.Clamp(profile.Rate, 0.5, 2.0)) * 10, -10, 10));

            using var stream = new MemoryStream();
            synthesizer.SetOutputToWaveStream(stream);
            synthesizer.Speak(text);
            return stream.ToArray();
        }, ct);
    }
}
