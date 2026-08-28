namespace OpenSuperWhisper.Core.Models;

public sealed class AppSettings
{
    public string ModelPath { get; set; } = "";
    /// <summary>F01: which recognition model the user picked, e.g. "small"/"medium"/"large-v3-turbo"
    /// (see OpenSuperWhisper.Recognition.ModelCatalog for the full list). Default is "small" - the
    /// original, always-bundled model - so existing users who never touch this setting see no
    /// change in behavior. ModelPath above is the resolved on-disk path derived from this at
    /// startup/switch time; ModelSize is the durable user preference.</summary>
    public string ModelSize { get; set; } = "small";
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
}
