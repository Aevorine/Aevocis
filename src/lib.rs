//! Library surface for the OpenSuperWhisper native Rust rewrite's first
//! vertical slice: global push-to-talk hotkey -> target-window capture ->
//! audio capture -> SenseVoice recognition -> safety re-check -> text
//! injection, plus a minimal Slint status overlay. See `src/main.rs` for how
//! these pieces are wired together, and `native/src/main.cpp` for the
//! reference design this replicates in idiomatic Rust (not copied).

pub mod audio;
pub mod hotkey;
pub mod inject;
pub mod recognizer;
pub mod target;

use std::path::PathBuf;

/// Dev-machine fallback location of the SenseVoice-small int8 weights already
/// shipped with the WPF app (v1.2.0's "闪电引擎" / lightning engine). The
/// ~237MB `.onnx` file is intentionally not vendored into this repo. Override
/// with the `OSW_SENSEVOICE_MODEL_DIR` environment variable on any other
/// machine, or once this becomes a packaged build.
pub const DEV_DEFAULT_MODEL_DIR: &str =
    r"D:\Documents\WorkDocuments\Github\Fork\OpenSuperWhisper\src-reference\OpenSuperWhisper.App\Models\sensevoice";

/// Resolves the SenseVoice model directory: `OSW_SENSEVOICE_MODEL_DIR` env var
/// first, then the known dev-machine path if it actually has the model file,
/// then a relative `models/sensevoice` for a future packaged layout.
pub fn resolve_model_dir() -> PathBuf {
    if let Ok(dir) = std::env::var("OSW_SENSEVOICE_MODEL_DIR") {
        return PathBuf::from(dir);
    }
    let dev_default = PathBuf::from(DEV_DEFAULT_MODEL_DIR);
    if dev_default.join("model.int8.onnx").is_file() {
        return dev_default;
    }
    PathBuf::from("models/sensevoice")
}
