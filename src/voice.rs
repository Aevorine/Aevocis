//! F05 voice commands ("删除这段" cancels the dictation, "全部大写" uppercases
//! the utterance before it) and F13 voice macros ("打开微信说早上好" launches an
//! app then types a greeting), plus the plain-text formats the (forthcoming)
//! settings window round-trips them through.
//!
//! Faithful port of the C# app's `VoiceCommandMatcher` / `MacroExecutor` /
//! `TriggerTextNormalizer` / `VoiceCommandTextFormat` / `VoiceMacroTextFormat`
//! (`src-reference/OpenSuperWhisper.Core/*.cs`) and their stores
//! (`src-reference/OpenSuperWhisper.Storage/{VoiceCommand,Macro}Store.cs`):
//! same default commands, same suffix-matching semantics, same trailing
//! punctuation normalization before matching, same "one bad line/action
//! doesn't lose the rest" parsing behavior. Storage follows this crate's
//! usual atomic write-temp-then-rename pattern (see `settings.rs::save`)
//! under `%LOCALAPPDATA%\Aevocis`.
//!
//! Matching (`match_command`/`match_macro`) is kept pure and side-effect-free
//! -- callers (the not-yet-built dictation controller) decide what to *do*
//! with a match; this module only answers "did this utterance hit a command
//! or macro, and what's left over".

use serde::{Deserialize, Serialize};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    INPUT, INPUT_0, INPUT_KEYBOARD, KEYBD_EVENT_FLAGS, KEYBDINPUT, KEYEVENTF_KEYUP, SendInput, VIRTUAL_KEY,
};
use windows::Win32::UI::Shell::ShellExecuteW;
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;
use windows::core::{PCWSTR, w};

use crate::target::TargetToken;

// ---------------------------------------------------------------------
// Data model
// ---------------------------------------------------------------------

