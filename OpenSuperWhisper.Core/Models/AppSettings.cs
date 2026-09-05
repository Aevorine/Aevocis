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
    /// <summary>v1.2.0: which recognition engine to use - "sensevoice" (闪电引擎, the default:
    /// SenseVoice int8 via sherpa-onnx, Chinese-optimized, ~0.2s per utterance, ~340MB peak) or
    /// "whisper" (Whisper.net, 99 languages, slower/heavier; its model is chosen by ModelSize
    /// below and downloaded on demand). Any unknown value is treated as "sensevoice" so a
    /// corrupted/foreign settings.json never selects a broken engine.</summary>
    public string RecognitionEngine { get; set; } = "sensevoice";

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
    /// <summary>F09: defaults to Hold so existing users see no behavior change until they opt
    /// into Toggle mode.</summary>
    public PushToTalkMode PushToTalkMode { get; set; } = PushToTalkMode.Hold;
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

    /// <summary>F11: when true, recognized text is shown in an editable confirmation window
    /// instead of being injected immediately - Enter injects, Esc cancels the dictation. Defaults
    /// to false so existing users see no behavior change (still "recognize -> type immediately")
    /// until they opt in.</summary>
    public bool ShowDraftBeforeInject { get; set; } = false;

    /// <summary>F33: when true (the default) AND <see cref="ShowDraftBeforeInject"/> is also true,
    /// DictationController watches edits the user makes inside the F11 draft-confirm window - the
    /// ONLY signal this feature ever uses, see <see cref="Core.TermLearning"/>'s doc comment for
    /// the full scope boundary - and once the same (recognized fragment -> user's replacement)
    /// edit has recurred <see cref="Core.TermLearning.PromotionThreshold"/> times across separate
    /// dictations, automatically adds it to the real term dictionary and tells the user via a tray
    /// balloon. Has no effect at all while ShowDraftBeforeInject is off (there is no edit signal to
    /// learn from without that window), regardless of this flag's value. Defaults to true - unlike
    /// ShowDraftBeforeInject itself, this is inert until the user opts into that window, so
    /// defaulting it on doesn't change behavior for the vast majority of users who never enable
    /// ShowDraftBeforeInject in the first place.</summary>
    public bool TermLearningEnabled { get; set; } = true;

    /// <summary>F32: Win32 MOD_* flags (see OpenSuperWhisper.Hotkeys.GlobalToggleWindowHotkey's
    /// ModControl/ModAlt/ModShift/ModWin re-exports, or winuser.h) for the dedicated show/hide
    /// main window hotkey - kept as a plain int here, not a Hotkeys-project type, because Core
    /// must not depend on Hotkeys (Hotkeys already depends on Core; a reverse reference would be
    /// circular). Default 0x0003 = MOD_CONTROL (0x2) | MOD_ALT (0x1), i.e. Ctrl+Alt - chosen
    /// together with <see cref="ShowHideVirtualKeyCode"/> below for Ctrl+Alt+H.</summary>
    public int ShowHideHotkeyModifier { get; set; } = 0x0003;

    /// <summary>F32: the non-modifier key for the show/hide hotkey, as a Win32 VK_* code. Default
    /// 0x48 = VK_H ("H" for 隐藏/显示 - hide/show) - chosen because Ctrl+Alt+H is not reserved by
    /// Windows itself (unlike e.g. Ctrl+Alt+Del/Esc/Tab/Arrows) nor by any of this app's other
    /// stated target apps (VSCode, Word, browsers, WeChat, mail) in their default keymaps, so it
    /// is unlikely to collide with something the user already relies on. If RegisterHotKey still
    /// reports it as taken by some other running app (see GlobalToggleWindowHotkey.LastWin32Error),
    /// the user can rebind it in Settings the same way as the push-to-talk key.</summary>
    public int ShowHideVirtualKeyCode { get; set; } = 0x48;
}
