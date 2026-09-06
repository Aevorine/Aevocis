//! Settings window controller. The Slint component this wraps
//! (`SettingsWindow`) is compiled separately from `main.rs`'s
//! `slint::include_modules!()` target (see `build.rs`'s doc comment for why),
//! so its generated types are pulled in here manually.
include!(concat!(env!("OUT_DIR"), "/settings_window_ui.rs"));

use std::cell::RefCell;
use std::rc::Rc;

use slint::{ComponentHandle, Model};

use crate::settings::{AppSettings, PushToTalkMode};

/// Everything the caller must supply to populate a freshly-opened window.
pub struct OpenParams {
    pub settings: AppSettings,
    pub microphone_names: Vec<String>,
    pub voice_commands_text: String,
    pub voice_macros_text: String,
}

/// Everything the window hands back when the user clicks "保存". Text areas
/// are handed back raw (not yet parsed) so the caller can apply
/// `voice::parse_commands`/`voice::parse_macros` without this module needing
/// a compile-time dependency on that module.
pub struct SaveResult {
    pub settings: AppSettings,
    pub voice_commands_text: String,
    pub voice_macros_text: String,
}

/// Owns the live window plus its resource-usage poll timer, so both stay
/// alive exactly as long as the window is open (the caller should hold this
/// in the same place it already holds other long-lived window handles).
pub struct Controller {
    pub window: SettingsWindow,
    _resource_timer: slint::Timer,
}

fn retention_days_to_index(days: u32) -> i32 {
    match days {
        7 => 1,
        30 => 2,
        90 => 3,
        _ => 0,
    }
}

fn retention_index_to_days(index: i32) -> u32 {
    match index {
        1 => 7,
        2 => 30,
        3 => 90,
        _ => 0,
    }
}

/// `procName|按键名` per line, mirroring the format `voice.rs` uses for
/// commands/macros -- kept local since only this window edits this field.
fn format_app_hotkeys(map: &std::collections::HashMap<String, u32>) -> String {
    let mut lines: Vec<String> = map.iter().map(|(proc, vk)| format!("{proc}|{}", crate::hotkey_capture::vk_label(*vk))).collect();
    lines.sort();
    lines.join("\n")
}

fn parse_app_hotkeys(text: &str) -> std::collections::HashMap<String, u32> {
    let mut map = std::collections::HashMap::new();
    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() {
            continue;
        }
        let mut parts = line.splitn(2, '|');
        let (Some(proc), Some(key_name)) = (parts.next(), parts.next()) else { continue };
        let proc = proc.trim().trim_end_matches(".exe").to_lowercase();
        if proc.is_empty() {
            continue;
        }
        if let Some(vk) = crate::hotkey_capture::parse_vk_label(key_name.trim()) {
            map.insert(proc, vk);
        }
    }
    map
}

