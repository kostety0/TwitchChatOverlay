using System.Windows.Input;

namespace TwitchChatOverlay.UI.Hotkeys;

/// <summary>
/// Text form of a global hotkey ("Ctrl+Shift+M") — what gets persisted in settings.
/// Kept separate from registration so the settings UI can record combos before
/// <c>RegisterHotKey</c> ever enters the picture.
/// </summary>
public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    public static HotkeyGesture Empty { get; } = new(ModifierKeys.None, Key.None);

    public bool IsEmpty => Key == Key.None;

    /// <summary>True once the combo is safe to register: a bare letter would swallow that key
    /// system-wide, so at least one modifier is required.</summary>
    public bool IsValid => Key != Key.None && Modifiers != ModifierKeys.None;

    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key.ToString());

        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        var key = Key.None;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (!Enum.TryParse(raw, ignoreCase: true, out key))
                    {
                        return false;
                    }
                    break;
            }
        }

        if (key == Key.None)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    /// <summary>Modifier keys pressed on their own are not a combo yet — the recorder ignores them.</summary>
    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        Key.System;
}
