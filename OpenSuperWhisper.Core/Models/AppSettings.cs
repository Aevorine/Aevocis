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

    /// <summary>F06: process name (Process.ProcessName, e.g. "WeChat", "Code" - no ".exe"
    /// suffix) -> a Whisper initial prompt used only while that app has focus, to bias
    /// recognition style/vocabulary (e.g. keep English identifiers untranslated in an editor,
    /// prefer colloquial phrasing in a chat app). Matched case-insensitively via
    /// AppSpecificLookup, not via this dictionary's own comparer - System.Text.Json rebuilds
    /// Dictionary properties with the default (case-sensitive) comparer on every settings.json
    /// load, so a comparer set here wouldn't survive a save/load round trip. Empty by default:
    /// no entries means zero behavior change for existing users, and the recommended presets
    /// (WeChat/Code/Claude) are offered in the Settings UI as opt-in quick-adds, not baked in
    /// here.</summary>
    public Dictionary<string, string> AppSpecificPrompts { get; set; } = new();

    /// <summary>F12: process name -> a dedicated push-to-talk virtual-key code that fires only
    /// while that app has focus, instead of the global <see cref="PushToTalkVirtualKeyCode"/> -
    /// so the same physical key can mean something else while that app is focused, without
    /// affecting any other app. Matched case-insensitively via AppSpecificLookup (see
    /// AppSpecificPrompts for why). Empty by default: no entries means the global key always
    /// applies everywhere, identical to pre-F12 behavior.</summary>
    public Dictionary<string, int> AppSpecificHotkeys { get; set; } = new();
}
