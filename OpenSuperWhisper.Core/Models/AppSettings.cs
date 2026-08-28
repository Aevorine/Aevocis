namespace OpenSuperWhisper.Core.Models;

public sealed class AppSettings
{
    public string ModelPath { get; set; } = "";
    public string Language { get; set; } = "auto";
    /// <summary>WASAPI endpoint ID of the microphone to record from, or "" to follow whichever
    /// device Windows shows as the system's default input device.</summary>
    public string MicrophoneDeviceId { get; set; } = "";
    public int PushToTalkVirtualKeyCode { get; set; } = 0xA3; // VK_RCONTROL
    public bool AutoStartWithWindows { get; set; }
    public bool AutocorrectPunctuation { get; set; } = true;
    /// <summary>F23: history records older than this many days are purged automatically on
    /// startup. 0 means "keep forever" - the historical default, so existing users see no
    /// behavior change until they opt in.</summary>
    public int HistoryRetentionDays { get; set; } = 0;
    /// <summary>F29: true once the user has dismissed the first-launch onboarding window (any
    /// way - "知道了", "跳过", or just closing it). Defaults to false so a brand-new install
    /// (and any settings.json from before this field existed, which deserializes it as the
    /// default) shows onboarding exactly once.</summary>
    public bool HasSeenOnboarding { get; set; } = false;
}
