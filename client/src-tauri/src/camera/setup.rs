//! Installs and removes the camera source DLL. Registering a COM class for the Windows Frame Server
//! (a service) needs administrator rights and a DLL its LocalService account can read, which a
//! per-user install folder is not. So the client runs a copy of itself elevated:
//! `hyperharbor-client.exe --setup-camera <dll> <result file>` copies the DLL under Program Files
//! and registers it; `--remove-camera <result file>` undoes that. The elevated copy reports back
//! through the result file, because an elevated process cannot return output to its caller.

use std::path::{Path, PathBuf};

pub const DLL_NAME: &str = "hyperharbor_vcam.dll";
const INSTALL_FOLDER: &str = "HyperHarbor Camera";

/// The folder the DLL is installed to: under Program Files, which every account can read.
pub fn install_dir(program_files: &Path) -> PathBuf {
    program_files.join(INSTALL_FOLDER)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SetupCommand {
    Install { source: PathBuf, result: PathBuf },
    Remove { result: PathBuf },
}

/// The command in an elevated copy's arguments (the program name excluded), if any.
pub fn parse_args(args: &[String]) -> Option<SetupCommand> {
    match args {
        [flag, source, result] if flag == "--setup-camera" => Some(SetupCommand::Install {
            source: PathBuf::from(source),
            result: PathBuf::from(result),
        }),
        [flag, result] if flag == "--remove-camera" => Some(SetupCommand::Remove {
            result: PathBuf::from(result),
        }),
        _ => None,
    }
}

/// What the machine-changing steps are, so the order they run in is tested without administrator
/// rights.
pub trait SetupSystem {
    /// Stops the Frame Server so the DLL is not in use. It starts again on demand.
    fn stop_frame_server(&self) -> Result<(), String>;
    /// Whether the DLL to install is there.
    fn exists(&self, path: &Path) -> bool;
    fn create_dir(&self, dir: &Path) -> Result<(), String>;
    fn copy(&self, from: &Path, to: &Path) -> Result<(), String>;
    fn register(&self, dll: &Path) -> Result<(), String>;
    fn unregister(&self, dll: &Path) -> Result<(), String>;
    /// The DLL path the class is registered with, if it is registered.
    fn registered_path(&self) -> Option<PathBuf>;
    fn remove_dir(&self, dir: &Path) -> Result<(), String>;
}

/// Copies the DLL into `dir` and registers it, then checks the registration points at the copy.
pub fn install(system: &dyn SetupSystem, source: &Path, dir: &Path) -> Result<(), String> {
    if !system.exists(source) {
        return Err(format!(
            "The camera source was not found at {}.",
            source.display()
        ));
    }
    let target = dir.join(DLL_NAME);
    system.stop_frame_server()?;
    system.create_dir(dir)?;
    system.copy(source, &target)?;
    system.register(&target)?;
    match system.registered_path() {
        Some(path) if same_path(&path, &target) => Ok(()),
        Some(other) => Err(format!(
            "The camera source is registered at {} instead of {}.",
            other.display(),
            target.display()
        )),
        None => Err("The camera source did not register.".to_string()),
    }
}

/// Whether the installed DLL is the one that ships with this program. A client update replaces the
/// program's own files but not the copy under Program Files, which only an elevated setup can
/// change, so the two can differ afterwards. `None` when either file cannot be read.
pub fn source_is_current(installed: &Path, bundled: &Path) -> Option<bool> {
    use sha2::{Digest, Sha256};
    let installed = std::fs::read(installed).ok()?;
    let bundled = std::fs::read(bundled).ok()?;
    Some(Sha256::digest(&installed) == Sha256::digest(&bundled))
}

/// Unregisters the DLL and deletes its folder.
pub fn remove(system: &dyn SetupSystem, dir: &Path) -> Result<(), String> {
    let target = dir.join(DLL_NAME);
    system.unregister(&target)?;
    system.stop_frame_server()?;
    system.remove_dir(dir)
}

/// The elevated copy installs only the DLL that ships with the program: a file named exactly
/// `hyperharbor_vcam.dll` in the folder of the running executable. Both paths must already be
/// canonical (no `..`, no links), so a plain comparison is enough. Without this, the elevated
/// process could be pointed at any file and register it as a COM class the Frame Server loads.
pub fn is_trusted_source(source: &Path, exe_dir: &Path) -> bool {
    let named = source
        .file_name()
        .is_some_and(|name| name.to_string_lossy().eq_ignore_ascii_case(DLL_NAME));
    named && source.parent().is_some_and(|dir| same_path(dir, exe_dir))
}

fn same_path(a: &Path, b: &Path) -> bool {
    a.to_string_lossy()
        .eq_ignore_ascii_case(&b.to_string_lossy())
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
    use std::cell::RefCell;

    #[test]
    fn an_installed_copy_is_current_only_when_it_matches_the_bundled_one() {
        let dir =
            std::env::temp_dir().join(format!("hyperharbor-camera-current-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let installed = dir.join("installed.dll");
        let bundled = dir.join("bundled.dll");
        std::fs::write(&installed, b"version one").unwrap();
        std::fs::write(&bundled, b"version one").unwrap();
        assert_eq!(source_is_current(&installed, &bundled), Some(true));

        std::fs::write(&bundled, b"version two").unwrap();
        assert_eq!(source_is_current(&installed, &bundled), Some(false));

        assert_eq!(
            source_is_current(&installed, &dir.join("missing.dll")),
            None
        );
        let _ = std::fs::remove_dir_all(dir);
    }

    #[derive(Default)]
    struct Fake {
        calls: RefCell<Vec<String>>,
        fail_at: Option<&'static str>,
        registered: RefCell<Option<PathBuf>>,
        register_elsewhere: bool,
        missing_source: bool,
    }

    impl Fake {
        fn step(&self, name: &str, detail: &str) -> Result<(), String> {
            self.calls
                .borrow_mut()
                .push(format!("{name} {detail}").trim().to_string());
            if self.fail_at == Some(name) {
                Err(format!("{name} failed"))
            } else {
                Ok(())
            }
        }
    }

    impl SetupSystem for Fake {
        fn stop_frame_server(&self) -> Result<(), String> {
            self.step("stop", "")
        }
        fn exists(&self, _path: &Path) -> bool {
            !self.missing_source
        }
        fn create_dir(&self, dir: &Path) -> Result<(), String> {
            self.step("mkdir", &dir.display().to_string())
        }
        fn copy(&self, from: &Path, to: &Path) -> Result<(), String> {
            self.step("copy", &format!("{} -> {}", from.display(), to.display()))
        }
        fn register(&self, dll: &Path) -> Result<(), String> {
            self.step("register", &dll.display().to_string())?;
            *self.registered.borrow_mut() = Some(if self.register_elsewhere {
                PathBuf::from(r"C:\Other\x.dll")
            } else {
                dll.to_path_buf()
            });
            Ok(())
        }
        fn unregister(&self, dll: &Path) -> Result<(), String> {
            self.step("unregister", &dll.display().to_string())?;
            *self.registered.borrow_mut() = None;
            Ok(())
        }
        fn registered_path(&self) -> Option<PathBuf> {
            self.registered.borrow().clone()
        }
        fn remove_dir(&self, dir: &Path) -> Result<(), String> {
            self.step("rmdir", &dir.display().to_string())
        }
    }

    const DIR: &str = r"C:\Program Files\HyperHarbor Camera";

    #[test]
    fn the_install_folder_is_under_program_files() {
        assert_eq!(
            install_dir(Path::new(r"C:\Program Files")),
            PathBuf::from(DIR)
        );
    }

    #[test]
    fn install_stops_the_frame_server_before_it_replaces_the_dll_and_registers_last() {
        let fake = Fake::default();
        install(&fake, Path::new("fake:source.dll"), Path::new(DIR)).unwrap();
        let calls = fake.calls.borrow().clone();
        let order: Vec<&str> = calls.iter().map(|c| c.split(' ').next().unwrap()).collect();
        assert_eq!(order, ["stop", "mkdir", "copy", "register"]);
        assert!(calls[2].ends_with(r"C:\Program Files\HyperHarbor Camera\hyperharbor_vcam.dll"));
    }

    #[test]
    fn a_failed_step_stops_the_install_and_says_which() {
        for step in ["stop", "mkdir", "copy", "register"] {
            let fake = Fake {
                fail_at: Some(step),
                ..Fake::default()
            };
            let result = install(&fake, Path::new("fake:source.dll"), Path::new(DIR));
            assert_eq!(result, Err(format!("{step} failed")));
        }
        // Nothing runs after the step that failed.
        let fake = Fake {
            fail_at: Some("copy"),
            ..Fake::default()
        };
        let _ = install(&fake, Path::new("fake:source.dll"), Path::new(DIR));
        assert!(!fake
            .calls
            .borrow()
            .iter()
            .any(|c| c.starts_with("register")));
    }

    #[test]
    fn a_registration_that_points_elsewhere_is_an_error() {
        let fake = Fake {
            register_elsewhere: true,
            ..Fake::default()
        };
        let result = install(&fake, Path::new("fake:source.dll"), Path::new(DIR));
        assert!(result.unwrap_err().contains(r"C:\Other\x.dll"));
    }

    #[test]
    fn a_missing_source_file_is_reported_before_anything_changes() {
        let fake = Fake {
            missing_source: true,
            ..Fake::default()
        };
        let result = install(
            &fake,
            Path::new(r"C:\definitely\missing\hyperharbor_vcam.dll"),
            Path::new(DIR),
        );
        assert!(result.unwrap_err().contains("was not found"));
        assert!(fake.calls.borrow().is_empty());
    }

    #[test]
    fn remove_unregisters_before_it_deletes_the_folder() {
        let fake = Fake::default();
        remove(&fake, Path::new(DIR)).unwrap();
        let order: Vec<String> = fake
            .calls
            .borrow()
            .iter()
            .map(|c| c.split(' ').next().unwrap().to_string())
            .collect();
        assert_eq!(order, ["unregister", "stop", "rmdir"]);
    }

    #[test]
    fn only_the_dll_beside_the_program_is_trusted() {
        let exe_dir = Path::new(r"C:UsersmeAppDataLocalHyperHarbor");
        assert!(is_trusted_source(
            &exe_dir.join("hyperharbor_vcam.dll"),
            exe_dir
        ));
        assert!(
            is_trusted_source(&exe_dir.join("HyperHarbor_VCAM.DLL"), exe_dir),
            "case does not matter on Windows"
        );
        // Another file name, another folder, a subfolder, or the folder above are all refused.
        assert!(!is_trusted_source(&exe_dir.join("evil.dll"), exe_dir));
        assert!(!is_trusted_source(
            Path::new(r"C:Temphyperharbor_vcam.dll"),
            exe_dir
        ));
        assert!(!is_trusted_source(
            &exe_dir.join("sub").join("hyperharbor_vcam.dll"),
            exe_dir
        ));
        assert!(!is_trusted_source(
            Path::new(r"C:UsersmeAppDataLocalhyperharbor_vcam.dll"),
            exe_dir
        ));
        assert!(!is_trusted_source(exe_dir, exe_dir));
    }

    #[test]
    fn arguments_are_parsed_exactly() {
        let args = |parts: &[&str]| parts.iter().map(|s| s.to_string()).collect::<Vec<_>>();
        assert_eq!(
            parse_args(&args(&["--setup-camera", r"C:\a\x.dll", r"C:\r.txt"])),
            Some(SetupCommand::Install {
                source: PathBuf::from(r"C:\a\x.dll"),
                result: PathBuf::from(r"C:\r.txt")
            })
        );
        assert_eq!(
            parse_args(&args(&["--remove-camera", r"C:\r.txt"])),
            Some(SetupCommand::Remove {
                result: PathBuf::from(r"C:\r.txt")
            })
        );
        for other in [
            &[][..],
            &["--setup-camera"],
            &["--setup-camera", "a"],
            &["--remove-camera"],
            &["--other", "a", "b"],
            &["--setup-camera", "a", "b", "c"],
        ] {
            assert_eq!(parse_args(&args(other)), None, "{other:?}");
        }
    }

    #[test]
    fn the_outcome_round_trips_through_the_result_file() {
        let dir = std::env::temp_dir().join(format!("hh-camsetup-{}", std::process::id()));
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
    }
}
