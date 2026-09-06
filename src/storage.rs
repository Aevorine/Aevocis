//! Shared durable file replacement for the small user-data stores.

use std::ffi::OsStr;
use std::io;
use std::iter::once;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;

use windows::Win32::Storage::FileSystem::{
    MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
};
use windows::core::PCWSTR;

fn wide(path: &Path) -> Vec<u16> {
    OsStr::new(path).encode_wide().chain(once(0)).collect()
}

/// Writes `bytes` next to `path`, then replaces `path` in one Win32 move.
pub fn atomic_write(path: &Path, bytes: &[u8]) -> io::Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }

    let file_name = path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("data");
    let tmp = path.with_file_name(format!(".{file_name}.tmp"));
    std::fs::write(&tmp, bytes)?;

    let from = wide(&tmp);
    let to = wide(path);
    let result = unsafe {
        MoveFileExW(
            PCWSTR(from.as_ptr()),
            PCWSTR(to.as_ptr()),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    };
    if let Err(error) = result {
        let io_error = io::Error::last_os_error();
        let _ = std::fs::remove_file(&tmp);
        return Err(io::Error::other(format!("{error}; {io_error}")));
    }
    Ok(())
}
