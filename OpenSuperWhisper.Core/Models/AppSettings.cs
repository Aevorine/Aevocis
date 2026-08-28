namespace OpenSuperWhisper.Core.Models;

/// <summary>F09: how a physical key press/release maps to "start/stop recording".</summary>
public enum PushToTalkMode
{
    /// <summary>Historical behavior: key-down starts, key-up stops. Good for short utterances.</summary>
    Hold,
    /// <summary>First key-down starts, the next key-down stops (key-up is ignored) - good for
    /// long-form dictation where holding a key down the whole time is uncomfortable.</summary>
    Toggle,
}

public sealed class AppSettings
{
    public string ModelPath { get; set; } = "";
    public string Language { get; set; } = "auto";
    /// <summary>WASAPI endpoint ID of the microphone to record from, or "" to follow whichever
    /// device Windows shows as the system's default input device.</summary>
    public string MicrophoneDeviceId { get; set; } = "";
    public int PushToTalkVirtualKeyCode { get; set; } = 0xA3; // VK_RCONTROL
    /// <summary>F09: defaults to Hold so existing users see no behavior change until they opt
    /// into Toggle mode.</summary>
    public PushToTalkMode PushToTalkMode { get; set; } = PushToTalkMode.Hold;
    public bool AutoStartWithWindows { get; set; }
    public bool AutocorrectPunctuation { get; set; } = true;
    /// <summary>F23: history records older than this many days are purged automatically on
    /// startup. 0 means "keep forever" - the historical default, so existing users see no
    /// behavior change until they opt in.</summary>
    public int HistoryRetentionDays { get; set; } = 0;
    /// <summary>F11: when true, recognized text is shown in an editable confirmation window
    /// instead of being injected immediately - Enter injects, Esc cancels the dictation. Defaults
    /// to false so existing users see no behavior change (still "recognize -> type immediately")
    /// until they opt in.</summary>
    public bool ShowDraftBeforeInject { get; set; } = false;
}