/// What a matched voice command does. Deliberately scoped to what
/// `SendInput` can actually achieve: Windows has no general API to read or
/// edit text another app already has on screen, so `Cancel` can only mean
/// "don't type this utterance" -- it cannot reach into a target app and
/// delete arbitrary existing content.
#[derive(Serialize, Deserialize, Clone, Copy, PartialEq, Eq, Debug)]
pub enum VoiceCommandAction {
    /// The entire utterance was this command: the dictation is discarded
    /// (not typed, not saved to history) instead of injected.
    Cancel,
    /// The entire utterance was this command: send one Enter keystroke
    /// instead of typing the two characters "换行".
    SendEnter,
    /// The utterance *ends with* this command's phrase, with real content
    /// before it: uppercase that leading content and inject it (the command
    /// phrase itself is never typed). Only meaningful for Latin letters --
    /// Chinese has no case, which is expected, not a bug.
    UppercaseSuffix,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct VoiceCommand {
    pub phrase: String,
    pub action: VoiceCommandAction,
}

/// One step in a voice macro's action sequence. Deliberately just these
/// three -- enough for "launch an app / type something / press a key",
/// not a general scripting facility.
#[derive(Serialize, Deserialize, Clone, Copy, PartialEq, Eq, Debug)]
pub enum MacroActionType {
    /// `value` is a path, an exe name resolvable via PATH, or a URL -- shell-
    /// executed exactly as if the user had typed it into Run. This app never
    /// guesses install paths on the user's behalf; guessing wrong is worse
    /// than not guessing.
    LaunchApp,
    /// `value` is text injected into the currently-focused window.
    TypeText,
    /// `value` is a key name from the small fixed table `execute_macro`
    /// resolves (see `parse_virtual_key`), e.g. "Enter"/"Tab".
    SendKey,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct MacroAction {
    pub action_type: MacroActionType,
    pub value: String,
}

/// One user-defined voice macro: "trigger phrase hit -> run these actions in
/// order". E.g. "打开微信说早上好" = `LaunchApp("WeChat.exe")` +
/// `TypeText("早上好")`.
#[derive(Serialize, Deserialize, Clone, Debug)]
pub struct VoiceMacro {
    pub trigger: String,
    pub actions: Vec<MacroAction>,
}

// ---------------------------------------------------------------------
// Storage: %LOCALAPPDATA%\Aevocis\{voice_commands,macros}.json
// ---------------------------------------------------------------------

fn commands_path() -> std::path::PathBuf {
    crate::app_data_dir().join("voice_commands.json")
}

fn macros_path() -> std::path::PathBuf {
    crate::app_data_dir().join("macros.json")
}

/// The four commands shipped out of the box, matching the C# app's
/// `VoiceCommandStore.DefaultCommands` exactly (phrase text and action both).
/// Unlike macros -- where a sensible default is impossible, since it would
/// have to guess apps/text only the user knows -- these are broadly useful
/// with zero configuration, so a missing/corrupt store resets to *this*
/// list, not an empty one.
fn default_commands() -> Vec<VoiceCommand> {
    vec![
        VoiceCommand { phrase: "删除这段".to_string(), action: VoiceCommandAction::Cancel },
        VoiceCommand { phrase: "算了不要了".to_string(), action: VoiceCommandAction::Cancel },
        VoiceCommand { phrase: "换行".to_string(), action: VoiceCommandAction::SendEnter },
        VoiceCommand { phrase: "全部大写".to_string(), action: VoiceCommandAction::UppercaseSuffix },
    ]
}

/// Loads persisted voice commands, falling back to `default_commands()` (not
/// an empty list) if the file is missing or fails to parse -- a corrupt or
/// absent store must never leave voice commands non-functional.
pub fn load_commands() -> Vec<VoiceCommand> {
    std::fs::read_to_string(commands_path())
        .ok()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_else(default_commands)
}

/// Atomically persists `list`: write to a sibling temp file, then rename
/// over the real path, so a crash or concurrent read mid-write can never
/// observe a half-written file -- same reasoning as `settings.rs::save`.
pub fn save_commands(list: &[VoiceCommand]) {
    let path = commands_path();
    let Ok(json) = serde_json::to_string_pretty(list) else { return };
    let tmp = path.with_extension("json.tmp");
    if std::fs::write(&tmp, json).is_ok() {
        let _ = std::fs::rename(&tmp, &path);
    }
}

/// Loads persisted macros, falling back to an empty list if the file is
/// missing or fails to parse. Unlike voice commands, no sensible default
/// macro exists -- `LaunchApp`/`TypeText` values only make sense once the
/// user has told us about apps/text they personally use.
pub fn load_macros() -> Vec<VoiceMacro> {
    std::fs::read_to_string(macros_path())
        .ok()
        .and_then(|s| serde_json::from_str(&s).ok())
        .unwrap_or_default()
}

/// Atomically persists `list` (same temp-file-then-rename pattern as
/// `save_commands`).
pub fn save_macros(list: &[VoiceMacro]) {
    let path = macros_path();
    let Ok(json) = serde_json::to_string_pretty(list) else { return };
    let tmp = path.with_extension("json.tmp");
    if std::fs::write(&tmp, json).is_ok() {
        let _ = std::fs::rename(&tmp, &path);
    }
}

// ---------------------------------------------------------------------
// Trigger text normalization (shared by both matchers)
// ---------------------------------------------------------------------

/// Strips a trailing run of punctuation/whitespace so a spoken command or
/// macro trigger phrase still matches even after punctuation
/// autocorrection appended a period, or the recognizer itself produced
/// trailing punctuation the user never actually said out loud.
///
/// Faithful port of the C# app's `TriggerTextNormalizer` regex
/// (`[\s.!?。！？…、，,;:；：""'）)\]】”’]+$`): `text.trim()`, then repeatedly
/// strip the *trailing* run of whitespace or any of
/// `. ! ? 。 ！ ？ … 、 ， , ; : ； ： " ' ） ) ] 】 ” ’`. Only the end of the
/// string is ever touched -- the interior and the start are left alone.
/// Operates on `char`s throughout (never a byte index) so it can never slice
/// on a non-UTF-8-char-boundary for multi-byte (CJK) text.
pub fn normalize_trigger_text(text: &str) -> String {
    let chars: Vec<char> = text.trim().chars().collect();
    let mut end = chars.len();
    while end > 0 && is_trailing_noise(chars[end - 1]) {
        end -= 1;
    }
    chars[..end].iter().collect()
}

fn is_trailing_noise(c: char) -> bool {
    c.is_whitespace()
        || matches!(
            c,
            '.' | '!'
                | '?'
                | '。'
                | '！'
                | '？'
                | '…'
                | '、'
                | '，'
                | ','
                | ';'
                | ':'
                | '；'
                | '：'
                | '"'
                | '\''
                | '）'
                | ')'
                | ']'
                | '】'
                | '\u{201D}' // ” right double quotation mark
                | '\u{2019}' // ’ right single quotation mark
        )
}

/// Unicode-aware case-insensitive equality. `str::eq_ignore_ascii_case` only
/// folds ASCII letters, which would silently fail to case-fold e.g. an
/// accented Latin phrase; per-`char` lower-casing is the closest available
/// match to C#'s `StringComparison.OrdinalIgnoreCase` without pulling in an
/// ICU dependency, and is exact for every phrase this app actually deals
/// with (Chinese has no case; ASCII case-folds trivially).
fn ci_eq(a: &str, b: &str) -> bool {
    a.to_lowercase() == b.to_lowercase()
}

/// Case-insensitive "does `text_chars` end with `suffix_chars`", operating on
/// already-`char`-split slices so the caller can reuse both the boolean
/// result and the char-index split point without re-scanning the string or
/// ever computing a byte offset (which would risk landing mid-character for
/// CJK text).
fn ci_ends_with(text_chars: &[char], suffix_chars: &[char]) -> bool {
    if suffix_chars.len() > text_chars.len() {
        return false;
    }
    let start = text_chars.len() - suffix_chars.len();
    text_chars[start..].iter().zip(suffix_chars.iter()).all(|(a, b)| a.to_lowercase().eq(b.to_lowercase()))
}

// ---------------------------------------------------------------------
// Matching (pure, no side effects)
// ---------------------------------------------------------------------

#[derive(Debug, PartialEq)]
pub struct CommandMatch {
    pub action: VoiceCommandAction,
    pub remaining_text: String,
}

/// Finds the first command in `commands` (in list order) that `text` hits,
/// after normalizing `text` once via `normalize_trigger_text`.
///
/// `Cancel`/`SendEnter` only match when the *entire* normalized utterance
/// equals the configured phrase (also trimmed) -- a dictation that merely
/// contains "换行" mid-sentence must still get typed normally.
/// `UppercaseSuffix` instead matches when the utterance *ends with* the
/// phrase and there is real content before it once that prefix is itself
/// re-normalized -- e.g. "hello world 全部大写" matches with remaining text
/// "hello world" -- so the command can apply to text spoken in the same
/// breath as other dictated content.
pub fn match_command(text: &str, commands: &[VoiceCommand]) -> Option<CommandMatch> {
    let normalized = normalize_trigger_text(text);
    if normalized.is_empty() {
        return None;
    }
    let normalized_chars: Vec<char> = normalized.chars().collect();

    for cmd in commands {
        let phrase = cmd.phrase.trim();
        if phrase.is_empty() {
            continue;
        }

        match cmd.action {
            VoiceCommandAction::UppercaseSuffix => {
                let phrase_chars: Vec<char> = phrase.chars().collect();
                if normalized_chars.len() <= phrase_chars.len() {
                    continue;
                }
                if !ci_ends_with(&normalized_chars, &phrase_chars) {
                    continue;
                }
                let start = normalized_chars.len() - phrase_chars.len();
                let prefix: String = normalized_chars[..start].iter().collect();
                let remaining = normalize_trigger_text(&prefix);
                if remaining.is_empty() {
                    continue; // nothing to uppercase -- not a real match
                }
                return Some(CommandMatch { action: cmd.action, remaining_text: remaining });
            }
            VoiceCommandAction::Cancel | VoiceCommandAction::SendEnter => {
                if ci_eq(&normalized, phrase) {
                    return Some(CommandMatch { action: cmd.action, remaining_text: String::new() });
                }
            }
        }
    }
    None
}

/// Finds the first macro in `macros` (in list order) whose trigger phrase
/// (trimmed) exactly, case-insensitively equals the *entire* normalized
/// utterance. Unlike `UppercaseSuffix` commands, a macro never matches a
/// suffix -- "一句话完成"切软件+打字 requires the whole utterance to be the
/// trigger, not a phrase tacked onto other dictated text.
pub fn match_macro<'a>(text: &str, macros: &'a [VoiceMacro]) -> Option<&'a VoiceMacro> {
    let normalized = normalize_trigger_text(text);
    if normalized.is_empty() {
        return None;
    }

