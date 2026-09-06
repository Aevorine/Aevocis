# SPEC — native-rust full feature-parity push (2026-09-06)

User instruction: "C# → Rust+Slint migration — is it done? If not, finish it completely, in one pass."
Answer: NOT done as of session start. Core loop only (~1244 lines): hotkey, capture, SenseVoice
recognize, inject, tray, history, autostart, show/hide hotkey. Missing ~14 feature groups present
in the C# reference (src-reference/, ~5745 lines + 6 XAML windows).

## Ground truth (from 5 parallel research forks reading src-reference/ directly, 2026-09-06)
Condensed specs live only in this session's transcript; the essential facts are inlined into each
task below so this file alone is sufficient to resume.

## Explicit descopes (architecture-moot, not oversights — report to user, don't hide)
- **F01 model switching / F16 GPU accel**: native-rust is deliberately SenseVoice-only (committed
  2026-09-06 decision, see project memory). C# still has live dual-engine (SenseVoice+Whisper) with
  GPU accel being Whisper-only. Porting a second engine would contradict the simplification that was
  the whole point of the rewrite. NOT building.
- **F06 per-app prompt bias**: sherpa-onnx's `OfflineSenseVoiceModelConfig` (already used in
  `recognizer.rs`) only exposes `model/language/use_itn` — no prompt/hotwords biasing surface for
  this model type. Term-dictionary post-correction (F02, implemented) covers the same practical need.
- **F17 progressive transcript streaming**: SenseVoice is a non-streaming CTC decoder (one-shot
  `decode()`), no partial-result API exists to port.
- **F03 mixed-language prompt priming**: moot — SenseVoice's `language: "auto"` already does native
  language ID across mixed CN/EN in one decode, no Whisper-style prompt priming needed.
- **F19 fast startup**: structurally already solved — Rust is ahead-of-time native code, no JIT/ReadyToRun
  step exists to add.
- **F15 model resident in memory**: already true (`Arc<Recognizer>` loaded once in `main()`, held for
  process lifetime).
- Light/dark OS theme following: native-rust ships light-only "纸感 Paper" by design; not requested,
  not implemented this round.

## Palette correction (real bug found this session)
Current `ui/main_window.slint` uses `#f6f3ec`/`#b08d57`(gold)/`#2b2620`/`#8a8272` — close but NOT the
actual confirmed theme. Real values from `src-reference/OpenSuperWhisper.App/Assets/Theme.Light.xaml`:
Background `#FAF7F0`, Header `#EFE9DC`, Surface `#FFFFFF`, Border `#E6DED0`, Ink `#2C2924`,
Muted `#7A7263`, **Accent `#4D6A5A`** (muted dark green — NOT gold), AccentForeground `#FAF7F0`.
Fix in `theme.rs` shared constants + apply everywhere (main window + every new window).

## Task DAG
- [x] A (blocking, done by orchestrator): `src/settings.rs` (AppSettings, atomic load/save, export/
      import) + `src/theme.rs` (palette constants) + fix `ui/main_window.slint` colors.
- [ ] B (worktree agent): `src/term_dictionary.rs` (matching engine + terms.json store) +
      `src/punctuation.rs` + `ui/term_dictionary_window.slint` + `src/term_dictionary_window.rs` glue.
- [ ] C (worktree agent): `src/voice.rs` — VoiceCommand+VoiceMacro models, matcher, executor, stores
      (voice_commands.json / macros.json), TriggerTextNormalizer. Pure logic, no dedicated UI (edited
      via textboxes in settings window, like the C# app).
- [ ] D (worktree agent): `src/draft_confirm.rs` + `ui/draft_confirm_window.slint`.
- [ ] E (worktree agent): `src/onboarding.rs` + `ui/onboarding_window.slint`.
- [ ] F (worktree agent): `src/crash_reporter.rs` (panic hook + rotation) + `src/priority.rs`
      (SetPriorityClass helpers) + `src/update.rs` (GitHub release check + silent-installer relaunch).
- [ ] G (orchestrator): `src/settings_window.rs` + `ui/settings_window.slint` — the big cross-cutting
      UI (~20 controls), depends on A/B/C's public interfaces.
- [ ] H (orchestrator): main.rs integration — hotkey.rs rework for configurable VK + per-app override
      + Hold/Toggle mode; full post-processing pipeline order in on_hotkey_up (voice command match →
      voice macro match → term-dict → punctuation → draft-confirm gate → inject → history); waveform
      level meter in Overlay; process priority calls; crash reporter init; update check + tray item;
      history retention purge + clear button; onboarding trigger; settings/term-dict window open
      wiring; single-instance mutex; tray menu additions.
- [ ] I: full workspace build + real launch smoke test + update TECH_ROADMAP.md verification section.

## Pipeline order (from C# DictationController.cs, ground truth for H)
1. stop capture, check min length (already exists)
2. voice command match on RAW text → if matched, handle + return (skip everything below)
3. voice macro match on RAW text → if matched, execute + return (no history)
4. term-dictionary correction (unconditional if any corrections stored)
5. punctuation fix (gated by `autocorrect_punctuation` setting)
6. draft-confirm gate (gated by `show_draft_before_inject`) — null/cancel aborts, no inject/history
7. inject, then history append

## Storage paths (unify under Aevocis, NOT the C# app's inconsistent OpenSuperWhisper/Aevocis split —
that split was itself flagged as a bug by the research fork, don't replicate it)
All under `%LOCALAPPDATA%\Aevocis\`: settings.json, terms.json, voice_commands.json, macros.json,
history.json (existing), crash-reports\*.txt.
