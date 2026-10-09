//! Names of the virtual cameras. The name is how a camera is found again: Windows reports it
//! with " (Windows Virtual Camera)" appended (observed on Windows 11 build 26200), so a created
//! name matches a listed device either exactly or with that suffix.

/// Every camera this client creates starts with this and ends with ")".
pub const NAME_PREFIX: &str = "HyperHarbor Camera (";

/// What Windows appends to a virtual camera's friendly name when it lists the device.
pub const OS_SUFFIX: &str = " (Windows Virtual Camera)";

/// The longest VM label kept in a name. Longer names are cut so device lists stay readable.
const MAX_LABEL_CHARS: usize = 40;

/// Cleans a VM name for use inside a camera name: control characters and parentheses are
/// replaced (parentheses would confuse `owned_label`), runs of spaces collapse, and the length
/// is capped. An empty result becomes "VM".
pub fn sanitize_label(vm_name: &str) -> String {
    let replaced: String = vm_name
        .chars()
        .map(|c| match c {
            '(' => '[',
            ')' => ']',
            c if c.is_control() => ' ',
            c => c,
        })
        .collect();
    let collapsed = replaced.split_whitespace().collect::<Vec<_>>().join(" ");
    let capped: String = collapsed.chars().take(MAX_LABEL_CHARS).collect();
    let trimmed = capped.trim();
    if trimmed.is_empty() {
        "VM".to_string()
    } else {
        trimmed.to_string()
    }
}

/// The friendly name of a camera for `vm_name`: "HyperHarbor Camera (VM name)". The service uses
/// `unique_friendly_name`, which adds a number when two VMs share a name.
#[cfg(test)]
pub fn friendly_name(vm_name: &str) -> String {
    format!("{NAME_PREFIX}{})", sanitize_label(vm_name))
}

/// Like `friendly_name`, but never equal to a name in `taken`: a second camera for a VM with the
/// same name becomes "HyperHarbor Camera (VM name #2)". Two cameras with one name could not be
/// told apart when the symbolic link is looked up.
pub fn unique_friendly_name<S: AsRef<str>>(vm_name: &str, taken: &[S]) -> String {
    let label = sanitize_label(vm_name);
    let is_taken = |candidate: &str| {
        taken
            .iter()
            .any(|name| names_match(name.as_ref(), candidate))
    };
    let first = format!("{NAME_PREFIX}{label})");
    if !is_taken(&first) {
        return first;
    }
    (2..)
        .map(|n| format!("{NAME_PREFIX}{label} #{n})"))
        .find(|candidate| !is_taken(candidate))
        .expect("an unbounded range always yields a free name")
}

/// True when `listed` is the device created as `created`: the same name, or the name with the
/// suffix Windows adds. Comparison ignores case.
pub fn names_match(listed: &str, created: &str) -> bool {
    let listed = listed.to_lowercase();
    let created = created.to_lowercase();
    listed == created || listed == format!("{created}{}", OS_SUFFIX.to_lowercase())
}

/// The label inside a camera name this client creates, or None for any other device. Accepts the
/// name with or without the suffix Windows adds.
pub fn owned_label(device_name: &str) -> Option<&str> {
    let name = device_name.strip_suffix(OS_SUFFIX).unwrap_or(device_name);
    let inner = name.strip_prefix(NAME_PREFIX)?.strip_suffix(')')?;
    if inner.is_empty() {
        None
    } else {
        Some(inner)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn name_has_the_documented_shape() {
        assert_eq!(friendly_name("Work PC"), "HyperHarbor Camera (Work PC)");
    }

    #[test]
    fn parentheses_and_control_characters_cannot_break_the_name() {
        assert_eq!(sanitize_label("a(b)c"), "a[b]c");
        assert_eq!(sanitize_label("a\r\nb\tc"), "a b c");
        assert_eq!(sanitize_label("  spaced   out  "), "spaced out");
        let name = friendly_name("evil)\n;camerastoredirect:s:*");
        assert!(!name.contains('\n') && !name.contains('\r'));
        assert_eq!(name.matches(')').count(), 1); // only the closing one is left
        assert!(owned_label(&name).is_some());
    }

    #[test]
    fn empty_and_long_names_are_handled() {
        assert_eq!(sanitize_label(""), "VM");
        assert_eq!(sanitize_label("  \t "), "VM");
        let long = "x".repeat(200);
        assert_eq!(sanitize_label(&long).chars().count(), MAX_LABEL_CHARS);
    }

    #[test]
    fn multibyte_names_are_cut_on_character_boundaries() {
        let name = "é".repeat(100);
        assert_eq!(sanitize_label(&name).chars().count(), MAX_LABEL_CHARS);
    }

    #[test]
    fn a_second_camera_for_the_same_vm_gets_a_number() {
        let none: [&str; 0] = [];
        assert_eq!(
            unique_friendly_name("Work", &none),
            "HyperHarbor Camera (Work)"
        );
        let taken = ["HyperHarbor Camera (Work)"];
        assert_eq!(
            unique_friendly_name("Work", &taken),
            "HyperHarbor Camera (Work #2)"
        );
        let taken = ["HyperHarbor Camera (Work)", "HyperHarbor Camera (Work #2)"];
        assert_eq!(
            unique_friendly_name("Work", &taken),
            "HyperHarbor Camera (Work #3)"
        );
    }

    #[test]
    fn a_taken_name_is_recognised_with_the_suffix_windows_adds() {
        let taken = ["HyperHarbor Camera (Work) (Windows Virtual Camera)"];
        assert_eq!(
            unique_friendly_name("Work", &taken),
            "HyperHarbor Camera (Work #2)"
        );
    }

    #[test]
    fn listed_names_match_with_or_without_the_suffix_ignoring_case() {
        let created = "HyperHarbor Camera (Work)";
        assert!(names_match("HyperHarbor Camera (Work)", created));
        assert!(names_match(
            "HyperHarbor Camera (Work) (Windows Virtual Camera)",
            created
        ));
        assert!(names_match(
            "hyperharbor camera (work) (windows virtual camera)",
            created
        ));
        assert!(!names_match(
            "HyperHarbor Camera (Work #2) (Windows Virtual Camera)",
            created
        ));
        assert!(!names_match("HyperHarbor Camera (Work) extra", created));
        assert!(!names_match("Integrated Camera", created));
    }

    #[test]
    fn only_cameras_this_client_creates_are_recognised() {
        assert_eq!(owned_label("HyperHarbor Camera (Work)"), Some("Work"));
        assert_eq!(
            owned_label("HyperHarbor Camera (Work #2) (Windows Virtual Camera)"),
            Some("Work #2")
        );
        assert_eq!(owned_label("Integrated Camera"), None);
        assert_eq!(owned_label("OBS Virtual Camera"), None);
        assert_eq!(owned_label("HyperHarbor Camera ()"), None);
        assert_eq!(owned_label("HyperHarbor Camera (Work"), None);
        assert_eq!(owned_label("Not HyperHarbor Camera (Work)"), None);
    }

    #[test]
    fn sanitized_labels_round_trip_through_owned_label() {
        for vm in ["Work PC", "a(b)c", "ünï", "x".repeat(90).as_str()] {
            let name = friendly_name(vm);
            assert_eq!(owned_label(&name), Some(sanitize_label(vm).as_str()));
        }
    }
}
