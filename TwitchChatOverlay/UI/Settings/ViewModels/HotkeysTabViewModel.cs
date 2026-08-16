using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchChatOverlay.Core.Settings;
using TwitchChatOverlay.UI.Hotkeys;

namespace TwitchChatOverlay.UI.Settings.ViewModels;

/// <summary>One recordable hotkey row.</summary>
public sealed partial class HotkeyBinding : ObservableObject
{
    private readonly Func<HotkeysSettings, string> _read;
    private readonly Action<HotkeysSettings, string> _write;
    private readonly Action _onChanged;

    public HotkeyBinding(
        string label,
        Func<HotkeysSettings, string> read,
        Action<HotkeysSettings, string> write,
        Action onChanged)
    {
        Label = label;
        _read = read;
        _write = write;
        _onChanged = onChanged;
    }

    public string Label { get; }

    [ObservableProperty] private string _gestureText = string.Empty;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string? _error;

    public void Load(HotkeysSettings settings) => GestureText = _read(settings);

    public void StartRecording()
    {
        Error = null;
        IsRecording = true;
    }

    /// <summary>Called by the view for each key press while recording.</summary>
    public void Capture(Key key, ModifierKeys modifiers, HotkeysSettings settings)
    {
        if (HotkeyGesture.IsModifierKey(key))
        {
            return;
        }

        if (key == Key.Escape)
        {
            IsRecording = false;
            return;
        }

        var gesture = new HotkeyGesture(modifiers, key);
        if (!gesture.IsValid)
        {
            Error = "Добавьте модификатор: Ctrl, Alt, Shift или Win";
            return;
        }

        Error = null;
        GestureText = gesture.ToString();
        IsRecording = false;
        _write(settings, GestureText);
        _onChanged();
    }

    public void Clear(HotkeysSettings settings)
    {
        GestureText = string.Empty;
        IsRecording = false;
        Error = null;
        _write(settings, string.Empty);
        _onChanged();
    }
}

public sealed partial class HotkeysTabViewModel : SettingsTabViewModel
{
    public HotkeysTabViewModel(SettingsService settingsService, SettingsApplier applier)
        : base(settingsService, applier)
    {
        Bindings =
        [
            new HotkeyBinding("Заглушить / включить озвучку", h => h.ToggleMute, (h, v) => h.ToggleMute = v, OnBindingChanged),
            new HotkeyBinding("Пропустить текущее сообщение", h => h.SkipCurrent, (h, v) => h.SkipCurrent = v, OnBindingChanged),
            new HotkeyBinding("Очистить очередь", h => h.ClearQueue, (h, v) => h.ClearQueue = v, OnBindingChanged),
            new HotkeyBinding("Скрыть / показать оверлей", h => h.ToggleOverlayVisibility, (h, v) => h.ToggleOverlayVisibility = v, OnBindingChanged),
            new HotkeyBinding("Открыть настройки", h => h.OpenSettings, (h, v) => h.OpenSettings = v, OnBindingChanged)
        ];

        Refresh();
    }

    public IReadOnlyList<HotkeyBinding> Bindings { get; }

    public sealed override void Refresh()
    {
        foreach (var binding in Bindings)
        {
            binding.Load(Settings.Hotkeys);
        }
    }

    /// <summary>Invoked by the view's key handler — the VM owns the settings object, the view
    /// only knows which row is recording.</summary>
    public void CaptureFor(HotkeyBinding binding, Key key, ModifierKeys modifiers) =>
        binding.Capture(key, modifiers, Settings.Hotkeys);

    [RelayCommand]
    private void StartRecording(HotkeyBinding binding)
    {
        foreach (var other in Bindings.Where(b => b != binding))
        {
            other.IsRecording = false;
        }

        binding.StartRecording();
    }

    [RelayCommand]
    private void ClearBinding(HotkeyBinding binding) => binding.Clear(Settings.Hotkeys);

    private void OnBindingChanged() => Applier.Schedule();
}
