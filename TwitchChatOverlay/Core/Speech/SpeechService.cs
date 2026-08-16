using Microsoft.Extensions.Logging;
using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.Core.Speech;

/// <summary>
/// Picks the engine for each utterance and handles the automatic Piper → SAPI fallback.
/// Also owns per-viewer voice assignment so a given user always sounds the same.
/// </summary>
public sealed class SpeechService
{
    private readonly PiperSpeechEngine _piper;
    private readonly SapiSpeechEngine _sapi;
    private readonly VoiceCatalog _catalog;
    private readonly SettingsService _settingsService;
    private readonly ILogger<SpeechService> _logger;

    public SpeechService(
        PiperSpeechEngine piper,
        SapiSpeechEngine sapi,
        VoiceCatalog catalog,
        SettingsService settingsService,
        ILogger<SpeechService> logger)
    {
        _piper = piper;
        _sapi = sapi;
        _catalog = catalog;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>Non-null when the configured engine is unusable and the app silently fell
    /// back to SAPI — the settings UI surfaces this so the user knows why.</summary>
    public string? FallbackNotice { get; private set; }

    public ISpeechEngine ResolveEngine()
    {
        var configured = _settingsService.Current.Voice.Engine;

        if (configured == SpeechEngineType.Piper && !_piper.IsAvailable)
        {
            FallbackNotice = $"{_piper.UnavailableReason}. Используется Windows SAPI.";
            _logger.LogWarning("Piper недоступен: {Reason}. Откат на SAPI", _piper.UnavailableReason);
            return _sapi;
        }

        FallbackNotice = null;
        return configured == SpeechEngineType.Piper ? _piper : _sapi;
    }

    /// <summary>
    /// Builds the voice profile for a message. With "уникальный голос на зрителя" enabled,
    /// the voice is chosen from the pool by a stable hash of the user id, so the same viewer
    /// always gets the same voice across restarts.
    /// </summary>
    public VoiceProfile? BuildProfile(string userId)
    {
        var voiceSettings = _settingsService.Current.Voice;
        var engine = ResolveEngine();

        var voice = voiceSettings.UniqueVoicePerViewer
            ? PickVoiceForViewer(engine.EngineType, userId, voiceSettings)
            : _catalog.Resolve(engine.EngineType, voiceSettings.SelectedVoiceId, voiceSettings.Gender);

        if (voice is null)
        {
            return null;
        }

        return new VoiceProfile
        {
            Voice = voice,
            Rate = voiceSettings.Rate,
            Pitch = voiceSettings.Pitch
        };
    }

    private VoiceDescriptor? PickVoiceForViewer(SpeechEngineType engineType, string userId, VoiceSettings voiceSettings)
    {
        var available = _catalog.GetVoices(engineType);
        if (available.Count == 0)
        {
            return null;
        }

        var pool = voiceSettings.VoicePoolForAssignment.Count > 0
            ? available.Where(v => voiceSettings.VoicePoolForAssignment.Contains(v.Id, StringComparer.OrdinalIgnoreCase)).ToList()
            : available.ToList();

        if (pool.Count == 0)
        {
            pool = available.ToList();
        }

        var index = (int)(StableHash(userId) % (uint)pool.Count);
        return pool[index];
    }

    /// <summary>FNV-1a: string.GetHashCode() is randomized per process, which would reshuffle
    /// every viewer's voice on each restart.</summary>
    private static uint StableHash(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= prime;
        }

        return hash;
    }

    public Task<byte[]> SynthesizeAsync(string text, VoiceProfile profile, CancellationToken ct)
    {
        var engine = profile.Voice.Engine == SpeechEngineType.Piper && _piper.IsAvailable
            ? (ISpeechEngine)_piper
            : _sapi;

        return engine.SynthesizeAsync(text, profile, ct);
    }
}