/// Opens the Settings window populated from `params`. `on_save` fires once
/// when the user clicks "保存" (the window is not closed automatically by
/// this module for "取消" or the titlebar X either -- wire those in the
/// caller if a different behavior than "just leave it open" is wanted, or
/// extend this function; for this app's tray-app conventions, closing this
/// secondary window outright on both paths is what callers should do).
pub fn open(params: OpenParams, on_save: impl FnOnce(SaveResult) + 'static, on_open_term_dictionary: impl FnOnce() + 'static) -> Controller {
    let win = SettingsWindow::new().expect("failed to create settings window");

    win.set_ptt_key_label(crate::hotkey_capture::vk_label(params.settings.push_to_talk_virtual_key).into());
    win.set_ptt_key_vk(params.settings.push_to_talk_virtual_key as i32);
    win.set_ptt_mode_toggle(matches!(params.settings.push_to_talk_mode, PushToTalkMode::Toggle));
    win.set_show_hide_key_label(
        crate::hotkey_capture::combo_label(params.settings.show_hide_hotkey_modifiers, params.settings.show_hide_virtual_key).into(),
    );
    win.set_show_hide_key_vk(params.settings.show_hide_virtual_key as i32);
    win.set_show_hide_modifiers(params.settings.show_hide_hotkey_modifiers as i32);

    let mut mic_display: Vec<slint::SharedString> = vec!["系统默认".into()];
    mic_display.extend(params.microphone_names.iter().map(|n| n.as_str().into()));
    let mic_selected_index =
        params.microphone_names.iter().position(|n| *n == params.settings.microphone_device_id).map(|i| i as i32 + 1).unwrap_or(0);
    let mic_model = Rc::new(slint::VecModel::from(mic_display));
    win.set_microphone_options(mic_model.into());
    win.set_microphone_selected_index(mic_selected_index);

    win.set_autostart_enabled(params.settings.auto_start_with_windows);
    win.set_autocorrect_enabled(params.settings.autocorrect_punctuation);
    win.set_draft_confirm_enabled(params.settings.show_draft_before_inject);
    win.set_retention_selected_index(retention_days_to_index(params.settings.history_retention_days));
    win.set_app_hotkeys_text(format_app_hotkeys(&params.settings.app_specific_hotkeys).into());
    win.set_voice_commands_text(params.voice_commands_text.into());
    win.set_voice_macros_text(params.voice_macros_text.into());

    let monitor = Rc::new(RefCell::new(crate::resource_usage::ResourceMonitor::new()));
    let resource_timer = slint::Timer::default();
    {
        let weak = win.as_weak();
        resource_timer.start(slint::TimerMode::Repeated, std::time::Duration::from_secs(1), move || {
            if let Some(win) = weak.upgrade() {
                let usage = monitor.borrow_mut().sample();
                win.set_resource_usage_text(format!("内存 {:.0} MB · CPU {:.0}%", usage.memory_mb, usage.cpu_percent).into());
            }
        });
    }

    {
        let weak = win.as_weak();
        win.on_capture_ptt_key(move || {
            let weak2 = weak.clone();
            crate::hotkey_capture::arm(move |vk| {
                if let Some(win) = weak2.upgrade() {
                    win.set_ptt_key_label(crate::hotkey_capture::vk_label(vk).into());
                    win.set_ptt_key_vk(vk as i32);
                }
            });
        });
    }
    {
        let weak = win.as_weak();
        win.on_capture_show_hide_key(move || {
            let weak2 = weak.clone();
            crate::hotkey_capture::arm(move |vk| {
                if let Some(win) = weak2.upgrade() {
                    let modifiers = crate::hotkey_capture::current_modifier_flags();
                    win.set_show_hide_key_label(crate::hotkey_capture::combo_label(modifiers, vk).into());
                    win.set_show_hide_key_vk(vk as i32);
                    win.set_show_hide_modifiers(modifiers as i32);
                }
            });
        });
    }

    win.on_open_term_dictionary({
        let cell = Rc::new(RefCell::new(Some(Box::new(on_open_term_dictionary) as Box<dyn FnOnce()>)));
        move || {
            if let Some(cb) = cell.borrow_mut().take() {
                cb();
            }
        }
    });

    win.on_export_settings({
        let weak = win.as_weak();
        move || {
            let Some(win) = weak.upgrade() else { return };
            export_settings_dialog(&win);
        }
    });
    win.on_import_settings({
        let weak = win.as_weak();
        move || {
            let Some(win) = weak.upgrade() else { return };
            import_settings_dialog(&win);
        }
    });

    win.on_cancel({
        let weak = win.as_weak();
        move || {
            if let Some(win) = weak.upgrade() {
                let _ = win.hide();
            }
        }
    });

    let on_save_cell = Rc::new(RefCell::new(Some(Box::new(on_save) as Box<dyn FnOnce(SaveResult)>)));
    win.on_save({
        let weak = win.as_weak();
        move || {
            let Some(win) = weak.upgrade() else { return };
            let settings = AppSettings {
                language: "auto".to_string(),
                microphone_device_id: {
                    let idx = win.get_microphone_selected_index();
                    let options = win.get_microphone_options();
                    if idx > 0 { options.row_data((idx) as usize).map(|s| s.to_string()).unwrap_or_default() } else { String::new() }
                },
                push_to_talk_virtual_key: win.get_ptt_key_vk() as u32,
                push_to_talk_mode: if win.get_ptt_mode_toggle() { PushToTalkMode::Toggle } else { PushToTalkMode::Hold },
                auto_start_with_windows: win.get_autostart_enabled(),
                autocorrect_punctuation: win.get_autocorrect_enabled(),
                history_retention_days: retention_index_to_days(win.get_retention_selected_index()),
                has_seen_onboarding: true,
                app_specific_hotkeys: parse_app_hotkeys(&win.get_app_hotkeys_text()),
                show_draft_before_inject: win.get_draft_confirm_enabled(),
                show_hide_hotkey_modifiers: win.get_show_hide_modifiers() as u32,
                show_hide_virtual_key: win.get_show_hide_key_vk() as u32,
            };
            let result = SaveResult {
                settings,
                voice_commands_text: win.get_voice_commands_text().to_string(),
                voice_macros_text: win.get_voice_macros_text().to_string(),
            };
            let _ = win.hide();
            if let Some(cb) = on_save_cell.borrow_mut().take() {
                cb(result);
            }
        }
    });

    win.show().expect("failed to show settings window");
    Controller { window: win, _resource_timer: resource_timer }
}

fn export_settings_dialog(win: &SettingsWindow) {
    let default_path = dirs_desktop().join("Aevocis-settings-export.json");
    match crate::settings::export_bundle(
        &default_path,
        &crate::settings::SettingsBundle { settings: crate::settings::load(), terms: None, voice_commands: None, macros: None },
    ) {
        Ok(()) => win.set_resource_usage_text(format!("已导出到 {}", default_path.display()).into()),
        Err(e) => win.set_resource_usage_text(format!("导出失败: {e}").into()),
    }
}

fn import_settings_dialog(win: &SettingsWindow) {
    let default_path = dirs_desktop().join("Aevocis-settings-export.json");
    match crate::settings::import_bundle(&default_path) {
        Ok(bundle) => {
            win.set_autostart_enabled(bundle.settings.auto_start_with_windows);
            win.set_autocorrect_enabled(bundle.settings.autocorrect_punctuation);
            win.set_draft_confirm_enabled(bundle.settings.show_draft_before_inject);
            win.set_retention_selected_index(retention_days_to_index(bundle.settings.history_retention_days));
            win.set_ptt_key_label(crate::hotkey_capture::vk_label(bundle.settings.push_to_talk_virtual_key).into());
            win.set_ptt_key_vk(bundle.settings.push_to_talk_virtual_key as i32);
            win.set_resource_usage_text(format!("已从 {} 导入", default_path.display()).into());
        }
        Err(e) => win.set_resource_usage_text(format!("导入失败（{}）: {e}", default_path.display()).into()),
    }
}

fn dirs_desktop() -> std::path::PathBuf {
    std::env::var("USERPROFILE").map(|p| std::path::PathBuf::from(p).join("Desktop")).unwrap_or_else(|_| std::path::PathBuf::from("."))
}
