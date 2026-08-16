using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.UI.Settings.ViewModels;

public sealed record OverflowPolicyOption(QueueOverflowPolicy Value, string DisplayName);

public sealed partial class QueueTabViewModel : SettingsTabViewModel
{
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 50, ErrorMessage = "Размер очереди: от 1 до 50 сообщений")]
    private int _maxQueueSize;

    [ObservableProperty] private OverflowPolicyOption? _selectedOverflowPolicy;
    [ObservableProperty] private bool _skipOldMessages;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(1, 3600, ErrorMessage = "Возраст сообщения: от 1 до 3600 секунд")]
    private int _skipOlderThanSeconds;

    public IReadOnlyList<OverflowPolicyOption> OverflowPolicies { get; } = new[]
    {
        new OverflowPolicyOption(QueueOverflowPolicy.DropNewest, "Отбрасывать новые"),
        new OverflowPolicyOption(QueueOverflowPolicy.EvictOldest, "Вытеснять старые")
    };

    public QueueTabViewModel(SettingsService settingsService, SettingsApplier applier)
        : base(settingsService, applier)
    {
        Refresh();
    }

    public sealed override void Refresh()
    {
        LoadFromSettings(() =>
        {
            var q = Settings.Queue;

            MaxQueueSize = q.MaxQueueSize;
            SelectedOverflowPolicy = OverflowPolicies.FirstOrDefault(p => p.Value == q.OverflowPolicy) ?? OverflowPolicies[0];
            SkipOldMessages = q.SkipOldMessages;
            SkipOlderThanSeconds = q.SkipOlderThanSeconds;
        });
    }

    partial void OnMaxQueueSizeChanged(int value) => Apply(nameof(MaxQueueSize), () => Settings.Queue.MaxQueueSize = value);
    partial void OnSkipOldMessagesChanged(bool value) => Apply(nameof(SkipOldMessages), () => Settings.Queue.SkipOldMessages = value);
    partial void OnSkipOlderThanSecondsChanged(int value) => Apply(nameof(SkipOlderThanSeconds), () => Settings.Queue.SkipOlderThanSeconds = value);

    partial void OnSelectedOverflowPolicyChanged(OverflowPolicyOption? value) => Apply(nameof(SelectedOverflowPolicy), () =>
    {
        if (value is not null)
        {
            Settings.Queue.OverflowPolicy = value.Value;
        }
    });
}
