using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchChatOverlay.Core.Settings;
using TwitchChatOverlay.UI.Overlay;
using Screen = System.Windows.Forms.Screen;

namespace TwitchChatOverlay.UI.Settings.ViewModels;

public sealed record MonitorOption(string DeviceName, string DisplayName);

public sealed record AnimationOption(OverlayAnimationType Value, string DisplayName);

public sealed partial class OverlayTabViewModel : SettingsTabViewModel
{
    private readonly OverlayWindow _overlay;

    [ObservableProperty] private MonitorOption? _selectedMonitor;
    [ObservableProperty] private AnchorCorner _anchor;
    [ObservableProperty] private int _offsetX;
    [ObservableProperty] private int _offsetY;
    [ObservableProperty] private int _cardWidth;
    [ObservableProperty] private double _usernameFontSize;
    [ObservableProperty] private double _messageFontSize;
    [ObservableProperty] private string _fontFamily = "Segoe UI";

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [RegularExpression("^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", ErrorMessage = "Цвет в формате #RRGGBB")]
    private string _backgroundColorHex = "#1E1E1E";

    [ObservableProperty] private int _backgroundOpacityPercent;
    [ObservableProperty] private int _cornerRadius;
    [ObservableProperty] private AnimationOption? _selectedAnimation;
    [ObservableProperty] private int _animationDurationMs;
    [ObservableProperty] private double _postSpeechTailSeconds;
    [ObservableProperty] private bool _showBadges;
    [ObservableProperty] private bool _showAvatar;
    [ObservableProperty] private bool _useTwitchNameColor;
    [ObservableProperty] private bool _showMessageText;
    [ObservableProperty] private bool _isSetupMode;

    public ObservableCollection<MonitorOption> Monitors { get; } = new();
    public IReadOnlyList<string> SystemFonts { get; }

    public IReadOnlyList<AnimationOption> Animations { get; } = new[]
    {
        new AnimationOption(OverlayAnimationType.FadeIn, "Появление"),
        new AnimationOption(OverlayAnimationType.FadeOut, "Затухание"),
        new AnimationOption(OverlayAnimationType.SlideLeft, "Выезд слева"),
        new AnimationOption(OverlayAnimationType.SlideRight, "Выезд справа"),
        new AnimationOption(OverlayAnimationType.None, "Без анимации")
    };

    /// <summary>Preset swatches — WPF ships no colour picker and a hex box alone is unfriendly
    /// for the non-technical streamer this app targets.</summary>
    public IReadOnlyList<string> ColorPresets { get; } = new[]
    {
        "#000000", "#1E1E1E", "#2D2D30", "#3C3C3C",
        "#0E1621", "#171C26", "#241B2F", "#3A1C2A",
        "#9146FF", "#1F6FEB", "#0E7C5A", "#8B1E1E"
    };

    public OverlayTabViewModel(SettingsService settingsService, SettingsApplier applier, OverlayWindow overlay)
        : base(settingsService, applier)
    {
        _overlay = overlay;

        SystemFonts = System.Windows.Media.Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ReloadMonitors();
        Refresh();
    }

    /// <summary>
    /// Rebuilds the monitor list (hardware may have changed while the window was hidden).
    /// Wrapped in LoadFromSettings because clearing the collection makes the ComboBox null its
    /// SelectedItem, which would otherwise be written straight back into the settings.
    /// </summary>
    public void ReloadMonitors() => LoadFromSettings(() =>
    {
        var previous = SelectedMonitor?.DeviceName ?? Settings.Overlay.MonitorDeviceName;

        Monitors.Clear();
        var index = 1;
        foreach (var screen in Screen.AllScreens)
        {
            var label = $"Монитор {index}: {screen.Bounds.Width}×{screen.Bounds.Height}" + (screen.Primary ? " (основной)" : string.Empty);
            Monitors.Add(new MonitorOption(screen.DeviceName, label));
            index++;
        }

        SelectedMonitor = Monitors.FirstOrDefault(m => m.DeviceName == previous) ?? Monitors.FirstOrDefault();
    });

