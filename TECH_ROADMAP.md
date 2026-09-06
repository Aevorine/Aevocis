# TECH_ROADMAP — native-rust Rust + Slint Windows delivery (2026-09-06)

## Why this work happened
User asked whether the C#→Rust+Slint migration was complete. Verified against the actual repo,
then repaired the Rust+Slint delivery path. The old C# implementation remains a reference only;
the Windows release surface is `native-rust` and its Aevocis installer.

## Architecture decisions this round

**Multi-window Slint build strategy.** `slint::include_modules!()` (used by `main.rs` for
`MainWindow`) can only ever target the single `.slint` file `build.rs` compiled *last* via plain
`slint_build::compile()`. Every additional window (Settings, Term Dictionary, Draft Confirm,
Onboarding) therefore uses `slint_build::compile_with_output_path()` into its own named `OUT_DIR`
file, manually pulled in via `include!(concat!(env!("OUT_DIR"), "/<name>.rs"))` at the top of that
window's own Rust controller module. Verified against the actual vendored `slint-build 1.17.1`
source (`compile()`'s doc comment + implementation), not assumed from memory.

**Palette fix.** `ui/main_window.slint` shipped with a placeholder gold accent (`#b08d57`) that was
never actually the confirmed "纸感 Paper" theme — the real values (`Theme.Light.xaml` in the C#
reference) use a muted dark-green accent (`#4D6A5A`). Extracted into `ui/theme.slint` as a shared
`global Theme` singleton every window imports, so the palette can only drift by editing one file.

**Hotkey subsystem redesign (`hotkey.rs`).** The original design hardcoded `VK_RCONTROL` matching
inside the `WH_KEYBOARD_LL` hook procedure itself. Supporting a user-configurable push-to-talk key,
a per-foreground-app override, Hold-vs-Toggle mode, and Settings' hotkey-rebind capture UI all
require deciding "is this key currently the hotkey?" using live settings state — which the hook
proc (a plain `extern "system" fn`, no captured state) cannot hold. Redesigned `hotkey.rs` to report
*every* non-injected key event's raw VK code to the caller; all matching/debounce/mode logic now
lives in `main.rs`, which already owns the settings-aware thread-local `STATE`.

**No `regex` crate added.** The C# term-dictionary/voice-command matchers use `.NET Regex` with
negative lookaround, which Rust's `regex` crate does not support. Ported by hand via direct
char-boundary scanning instead of pulling in a heavier regex engine (`fancy-regex`) for one feature.

**Explicit descopes** (architecture-moot, not oversights): Whisper multi-model download/switching,
GPU acceleration (Whisper-only in the C# app), per-app Whisper-prompt biasing, and progressive
partial-transcript streaming. Full reasoning in `SPEC.md`.

**Export/import settings uses a fixed Desktop path, not a native file dialog.** No file-dialog crate
(e.g. `rfd`) was added given this round's dependency-risk budget; `%USERPROFILE%\Desktop\Aevocis-
settings-export.json` is used directly. Documented here rather than silently shipped as if it were
a full dialog — a real Open/Save dialog is a reasonable follow-up, not a correctness issue.

## Verification results

- `cargo check` passed after the compile-fix, durable-storage, target-safety, single-instance,
  update-thread, and SHA-256 changes.
- `cargo build --release` passed and produced `target/release/osw_native.exe` with file version
  `0.2.1`; the linker emitted the existing mixed-CRT `LNK4098` warning, but the executable started
  and remained responsive. This warning is retained as a follow-up hardening item rather than
  being hidden.
- Inno Setup 6.7.3 compiled `dist/Aevocis-Setup-0.2.1.exe` successfully. An isolated silent
  install created `Aevocis/Aevocis.exe`, the SenseVoice model, tokens, model license, and uninstaller;
  the installed EXE loaded the model from its exe-relative path and remained responsive.
- End-user launch smoke test passed: the release process loaded SenseVoice, armed both global
  hotkeys, exposed the `Aevocis` window, and remained responsive. A second launch exited while the
  first PID remained the only instance.
- Dual-track verifier: silent failures 0; actual damage 0. Semgrep full tracked-source scan:
  0 findings / 0 blocking. Gitleaks full-history scan: 0 leaks.
- The exact 45-file candidate manifest was reviewed before egress. The generic Harness privacy
  gate reports 10 literal matches for legitimate technical terms such as `TargetToken`,
  `tokens.txt`, and the installer manifest's `publicKeyToken`; it found no credential-shaped
  value. The result is retained as a gate false-positive, not relabeled as a PASS.
- `ensure-project-harness` was incompatible with the nested hidden `.git` directory during marker
  detection; the local control plane was created manually and Harness Doctor now reports all four
  required documents PASS.

## Known boundaries and follow-up choices

Android/tablet UI and a Claude Code JSON CLI bridge are not falsely claimed by this Windows EXE.
They remain explicit choices M39/M40/M42 in `APP_METRICS.md`. The same file contains 44 measurable
feature/performance/security candidates; only the P0 Windows delivery line is implemented here.
