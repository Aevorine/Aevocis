//! Regenerates `assets/app.ico` (multi-resolution Windows icon, for the exe
//! resource + taskbar/title bar) and `assets/app.png` (a single 256x256 PNG,
//! referenced by `ui/main_window.slint`'s `icon` property) from the same
//! source artwork the shipping C# app uses.
//!
//! This mirrors how `src-reference/OpenSuperWhisper.App/app.ico` was produced
//! (multi-res ICO with PNG-encoded frames), just via the `image` crate instead
//! of whatever tool built that one -- not a code port, same end result.
//!
//! Usage: `cargo run --example make_icon -- <source-png>`. Only needs re-running
//! if the source artwork changes; the generated files are committed like any
//! other asset.

use std::fs::File;

use image::codecs::ico::{IcoEncoder, IcoFrame};
use image::{ExtendedColorType, imageops::FilterType};

/// Same source artwork the WPF app's `app.ico` was generated from.
/// Standard Windows icon sizes: taskbar/Alt-Tab commonly want 16/32/48, the
/// shell and high-DPI taskbars want up to 256.
const ICO_SIZES: [u32; 7] = [16, 24, 32, 48, 64, 128, 256];

/// Size of the standalone PNG used by the Slint UI's `icon` property.
const UI_ICON_SIZE: u32 = 256;

fn main() {
    let source_png = std::env::args().nth(1).expect("usage: cargo run --example make_icon -- <source-png>");
    let src = image::open(&source_png)
        .unwrap_or_else(|e| panic!("failed to open source artwork at {source_png}: {e}"))
        .to_rgba8();
    println!("Loaded source artwork: {}x{}", src.width(), src.height());

    // `IcoFrame::as_png` takes the *raw* RGBA8 pixel buffer and PNG-encodes it
    // internally (despite the name, `buf` is not already-encoded PNG bytes --
    // confirmed by its own length assertion: it wants `width*height*4` raw
    // bytes, not a compressed byte count).
    let mut frames = Vec::with_capacity(ICO_SIZES.len());
    for &size in &ICO_SIZES {
        let resized = image::imageops::resize(&src, size, size, FilterType::Lanczos3);
        frames.push(
            IcoFrame::as_png(resized.as_raw(), size, size, ExtendedColorType::Rgba8)
                .unwrap_or_else(|e| panic!("failed to build ICO frame for {size}x{size}: {e}")),
        );
    }

    let ico_path = "assets/app.ico";
    let ico_file = File::create(ico_path).unwrap_or_else(|e| panic!("failed to create {ico_path}: {e}"));
    IcoEncoder::new(ico_file)
        .encode_images(&frames)
        .unwrap_or_else(|e| panic!("failed to write {ico_path}: {e}"));
    println!("Wrote {ico_path} ({} resolutions)", ICO_SIZES.len());

    let ui_icon = image::imageops::resize(&src, UI_ICON_SIZE, UI_ICON_SIZE, FilterType::Lanczos3);
    let png_path = "assets/app.png";
    ui_icon
        .save(png_path)
        .unwrap_or_else(|e| panic!("failed to write {png_path}: {e}"));
    println!("Wrote {png_path} ({UI_ICON_SIZE}x{UI_ICON_SIZE})");
}
