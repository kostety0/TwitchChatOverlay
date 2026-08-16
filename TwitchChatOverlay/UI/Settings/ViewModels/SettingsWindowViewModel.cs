using CommunityToolkit.Mvvm.ComponentModel;

namespace TwitchChatOverlay.UI.Settings.ViewModels;

public sealed class SettingsWindowViewModel : ObservableObject, IDisposable
{
    public SettingsWindowViewModel(
        ConnectionTabViewModel connection,
        OverlayTabViewModel overlay,
        VoiceTabViewModel voice,
        FiltersTabViewModel filters,
        QueueTabViewModel queue,
        HotkeysTabViewModel hotkeys,
        GeneralTabViewModel general)
    {
        Connection = connection;
        Overlay = overlay;
        Voice = voice;
        Filters = filters;
        Queue = queue;
        Hotkeys = hotkeys;
        General = general;

        General.SettingsReplaced += OnSettingsReplaced;
    }

    public ConnectionTabViewModel Connection { get; }
    public OverlayTabViewModel Overlay { get; }
    public VoiceTabViewModel Voice { get; }
    public FiltersTabViewModel Filters { get; }
    public QueueTabViewModel Queue { get; }
    public HotkeysTabViewModel Hotkeys { get; }
    public GeneralTabViewModel General { get; }

    /// <summary>A reset or import swapped the whole settings object — every tab must re-read it.</summary>
    private void OnSettingsReplaced(object? sender, EventArgs e)
    {
        Connection.Refresh();
        Overlay.Refresh();
        Voice.Refresh();
        Filters.Refresh();
        Queue.Refresh();
        Hotkeys.Refresh();
        General.Refresh();
    }

    /// <summary>Called when the window is shown again — hardware may have changed since.</summary>
    public void OnWindowActivated()
    {
        Overlay.ReloadMonitors();
        Voice.ReloadOutputDevices();
    }

    public void Dispose()
    {
        General.SettingsReplaced -= OnSettingsReplaced;
        Connection.Dispose();
    }
}
