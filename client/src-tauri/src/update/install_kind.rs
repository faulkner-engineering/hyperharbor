use std::path::Path;

use serde::Serialize;

/// How this copy of the client was installed. Only an installed copy can replace itself; a portable
/// executable has no installer to run, so it only points the user at the release.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum InstallKind {
    Installed,
    Portable,
}

/// The NSIS installer writes uninstall.exe beside the application, so its presence marks an
/// installed copy.
const UNINSTALLER: &str = "uninstall.exe";

pub fn detect() -> InstallKind {
    std::env::current_exe()
        .ok()
        .and_then(|exe| exe.parent().map(|dir| kind_for(dir, |path| path.is_file())))
        .unwrap_or(InstallKind::Portable)
}

fn kind_for(exe_dir: &Path, is_file: impl Fn(&Path) -> bool) -> InstallKind {
    if is_file(&exe_dir.join(UNINSTALLER)) {
        InstallKind::Installed
    } else {
        InstallKind::Portable
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_folder_with_the_uninstaller_is_an_installed_copy() {
        let dir = Path::new(r"C:\Users\someone\AppData\Local\HyperHarbor");
        let kind = kind_for(dir, |path| path.ends_with("uninstall.exe"));
        assert_eq!(kind, InstallKind::Installed);
    }

    #[test]
    fn a_folder_without_it_is_portable() {
        let dir = Path::new(r"C:\Downloads");
        assert_eq!(kind_for(dir, |_| false), InstallKind::Portable);
    }
}