    for m in macros {
        let phrase = m.trigger.trim();
        if phrase.is_empty() {
            continue;
        }
        if ci_eq(&normalized, phrase) {
            return Some(m);
        }
    }
    None
}

// ---------------------------------------------------------------------
// Macro execution
// ---------------------------------------------------------------------

/// Resolves a `SendKey` action's `value` (trimmed, case-insensitive) into a
/// virtual-key code. Deliberately just this handful -- Enter and a few keys
/// that are actually useful mid-dictation -- not a full `VK_*` table,
/// matching the C# app's own `VirtualKeys.TryParse` scope exactly (English
/// names plus a couple of common Chinese aliases).
fn parse_virtual_key(value: &str) -> Option<u16> {
    match value.trim().to_lowercase().as_str() {
        "enter" | "return" | "回车" | "换行" => Some(0x0D),
        "backspace" | "退格" | "删除" => Some(0x08),
        "tab" => Some(0x09),
        "escape" | "esc" => Some(0x1B),
        "space" | "空格" => Some(0x20),
        _ => None,
    }
}

/// Builds one synthetic key event carrying a real virtual-key code -- unlike
/// `inject.rs`'s `keyboard_input` (which carries a UTF-16 code unit under
/// `KEYEVENTF_UNICODE`), this sends an actual key press (e.g. a real Enter
/// key), so `wVk` is set and no `KEYEVENTF_UNICODE` flag is present.
fn virtual_key_input(vk: u16, flags: KEYBD_EVENT_FLAGS) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT { wVk: VIRTUAL_KEY(vk), wScan: 0, dwFlags: flags, time: 0, dwExtraInfo: 0 },
        },
    }
}

