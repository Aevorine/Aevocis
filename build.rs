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
    slint_build::compile("ui/main_window.slint").expect("Slint build failed");

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
