# Aevocis Rust + Slint project instructions

Read `README.md`, `SPEC.md`, `TECH_ROADMAP.md`, and `APP_METRICS.md` before
substantial changes. This repository is the Windows Rust + Slint delivery
surface; the sibling C# reference tree is not a release artifact.

## Non-negotiable behavior

- Preserve existing effective dictation behavior and verify fixes from an
  end-user path before changing implementation.
- Do not add unit tests for this project. Use `cargo check`, optimized release
  builds, installer compilation, and real desktop start/stop or UI flows.
- Keep recognition local. Do not commit model weights, recordings, histories,
  settings, logs, credentials, tokens, or machine-specific absolute paths.
- Text/key injection must recheck the captured foreground target immediately
  before acting; update downloads must stay off the Slint event loop and must
  verify the release SHA-256 before launching an installer.
- The installer accepts a user-selected parent location but the final leaf
  directory must always be `Aevocis`.

## Verification

From this directory:

```powershell
cargo check
cargo build --release
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\Aevocis.iss
```

Before any public release, run the dual-track check, full Semgrep and
gitleaks scans, inspect `git diff --check`, and review the exact release
candidate manifest.