/// Shell-executes `value` exactly as if the user had typed it into Run --
/// works uniformly for an exe name on PATH, an absolute path, or a URL. This
/// app never guesses install paths on the user's behalf.
fn launch_app(value: &str) -> Result<(), String> {
    // Kept alive for the duration of the call: `file_ptr` below borrows from
    // this buffer, and ShellExecuteW is a plain FFI call with no lifetime to
    // enforce that for us.
    let file: Vec<u16> = value.encode_utf16().chain(std::iter::once(0)).collect();
    let file_ptr = PCWSTR(file.as_ptr());

    let result = unsafe {
        ShellExecuteW(None, w!("open"), file_ptr, PCWSTR::null(), PCWSTR::null(), SW_SHOWNORMAL)
    };
    // Win32 docs for ShellExecuteW: a return value greater than 32 indicates
    // success; every documented SE_ERR_* failure code is <= 32.
    let code = result.0 as isize;
    if code > 32 { Ok(()) } else { Err(format!("打开「{value}」失败（错误码 {code}）")) }
}

/// Sends one key-down + key-up `INPUT` pair for virtual-key `vk`.
fn send_virtual_key(vk: u16) -> Result<(), String> {
    let inputs = [virtual_key_input(vk, KEYBD_EVENT_FLAGS(0)), virtual_key_input(vk, KEYEVENTF_KEYUP)];
    let sent = unsafe { SendInput(&inputs, core::mem::size_of::<INPUT>() as i32) };
    if sent as usize == inputs.len() { Ok(()) } else { Err("发送按键失败".to_string()) }
}

fn macro_action_label(t: MacroActionType) -> &'static str {
    match t {
        MacroActionType::LaunchApp => "打开",
        MacroActionType::TypeText => "打字",
        MacroActionType::SendKey => "按键",
    }
}

/// Runs `m.actions` in order via `target`'s currently-focused window.
/// Mirrors the C# app's `MacroExecutor.Execute`: each action's failure (bad
/// launch value, unknown key name, `SendInput`/injection rejection) is
/// caught individually and recorded in the returned list, but never stops
/// the remaining actions -- e.g. a macro "打开:一个装错的路径;打字:早上好"
/// still types "早上好" even though the launch failed, instead of silently
/// doing nothing at all. An empty return means every action succeeded.
pub fn execute_macro(m: &VoiceMacro, target: &TargetToken) -> Vec<String> {
    let mut errors = Vec::new();
    for action in &m.actions {
        let result: Result<(), String> = match action.action_type {
            MacroActionType::LaunchApp => launch_app(&action.value),
            MacroActionType::TypeText => {
                if crate::inject::inject_unicode(&action.value, target) {
                    Ok(())
                } else {
                    Err("打字失败（目标窗口已切换或注入被拒绝）".to_string())
                }
            }
            MacroActionType::SendKey => match parse_virtual_key(&action.value) {
                Some(vk) => send_virtual_key(vk),
                None => Err(format!("未知按键名：{}", action.value)),
            },
        };
        if let Err(msg) = result {
            errors.push(format!("{}:{} - {msg}", macro_action_label(action.action_type), action.value));
        }
    }
    errors
}

