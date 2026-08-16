using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using TwitchChatOverlay.Core.Settings;
using TwitchChatOverlay.UI.Settings.ViewModels;

namespace TwitchChatOverlay.UI.Settings;

public partial class SettingsWindow : Window
{
    private readonly SettingsWindowViewModel _viewModel;
    private readonly SettingsService _settingsService;

    public SettingsWindow(SettingsWindowViewModel viewModel, SettingsService settingsService)
    {
        _viewModel = viewModel;
        _settingsService = settingsService;

        InitializeComponent();
        DataContext = viewModel;

        Activated += (_, _) => _viewModel.OnWindowActivated();
    }

    /// <summary>
    /// Hotkey recording. Handled at the window level with tunneling so the combination is
    /// captured before any control turns it into text or a focus change.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var recording = _viewModel.Hotkeys.Bindings.FirstOrDefault(b => b.IsRecording);
        if (recording is null)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        // System keys (Alt combos) arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        _viewModel.Hotkeys.CaptureFor(recording, key, Keyboard.Modifiers);
        e.Handled = true;
    }

    /// <summary>Set by the app during shutdown so "свернуть в трей при закрытии" cannot keep
    /// the process alive.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Leaving setup mode on close, otherwise the overlay would stay draggable and pinned
        // on screen with no visible way to turn it off.
        if (_viewModel.Overlay.IsSetupMode)
        {
            _viewModel.Overlay.IsSetupMode = false;
        }

        if (!AllowClose && _settingsService.Current.General.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }
}
