//! Runs a copy of this program with administrator rights for one job and reads what it reported.
//! The elevated copy is started with a flag (`--setup-camera`, `--trust-rdp-signer`, and so on),
//! does that job, writes its outcome to a result file, and exits: an elevated process cannot return
//! output to its caller any other way.
#![allow(unsafe_code)]

use std::path::Path;
use windows::core::{w, HSTRING, PCWSTR};
use windows::Win32::Foundation::{CloseHandle, ERROR_CANCELLED};
use windows::Win32::System::Threading::WaitForSingleObject;
use windows::Win32::UI::Shell::{ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW};
use windows::Win32::UI::WindowsAndMessaging::SW_HIDE;

/// How long to wait for the elevated copy: camera setup includes stopping and restarting a service.
const TIMEOUT_MS: u32 = 120_000;

/// A result file in the temporary folder, named for this process and `job`.
pub fn result_file(job: &str) -> std::path::PathBuf {
    std::env::temp_dir().join(format!("hyperharbor-{job}-{}.txt", std::process::id()))
}

/// Runs this program again with administrator rights (the user answers a UAC prompt) and returns
/// what it reported. `result` is the file the elevated copy writes.
pub fn run(args: &[String], result: &Path) -> Result<(), String> {
    let _ = std::fs::remove_file(result);
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let parameters = args
        .iter()
        .map(|arg| format!("\"{arg}\""))
        .collect::<Vec<_>>()
        .join(" ");
    let (file, parameters) = (HSTRING::from(exe.as_os_str()), HSTRING::from(parameters));
    let mut info = SHELLEXECUTEINFOW {
        cbSize: std::mem::size_of::<SHELLEXECUTEINFOW>() as u32,
        fMask: SEE_MASK_NOCLOSEPROCESS,
        lpVerb: w!("runas"),
        lpFile: PCWSTR(file.as_ptr()),
        lpParameters: PCWSTR(parameters.as_ptr()),
        nShow: SW_HIDE.0,
        ..Default::default()
    };
    // SAFETY: `info` is fully initialised and its strings outlive the call; the process handle it
    // returns is waited on and closed once.
    unsafe {
        if let Err(error) = ShellExecuteExW(&mut info) {
            return Err(if error.code() == ERROR_CANCELLED.to_hresult() {
                "Setup was cancelled.".to_string()
            } else {
                format!("Setup could not be started: {}", error.message().trim())
            });
        }
        if !info.hProcess.is_invalid() {
            let wait = WaitForSingleObject(info.hProcess, TIMEOUT_MS);
            let _ = CloseHandle(info.hProcess);
            if wait.0 != 0 {
                return Err("Setup did not finish in time.".to_string());
            }
        }
    }
    let outcome = read_result(result);
    let _ = std::fs::remove_file(result);
    outcome
}

/// Writes the outcome where the caller (not elevated) reads it.
pub fn write_result(path: &Path, outcome: &Result<(), String>) {
    let text = match outcome {
        Ok(()) => "ok".to_string(),
        Err(message) => format!("error: {message}"),
    };
    let _ = std::fs::write(path, text);
}

/// The outcome the elevated copy wrote. A missing file means it never ran to the end.
pub fn read_result(path: &Path) -> Result<(), String> {
    match std::fs::read_to_string(path) {
        Ok(text) if text.trim() == "ok" => Ok(()),
        Ok(text) => Err(text.trim().trim_start_matches("error: ").to_string()),
        Err(_) => Err("Setup did not finish.".to_string()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_outcome_round_trips_through_the_result_file() {
        let dir = std::env::temp_dir().join(format!("hh-elevated-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let ok = dir.join("ok.txt");
        write_result(&ok, &Ok(()));
        assert_eq!(read_result(&ok), Ok(()));
        let failed = dir.join("failed.txt");
        write_result(&failed, &Err("copy failed".into()));
        assert_eq!(read_result(&failed), Err("copy failed".to_string()));
        assert!(read_result(&dir.join("missing.txt"))
            .unwrap_err()
            .contains("did not finish"));
        let _ = std::fs::remove_dir_all(dir);
    }
}