// ---------------------------------------------------------------------
// Plain-text editor formats (settings-window textbox round-trip)
// ---------------------------------------------------------------------

fn command_action_label(action: VoiceCommandAction) -> &'static str {
    match action {
        VoiceCommandAction::Cancel => "取消",
        VoiceCommandAction::SendEnter => "换行",
        VoiceCommandAction::UppercaseSuffix => "大写后缀",
    }
}

fn command_action_from_label(label: &str) -> Option<VoiceCommandAction> {
    match label {
        "取消" => Some(VoiceCommandAction::Cancel),
        "换行" => Some(VoiceCommandAction::SendEnter),
        "大写后缀" => Some(VoiceCommandAction::UppercaseSuffix),
        _ => None,
    }
}

/// One line per command: `标签|触发词`, e.g. `换行|换行`. Kept as plain string
/// logic (no UI dependency) so the settings window (built separately) can
/// reuse it as-is, mirroring the C# app's `VoiceCommandTextFormat`.
pub fn format_commands(list: &[VoiceCommand]) -> String {
    list.iter()
        .map(|c| format!("{}|{}", command_action_label(c.action), c.phrase))
        .collect::<Vec<_>>()
        .join("\n")
}

/// Parses the text box content, one command per line. A line that's blank,
/// or doesn't parse (missing `|`, empty phrase, unknown action label), is
/// silently skipped rather than aborting the whole parse -- a typo on one
/// line must not lose every other line the user already configured. Splits
/// on the *first* `|` only (`splitn(2, '|')`), since a phrase could in
/// principle contain another `|`.
pub fn parse_commands(text: &str) -> Vec<VoiceCommand> {
    let mut result = Vec::new();
    for raw_line in text.split('\n') {
        let line = raw_line.trim();
        if line.is_empty() {
            continue;
        }
        let mut parts = line.splitn(2, '|');
        let label = parts.next().unwrap_or("").trim();
        let Some(phrase) = parts.next() else { continue }; // no '|' present
        let phrase = phrase.trim();
        if phrase.is_empty() {
            continue;
        }
        let Some(action) = command_action_from_label(label) else { continue };
        result.push(VoiceCommand { phrase: phrase.to_string(), action });
    }
    result
}

/// One line per macro: `触发短语|动作标签:值;动作标签:值;...`, e.g.
/// `打开微信说早上好|打开:WeChat.exe;打字:早上好`. Mirrors the C# app's
/// `VoiceMacroTextFormat`.
pub fn format_macros(list: &[VoiceMacro]) -> String {
    list.iter()
        .map(|m| {
            let actions = m
                .actions
                .iter()
                .map(|a| format!("{}:{}", macro_action_label(a.action_type), a.value))
                .collect::<Vec<_>>()
                .join(";");
            format!("{}|{actions}", m.trigger)
        })
        .collect::<Vec<_>>()
        .join("\n")
}

fn macro_action_from_label(label: &str) -> Option<MacroActionType> {
    match label {
        "打开" => Some(MacroActionType::LaunchApp),
        "打字" => Some(MacroActionType::TypeText),
        "按键" => Some(MacroActionType::SendKey),
        _ => None,
    }
}

