//! Library surface for the OpenSuperWhisper native Rust rewrite's first
//! vertical slice: global push-to-talk hotkey -> target-window capture ->
//! audio capture -> SenseVoice recognition -> safety re-check -> text
//! injection, plus a minimal Slint status overlay. See `src/main.rs` for how
//! these pieces are wired together, and `native/src/main.cpp` for the
//! reference design this replicates in idiomatic Rust (not copied).

pub mod inject;
pub mod target;
