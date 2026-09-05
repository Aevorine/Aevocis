//! Thin wrapper around sherpa-onnx's offline recognizer, configured for the
//! SenseVoice-small int8 model.
//!
//! SenseVoice is a non-autoregressive CTC model: a single `decode()` call
//! yields the full utterance directly (no streaming/partial-result API, and
//! none is needed -- matches the shipping C# engine's behavior in
//! `src-reference/OpenSuperWhisper.Recognition/SenseVoiceTranscriptionEngine.cs`,
//! which this Rust port targets API-parity with, without copying its code).

use std::path::Path;

use sherpa_onnx::{OfflineRecognizer, OfflineRecognizerConfig, OfflineSenseVoiceModelConfig};

pub struct Recognizer {
    inner: OfflineRecognizer,
}

impl Recognizer {
    /// Loads the recognizer from a directory containing `model.int8.onnx` and
    /// `tokens.txt` (the same directory layout the shipping WPF app uses).
    pub fn load(model_dir: &Path) -> Result<Self, String> {
        let model_file = model_dir.join("model.int8.onnx");
        let tokens_file = model_dir.join("tokens.txt");
        if !model_file.is_file() {
            return Err(format!("SenseVoice model not found at {}", model_file.display()));
        }
        if !tokens_file.is_file() {
            return Err(format!("SenseVoice tokens not found at {}", tokens_file.display()));
        }

        let mut config = OfflineRecognizerConfig::default();
        config.model_config.sense_voice = OfflineSenseVoiceModelConfig {
            model: Some(model_file.to_string_lossy().into_owned()),
            language: Some("auto".into()),
            use_itn: true,
        };
        config.model_config.tokens = Some(tokens_file.to_string_lossy().into_owned());
        config.model_config.num_threads = std::thread::available_parallelism()
            .map(|n| n.get().min(4) as i32)
            .unwrap_or(2);

        let inner = OfflineRecognizer::create(&config)
            .ok_or_else(|| "sherpa-onnx failed to create the SenseVoice recognizer".to_string())?;
        Ok(Self { inner })
    }

    /// Runs one full-utterance decode. `samples` need not already be at
    /// SenseVoice's native rate -- sherpa-onnx resamples internally against
    /// `sample_rate` if it differs.
    pub fn recognize(&self, samples: &[f32], sample_rate: i32) -> String {
        let stream = self.inner.create_stream();
        stream.accept_waveform(sample_rate, samples);
        self.inner.decode(&stream);
        stream.get_result().map(|r| r.text).unwrap_or_default()
    }
}
