//! The Windows side of camera setup: the machine-changing steps, the registry check, and running an
//! elevated copy of this program.
#![allow(unsafe_code)]

use super::setup::{self, SetupCommand, SetupSystem, DLL_NAME};
use hyperharbor_vcam_protocol::SOURCE_CLSID_STRING;
use std::ffi::c_void;
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, Instant};
use windows::core::{w, HSTRING, PCWSTR};
use windows::Win32::Foundation::{CloseHandle, ERROR_CANCELLED};
use windows::Win32::System::Registry::{RegGetValueW, HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ};
use windows::Win32::System::Threading::WaitForSingleObject;
use windows::Win32::UI::Shell::{ShellExecuteExW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW};
use windows::Win32::UI::WindowsAndMessaging::SW_HIDE;

const CREATE_NO_WINDOW: u32 = 0x0800_0000;
/// How long to wait for the elevated copy: setup includes stopping and restarting a service.
const SETUP_TIMEOUT_MS: u32 = 120_000;

/// The path the class is registered with, if it is registered.
pub fn registered_server_path() -> Option<PathBuf> {
    let subkey = HSTRING::from(format!(
        r"Software\Classes\CLSID\{SOURCE_CLSID_STRING}\InprocServer32"
    ));
    let mut size = 0u32;
    // SAFETY: the first call asks for the size of the value; the second reads it into a buffer of
    // that size. Both pass valid pointers for the duration of the call.
    unsafe {
        let status = RegGetValueW(
            HKEY_LOCAL_MACHINE,
            PCWSTR(subkey.as_ptr()),
            PCWSTR::null(),
            RRF_RT_REG_SZ,
            None,
            None,
            Some(&mut size),
        );
        if status.0 != 0 || size < 2 {
            return None;
        }
        let mut buffer = vec![0u16; (size as usize).div_ceil(2)];
        let status = RegGetValueW(
            HKEY_LOCAL_MACHINE,
            PCWSTR(subkey.as_ptr()),
            PCWSTR::null(),
            RRF_RT_REG_SZ,
            None,
            Some(buffer.as_mut_ptr().cast::<c_void>()),
            Some(&mut size),
        );
        if status.0 != 0 {
            return None;
        }
        let length = buffer.iter().position(|&c| c == 0).unwrap_or(buffer.len());
        Some(PathBuf::from(String::from_utf16_lossy(&buffer[..length])))
    }
}

/// True when the camera source is registered and its DLL is where it says.
pub fn source_installed() -> bool {
    registered_server_path().is_some_and(|path| path.is_file())
}

fn program_files() -> PathBuf {
    std::env::var_os("ProgramFiles")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from(r"C:\Program Files"))
}

fn run(program: &str, args: &[&str]) -> Result<std::process::Output, String> {
    Command::new(program)
        .args(args)
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .map_err(|e| format!("{program} could not be started: {e}"))
}

pub struct WindowsSetup;

impl SetupSystem for WindowsSetup {
    fn stop_frame_server(&self) -> Result<(), String> {
        // Stopping a service that is not running fails with 1062, which is what is wanted.
        let _ = run("sc.exe", &["stop", "FrameServer"]);
        let deadline = Instant::now() + Duration::from_secs(15);
        while Instant::now() < deadline {
            let output = run("sc.exe", &["query", "FrameServer"])?;
            let text = String::from_utf8_lossy(&output.stdout);
            if text.contains("STOPPED") || !output.status.success() {
                return Ok(());
            }
            std::thread::sleep(Duration::from_millis(300));
        }
        Err("The Windows camera service did not stop.".to_string())
    }

    fn exists(&self, path: &Path) -> bool {
        path.is_file()
    }

    fn create_dir(&self, dir: &Path) -> Result<(), String> {
        std::fs::create_dir_all(dir)
            .map_err(|e| format!("{} could not be created: {e}", dir.display()))
    }

    fn copy(&self, from: &Path, to: &Path) -> Result<(), String> {
        let mut last = String::new();
        for _ in 0..10 {
            match std::fs::copy(from, to) {
                Ok(_) => return Ok(()),
                Err(e) => last = e.to_string(),
            }
            std::thread::sleep(Duration::from_millis(500));
        }
        Err(format!("The camera source could not be copied: {last}"))
    }

    fn register(&self, dll: &Path) -> Result<(), String> {
        regsvr32(&["/s", &dll.display().to_string()])
    }

    fn unregister(&self, dll: &Path) -> Result<(), String> {
        regsvr32(&["/u", "/s", &dll.display().to_string()])
    }

    fn registered_path(&self) -> Option<PathBuf> {
        registered_server_path()
    }

    fn remove_dir(&self, dir: &Path) -> Result<(), String> {
        match std::fs::remove_dir_all(dir) {
            Ok(()) => Ok(()),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(format!("{} could not be removed: {e}", dir.display())),
        }
    }
}

fn regsvr32(args: &[&str]) -> Result<(), String> {
    let output = run("regsvr32.exe", args)?;
    if output.status.success() {
        Ok(())
    } else {
        Err(format!(
            "regsvr32 failed with exit code {:?}.",
            output.status.code()
        ))
    }
}

/// Installs `source` only if it is the DLL that ships beside this program.
fn install_trusted(source: &Path, dir: &Path) -> Result<(), String> {
    let exe_dir = std::env::current_exe()
        .and_then(|exe| std::fs::canonicalize(exe.parent().unwrap_or(Path::new("."))))
        .map_err(|e| format!("The program's folder could not be read: {e}"))?;
    let source = std::fs::canonicalize(source)
        .map_err(|_| format!("The camera source was not found at {}.", source.display()))?;
    if !setup::is_trusted_source(&source, &exe_dir) {
        return Err(
            "Only the camera source that ships with HyperHarbor can be installed.".to_string(),
        );
    }
    setup::install(&WindowsSetup, &source, dir)
}

/// Runs a setup command in this (elevated) process and writes the outcome to its result file.
/// Returns the process exit code.
pub fn run_helper(command: &SetupCommand) -> i32 {
    let dir = setup::install_dir(&program_files());
    let (outcome, result) = match command {
        SetupCommand::Install { source, result } => (install_trusted(source, &dir), result),
        SetupCommand::Remove { result } => (setup::remove(&WindowsSetup, &dir), result),
    };
    setup::write_result(result, &outcome);
    i32::from(outcome.is_err())
}

/// Runs this program again with administrator rights (the user answers a UAC prompt) and returns
/// what it reported. `result` is the file the elevated copy writes.
pub fn run_elevated(args: &[String], result: &Path) -> Result<(), String> {
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
            let wait = WaitForSingleObject(info.hProcess, SETUP_TIMEOUT_MS);
            let _ = CloseHandle(info.hProcess);
            if wait.0 != 0 {
                return Err("Setup did not finish in time.".to_string());
            }
        }
    }
    setup::read_result(result)
}

/// The command for the elevated copy to install the DLL at `source`.
pub fn install_args(source: &Path, result: &Path) -> Vec<String> {
    vec![
        "--setup-camera".to_string(),
        source.display().to_string(),
        result.display().to_string(),
    ]
}

/// The path of the DLL shipped next to the application (a resource of the installed program).
pub fn bundled_dll(resource_dir: &Path) -> PathBuf {
    resource_dir.join(DLL_NAME)
}