    public sealed override void Refresh()
    {
        LoadFromSettings(() =>
        {
            var o = Settings.Overlay;

            SelectedMonitor = Monitors.FirstOrDefault(m => m.DeviceName == o.MonitorDeviceName)
                              ?? Monitors.FirstOrDefault();
            Anchor = o.Anchor;
            OffsetX = o.OffsetX;
            OffsetY = o.OffsetY;
            CardWidth = o.CardWidth;
            UsernameFontSize = o.UsernameFontSize;
            MessageFontSize = o.MessageFontSize;
            FontFamily = o.FontFamily;
            BackgroundColorHex = o.BackgroundColorHex;
            BackgroundOpacityPercent = o.BackgroundOpacityPercent;
            CornerRadius = o.CornerRadius;
            SelectedAnimation = Animations.FirstOrDefault(a => a.Value == o.AnimationType) ?? Animations[0];
            AnimationDurationMs = o.AnimationDurationMs;
            PostSpeechTailSeconds = o.PostSpeechTailSeconds;
            ShowBadges = o.ShowBadges;
            ShowAvatar = o.ShowAvatar;
            UseTwitchNameColor = o.UseTwitchNameColor;
            ShowMessageText = o.ShowMessageText;
        });
    }

    partial void OnSelectedMonitorChanged(MonitorOption? value) => Apply(nameof(SelectedMonitor), () =>
        Settings.Overlay.MonitorDeviceName = value?.DeviceName);

    partial void OnAnchorChanged(AnchorCorner value) => Apply(nameof(Anchor), () => Settings.Overlay.Anchor = value);
    partial void OnOffsetXChanged(int value) => Apply(nameof(OffsetX), () => Settings.Overlay.OffsetX = value);
    partial void OnOffsetYChanged(int value) => Apply(nameof(OffsetY), () => Settings.Overlay.OffsetY = value);
    partial void OnCardWidthChanged(int value) => Apply(nameof(CardWidth), () => Settings.Overlay.CardWidth = value);
    partial void OnUsernameFontSizeChanged(double value) => Apply(nameof(UsernameFontSize), () => Settings.Overlay.UsernameFontSize = value);
    partial void OnMessageFontSizeChanged(double value) => Apply(nameof(MessageFontSize), () => Settings.Overlay.MessageFontSize = value);
    partial void OnFontFamilyChanged(string value) => Apply(nameof(FontFamily), () => Settings.Overlay.FontFamily = value);
    partial void OnBackgroundColorHexChanged(string value) => Apply(nameof(BackgroundColorHex), () => Settings.Overlay.BackgroundColorHex = value);
    partial void OnBackgroundOpacityPercentChanged(int value) => Apply(nameof(BackgroundOpacityPercent), () => Settings.Overlay.BackgroundOpacityPercent = value);
    partial void OnCornerRadiusChanged(int value) => Apply(nameof(CornerRadius), () => Settings.Overlay.CornerRadius = value);
    partial void OnAnimationDurationMsChanged(int value) => Apply(nameof(AnimationDurationMs), () => Settings.Overlay.AnimationDurationMs = value);
    partial void OnPostSpeechTailSecondsChanged(double value) => Apply(nameof(PostSpeechTailSeconds), () => Settings.Overlay.PostSpeechTailSeconds = value);
    partial void OnShowBadgesChanged(bool value) => Apply(nameof(ShowBadges), () => Settings.Overlay.ShowBadges = value);
    partial void OnShowAvatarChanged(bool value) => Apply(nameof(ShowAvatar), () => Settings.Overlay.ShowAvatar = value);
    partial void OnUseTwitchNameColorChanged(bool value) => Apply(nameof(UseTwitchNameColor), () => Settings.Overlay.UseTwitchNameColor = value);
    partial void OnShowMessageTextChanged(bool value) => Apply(nameof(ShowMessageText), () => Settings.Overlay.ShowMessageText = value);

    partial void OnSelectedAnimationChanged(AnimationOption? value) => Apply(nameof(SelectedAnimation), () =>
    {
        if (value is not null)
        {
            Settings.Overlay.AnimationType = value.Value;
        }
    });

    partial void OnIsSetupModeChanged(bool value)
    {
        _overlay.SetSetupMode(value);

        if (!value)
        {
            // The drag wrote a new anchor/offsets straight into the settings object.
            Refresh();
            Applier.Schedule();
        }
    }

    [RelayCommand]
    private void Preview() => _overlay.ShowPreview();

    [RelayCommand]
    private void PickColor(string hex)
    {
        if (Regex.IsMatch(hex, "^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$"))
        {
            BackgroundColorHex = hex;
        }
    }
}
