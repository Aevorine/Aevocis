//! Launch-at-sign-in toggle via the HKCU `Run` registry key -- mirrors the
//! shipping C# app's `AutoStart.cs`. The registry key itself is the single
//! source of truth (no separate settings file that could fall out of sync
//! with it): `is_enabled()` always reflects the real current state, read
//! fresh, never cached.

use winreg::RegKey;
use winreg::enums::{HKEY_CURRENT_USER, KEY_READ, KEY_WRITE};

const RUN_KEY_PATH: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";
const VALUE_NAME: &str = "Aevocis";

fn open() -> std::io::Result<RegKey> {
    RegKey::predef(HKEY_CURRENT_USER).open_subkey_with_flags(RUN_KEY_PATH, KEY_READ | KEY_WRITE)
}

/// Whether Aevocis is currently registered to launch at sign-in.
pub fn is_enabled() -> bool {
    open().and_then(|key| key.get_value::<String, _>(VALUE_NAME)).is_ok()
}

/// Enables or disables launch-at-sign-in. Returns the underlying registry
/// error (e.g. an unwritable Run key) rather than swallowing it, so the tray
/// menu can tell the user the toggle didn't actually take effect instead of
/// silently showing a checkbox state that lies about reality.
pub fn set_enabled(enabled: bool) -> std::io::Result<()> {
    let key = open()?;
    if enabled {
        let exe = std::env::current_exe()?;
        key.set_value(VALUE_NAME, &format!("\"{}\"", exe.display()))
    } else {
        match key.delete_value(VALUE_NAME) {
            Ok(()) => Ok(()),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(e),
        }
    }
}