/// Parses the text box content, one macro per line. A malformed action token
/// is skipped (not the whole line); a line with no trigger phrase, no
/// actions text, or zero valid actions after parsing is skipped entirely --
/// same "one typo doesn't lose everything else" behavior as
/// `parse_commands`. Splits the trigger from the actions on the first `|`
/// only, and each action on the first `:` only.
pub fn parse_macros(text: &str) -> Vec<VoiceMacro> {
    let mut result = Vec::new();
    for raw_line in text.split('\n') {
        let line = raw_line.trim();
        if line.is_empty() {
            continue;
        }
        let mut parts = line.splitn(2, '|');
        let trigger = parts.next().unwrap_or("").trim();
        let Some(actions_part) = parts.next() else { continue }; // no '|' present
        let actions_part = actions_part.trim();
        if trigger.is_empty() || actions_part.is_empty() {
            continue;
        }

        let mut actions = Vec::new();
        for action_token in actions_part.split(';') {
            let action_token = action_token.trim();
            if action_token.is_empty() {
                continue;
            }
            let mut action_parts = action_token.splitn(2, ':');
            let label = action_parts.next().unwrap_or("").trim();
            let Some(value) = action_parts.next() else { continue }; // no ':' present
            let value = value.trim();
            if value.is_empty() {
                continue;
            }
            let Some(action_type) = macro_action_from_label(label) else { continue };
            actions.push(MacroAction { action_type, value: value.to_string() });
        }
        if actions.is_empty() {
            continue;
        }

        result.push(VoiceMacro { trigger: trigger.to_string(), actions });
    }
    result
}

