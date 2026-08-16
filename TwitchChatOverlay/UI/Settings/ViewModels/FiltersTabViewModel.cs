using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using TwitchChatOverlay.Core.Settings;

namespace TwitchChatOverlay.UI.Settings.ViewModels;

public sealed record AccessLevelOption(ChatAccessLevel Value, string DisplayName);

public sealed partial class FiltersTabViewModel : SettingsTabViewModel
{
    [ObservableProperty] private AccessLevelOption? _selectedAccessLevel;
    [ObservableProperty] private string _channelPointsRewardName = string.Empty;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(20, 500, ErrorMessage = "Длина сообщения: от 20 до 500 символов")]
    private int _maxMessageLength;

    [ObservableProperty] private bool _stripLinks;
    [ObservableProperty] private bool _stripEmotes;
    [ObservableProperty] private bool _ignoreCommands;
    [ObservableProperty] private bool _collapseRepeatedChars;
    [ObservableProperty] private string _wordBlacklistText = string.Empty;
    [ObservableProperty] private string _userBlacklistText = string.Empty;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(0, 300, ErrorMessage = "Кулдаун: от 0 до 300 секунд")]
    private int _userCooldownSeconds;

    public IReadOnlyList<AccessLevelOption> AccessLevels { get; } = new[]
    {
        new AccessLevelOption(ChatAccessLevel.Everyone, "Все"),
        new AccessLevelOption(ChatAccessLevel.Subscribers, "Подписчики"),
        new AccessLevelOption(ChatAccessLevel.VipAndAbove, "VIP и выше"),
        new AccessLevelOption(ChatAccessLevel.ModeratorsAndAbove, "Модераторы"),
        new AccessLevelOption(ChatAccessLevel.ChannelPointsOnly, "Только за баллы канала")
    };

    /// <summary>Drives visibility of the reward-name box.</summary>
    public bool IsChannelPointsMode => SelectedAccessLevel?.Value == ChatAccessLevel.ChannelPointsOnly;

    public FiltersTabViewModel(SettingsService settingsService, SettingsApplier applier)
        : base(settingsService, applier)
    {
        Refresh();
    }

    public sealed override void Refresh()
    {
        LoadFromSettings(() =>
        {
            var f = Settings.Filters;

            SelectedAccessLevel = AccessLevels.FirstOrDefault(a => a.Value == f.AccessLevel) ?? AccessLevels[0];
            ChannelPointsRewardName = f.ChannelPointsRewardName;
            MaxMessageLength = f.MaxMessageLength;
            StripLinks = f.StripLinks;
            StripEmotes = f.StripEmotes;
            IgnoreCommands = f.IgnoreCommands;
            CollapseRepeatedChars = f.CollapseRepeatedChars;
            WordBlacklistText = string.Join(Environment.NewLine, f.WordBlacklist);
            UserBlacklistText = string.Join(Environment.NewLine, f.UserBlacklist);
            UserCooldownSeconds = f.UserCooldownSeconds;
        });

        OnPropertyChanged(nameof(IsChannelPointsMode));
    }

    partial void OnSelectedAccessLevelChanged(AccessLevelOption? value) => Apply(nameof(SelectedAccessLevel), () =>
    {
        if (value is not null)
        {
            Settings.Filters.AccessLevel = value.Value;
        }

        OnPropertyChanged(nameof(IsChannelPointsMode));
    });

    partial void OnChannelPointsRewardNameChanged(string value) => Apply(nameof(ChannelPointsRewardName), () =>
        Settings.Filters.ChannelPointsRewardName = value.Trim());

    partial void OnMaxMessageLengthChanged(int value) => Apply(nameof(MaxMessageLength), () => Settings.Filters.MaxMessageLength = value);
    partial void OnStripLinksChanged(bool value) => Apply(nameof(StripLinks), () => Settings.Filters.StripLinks = value);
    partial void OnStripEmotesChanged(bool value) => Apply(nameof(StripEmotes), () => Settings.Filters.StripEmotes = value);
    partial void OnIgnoreCommandsChanged(bool value) => Apply(nameof(IgnoreCommands), () => Settings.Filters.IgnoreCommands = value);
    partial void OnCollapseRepeatedCharsChanged(bool value) => Apply(nameof(CollapseRepeatedChars), () => Settings.Filters.CollapseRepeatedChars = value);
    partial void OnUserCooldownSecondsChanged(int value) => Apply(nameof(UserCooldownSeconds), () => Settings.Filters.UserCooldownSeconds = value);

    partial void OnWordBlacklistTextChanged(string value) => Apply(nameof(WordBlacklistText), () =>
        Settings.Filters.WordBlacklist = SplitLines(value));

    partial void OnUserBlacklistTextChanged(string value) => Apply(nameof(UserBlacklistText), () =>
        Settings.Filters.UserBlacklist = SplitLines(value));

    private static List<string> SplitLines(string text) => text
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();
}
