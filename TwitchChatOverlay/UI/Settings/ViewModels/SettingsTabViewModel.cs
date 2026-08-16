using CommunityToolkit.Mvvm.ComponentModel;
using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.UI.Settings.ViewModels;

/// <summary>
/// Base for every settings tab. Derives from <see cref="ObservableValidator"/> so tabs get
/// INotifyDataErrorInfo for free via DataAnnotations; <see cref="Apply"/> then guarantees an
/// invalid value is shown in the box and highlighted but never written to the settings object.
/// </summary>
public abstract class SettingsTabViewModel : ObservableValidator
{
    private bool _suppressApply;

    protected SettingsTabViewModel(SettingsService settingsService, SettingsApplier applier)
    {
        SettingsService = settingsService;
        Applier = applier;
    }

    protected SettingsService SettingsService { get; }
    protected SettingsApplier Applier { get; }

    protected AppSettings Settings => SettingsService.Current;

    /// <summary>Populates the VM from the settings object without echoing every assignment
    /// back into it (and without scheduling a pointless save).</summary>
    protected void LoadFromSettings(Action load)
    {
        _suppressApply = true;
        try
        {
            load();
        }
        finally
        {
            _suppressApply = false;
        }
    }

    /// <summary>Writes one property through to the settings object, unless we are loading or
    /// the value failed validation.</summary>
    protected void Apply(string propertyName, Action apply)
    {
        if (_suppressApply || GetErrors(propertyName).Any())
        {
            return;
        }

        apply();
        Applier.Schedule();
    }

    /// <summary>Called when settings changed from somewhere else (setup-mode drag, import,
    /// reset) so the controls reflect the new state.</summary>
    public virtual void Refresh()
    {
    }
}
