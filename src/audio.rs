//! Fixed-ceiling microphone capture, downmixed to mono and resampled to 16kHz
//! on the fly so callers always get audio in exactly the format SenseVoice
//! expects.
//!
//! ## Design note: `cpal` instead of hand-rolled WASAPI
//!
//! The C++ prototype (`native/src/main.cpp`, `FixedWaveCapture`) drives WASAPI
//! directly through the legacy `waveInOpen`/`waveInAddBuffer` API into a
//! single preallocated arena. A from-scratch Rust port of that same design
//! using `windows::Win32::Media::Audio` would mean re-deriving: IMMDevice
//! enumeration, IAudioClient/IAudioCaptureClient activation, shared-mode
//! format negotiation against whatever the default device actually supports,
//! the event-driven buffer pump loop, and per-thread COM apartment
//! initialization -- all safety- and correctness-sensitive plumbing with no
//! memory-safe abstraction over it.
//!
//! `cpal` wraps that exact same WASAPI path on Windows, is a mature and
//! widely used crate, and already gets device format negotiation and stream
//! lifecycle right. For a first vertical slice this is meaningfully simpler
//! and more robust than hand-rolling the COM plumbing, at the cost of one
//! extra dependency; the always-16kHz-mono contract this module exposes to
//! the rest of the app is unaffected by that choice, so swapping to a
//! hand-rolled WASAPI backend later (if ever justified) would not ripple
//! outward.

use std::sync::{Arc, Mutex};

use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use cpal::{SampleFormat, Stream};
use sherpa_onnx::LinearResampler;

/// The sample rate SenseVoice (and this whole pipeline) works in.
pub const SAMPLE_RATE_OUT: u32 = 16_000;

/// Fixed capture ceiling, mirroring the C++ prototype's `kMaxSamples`
/// (`kSampleRate * 120`): 120 seconds at 16kHz mono f32 = 7.32 MiB. Capture
/// simply stops accepting new samples once this many have accumulated --
/// holding the hotkey indefinitely cannot grow memory without bound.
pub const MAX_SAMPLES: usize = SAMPLE_RATE_OUT as usize * 120;

pub struct AudioCapture {
    stream: Option<Stream>,
    buffer: Arc<Mutex<Vec<f32>>>,
}

impl AudioCapture {
    pub fn new() -> Self {
        Self {
            stream: None,
            buffer: Arc::new(Mutex::new(Vec::with_capacity(MAX_SAMPLES))),
        }
    }

    /// Opens the input device named by `device_name` (matched against
    /// `Device::name()`, exactly as returned by [`list_input_device_names`])
    /// if given and found, otherwise falls back to the system default input
    /// device -- so an empty string, a stale saved name (the device was
    /// unplugged), or a name that no longer resolves all degrade gracefully
    /// to "use whatever Windows currently considers default" rather than
    /// failing the whole recording. Safe to call again after `stop()`.
    pub fn start(&mut self, device_name: Option<&str>) -> Result<(), String> {
        let host = cpal::default_host();
        let device = device_name
            .filter(|n| !n.is_empty())
            .and_then(|wanted| {
                host.input_devices().ok()?.find(|d| d.name().map(|n| n == wanted).unwrap_or(false))
            })
            .or_else(|| host.default_input_device())
            .ok_or_else(|| "no default audio input device".to_string())?;
        let supported = device
            .default_input_config()
            .map_err(|e| format!("could not query default input config: {e}"))?;

        let in_sample_rate = supported.sample_rate().0 as i32;
        let channels = supported.channels() as usize;
        let sample_format = supported.sample_format();
        let stream_config = supported.config();

        {
            let mut buf = self.buffer.lock().unwrap();
            buf.clear();
        }

        let resampler = Arc::new(Mutex::new(
            LinearResampler::create(in_sample_rate, SAMPLE_RATE_OUT as i32)
                .ok_or_else(|| "sherpa-onnx failed to create the resampler".to_string())?,
        ));

        let err_fn = |err| eprintln!("audio input stream error: {err}");

        let stream = match sample_format {
            SampleFormat::F32 => {
                let buffer = Arc::clone(&self.buffer);
                let resampler = Arc::clone(&resampler);
                device.build_input_stream(
                    &stream_config,
                    move |data: &[f32], _: &_| feed(data, channels, &buffer, &resampler),
                    err_fn,
                    None,
                )
            }
            SampleFormat::I16 => {
                let buffer = Arc::clone(&self.buffer);
                let resampler = Arc::clone(&resampler);
                device.build_input_stream(
                    &stream_config,
                    move |data: &[i16], _: &_| {
                        let floats: Vec<f32> = data.iter().map(|&s| s as f32 / 32768.0).collect();
                        feed(&floats, channels, &buffer, &resampler)
                    },
                    err_fn,
                    None,
                )
            }
            SampleFormat::U16 => {
                let buffer = Arc::clone(&self.buffer);
                let resampler = Arc::clone(&resampler);
                device.build_input_stream(
                    &stream_config,
                    move |data: &[u16], _: &_| {
                        let floats: Vec<f32> = data.iter().map(|&s| (s as f32 - 32768.0) / 32768.0).collect();
                        feed(&floats, channels, &buffer, &resampler)
                    },
                    err_fn,
                    None,
                )
            }
            other => return Err(format!("unsupported input sample format: {other:?}")),
        }
        .map_err(|e| format!("could not open input stream: {e}"))?;

        stream.play().map_err(|e| format!("could not start input stream: {e}"))?;
        self.stream = Some(stream);
        Ok(())
    }

    /// Stops capture and returns everything collected so far, already at
    /// 16kHz mono.
    pub fn stop(&mut self) -> Vec<f32> {
        if let Some(stream) = self.stream.take() {
            let _ = stream.pause();
        }
        let mut buf = self.buffer.lock().unwrap();
        std::mem::take(&mut *buf)
    }
}

/// Lists available input device names for the Settings window's microphone
/// picker. Returns an empty `Vec` (never an error) if enumeration fails --
/// the picker just shows "system default" only in that case.
pub fn list_input_device_names() -> Vec<String> {
    let host = cpal::default_host();
    host.input_devices()
        .map(|devices| devices.filter_map(|d| d.name().ok()).collect())
        .unwrap_or_default()
}

impl Default for AudioCapture {
    fn default() -> Self {
        Self::new()
    }
}

fn feed(samples: &[f32], channels: usize, buffer: &Arc<Mutex<Vec<f32>>>, resampler: &Arc<Mutex<LinearResampler>>) {
    let mono: Vec<f32> = if channels <= 1 {
        samples.to_vec()
    } else {
        samples
            .chunks(channels)
            .map(|frame| frame.iter().sum::<f32>() / channels as f32)
            .collect()
    };

    let resampled = {
        let resampler = resampler.lock().unwrap();
        resampler.resample(&mono, false)
    };

    let mut buf = buffer.lock().unwrap();
    let remaining = MAX_SAMPLES.saturating_sub(buf.len());
    let n = remaining.min(resampled.len());
    buf.extend_from_slice(&resampled[..n]);
}
