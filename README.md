# Aevocis — Rust + Slint

Aevocis is the Windows desktop Rust + Slint rewrite of OpenSuperWhisper. The
release executable is `Aevocis.exe`; the old reference implementation is not
part of this Rust release surface.

## Build

```powershell
cargo build --release
```

The SenseVoice-small int8 model is intentionally not committed to Git. Put
`model.int8.onnx`, `tokens.txt`, and its model license file under
`models\sensevoice` next to the executable, or set
`OSW_SENSEVOICE_MODEL_DIR` to a directory containing those files. The Inno
Setup script packages the model from the local reference checkout when that
checkout is present.

## Installer

Compile `installer\Aevocis.iss` with Inno Setup from this directory. The
destination page lets the user choose the parent location; the actual install
directory is always named `Aevocis`, for example `D:\Apps\Aevocis`.

The installer is per-user by default, creates Start-menu integration, and can
optionally create a desktop shortcut or auto-start entry. The app stores
settings, history, terms, voice commands, and macros under
`%LOCALAPPDATA%\Aevocis`.

## Runtime guarantees

- Push-to-talk injection is rechecked against the captured foreground window.
- The process is single-instance and the tray icon toggles the main window.
- Settings and rule stores use durable atomic replacement on Windows.
- GitHub updates are restricted to `native-rust-v*` releases, run off the UI
  thread, and require the release asset's SHA-256 digest to match.
- Recognition is local; no telemetry or recording upload is part of this
  release.

This release targets Windows. Android/tablet support and a Claude Code bridge
remain explicit follow-up choices in `APP_METRICS.md`; they are not claimed as
implemented by the Windows executable.
