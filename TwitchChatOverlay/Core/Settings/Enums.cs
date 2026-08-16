namespace TwitchChatOverlay.Core.Settings;

public enum AnchorCorner
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    MiddleCenter,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

public enum OverlayAnimationType
{
    FadeIn,
    FadeOut,
    SlideLeft,
    SlideRight,
    None
}

public enum SpeechEngineType
{
    Piper,
    Sapi
}

public enum VoiceGender
{
    Male,
    Female
}

public enum ChatAccessLevel
{
    Everyone,
    Subscribers,
    VipAndAbove,
    ModeratorsAndAbove,
    ChannelPointsOnly
}

public enum QueueOverflowPolicy
{
    DropNewest,
    EvictOldest
}

public enum AppLanguage
{
    Russian,
    English
}
