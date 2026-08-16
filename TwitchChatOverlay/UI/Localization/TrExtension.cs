using System.Windows.Data;
using System.Windows.Markup;

namespace TwitchChatOverlay.UI.Localization;

/// <summary>XAML sugar: <c>Text="{loc:Tr Ov_CardWidth}"</c> instead of a full indexer binding.</summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object? ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Localization.Current,
            Mode = BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}
