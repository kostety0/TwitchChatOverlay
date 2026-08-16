namespace TwitchChatOverlay.Core.Settings;

/// <summary>
/// Root settings object persisted as JSON in %APPDATA%\TwitchChatOverlay\settings.json.
/// Every nested settings class must have property initializers with sane defaults:
/// System.Text.Json only overwrites properties present in the JSON file, so a config
/// from an older app version (missing new fields or whole sections) still loads safely.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public ConnectionSettings Connection { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public FiltersSettings Filters { get; set; } = new();
    public QueueSettings Queue { get; set; } = new();
    public HotkeysSettings Hotkeys { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
}

public sealed class ConnectionSettings
{
    public string ChannelName { get; set; } = string.Empty;
    public string TwitchUserId { get; set; } = string.Empty;
    public string TwitchLogin { get; set; } = string.Empty;

    /// <summary>Overrides the Client ID baked into the build. Only needed for self-built
    /// copies where no ID was compiled in — exposed on the Connection tab.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>DPAPI-protected (CurrentUser scope), Base64-encoded. Never stored in plain text.</summary>
    public string? ProtectedAccessToken { get; set; }
    public string? ProtectedRefreshToken { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
}

public sealed class OverlaySettings
{
    public string? MonitorDeviceName { get; set; }
    public AnchorCorner Anchor { get; set; } = AnchorCorner.BottomRight;
    public int OffsetX { get; set; } = 40;
    public int OffsetY { get; set; } = 40;
    public int CardWidth { get; set; } = 420;
    public double UsernameFontSize { get; set; } = 16;
    public double MessageFontSize { get; set; } = 15;
    public string FontFamily { get; set; } = "Segoe UI";
    public string BackgroundColorHex { get; set; } = "#14141C";
    public int BackgroundOpacityPercent { get; set; } = 80;
    public int CornerRadius { get; set; } = 12;
    public OverlayAnimationType AnimationType { get; set; } = OverlayAnimationType.FadeIn;
    public int AnimationDurationMs { get; set; } = 250;
    public double PostSpeechTailSeconds { get; set; } = 1.5;
    public bool ShowBadges { get; set; } = true;
    public bool ShowAvatar { get; set; } = false;
    public bool UseTwitchNameColor { get; set; } = true;
    public bool ShowMessageText { get; set; } = true;
}

public sealed class VoiceSettings
{
    public SpeechEngineType Engine { get; set; } = SpeechEngineType.Piper;
    public VoiceGender Gender { get; set; } = VoiceGender.Female;
    public string? SelectedVoiceId { get; set; }
    public double Rate { get; set; } = 1.0;
    public int Pitch { get; set; } = 0;
    public int Volume { get; set; } = 100;
    public string? OutputDeviceId { get; set; }
    public string PronunciationTemplate { get; set; } = "{user} пишет: {message}";
    public bool DontReadUsername { get; set; } = false;
    public bool TransliterateUsernames { get; set; } = true;
    public bool UniqueVoicePerViewer { get; set; } = false;
    public List<string> VoicePoolForAssignment { get; set; } = new();
    public double PauseBetweenMessagesSeconds { get; set; } = 0.3;
}

public sealed class FiltersSettings
{
    public ChatAccessLevel AccessLevel { get; set; } = ChatAccessLevel.Everyone;
    public string ChannelPointsRewardName { get; set; } = string.Empty;
    public int MaxMessageLength { get; set; } = 200;
    public bool StripLinks { get; set; } = true;
    public bool StripEmotes { get; set; } = true;
    public bool IgnoreCommands { get; set; } = true;
    public bool CollapseRepeatedChars { get; set; } = true;
    public List<string> WordBlacklist { get; set; } = new();
    public List<string> UserBlacklist { get; set; } = new();
    public int UserCooldownSeconds { get; set; } = 0;
}

public sealed class QueueSettings
{
    public int MaxQueueSize { get; set; } = 20;
    public QueueOverflowPolicy OverflowPolicy { get; set; } = QueueOverflowPolicy.EvictOldest;
    public bool SkipOldMessages { get; set; } = true;
    public int SkipOlderThanSeconds { get; set; } = 30;
}

public sealed class HotkeysSettings
{
    public string ToggleMute { get; set; } = string.Empty;
    public string SkipCurrent { get; set; } = string.Empty;
    public string ClearQueue { get; set; } = string.Empty;
    public string ToggleOverlayVisibility { get; set; } = string.Empty;
    public string OpenSettings { get; set; } = string.Empty;
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; } = false;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public AppLanguage Language { get; set; } = AppLanguage.Russian;
}
