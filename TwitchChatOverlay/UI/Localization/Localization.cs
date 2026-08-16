using System.ComponentModel;
using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.UI.Localization;

/// <summary>
/// Minimal string table for the settings window. An indexer plus an "Item[]" change
/// notification is all WPF needs to re-evaluate every <c>{loc:Tr Key}</c> binding, so the
/// language switch takes effect live without reopening the window.
/// </summary>
public sealed class Localization : INotifyPropertyChanged
{
    public static Localization Current { get; } = new();

    private IReadOnlyDictionary<string, string> _map = LocalizedStrings.Russian;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => _map.TryGetValue(key, out var value) ? value : key;

    public AppLanguage Language { get; private set; } = AppLanguage.Russian;

    public void SetLanguage(AppLanguage language)
    {
        if (Language == language)
        {
            return;
        }

        Language = language;
        _map = language == AppLanguage.English ? LocalizedStrings.English : LocalizedStrings.Russian;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