// ---------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalize_strips_trailing_punctuation_and_whitespace() {
        assert_eq!(normalize_trigger_text("换行。"), "换行");
        assert_eq!(normalize_trigger_text("换行！！  "), "换行");
        assert_eq!(normalize_trigger_text("  全部大写...  ,"), "全部大写");
        // Mixed run of different punctuation/whitespace kinds in one go.
        assert_eq!(normalize_trigger_text("删除这段, . ；"), "删除这段");
        // Interior and leading content must never be touched.
        assert_eq!(normalize_trigger_text("先说. 这句再说。"), "先说. 这句再说");
        // A phrase with no trailing noise is returned unchanged.
        assert_eq!(normalize_trigger_text("换行"), "换行");
    }

    fn default_test_commands() -> Vec<VoiceCommand> {
        default_commands()
    }

    #[test]
    fn match_command_cancel_exact_only() {
        let cmds = default_test_commands();
        let m = match_command("删除这段", &cmds).expect("should match Cancel");
        assert_eq!(m.action, VoiceCommandAction::Cancel);
        assert_eq!(m.remaining_text, "");

        let m2 = match_command("算了不要了。", &cmds).expect("trailing punctuation still matches");
        assert_eq!(m2.action, VoiceCommandAction::Cancel);

        // Mid-sentence occurrence must NOT trigger Cancel/SendEnter.
        assert!(match_command("我说了删除这段之后继续说话", &cmds).is_none());
    }

    #[test]
    fn match_command_send_enter_exact_only() {
        let cmds = default_test_commands();
        let m = match_command("换行", &cmds).expect("should match SendEnter");
        assert_eq!(m.action, VoiceCommandAction::SendEnter);
        assert_eq!(m.remaining_text, "");
    }

    #[test]
    fn match_command_uppercase_suffix_extracts_prefix() {
        let cmds = default_test_commands();
        let m = match_command("hello world 全部大写", &cmds).expect("should match UppercaseSuffix");
        assert_eq!(m.action, VoiceCommandAction::UppercaseSuffix);
        assert_eq!(m.remaining_text, "hello world");

        // The bare command phrase alone has nothing before it -- not a real
        // match (nothing to uppercase).
        assert!(match_command("全部大写", &cmds).is_none());

        // Trailing punctuation after the command phrase itself is tolerated.
        let m2 = match_command("hello world 全部大写。", &cmds).expect("trailing punctuation tolerated");
        assert_eq!(m2.remaining_text, "hello world");
    }

    #[test]
    fn match_macro_requires_whole_utterance_exact_match() {
        let macros = vec![VoiceMacro {
            trigger: "打开微信说早上好".to_string(),
            actions: vec![
                MacroAction { action_type: MacroActionType::LaunchApp, value: "WeChat.exe".to_string() },
                MacroAction { action_type: MacroActionType::TypeText, value: "早上好".to_string() },
            ],
        }];

        let m = match_macro("打开微信说早上好", &macros).expect("exact match should hit");
        assert_eq!(m.trigger, "打开微信说早上好");

        // Trailing punctuation is still normalized away before matching.
        assert!(match_macro("打开微信说早上好！", &macros).is_some());

        // Unlike UppercaseSuffix commands, a macro must NEVER match as a
        // suffix or a prefix -- only the whole normalized utterance.
        assert!(match_macro("我想打开微信说早上好啊", &macros).is_none());
        assert!(match_macro("打开微信说早上好然后关掉", &macros).is_none());
        assert!(match_macro("微信说早上好", &macros).is_none());
    }

    #[test]
    fn command_format_round_trips_defaults() {
        let cmds = default_test_commands();
        let text = format_commands(&cmds);
        let parsed = parse_commands(&text);
        assert_eq!(parsed.len(), cmds.len());
        for (original, round_tripped) in cmds.iter().zip(parsed.iter()) {
            assert_eq!(original.phrase, round_tripped.phrase);
            assert_eq!(original.action, round_tripped.action);
        }
    }

    #[test]
    fn command_parse_skips_malformed_lines() {
        let text = "换行|换行\nno-pipe-here\n未知标签|某短语\n取消|\n大写后缀|全部大写";
        let parsed = parse_commands(text);
        // Only the valid "换行|换行" and "大写后缀|全部大写" lines survive:
        // "no-pipe-here" has no '|', "未知标签|..." has an unrecognized
        // label, and "取消|" has an empty phrase.
        assert_eq!(parsed.len(), 2);
        assert_eq!(parsed[0].phrase, "换行");
        assert_eq!(parsed[0].action, VoiceCommandAction::SendEnter);
        assert_eq!(parsed[1].phrase, "全部大写");
        assert_eq!(parsed[1].action, VoiceCommandAction::UppercaseSuffix);
    }

    #[test]
    fn macro_format_round_trips_two_action_macro() {
        let macros = vec![VoiceMacro {
            trigger: "打开微信说早上好".to_string(),
            actions: vec![
                MacroAction { action_type: MacroActionType::LaunchApp, value: "WeChat.exe".to_string() },
                MacroAction { action_type: MacroActionType::TypeText, value: "早上好".to_string() },
            ],
        }];

        let text = format_macros(&macros);
        assert_eq!(text, "打开微信说早上好|打开:WeChat.exe;打字:早上好");

        let parsed = parse_macros(&text);
        assert_eq!(parsed.len(), 1);
        assert_eq!(parsed[0].trigger, "打开微信说早上好");
        assert_eq!(parsed[0].actions.len(), 2);
        assert_eq!(parsed[0].actions[0].action_type, MacroActionType::LaunchApp);
        assert_eq!(parsed[0].actions[0].value, "WeChat.exe");
        assert_eq!(parsed[0].actions[1].action_type, MacroActionType::TypeText);
        assert_eq!(parsed[0].actions[1].value, "早上好");
    }

    #[test]
    fn macro_parse_skips_malformed_lines_and_tokens() {
        // Line 1: valid trigger, one bad action token (unknown label) mixed
        // with one good one -- only the good one should survive.
        // Line 2: no '|' at all -- entire line skipped.
        // Line 3: valid trigger but every action token is malformed -- zero
        // valid actions means the whole line is skipped.
        let text = "触发一|未知:x;按键:Enter\nno-pipe-here\n触发二|也是未知:y";
        let parsed = parse_macros(text);
        assert_eq!(parsed.len(), 1);
        assert_eq!(parsed[0].trigger, "触发一");
        assert_eq!(parsed[0].actions.len(), 1);
        assert_eq!(parsed[0].actions[0].action_type, MacroActionType::SendKey);
        assert_eq!(parsed[0].actions[0].value, "Enter");
    }

    #[test]
    fn parse_virtual_key_resolves_fixed_table_case_insensitively() {
        assert_eq!(parse_virtual_key("Enter"), Some(0x0D));
        assert_eq!(parse_virtual_key("  RETURN "), Some(0x0D));
        assert_eq!(parse_virtual_key("回车"), Some(0x0D));
        assert_eq!(parse_virtual_key("换行"), Some(0x0D));
        assert_eq!(parse_virtual_key("Backspace"), Some(0x08));
        assert_eq!(parse_virtual_key("退格"), Some(0x08));
        assert_eq!(parse_virtual_key("删除"), Some(0x08));
        assert_eq!(parse_virtual_key("TAB"), Some(0x09));
        assert_eq!(parse_virtual_key("esc"), Some(0x1B));
        assert_eq!(parse_virtual_key("Escape"), Some(0x1B));
        assert_eq!(parse_virtual_key("space"), Some(0x20));
        assert_eq!(parse_virtual_key("空格"), Some(0x20));
        assert_eq!(parse_virtual_key("not-a-key"), None);
    }
}
