using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TwitchChatOverlay.Core.Settings;
using Screen = System.Windows.Forms.Screen;

namespace TwitchChatOverlay.UI.Overlay;

/// <summary>
/// The chat card overlay. The Win32 window is created once at startup and stays alive
/// for the whole process — cards are shown/hidden by animating <see cref="CardBorder"/>'s
/// opacity/transform, not by repeated Show()/Hide(), to keep WS_EX_NOACTIVATE behavior
/// consistent and avoid any focus flicker.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly ILogger<OverlayWindow> _logger;
    private DispatcherTimer? _previewHideTimer;
    private bool _isSetupMode;
    private bool _isDragging;
    private System.Windows.Point _dragGrabOffset;

    public OverlayWindow(SettingsService settingsService, ILogger<OverlayWindow> logger)
    {
        _settingsService = settingsService;
        _logger = logger;

        InitializeComponent();

        _settingsService.SettingsChanged += OnSettingsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) =>
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        };

        SizeChanged += (_, _) =>
        {
            if (!_isDragging)
            {
                RepositionToAnchor();
            }
        };
        Loaded += (_, _) =>
        {
            ApplyAppearanceFromSettings();
            RepositionToAnchor();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        WindowStyleHelper.ApplyOverlayStyles(hwnd);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ApplyAppearanceFromSettings();

            if (!_isDragging)
            {
                RepositionToAnchor();
            }
        });
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(RepositionToAnchor);
    }

    private void ApplyAppearanceFromSettings()
    {
        var o = _settingsService.Current.Overlay;

        Width = o.CardWidth;
        CardBorder.CornerRadius = new CornerRadius(o.CornerRadius);
        CardBorder.Background = new SolidColorBrush(WithOpacity(ParseColorOrDefault(o.BackgroundColorHex, Colors.Black), o.BackgroundOpacityPercent));

        var fontFamily = new FontFamily(o.FontFamily);
        UsernameText.FontFamily = fontFamily;
        MessageText.FontFamily = fontFamily;
        UsernameText.FontSize = o.UsernameFontSize;
        MessageText.FontSize = o.MessageFontSize;
        MessageText.Visibility = o.ShowMessageText ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Recomputes Left/Top from the anchor + monitor + offsets. Anchors are stored, not
    /// absolute coordinates, and this is re-run on every show, on settings changes, and
    /// on <see cref="SystemEvents.DisplaySettingsChanged"/> — otherwise the overlay drifts
    /// off-screen the moment the user changes resolution.
    /// </summary>
    private void RepositionToAnchor()
    {
        var o = _settingsService.Current.Overlay;
        var screen = FindTargetScreen(o.MonitorDeviceName);
        var bounds = screen.WorkingArea;
        var dpiScale = WindowStyleHelper.GetDpiScaleForBounds(bounds);

        var waLeft = bounds.Left / dpiScale;
        var waTop = bounds.Top / dpiScale;
        var waRight = bounds.Right / dpiScale;
        var waBottom = bounds.Bottom / dpiScale;

        var cardWidth = ActualWidth > 0 ? ActualWidth : Width;
        var cardHeight = ActualHeight > 0 ? ActualHeight : 0;

        double left, top;

        switch (o.Anchor)
        {
            case AnchorCorner.TopLeft:
                left = waLeft + o.OffsetX;
                top = waTop + o.OffsetY;
                break;
            case AnchorCorner.TopCenter:
                left = waLeft + (waRight - waLeft - cardWidth) / 2;
                top = waTop + o.OffsetY;
                break;
            case AnchorCorner.TopRight:
                left = waRight - cardWidth - o.OffsetX;
                top = waTop + o.OffsetY;
                break;
            case AnchorCorner.MiddleLeft:
                left = waLeft + o.OffsetX;
                top = waTop + (waBottom - waTop - cardHeight) / 2;
                break;
            case AnchorCorner.MiddleCenter:
                left = waLeft + (waRight - waLeft - cardWidth) / 2;
                top = waTop + (waBottom - waTop - cardHeight) / 2;
                break;
            case AnchorCorner.MiddleRight:
                left = waRight - cardWidth - o.OffsetX;
                top = waTop + (waBottom - waTop - cardHeight) / 2;
                break;
            case AnchorCorner.BottomLeft:
                left = waLeft + o.OffsetX;
                top = waBottom - cardHeight - o.OffsetY;
                break;
            case AnchorCorner.BottomCenter:
                left = waLeft + (waRight - waLeft - cardWidth) / 2;
                top = waBottom - cardHeight - o.OffsetY;
                break;
            case AnchorCorner.BottomRight:
            default:
                left = waRight - cardWidth - o.OffsetX;
                top = waBottom - cardHeight - o.OffsetY;
                break;
        }

        Left = left;
        Top = top;
    }

    private static Screen FindTargetScreen(string? deviceName)
    {
        if (!string.IsNullOrEmpty(deviceName))
        {
            var match = Array.Find(Screen.AllScreens, s => s.DeviceName == deviceName);
            if (match is not null)
            {
                return match;
            }
        }

        return Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    /// <summary>Shows the card with username/message, animated per the configured style.</summary>
    public void Display(string username, string? usernameColorHex, string message,
        IReadOnlyList<string>? badgeSetIds = null)
    {
        var o = _settingsService.Current.Overlay;

        var nameBrush = o.UseTwitchNameColor && TryParseColor(usernameColorHex, out var color)
            ? new SolidColorBrush(color)
            : Brushes.White;

        UsernameText.Text = username;
        UsernameText.Foreground = nameBrush;
        AccentStripe.Background = nameBrush;

        var badges = o.ShowBadges ? MapBadgeLabels(badgeSetIds) : Array.Empty<string>();
        BadgesPanel.ItemsSource = badges;
        BadgesPanel.Visibility = badges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        MessageText.Text = message;
        MessageText.Visibility = o.ShowMessageText ? Visibility.Visible : Visibility.Collapsed;

        UpdateLayout();
        RepositionToAnchor();
        PlayShowAnimation(o.AnimationType, o.AnimationDurationMs);
    }

    /// <summary>
    /// Twitch badge artwork lives behind the Helix badge endpoints and would mean fetching and
    /// caching images per channel; short text chips convey the same role information without
    /// any network dependency.
    /// </summary>
    private static IReadOnlyList<string> MapBadgeLabels(IReadOnlyList<string>? badgeSetIds)
    {
        if (badgeSetIds is null || badgeSetIds.Count == 0)
        {
            return Array.Empty<string>();
        }

        var labels = new List<string>(3);

        foreach (var setId in badgeSetIds)
        {
            var label = setId switch
            {
                "broadcaster" => "СТРИМЕР",
                "moderator" => "МОД",
                "vip" => "VIP",
                "subscriber" => "SUB",
                "founder" => "SUB",
                "premium" => "PRIME",
                "partner" => "✓",
                _ => null
            };

            if (label is not null && !labels.Contains(label))
            {
                labels.Add(label);
            }
        }

        return labels;
    }

    public void HideCard()
    {
        var o = _settingsService.Current.Overlay;
        PlayHideAnimation(o.AnimationType, o.AnimationDurationMs);
    }

    /// <summary>
    /// Hides or shows the whole overlay (the "скрыть/показать оверлей" hotkey). Hiding the
    /// window rather than muting the pipeline means queued messages are still spoken.
    /// </summary>
    public bool ToggleOverlayVisibility()
    {
        var willBeVisible = Visibility != Visibility.Visible;
        Visibility = willBeVisible ? Visibility.Visible : Visibility.Hidden;
        return willBeVisible;
    }

    /// <summary>
    /// Setup mode: the card stays up permanently and becomes draggable. WS_EX_TRANSPARENT is
    /// lifted for the duration so the window actually receives the mouse; WS_EX_NOACTIVATE is
    /// deliberately left in place, so even dragging cannot pull focus out of a running game.
    /// </summary>
    public void SetSetupMode(bool enabled)
    {
        _isSetupMode = enabled;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            WindowStyleHelper.SetClickThrough(hwnd, clickThrough: !enabled);
        }

        if (enabled)
        {
            _previewHideTimer?.Stop();
            Cursor = System.Windows.Input.Cursors.SizeAll;
            Display("Режим настройки", "#9146FF", "Перетащите карточку мышью в нужное место экрана.");
            CardBorder.BeginAnimation(OpacityProperty, null);
            CardBorder.Opacity = 1;
        }
        else
        {
            Cursor = System.Windows.Input.Cursors.Arrow;
            _isDragging = false;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }

            HideCard();
        }
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (!_isSetupMode)
        {
            return;
        }

        _isDragging = true;
        _dragGrabOffset = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_isDragging)
        {
            return;
        }

        // The cursor is read in device pixels and converted with this window's current DPI:
        // while the window is being moved its own coordinate space is a moving target.
        var (cursorX, cursorY) = WindowStyleHelper.GetCursorPosition();
        var dpi = VisualTreeHelper.GetDpi(this);

        Left = cursorX / dpi.DpiScaleX - _dragGrabOffset.X;
        Top = cursorY / dpi.DpiScaleY - _dragGrabOffset.Y;
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        ReleaseMouseCapture();
        WriteCurrentPositionToAnchor();
    }

    /// <summary>
    /// Converts the dropped position back into "monitor + corner + offset". Storing the result
    /// this way is what keeps the overlay in place after a resolution change.
    /// </summary>
    private void WriteCurrentPositionToAnchor()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var centerXDevice = (int)((Left + ActualWidth / 2) * dpi.DpiScaleX);
        var centerYDevice = (int)((Top + ActualHeight / 2) * dpi.DpiScaleY);

        var screen = Screen.FromPoint(new System.Drawing.Point(centerXDevice, centerYDevice));
        var bounds = screen.WorkingArea;
        var scale = WindowStyleHelper.GetDpiScaleForBounds(bounds);

        var waLeft = bounds.Left / scale;
        var waTop = bounds.Top / scale;
        var waRight = bounds.Right / scale;
        var waBottom = bounds.Bottom / scale;

        var centerX = Left + ActualWidth / 2;
        var centerY = Top + ActualHeight / 2;

        var column = (int)Math.Clamp((centerX - waLeft) / ((waRight - waLeft) / 3), 0, 2);
        var row = (int)Math.Clamp((centerY - waTop) / ((waBottom - waTop) / 3), 0, 2);

        var anchor = (row, column) switch
        {
            (0, 0) => AnchorCorner.TopLeft,
            (0, 1) => AnchorCorner.TopCenter,
            (0, 2) => AnchorCorner.TopRight,
            (1, 0) => AnchorCorner.MiddleLeft,
            (1, 1) => AnchorCorner.MiddleCenter,
            (1, 2) => AnchorCorner.MiddleRight,
            (2, 0) => AnchorCorner.BottomLeft,
            (2, 1) => AnchorCorner.BottomCenter,
            _ => AnchorCorner.BottomRight
        };

        // Centered anchors ignore the offset on that axis, so leave it at zero there.
        var offsetX = column switch
        {
            0 => Left - waLeft,
            2 => waRight - (Left + ActualWidth),
            _ => 0
        };

        var offsetY = row switch
        {
            0 => Top - waTop,
            2 => waBottom - (Top + ActualHeight),
            _ => 0
        };

        var overlay = _settingsService.Current.Overlay;
        overlay.MonitorDeviceName = screen.DeviceName;
        overlay.Anchor = anchor;
        overlay.OffsetX = (int)Math.Clamp(Math.Round(offsetX), 0, 300);
        overlay.OffsetY = (int)Math.Clamp(Math.Round(offsetY), 0, 300);

        _logger.LogInformation(
            "Позиция оверлея записана: {Monitor}, {Anchor}, отступы {X}/{Y}",
            screen.DeviceName, anchor, overlay.OffsetX, overlay.OffsetY);
    }

    /// <summary>Displays a canned message for manual verification — reused by the Settings
    /// window's "Предпросмотр" button (этап 5).</summary>
    public void ShowPreview()
    {
        Display("ЗрительТест", "#A970FF",
            "Это тестовое сообщение — так будет выглядеть карточка чата поверх игры.",
            new[] { "subscriber", "vip" });

        _previewHideTimer?.Stop();
        _previewHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _previewHideTimer.Tick += (_, _) =>
        {
            _previewHideTimer!.Stop();
            HideCard();
        };
        _previewHideTimer.Start();
    }

    private void PlayShowAnimation(OverlayAnimationType type, int durationMs)
    {
        CardBorder.BeginAnimation(OpacityProperty, null);
        CardTranslate.BeginAnimation(TranslateTransform.XProperty, null);

        var duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(durationMs, 1)));

        if (type == OverlayAnimationType.None)
        {
            CardBorder.Opacity = 1;
            CardTranslate.X = 0;
            return;
        }

        if (type is OverlayAnimationType.SlideLeft or OverlayAnimationType.SlideRight)
        {
            var startX = type == OverlayAnimationType.SlideLeft ? -60 : 60;
            CardTranslate.X = startX;
            CardBorder.Opacity = 1;
            CardTranslate.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(startX, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            return;
        }

        CardTranslate.X = 0;
        CardBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
    }

    private void PlayHideAnimation(OverlayAnimationType type, int durationMs)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(durationMs, 1)));

        if (type == OverlayAnimationType.None)
        {
            CardBorder.BeginAnimation(OpacityProperty, null);
            CardBorder.Opacity = 0;
            return;
        }

        CardBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(CardBorder.Opacity, 0, duration));

        if (type is OverlayAnimationType.SlideLeft or OverlayAnimationType.SlideRight)
        {
            var endX = type == OverlayAnimationType.SlideLeft ? -60 : 60;
            CardTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, endX, duration));
        }
    }

    private static bool TryParseColor(string? hex, out Color color)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                var converted = ColorConverter.ConvertFromString(hex);
                if (converted is Color parsed)
                {
                    color = parsed;
                    return true;
                }
            }
            catch (FormatException)
            {
                // falls through to default below
            }
        }

        color = default;
        return false;
    }

    private static Color ParseColorOrDefault(string? hex, Color fallback) =>
        TryParseColor(hex, out var color) ? color : fallback;

    private static Color WithOpacity(Color color, int opacityPercent)
    {
        var alpha = (byte)Math.Clamp(opacityPercent * 255 / 100, 0, 255);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }
}
