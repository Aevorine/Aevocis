//! GitHub-release-based update checking, ported from the C# app's
//! Velopack-based update flow. This app is distributed via a plain Inno
//! Setup installer (`native-rust/installer/Aevocis.iss`) rather than
//! Velopack, so "check for update" here means "poll GitHub's latest release
//! for a newer version, and if found, silently re-run that installer" --
//! the installer is already designed to be safely re-run over an existing
//! install (see the `.iss` file's own comments on that).
//!
//! Both entry points are best-effort by design: a failed update check must
//! never be allowed to crash or block the app's normal (non-update)
//! operation. `check_latest` therefore only ever returns `None` on any
//! error path -- offline, rate-limited, malformed response, whatever.

use std::io;

const REPO_API_URL: &str = "https://api.github.com/repos/Aevorine/Aevocis/releases/latest";

#[derive(Debug, Clone)]
pub struct UpdateInfo {
    pub version: String,
    pub download_url: String,
    pub html_url: String,
}

/// Checks GitHub's latest release for `Aevorine/Aevocis` and returns
/// `Some(UpdateInfo)` if it's newer than this build's own version
/// (`env!("CARGO_PKG_VERSION")`), `None` if already current or the check
/// fails for any reason.
pub fn check_latest() -> Option<UpdateInfo> {
    let body: serde_json::Value = ureq::get(REPO_API_URL)
        .set("User-Agent", "Aevocis-UpdateChecker")
        .call()
        .ok()?
        .into_json()
        .ok()?;

    let tag_name = body.get("tag_name")?.as_str()?;
    let html_url = body.get("html_url").and_then(|v| v.as_str()).unwrap_or("").to_string();
    let version = extract_version(tag_name)?;

    let assets = body.get("assets")?.as_array()?;
    let download_url = assets.iter().find_map(|asset| {
        let name = asset.get("name")?.as_str()?;
        if name.ends_with(".exe") {
            asset.get("browser_download_url")?.as_str().map(str::to_string)
        } else {
            None
        }
    })?;

    if parse_version(&version) > parse_version(env!("CARGO_PKG_VERSION")) {
        Some(UpdateInfo { version, download_url, html_url })
    } else {
        None
    }
}

/// Downloads the installer at `info.download_url` to a temp file, then
/// spawns it with silent-install flags and immediately exits this process so
/// the running app's own file lock on itself is released before the
/// installer tries to overwrite the install directory.
pub fn download_and_relaunch(info: &UpdateInfo) -> io::Result<()> {
    let temp_path = std::env::temp_dir().join("Aevocis-Update-Setup.exe");

    let response = ureq::get(&info.download_url)
        .call()
        .map_err(|e| io::Error::other(e.to_string()))?;
    let mut reader = response.into_reader();
    let mut file = std::fs::File::create(&temp_path)?;
    // Streamed straight to disk rather than buffered in memory first --
    // these installers are ~150MB, and there's no reason to hold the whole
    // thing in RAM just to write it back out.
    std::io::copy(&mut reader, &mut file)?;
    drop(file);

    // Do not wait for the installer and do not clean up the temp file
    // ourselves: the installer will overwrite/replace this process's own
    // files, and the OS temp dir gets cleaned periodically anyway.
    let spawn_result = std::process::Command::new(&temp_path)
        .args(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"])
        .spawn();

    match spawn_result {
        Ok(_) => std::process::exit(0),
        Err(e) => Err(e),
    }
}

/// Finds the last run of `\d+\.\d+\.\d+` in `tag` via manual char scanning
/// (no regex crate needed) -- release tags look like `v1.3.5-windows` or
/// `native-rust-v0.2.0`, and only the dotted version part is wanted.
fn extract_version(tag: &str) -> Option<String> {
    let chars: Vec<char> = tag.chars().collect();
    let n = chars.len();
    let mut best: Option<(usize, usize)> = None;
    let mut i = 0;
    while i < n {
        if chars[i].is_ascii_digit() {
            let start = i;
            let mut j = i;
            while j < n && chars[j].is_ascii_digit() {
                j += 1;
            }
            if j < n && chars[j] == '.' {
                let g2_start = j + 1;
                let mut k = g2_start;
                while k < n && chars[k].is_ascii_digit() {
                    k += 1;
                }
                if k > g2_start && k < n && chars[k] == '.' {
                    let g3_start = k + 1;
                    let mut m = g3_start;
                    while m < n && chars[m].is_ascii_digit() {
                        m += 1;
                    }
                    if m > g3_start {
                        best = Some((start, m));
                        i = m;
                        continue;
                    }
                }
            }
            i = j.max(start + 1);
        } else {
            i += 1;
        }
    }
    best.map(|(s, e)| chars[s..e].iter().collect())
}

/// Parses a dotted version string into a `(major, minor, patch)` tuple for
/// simple numeric comparison. A missing or unparseable part is treated as 0
/// rather than erroring -- this is a best-effort comparison, not validation.
fn parse_version(s: &str) -> (u32, u32, u32) {
    let mut parts = s.split('.').map(|p| p.parse::<u32>().unwrap_or(0));
    (parts.next().unwrap_or(0), parts.next().unwrap_or(0), parts.next().unwrap_or(0))
}
