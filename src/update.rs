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

use std::io::{self, Read};
use std::time::Duration;

use sha2::{Digest, Sha256};

/// The list endpoint, NOT `/releases/latest` -- `Aevorine/Aevocis` hosts TWO
/// independent release lines in one repo (the shipping C# app, tagged
/// `vX.Y.Z-windows` and flagged GitHub's own "Latest", plus this native
/// Rust rewrite, tagged `native-rust-vX.Y.Z` and deliberately published as a
/// prerelease so it doesn't steal that "Latest" flag). `/releases/latest`
/// ignores prereleases and would therefore hand this app the C#
/// (Velopack-built) installer -- confirmed for real during this feature's
/// own verification pass, where an earlier version of this function returned
/// `v1.3.5-windows`'s Velopack `.exe` as an "update" for this 0.2.0 Rust
/// build. Silently running that installer would be wrong twice over: wrong
/// binary format (Velopack, not this app's Inno Setup installer) and wrong
/// product line entirely. This function must only ever consider releases
/// whose tag starts with `native-rust-v`.
const REPO_RELEASES_URL: &str = "https://api.github.com/repos/Aevorine/Aevocis/releases";
const TAG_PREFIX: &str = "native-rust-v";

#[derive(Debug, Clone)]
pub struct UpdateInfo {
    pub version: String,
    pub download_url: String,
    pub html_url: String,
    pub sha256: String,
}

/// Checks GitHub's releases for `Aevorine/Aevocis`, considering ONLY
/// non-draft releases tagged `native-rust-v*` (see [`TAG_PREFIX`]'s doc
/// comment for why), and returns `Some(UpdateInfo)` for the newest one if
/// it's newer than this build's own version (`env!("CARGO_PKG_VERSION")`),
/// `None` if already current, no such release exists yet, or the check
/// fails for any reason (offline, rate-limited, malformed response).
pub fn check_latest() -> Option<UpdateInfo> {
    let releases: Vec<serde_json::Value> =
        ureq::get(REPO_RELEASES_URL).set("User-Agent", "Aevocis-UpdateChecker").timeout(Duration::from_secs(15)).call().ok()?.into_json().ok()?;

    let mut best: Option<((u32, u32, u32), UpdateInfo)> = None;
    for release in &releases {
        if release.get("draft").and_then(|v| v.as_bool()).unwrap_or(false) {
            continue;
        }
        let Some(tag_name) = release.get("tag_name").and_then(|v| v.as_str()) else { continue };
        if !tag_name.starts_with(TAG_PREFIX) {
            continue;
        }
        let Some(version) = extract_version(tag_name) else { continue };
        let parsed = parse_version(&version);
        if best.as_ref().is_some_and(|(v, _)| *v >= parsed) {
            continue;
        }
        let html_url = release.get("html_url").and_then(|v| v.as_str()).unwrap_or("").to_string();
        let Some(assets) = release.get("assets").and_then(|v| v.as_array()) else { continue };
        let Some((download_url, sha256)) = assets.iter().find_map(|asset| {
            let name = asset.get("name")?.as_str()?;
            if name.to_ascii_lowercase().ends_with(".exe") {
                let url = asset.get("browser_download_url")?.as_str()?.to_string();
                let digest = asset.get("digest")?.as_str()?.strip_prefix("sha256:")?.to_string();
                if is_sha256(&digest) { Some((url, digest)) } else { None }
            } else {
                None
            }
        }) else {
            continue;
        };
        best = Some((parsed, UpdateInfo { version, download_url, html_url, sha256 }));
    }

    let (parsed, info) = best?;
    if parsed > parse_version(env!("CARGO_PKG_VERSION")) { Some(info) } else { None }
}

/// Downloads the installer at `info.download_url` to a temp file, then
/// spawns it with silent-install flags and immediately exits this process so
/// the running app's own file lock on itself is released before the
/// installer tries to overwrite the install directory.
pub fn download_and_relaunch(info: &UpdateInfo) -> io::Result<()> {
    let temp_path = std::env::temp_dir().join(format!("Aevocis-Update-Setup-{}.exe", info.sha256));

    let response = ureq::get(&info.download_url)
        .set("User-Agent", "Aevocis-Updater")
        .timeout(Duration::from_secs(900))
        .call()
        .map_err(|e| io::Error::other(e.to_string()))?;
    let mut reader = response.into_reader();
    let mut file = std::fs::File::create(&temp_path)?;
    // Streamed straight to disk rather than buffered in memory first --
    // these installers are ~150MB, and there's no reason to hold the whole
    // thing in RAM just to write it back out.
    std::io::copy(&mut reader, &mut file)?;
    drop(file);

    let actual_sha256 = sha256_file(&temp_path)?;
    if !actual_sha256.eq_ignore_ascii_case(&info.sha256) {
        let _ = std::fs::remove_file(&temp_path);
        return Err(io::Error::new(io::ErrorKind::InvalidData, "更新包 SHA-256 校验失败"));
    }

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

fn is_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn sha256_file(path: &std::path::Path) -> io::Result<String> {
    let mut file = std::fs::File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buffer = [0u8; 64 * 1024];
    loop {
        let count = file.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        hasher.update(&buffer[..count]);
    }
    Ok(hasher.finalize().iter().map(|byte| format!("{byte:02x}")).collect())
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
