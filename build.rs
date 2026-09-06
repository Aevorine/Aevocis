//! Build script: compiles the `MainWindow` Slint component (see
//! `ui/main_window.slint`, brought into `src/main.rs` via
//! `slint::include_modules!()`) and, on Windows, embeds `assets/app.ico` as
//! the executable's icon resource so Explorer/the taskbar/Alt-Tab show it
//! even before any window exists -- the same technique the shipping C# app
//! gets for free from its `.csproj`'s `<ApplicationIcon>`.
//!
//! Also embeds an application manifest declaring the Common Controls v6
//! dependency. This is not optional decoration: `tray-icon`'s `menu` (via
//! `muda`)'s `common-controls-v6` Cargo feature statically imports a v6-only
//! export from `comctl32.dll`. Without a manifest telling the OS loader this
//! process wants v6, Windows resolves that import against the legacy v5.82
//! side-by-side assembly instead, and the process fails to start at all with
//! `STATUS_ENTRYPOINT_NOT_FOUND` -- confirmed by hitting exactly that crash
//! with the feature enabled and no manifest, then confirming removing either
//! the feature or (as done here) adding this manifest fixes it. The payoff
//! for getting this right rather than just dropping the feature: the tray
//! icon's right-click menu renders with modern Windows visual styles instead
//! of the unthemed Windows 2000-era default.
fn main() {
    // `slint::include_modules!()` in `main.rs` only ever pulls in whichever
    // `compile()` call ran LAST (it works by reading a single env var that
    // each call overwrites) -- so `MainWindow`/`HistoryEntry` must stay the
    // one plain `compile()` call, and every additional top-level window below
    // uses `compile_with_output_path` into its own named output file, manually
    // pulled in via a plain `include!(concat!(env!("OUT_DIR"), "/<name>.rs"))`
    // at the top of that window's own Rust controller module (see
    // `src/settings_window.rs`, `src/term_dictionary_window.rs`, etc.).
    slint_build::compile("ui/main_window.slint").expect("Slint build failed");

    let out_dir = std::env::var("OUT_DIR").expect("OUT_DIR not set (build.rs must run via cargo)");
    for (slint_path, out_name) in [
        ("ui/settings_window.slint", "settings_window_ui.rs"),
        ("ui/onboarding_window.slint", "onboarding_window.rs"),
        ("ui/term_dictionary_window.slint", "term_dictionary_window.rs"),
    ] {
        slint_build::compile_with_output_path(
            slint_path,
            std::path::Path::new(&out_dir).join(out_name),
            slint_build::CompilerConfiguration::default(),
        )
        .unwrap_or_else(|e| panic!("Slint build failed for {slint_path}: {e:?}"));
    }

    println!("cargo:rerun-if-changed=assets/app.ico");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        let mut res = winresource::WindowsResource::new();
        res.set_icon("assets/app.ico");
        res.set_manifest(
            r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <dependency>
    <dependentAssembly>
      <assemblyIdentity
        type="win32"
        name="Microsoft.Windows.Common-Controls"
        version="6.0.0.0"
        processorArchitecture="*"
        publicKeyToken="6595b64144ccf1df"
        language="*"
      />
    </dependentAssembly>
  </dependency>
</assembly>
"#,
        );
        res.compile().expect("failed to embed Windows .ico resource + manifest into the executable");
    }
}
