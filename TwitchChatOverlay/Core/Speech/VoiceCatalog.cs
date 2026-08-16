using System.IO;
using System.Speech.Synthesis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TwitchChatOverlay.Core.Settings;
using SapiVoiceGender = System.Speech.Synthesis.VoiceGender;
using VoiceGender = TwitchChatOverlay.Core.Settings.VoiceGender;

namespace TwitchChatOverlay.Core.Speech;

/// <summary>
/// Enumerates available voices for both engines with the metadata the settings UI needs.
/// Gender lives here rather than in the engines so the "Мужской / Женский" filter behaves
/// identically whether the user is on Piper or SAPI.
/// </summary>
public sealed class VoiceCatalog
{
    /// <summary>
    /// Piper model files carry no gender field, so speaker gender is mapped by name.
    /// Keys match the speaker segment of the standard rhasspy/piper-voices file names
    /// (e.g. "ru_RU-irina-medium.onnx" → "irina").
    /// </summary>
    private static readonly Dictionary<string, VoiceGender> PiperSpeakerGenders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["irina"] = VoiceGender.Female,
        ["denis"] = VoiceGender.Male,
        ["dmitri"] = VoiceGender.Male,
        ["ruslan"] = VoiceGender.Male
    };

    private readonly ILogger<VoiceCatalog> _logger;

    public VoiceCatalog(ILogger<VoiceCatalog> logger)
    {
        _logger = logger;
    }

    public static string VoicesDirectory => Path.Combine(AppContext.BaseDirectory, "Resources", "voices");

    public IReadOnlyList<VoiceDescriptor> GetVoices(SpeechEngineType engine) =>
        engine == SpeechEngineType.Piper ? GetPiperVoices() : GetSapiVoices();

    public IReadOnlyList<VoiceDescriptor> GetPiperVoices()
    {
        if (!Directory.Exists(VoicesDirectory))
        {
            return Array.Empty<VoiceDescriptor>();
        }

        var voices = new List<VoiceDescriptor>();

        foreach (var modelPath in Directory.EnumerateFiles(VoicesDirectory, "*.onnx", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileNameWithoutExtension(modelPath);
            var parts = fileName.Split('-');
            var speaker = parts.Length > 1 ? parts[1] : fileName;

            voices.Add(new VoiceDescriptor
            {
                Id = modelPath,
                DisplayName = speaker,
                Engine = SpeechEngineType.Piper,
                Gender = PiperSpeakerGenders.TryGetValue(speaker, out var gender) ? gender : VoiceGender.Male,
                Language = ReadLanguageFromConfig(modelPath) ?? (parts.Length > 0 ? parts[0] : string.Empty),
                Quality = ParseQuality(parts.Length > 2 ? parts[2] : null)
            });
        }

        return voices;
    }

    public IReadOnlyList<VoiceDescriptor> GetSapiVoices()
    {
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            return synthesizer.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => new VoiceDescriptor
                {
                    Id = v.VoiceInfo.Name,
                    DisplayName = v.VoiceInfo.Name,
                    Engine = SpeechEngineType.Sapi,
                    Gender = v.VoiceInfo.Gender == SapiVoiceGender.Female
                        ? VoiceGender.Female
                        : VoiceGender.Male,
                    Language = v.VoiceInfo.Culture.Name,
                    Quality = VoiceQuality.Unknown
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось перечислить голоса SAPI");
            return Array.Empty<VoiceDescriptor>();
        }
    }

    /// <summary>Resolves the persisted voice id, falling back to any voice matching the
    /// requested gender so a missing/renamed model never leaves the app mute.</summary>
    public VoiceDescriptor? Resolve(SpeechEngineType engine, string? voiceId, VoiceGender gender)
    {
        var voices = GetVoices(engine);
        if (voices.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            var exact = voices.FirstOrDefault(v => string.Equals(v.Id, voiceId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        return voices.FirstOrDefault(v => v.Gender == gender) ?? voices[0];
    }

    private string? ReadLanguageFromConfig(string modelPath)
    {
        var configPath = modelPath + ".json";
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.TryGetProperty("language", out var language) &&
                language.TryGetProperty("code", out var code))
            {
                return code.GetString();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Не удалось прочитать конфигурацию голоса {Path}", configPath);
        }

        return null;
    }

    private static VoiceQuality ParseQuality(string? qualitySegment) => qualitySegment?.ToLowerInvariant() switch
    {
        "high" => VoiceQuality.High,
        "medium" => VoiceQuality.Medium,
        "low" or "x_low" => VoiceQuality.Low,
        _ => VoiceQuality.Unknown
    };
}
