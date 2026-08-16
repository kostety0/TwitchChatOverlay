using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TwitchChatOverlay.Core.Audio;
using TwitchChatOverlay.Core.Settings;
using TwitchChatOverlay.Core.Speech;

namespace TwitchChatOverlay.UI.Settings.ViewModels;

public sealed record EngineOption(SpeechEngineType Value, string DisplayName);

/// <summary>A voice with a checkbox, for the "набор голосов для раздачи" multi-select.</summary>
public sealed partial class VoicePoolItem : ObservableObject
{
    private readonly Action<VoicePoolItem> _onToggled;

    public VoicePoolItem(VoiceDescriptor voice, bool isSelected, Action<VoicePoolItem> onToggled)
    {
        Voice = voice;
        _isSelected = isSelected;
        _onToggled = onToggled;
    }

    public VoiceDescriptor Voice { get; }

    public string DisplayName => string.IsNullOrEmpty(Voice.QualityLabel)
        ? Voice.DisplayName
        : $"{Voice.DisplayName} — {Voice.QualityLabel}";

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onToggled(this);
}

public sealed partial class VoiceTabViewModel : SettingsTabViewModel
{
    private const string TestPhrase = "Привет! Так будет звучать озвучка сообщений из чата.";

    private readonly SpeechService _speechService;
    private readonly VoiceCatalog _catalog;
    private readonly AudioPlaybackService _playbackService;
    private readonly ILogger<VoiceTabViewModel> _logger;

    [ObservableProperty] private EngineOption? _selectedEngine;
    [ObservableProperty] private VoiceGender _gender;
    [ObservableProperty] private VoiceDescriptor? _selectedVoice;
    [ObservableProperty] private double _rate;
    [ObservableProperty] private int _pitch;
    [ObservableProperty] private int _volume;
    [ObservableProperty] private AudioDeviceInfo? _selectedOutputDevice;
    [ObservableProperty] private string _pronunciationTemplate = string.Empty;
    [ObservableProperty] private bool _dontReadUsername;
    [ObservableProperty] private bool _transliterateUsernames;
    [ObservableProperty] private bool _uniqueVoicePerViewer;
    [ObservableProperty] private double _pauseBetweenMessagesSeconds;
    [ObservableProperty] private string? _engineNotice;
    [ObservableProperty] private string? _testError;
    [ObservableProperty] private bool _isTesting;

    public ObservableCollection<VoiceDescriptor> AvailableVoices { get; } = new();
    public ObservableCollection<VoicePoolItem> VoicePool { get; } = new();
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();

    public IReadOnlyList<EngineOption> Engines { get; } = new[]
    {
        new EngineOption(SpeechEngineType.Piper, "Piper (рекомендуется)"),
        new EngineOption(SpeechEngineType.Sapi, "Windows SAPI")
    };

    public VoiceTabViewModel(
        SettingsService settingsService,
        SettingsApplier applier,
        SpeechService speechService,
        VoiceCatalog catalog,
        AudioPlaybackService playbackService,
        ILogger<VoiceTabViewModel> logger)
        : base(settingsService, applier)
    {
        _speechService = speechService;
        _catalog = catalog;
        _playbackService = playbackService;
        _logger = logger;

        ReloadOutputDevices();
        Refresh();
    }

