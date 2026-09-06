//! Standalone proof that the SenseVoice pipeline (`osw_native::recognizer`)
//! actually runs end-to-end, independent of the hotkey/overlay/injection
//! machinery in `main.rs` (which needs a live desktop session to exercise).
//!
//! Usage: `cargo run --example recognize_wav -- <path-to-wav>`
//! Model directory resolution is the same as the main binary's: set
//! `OSW_SENSEVOICE_MODEL_DIR`, otherwise use the packaged or checkout-relative
//! SenseVoice model directory.

use osw_native::recognizer::Recognizer;

fn main() {
    let wav_path = std::env::args()
        .nth(1)
        .expect("usage: recognize_wav <path-to-wav>");

    let model_dir = osw_native::resolve_model_dir();
    eprintln!("Loading SenseVoice model from {}", model_dir.display());
    let recognizer = Recognizer::load(&model_dir).expect("failed to load SenseVoice model");

    let wave = sherpa_onnx::Wave::read(&wav_path).unwrap_or_else(|| panic!("failed to read wav file: {wav_path}"));
    eprintln!(
        "Loaded {} samples at {} Hz from {wav_path}",
        wave.num_samples(),
        wave.sample_rate()
    );

    let text = recognizer.recognize(wave.samples(), wave.sample_rate());
    println!("RECOGNIZED: {text}");
}
