# TECH_ROADMAP — native-rust feature-parity push (2026-09-06)

## Why this work happened
User asked whether the C#→Rust+Slint migration was complete. Verified against the actual repo
(not memory): it was not — see `SPEC.md` for the full gap analysis against `src-reference/` (5745
lines of C# + 6 XAML windows vs. native-rust's ~1244-line core loop).

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

## Verification results (filled in as each piece lands — see git log for real commit-by-commit
evidence; do not trust this file over `git log`/a real `cargo build` if they ever disagree)
- `cargo check --lib` on master: clean after settings.rs, theme fix, audio/history extensions,
  app_info.rs, resource_usage.rs, hotkey.rs rewrite, hotkey_capture.rs, settings_window.rs +
  settings_window.slint (multi-window build.rs wiring confirmed working).
- Parallel worktree agents (term-dict+punctuation, voice commands+macros, draft-confirm,
  onboarding, crash-reporter+priority+update) — status tracked via their own commits on
  `feature/*` branches; merged into master one at a time by the orchestrator with a real rebuild
  after each merge (matching this project's established worktree-merge discipline from the C#
  rewrite wave), not blind `git merge`.
- Full end-to-end smoke test (real launch, real dictation) — pending until integration (main.rs
  rewrite wiring all modules together) is complete.
