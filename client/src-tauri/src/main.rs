// Prevents an additional console window on Windows in release builds.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    // An elevated copy started by "Set up camera sharing" or "Trust HyperHarbor connections" does
    // that one job and exits.
    if let Some(code) = hyperharbor_client_lib::run_camera_setup_helper() {
        std::process::exit(code);
    }
    if let Some(code) = hyperharbor_client_lib::run_rdp_trust_helper() {
        std::process::exit(code);
    }
    hyperharbor_client_lib::run()
}
