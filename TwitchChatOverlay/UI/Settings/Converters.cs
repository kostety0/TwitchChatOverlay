using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TwitchChatOverlay.Core.Twitch;

namespace TwitchChatOverlay.UI.Settings;

/// <summary>Binds a radio button to one value of an enum property (anchor grid, gender switch).</summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is not string name)
        {
            return false;
        }

        return string.Equals(value.ToString(), name, StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Only the button being checked should write back; the one being unchecked must not.
        if (value is true && parameter is string name && Enum.TryParse(targetType, name, out var parsed))
        {
            return parsed!;
        }

        return Binding.DoNothing;
    }
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>Collapses an element when its bound string is null/empty — used for hints and errors.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Shows an element only when a count is zero — the "no voices found" notice.</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Colours the connection indicator: grey / amber / green / red.</summary>
public sealed class ConnectionStateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Disconnected = new(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly SolidColorBrush Connecting = new(Color.FromRgb(0xE0, 0xA0, 0x30));
    private static readonly SolidColorBrush Connected = new(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush Error = new(Color.FromRgb(0xD0, 0x45, 0x45));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TwitchConnectionState.Connecting => Connecting,
        TwitchConnectionState.Connected => Connected,
        TwitchConnectionState.Error => Error,
        _ => Disconnected
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Live swatch for the background-colour hex box.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                if (ColorConverter.ConvertFromString(hex) is Color color)
                {
                    return new SolidColorBrush(color);
                }
            }
            catch (FormatException)
            {
                // invalid hex while typing — fall through to transparent
            }
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Formats a voice for the dropdown: name plus its quality tier.</summary>
public sealed class VoiceDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Core.Speech.VoiceDescriptor voice)
        {
            var parts = new List<string> { voice.DisplayName };
            if (!string.IsNullOrEmpty(voice.Language))
            {
                parts.Add(voice.Language);
            }
            if (!string.IsNullOrEmpty(voice.QualityLabel))
            {
                parts.Add(voice.QualityLabel);
            }

            return string.Join(" · ", parts);
        }

        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
