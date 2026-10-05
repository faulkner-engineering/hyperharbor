fn main() {
    // tauri-build embeds icons/icon.ico in the executable and generate_context! embeds the window icon, but
    // neither tells Cargo to watch the icons, so an incremental build kept the old ones after `tauri icon`.
    println!("cargo:rerun-if-changed=icons");
    tauri_build::build()
}
