using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.UI.Hotkeys;

public enum HotkeyAction
{
    ToggleMute,
    SkipCurrent,
    ClearQueue,
    ToggleOverlayVisibility,
    OpenSettings
}

/// <summary>
/// System-wide hotkeys via RegisterHotKey. They must work while a fullscreen game has focus,
/// which rules out WPF InputBindings — those only fire when our own window is active.
/// A message-only window owns the registrations so no visible window has to stay alive.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    /// <summary>Without this a held-down combo repeats dozens of times a second.</summary>
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly SettingsService _settingsService;
    private readonly ILogger<GlobalHotkeyService> _logger;
    private readonly HwndSource _source;
    private readonly List<int> _registeredIds = new();

    public event EventHandler<HotkeyAction>? HotkeyPressed;

    /// <summary>Combinations another application already owns — surfaced so the user is not
    /// left wondering why one of their hotkeys does nothing.</summary>
    public IReadOnlyList<string> FailedRegistrations { get; private set; } = Array.Empty<string>();

    public GlobalHotkeyService(SettingsService settingsService, ILogger<GlobalHotkeyService> logger)
    {
        _settingsService = settingsService;
        _logger = logger;

        var parameters = new HwndSourceParameters("TwitchChatOverlayHotkeys")
        {
            // HWND_MESSAGE: invisible, never shown, exists purely to receive WM_HOTKEY.
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        _settingsService.SettingsChanged += OnSettingsChanged;
        ApplyFromSettings();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => ApplyFromSettings();

    /// <summary>Re-registers everything from the current settings. Cheap enough to just redo
    /// wholesale whenever settings change.</summary>
    public void ApplyFromSettings()
    {
        UnregisterAll();

        var failures = new List<string>();
        var hotkeys = _settingsService.Current.Hotkeys;

        Register(HotkeyAction.ToggleMute, hotkeys.ToggleMute, failures);
        Register(HotkeyAction.SkipCurrent, hotkeys.SkipCurrent, failures);
        Register(HotkeyAction.ClearQueue, hotkeys.ClearQueue, failures);
        Register(HotkeyAction.ToggleOverlayVisibility, hotkeys.ToggleOverlayVisibility, failures);
        Register(HotkeyAction.OpenSettings, hotkeys.OpenSettings, failures);

        FailedRegistrations = failures;
    }

    private void Register(HotkeyAction action, string gestureText, List<string> failures)
    {
        if (!HotkeyGesture.TryParse(gestureText, out var gesture) || !gesture.IsValid)
        {
            return;
        }

        var modifiers = MOD_NOREPEAT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= MOD_CONTROL;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= MOD_ALT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= MOD_SHIFT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= MOD_WIN;

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        var id = (int)action;

        if (RegisterHotKey(_source.Handle, id, modifiers, virtualKey))
        {
            _registeredIds.Add(id);
            _logger.LogInformation("Горячая клавиша {Gesture} зарегистрирована для {Action}", gestureText, action);
        }
        else
        {
            failures.Add(gestureText);
            _logger.LogWarning(
                "Не удалось зарегистрировать {Gesture} для {Action} — комбинация уже занята другим приложением",
                gestureText, action);
        }
    }

    private void UnregisterAll()
    {
        foreach (var id in _registeredIds)
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _registeredIds.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        var id = wParam.ToInt32();
        if (Enum.IsDefined(typeof(HotkeyAction), id))
        {
            HotkeyPressed?.Invoke(this, (HotkeyAction)id);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