    /// <summary>Same guard as the monitor list: clearing the collection nulls the ComboBox
    /// selection, and that must not be mistaken for the user picking "no device".</summary>
    public void ReloadOutputDevices() => LoadFromSettings(() =>
    {
        var previous = SelectedOutputDevice?.Id ?? Settings.Voice.OutputDeviceId ?? string.Empty;

        OutputDevices.Clear();
        foreach (var device in _playbackService.GetOutputDevices())
        {
            OutputDevices.Add(device);
        }

        SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Id == previous) ?? OutputDevices.FirstOrDefault();
    });

    public sealed override void Refresh()
    {
        LoadFromSettings(() =>
        {
            var v = Settings.Voice;

            SelectedEngine = Engines.FirstOrDefault(e => e.Value == v.Engine) ?? Engines[0];
            Gender = v.Gender;
            Rate = v.Rate;
            Pitch = v.Pitch;
            Volume = v.Volume;
            PronunciationTemplate = v.PronunciationTemplate;
            DontReadUsername = v.DontReadUsername;
            TransliterateUsernames = v.TransliterateUsernames;
            UniqueVoicePerViewer = v.UniqueVoicePerViewer;
            PauseBetweenMessagesSeconds = v.PauseBetweenMessagesSeconds;

            SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Id == (v.OutputDeviceId ?? string.Empty))
                                   ?? OutputDevices.FirstOrDefault();

            ReloadVoices();
        });

        UpdateEngineNotice();
    }

    /// <summary>
    /// Rebuilds the voice list, filtered by the gender switch. The list follows the *effective*
    /// engine, not the configured one: with Piper selected but unavailable the app speaks
    /// through SAPI, so offering Piper's (empty) voice list would leave the user unable to
    /// choose the voice they will actually hear.
    /// </summary>
    private void ReloadVoices()
    {
        var engine = _speechService.ResolveEngine().EngineType;
        var all = _catalog.GetVoices(engine);
        var filtered = all.Where(v => v.Gender == Gender).ToList();

        AvailableVoices.Clear();
        foreach (var voice in filtered)
        {
            AvailableVoices.Add(voice);
        }

        var storedId = Settings.Voice.SelectedVoiceId;
        SelectedVoice = AvailableVoices.FirstOrDefault(v => string.Equals(v.Id, storedId, StringComparison.OrdinalIgnoreCase))
                        ?? AvailableVoices.FirstOrDefault();

        // The assignment pool spans every voice of the engine, not just the current gender:
        // a streamer may well want a mixed set of male and female voices for viewers.
        VoicePool.Clear();
        var pool = Settings.Voice.VoicePoolForAssignment;
        foreach (var voice in all)
        {
            VoicePool.Add(new VoicePoolItem(
                voice,
                pool.Contains(voice.Id, StringComparer.OrdinalIgnoreCase),
                OnPoolItemToggled));
        }
    }

    private void OnPoolItemToggled(VoicePoolItem item)
    {
        var pool = Settings.Voice.VoicePoolForAssignment;

        if (item.IsSelected)
        {
            if (!pool.Contains(item.Voice.Id, StringComparer.OrdinalIgnoreCase))
            {
                pool.Add(item.Voice.Id);
            }
        }
        else
        {
            pool.RemoveAll(id => string.Equals(id, item.Voice.Id, StringComparison.OrdinalIgnoreCase));
        }

        Applier.Schedule();
    }

    private void UpdateEngineNotice()
    {
        // Resolving the engine is what detects a missing piper.exe / model and sets the notice.
        _speechService.ResolveEngine();
        EngineNotice = _speechService.FallbackNotice;
    }

    partial void OnSelectedEngineChanged(EngineOption? value) => Apply(nameof(SelectedEngine), () =>
    {
        if (value is null)
        {
            return;
        }

        Settings.Voice.Engine = value.Value;
        LoadFromSettings(ReloadVoices);
        UpdateEngineNotice();
    });

    partial void OnGenderChanged(VoiceGender value) => Apply(nameof(Gender), () =>
    {
        Settings.Voice.Gender = value;
        // Switching gender re-filters the list and picks a matching voice, so the next
        // "Проверить голос" actually sounds different.
        LoadFromSettings(ReloadVoices);

        if (SelectedVoice is not null)
        {
            Settings.Voice.SelectedVoiceId = SelectedVoice.Id;
        }
    });

    partial void OnSelectedVoiceChanged(VoiceDescriptor? value) => Apply(nameof(SelectedVoice), () =>
        Settings.Voice.SelectedVoiceId = value?.Id);

    partial void OnRateChanged(double value) => Apply(nameof(Rate), () => Settings.Voice.Rate = Math.Round(value, 1));
    partial void OnPitchChanged(int value) => Apply(nameof(Pitch), () => Settings.Voice.Pitch = value);
    partial void OnVolumeChanged(int value) => Apply(nameof(Volume), () => Settings.Voice.Volume = value);
    partial void OnDontReadUsernameChanged(bool value) => Apply(nameof(DontReadUsername), () => Settings.Voice.DontReadUsername = value);
    partial void OnTransliterateUsernamesChanged(bool value) => Apply(nameof(TransliterateUsernames), () => Settings.Voice.TransliterateUsernames = value);
    partial void OnUniqueVoicePerViewerChanged(bool value) => Apply(nameof(UniqueVoicePerViewer), () => Settings.Voice.UniqueVoicePerViewer = value);
    partial void OnPauseBetweenMessagesSecondsChanged(double value) => Apply(nameof(PauseBetweenMessagesSeconds), () => Settings.Voice.PauseBetweenMessagesSeconds = Math.Round(value, 1));
    partial void OnPronunciationTemplateChanged(string value) => Apply(nameof(PronunciationTemplate), () => Settings.Voice.PronunciationTemplate = value);

    partial void OnSelectedOutputDeviceChanged(AudioDeviceInfo? value) => Apply(nameof(SelectedOutputDevice), () =>
        Settings.Voice.OutputDeviceId = value?.Id);

    [RelayCommand]
    private async Task TestVoiceAsync()
    {
        TestError = null;
        IsTesting = true;

        try
        {
            // Save first so the engine reads exactly what the UI shows.
            await Applier.SaveNowAsync();

            var voice = SelectedVoice ?? _catalog.Resolve(SelectedEngine?.Value ?? SpeechEngineType.Sapi, Settings.Voice.SelectedVoiceId, Gender);
            if (voice is null)
            {
                TestError = "Нет доступных голосов для выбранного движка.";
                return;
            }

            var profile = new VoiceProfile { Voice = voice, Rate = Rate, Pitch = Pitch };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var audio = await _speechService.SynthesizeAsync(TestPhrase, profile, cts.Token);
            await _playbackService.PlayAsync(audio, SelectedOutputDevice?.Id, Volume, Pitch, cts.Token);

            UpdateEngineNotice();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Проверка голоса не удалась");
            TestError = $"Не удалось воспроизвести: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }
}
