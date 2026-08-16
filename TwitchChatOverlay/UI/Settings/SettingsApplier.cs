using System.Windows.Threading;
using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.UI.Settings;

/// <summary>
/// Debounces writes to disk. Every control applies its change to <see cref="SettingsService.Current"/>
/// immediately (so the running app reacts at once) but the JSON file — and the SettingsChanged
/// event that follows it — is written a beat later, otherwise dragging a slider would fire
/// hundreds of saves.
/// </summary>
public sealed class SettingsApplier
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);

    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _timer;

    public SettingsApplier(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _timer = new DispatcherTimer { Interval = Delay };
        _timer.Tick += async (_, _) =>
        {
            _timer.Stop();
            await _settingsService.SaveAsync();
        };
    }

    public void Schedule()
    {
        _timer.Stop();
        _timer.Start();
    }

    public Task SaveNowAsync()
    {
        _timer.Stop();
        return _settingsService.SaveAsync();
    }
}
