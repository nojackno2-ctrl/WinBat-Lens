//! Embeds the application icon and version resource into the EXE.
//!
//! The version shown in Explorer, read back by the installer and compared by
//! the single-instance handover all come from Cargo.toml through here.

fn main() {
    println!("cargo:rerun-if-changed=app_icon.ico");
    println!("cargo:rerun-if-changed=build.rs");

    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() != Ok("windows") {
        return;
    }

    let mut resource = winresource::WindowsResource::new();
    resource
        .set_icon("app_icon.ico")
        .set("ProductName", "WinBat Lens")
        .set("FileDescription", "WinBat Lens")
        .set("CompanyName", "WinBat Lens Team")
        .set("LegalCopyright", "WinBat Lens Team")
        .set("OriginalFilename", "WinBatLens.exe")
        .set("InternalName", "WinBatLens");

    if let Err(error) = resource.compile() {
        panic!("failed to embed the Windows resource: {error}");
    }
}
