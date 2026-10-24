//! The library on this device: ISO images and machine profiles kept here and sent to any paired host.
//!
//! Everything lives in one folder, so it can later be backed up as a whole:
//! - library.json: the manifest (items, and which hosts have a copy of each), written atomically.
//! - isos/<name>: the images. Copies and downloads go to a hidden ".partial" file first.
//! - profiles/<id>.json: each profile as MachineProfile JSON, the shape the host's API uses.
//! - images/<name>: the wallpaper images the profiles name.
//!
//! Nothing here is a secret: profiles hold account names, never passwords.

use std::path::{Path, PathBuf};
use std::sync::Mutex;

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::error::ClientError;

const MANIFEST: &str = "library.json";
const ISO_FOLDER: &str = "isos";
const PROFILE_FOLDER: &str = "profiles";
const IMAGE_FOLDER: &str = "images";
const MANIFEST_VERSION: u32 = 1;

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Manifest {
    #[serde(default)]
    pub version: u32,
    #[serde(default)]
    pub isos: Vec<LibraryIso>,
    #[serde(default)]
    pub profiles: Vec<LibraryProfile>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct LibraryIso {
    pub id: String,
    pub name: String,
    pub size_bytes: u64,
    /// Seconds since the Unix epoch.
    pub added_at: u64,
    #[serde(default)]
    pub links: Vec<IsoLink>,
}

/// A host that has a copy of a library image.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct IsoLink {
    pub host_id: String,
    pub host_name: String,
    /// The image's name in the host's ISO library.
    pub remote_name: String,
    pub size_bytes: u64,
    /// The host's modifiedAt when the copy was made, as the host wrote it.
    pub remote_modified_at: String,
    pub synced_at: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct LibraryProfile {
    pub id: String,
    pub name: String,
    pub os: String,
    #[serde(default)]
    pub description: Option<String>,
    /// SHA-256 of the profile (canonical JSON) and its images; changes whenever the content does.
    pub hash: String,
    /// Wallpaper images the profile names, kept in images/.
    #[serde(default)]
    pub images: Vec<String>,
    pub updated_at: u64,
    #[serde(default)]
    pub links: Vec<ProfileLink>,
}

/// A host that has a copy of a library profile.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ProfileLink {
    pub host_id: String,
    pub host_name: String,
    /// The profile's ID on the host.
    pub remote_id: String,
    /// The library hash of the content both sides had at the last sync.
    pub sent_hash: String,
    /// The host's updatedAt after the last sync, as the host wrote it.
    pub remote_updated_at: String,
    pub synced_at: u64,
}

/// How a host's copy compares with the library.
#[derive(Debug, Clone, Copy, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum LinkState {
    InSync,
    /// The library's copy changed since the last sync: send it.
    LibraryNewer,
    /// The host's copy changed since the last sync: update the library from it.
    HostNewer,
    /// Both changed; the user picks one.
    BothChanged,
    MissingOnHost,
}

#[derive(Debug, Clone, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct LinkStatus {
    pub item_id: String,
    pub host_id: String,
    pub state: LinkState,
}

/// An image in a host's ISO library, as far as sync is concerned.
pub struct HostIso<'a> {
    pub name: &'a str,
    pub size_bytes: u64,
    pub modified_at: &'a str,
}

/// A profile on a host, as far as sync is concerned.
pub struct HostProfile<'a> {
    pub id: &'a str,
    pub updated_at: &'a str,
}

pub struct LibraryStore {
    root: PathBuf,
    manifest: Mutex<Manifest>,
}

impl LibraryStore {
    /// Opens the library at `root`. A missing or unreadable manifest starts an empty library; an unreadable one
    /// is kept beside it as library.json.bad so nothing is lost.
    pub fn new(root: PathBuf) -> Self {
        let path = root.join(MANIFEST);
        let manifest = match std::fs::read(&path) {
            Ok(bytes) => serde_json::from_slice(&bytes).unwrap_or_else(|_| {
                let _ = std::fs::rename(&path, root.join("library.json.bad"));
                Manifest::default()
            }),
            Err(_) => Manifest::default(),
        };
        Self {
            root,
            manifest: Mutex::new(manifest),
        }
    }

    pub fn list(&self) -> Manifest {
        self.manifest.lock().unwrap().clone()
    }

    pub fn iso(&self, id: &str) -> Result<LibraryIso, ClientError> {
        self.manifest
            .lock()
            .unwrap()
            .isos
            .iter()
            .find(|iso| iso.id == id)
            .cloned()
            .ok_or_else(not_in_library)
    }

    pub fn profile(&self, id: &str) -> Result<LibraryProfile, ClientError> {
        self.manifest
            .lock()
            .unwrap()
            .profiles
            .iter()
            .find(|profile| profile.id == id)
            .cloned()
            .ok_or_else(not_in_library)
    }

    /// The library image with this name, if any (names are compared without case, as Windows does).
    pub fn iso_named(&self, name: &str) -> Option<LibraryIso> {
        self.manifest
            .lock()
            .unwrap()
            .isos
            .iter()
            .find(|iso| iso.name.eq_ignore_ascii_case(name))
            .cloned()
    }

    pub fn iso_file(&self, name: &str) -> Result<PathBuf, ClientError> {
        check_iso_name(name)?;
        Ok(self.root.join(ISO_FOLDER).join(name))
    }

    /// Where a copy or download of `name` is written until it is complete. The name is fixed, so an
    /// interrupted download resumes from what it already has.
    pub fn iso_partial(&self, name: &str) -> Result<PathBuf, ClientError> {
        check_iso_name(name)?;
        let folder = self.root.join(ISO_FOLDER);
        std::fs::create_dir_all(&folder).map_err(storage)?;
        Ok(folder.join(format!(".{name}.partial")))
    }

    pub fn image_file(&self, name: &str) -> Result<PathBuf, ClientError> {
        check_image_name(name)?;
        Ok(self.root.join(IMAGE_FOLDER).join(name))
    }

    /// Moves a complete partial file into place and records the image.
    pub fn finish_iso(&self, name: &str, partial: &Path) -> Result<LibraryIso, ClientError> {
        let target = self.iso_file(name)?;
        let mut manifest = self.manifest.lock().unwrap();
        if manifest
            .isos
            .iter()
            .any(|iso| iso.name.eq_ignore_ascii_case(name))
        {
            let _ = std::fs::remove_file(partial);
            return Err(ClientError::InvalidRequest(format!(
                "\"{name}\" is already in the library"
            )));
        }
        std::fs::rename(partial, &target).map_err(storage)?;
        let size_bytes = std::fs::metadata(&target).map_err(storage)?.len();
        let iso = LibraryIso {
            id: new_id()?,
            name: name.to_string(),
            size_bytes,
            added_at: now(),
            links: Vec::new(),
        };
        manifest.isos.push(iso.clone());
        manifest.isos.sort_by_key(|iso| iso.name.to_lowercase());
        self.save(&manifest)?;
        Ok(iso)
    }

    pub fn rename_iso(&self, id: &str, new_name: &str) -> Result<LibraryIso, ClientError> {
        let target = self.iso_file(new_name)?;
        let mut manifest = self.manifest.lock().unwrap();
        if manifest
            .isos
            .iter()
            .any(|iso| iso.id != id && iso.name.eq_ignore_ascii_case(new_name))
        {
            return Err(ClientError::InvalidRequest(format!(
                "\"{new_name}\" is already in the library"
            )));
        }
        let iso = manifest
            .isos
            .iter_mut()
            .find(|iso| iso.id == id)
            .ok_or_else(not_in_library)?;
        std::fs::rename(self.root.join(ISO_FOLDER).join(&iso.name), &target).map_err(storage)?;
        iso.name = new_name.to_string();
        let renamed = iso.clone();
        manifest.isos.sort_by_key(|iso| iso.name.to_lowercase());
        self.save(&manifest)?;
        Ok(renamed)
    }

    /// Removes an image or profile and its files. Wallpaper images no other profile names go too.
    pub fn remove(&self, id: &str) -> Result<(), ClientError> {
        let mut manifest = self.manifest.lock().unwrap();
        if let Some(index) = manifest.isos.iter().position(|iso| iso.id == id) {
            let iso = manifest.isos.remove(index);
            remove_if_present(&self.root.join(ISO_FOLDER).join(&iso.name))?;
        } else if let Some(index) = manifest.profiles.iter().position(|p| p.id == id) {
            let profile = manifest.profiles.remove(index);
            remove_if_present(&self.profile_file(&profile.id))?;
            for image in &profile.images {
                if !manifest.profiles.iter().any(|p| p.images.contains(image)) {
                    remove_if_present(&self.root.join(IMAGE_FOLDER).join(image))?;
                }
            }
        } else {
            return Err(not_in_library());
        }
        self.save(&manifest)
    }

    /// A wallpaper image already in the library, if any.
    pub fn image_bytes(&self, name: &str) -> Option<Vec<u8>> {
        std::fs::read(self.image_file(name).ok()?).ok()
    }

    /// The profile as MachineProfile JSON.
    pub fn profile_document(&self, id: &str) -> Result<serde_json::Value, ClientError> {
        let profile = self.profile(id)?;
        let bytes = std::fs::read(self.profile_file(&profile.id)).map_err(storage)?;
        serde_json::from_slice(&bytes).map_err(|e| ClientError::Storage(e.to_string()))
    }

    /// Saves a profile and its wallpaper images, as a new item or over `existing`. Returns the item.
    pub fn save_profile(
        &self,
        existing: Option<&str>,
        document: &serde_json::Value,
        images: &[(String, Vec<u8>)],
    ) -> Result<LibraryProfile, ClientError> {
        let name = document["name"]
            .as_str()
            .ok_or_else(|| ClientError::InvalidResponse("the profile has no name".into()))?
            .to_string();
        let os = document["os"].as_str().unwrap_or("windows").to_string();
        let description = document["description"].as_str().map(str::to_string);
        for (image, content) in images {
            let path = self.image_file(image)?;
            write_atomically(&path, content)?;
        }
        let hash = profile_hash(document, images);
        let image_names: Vec<String> = images.iter().map(|(name, _)| name.clone()).collect();

        let mut manifest = self.manifest.lock().unwrap();
        let id = match existing {
            Some(id) => {
                let item = manifest
                    .profiles
                    .iter_mut()
                    .find(|p| p.id == id)
                    .ok_or_else(not_in_library)?;
                item.name = name;
                item.os = os;
                item.description = description;
                if item.hash != hash {
                    item.hash = hash;
                    item.updated_at = now();
                }
                let dropped: Vec<String> = item
                    .images
                    .iter()
                    .filter(|image| !image_names.contains(image))
                    .cloned()
                    .collect();
                item.images = image_names;
                let id = item.id.clone();
                for image in dropped {
                    if !manifest.profiles.iter().any(|p| p.images.contains(&image)) {
                        remove_if_present(&self.root.join(IMAGE_FOLDER).join(&image))?;
                    }
                }
                id
            }
            None => {
                let id = new_id()?;
                manifest.profiles.push(LibraryProfile {
                    id: id.clone(),
                    name,
                    os,
                    description,
                    hash,
                    images: image_names,
                    updated_at: now(),
                    links: Vec::new(),
                });
                id
            }
        };
        let json =
            serde_json::to_vec_pretty(document).map_err(|e| ClientError::Storage(e.to_string()))?;
        write_atomically(&self.profile_file(&id), &json)?;
        manifest.profiles.sort_by_key(|p| p.name.to_lowercase());
        self.save(&manifest)?;
        Ok(manifest
            .profiles
            .iter()
            .find(|p| p.id == id)
            .unwrap()
            .clone())
    }

    /// The library profile linked to this profile on this host, if any.
    pub fn profile_linked_to(&self, host_id: &str, remote_id: &str) -> Option<LibraryProfile> {
        self.manifest
            .lock()
            .unwrap()
            .profiles
            .iter()
            .find(|p| {
                p.links
                    .iter()
                    .any(|link| link.host_id == host_id && link.remote_id == remote_id)
            })
            .cloned()
    }

    /// Records (or replaces) the host's copy of an image.
    pub fn link_iso(&self, id: &str, link: IsoLink) -> Result<LibraryIso, ClientError> {
        let mut manifest = self.manifest.lock().unwrap();
        let iso = manifest
            .isos
            .iter_mut()
            .find(|iso| iso.id == id)
            .ok_or_else(not_in_library)?;
        iso.links
            .retain(|existing| existing.host_id != link.host_id);
        iso.links.push(link);
        let linked = iso.clone();
        self.save(&manifest)?;
        Ok(linked)
    }

    /// Records (or replaces) the host's copy of a profile.
    pub fn link_profile(&self, id: &str, link: ProfileLink) -> Result<LibraryProfile, ClientError> {
        let mut manifest = self.manifest.lock().unwrap();
        let profile = manifest
            .profiles
            .iter_mut()
            .find(|p| p.id == id)
            .ok_or_else(not_in_library)?;
        profile
            .links
            .retain(|existing| existing.host_id != link.host_id);
        profile.links.push(link);
        let linked = profile.clone();
        self.save(&manifest)?;
        Ok(linked)
    }

    /// Forgets that a host has a copy; the host's copy stays.
    pub fn unlink(&self, id: &str, host_id: &str) -> Result<(), ClientError> {
        let mut manifest = self.manifest.lock().unwrap();
        if let Some(iso) = manifest.isos.iter_mut().find(|iso| iso.id == id) {
            iso.links.retain(|link| link.host_id != host_id);
        } else if let Some(profile) = manifest.profiles.iter_mut().find(|p| p.id == id) {
            profile.links.retain(|link| link.host_id != host_id);
        } else {
            return Err(not_in_library());
        }
        self.save(&manifest)
    }

    fn profile_file(&self, id: &str) -> PathBuf {
        self.root.join(PROFILE_FOLDER).join(format!("{id}.json"))
    }

    fn save(&self, manifest: &Manifest) -> Result<(), ClientError> {
        let mut manifest = manifest.clone();
        manifest.version = MANIFEST_VERSION;
        let json = serde_json::to_vec_pretty(&manifest)
            .map_err(|e| ClientError::Storage(e.to_string()))?;
        write_atomically(&self.root.join(MANIFEST), &json)
    }
}

/// The state of every link to this host, compared with the host's current images and profiles.
pub fn statuses(
    manifest: &Manifest,
    host_id: &str,
    isos: &[HostIso<'_>],
    profiles: &[HostProfile<'_>],
) -> Vec<LinkStatus> {
    let iso_states = manifest.isos.iter().flat_map(|iso| {
        iso.links
            .iter()
            .filter(|link| link.host_id == host_id)
            .map(|link| LinkStatus {
                item_id: iso.id.clone(),
                host_id: host_id.to_string(),
                state: iso_state(link, isos),
            })
    });
    let profile_states = manifest.profiles.iter().flat_map(|profile| {
        profile
            .links
            .iter()
            .filter(|link| link.host_id == host_id)
            .map(|link| LinkStatus {
                item_id: profile.id.clone(),
                host_id: host_id.to_string(),
                state: profile_state(&profile.hash, link, profiles),
            })
    });
    iso_states.chain(profile_states).collect()
}

fn iso_state(link: &IsoLink, isos: &[HostIso<'_>]) -> LinkState {
    match isos
        .iter()
        .find(|iso| iso.name.eq_ignore_ascii_case(&link.remote_name))
    {
        None => LinkState::MissingOnHost,
        Some(iso)
            if iso.size_bytes == link.size_bytes
                && same_instant(iso.modified_at, &link.remote_modified_at) =>
        {
            LinkState::InSync
        }
        Some(_) => LinkState::HostNewer,
    }
}

fn profile_state(hash: &str, link: &ProfileLink, profiles: &[HostProfile<'_>]) -> LinkState {
    let Some(remote) = profiles.iter().find(|p| p.id == link.remote_id) else {
        return LinkState::MissingOnHost;
    };
    let library_changed = hash != link.sent_hash;
    let host_changed = !same_instant(remote.updated_at, &link.remote_updated_at);
    match (library_changed, host_changed) {
        (false, false) => LinkState::InSync,
        (true, false) => LinkState::LibraryNewer,
        (false, true) => LinkState::HostNewer,
        (true, true) => LinkState::BothChanged,
    }
}

/// Whether two host timestamps are the same moment. The host writes them as "...Z" in one place and "+00:00"
/// in another, so they are compared as instants, falling back to the text when either does not parse.
fn same_instant(a: &str, b: &str) -> bool {
    match (instant(a), instant(b)) {
        (Some(a), Some(b)) => a == b,
        _ => a == b,
    }
}

/// An RFC 3339 timestamp as nanoseconds since the Unix epoch.
fn instant(text: &str) -> Option<i128> {
    let bytes = text.as_bytes();
    let number = |range: std::ops::Range<usize>| -> Option<i64> { text.get(range)?.parse().ok() };
    if bytes.len() < 20 || bytes[4] != b'-' || bytes[7] != b'-' || bytes[13] != b':' {
        return None;
    }
    let (year, month, day) = (number(0..4)?, number(5..7)?, number(8..10)?);
    let (hour, minute, second) = (number(11..13)?, number(14..16)?, number(17..19)?);
    let mut rest = &text[19..];
    let mut nanos: i64 = 0;
    if let Some(fraction) = rest.strip_prefix('.') {
        let digits = fraction.bytes().take_while(u8::is_ascii_digit).count();
        let mut padded: String = fraction[..digits].chars().take(9).collect();
        while padded.len() < 9 {
            padded.push('0');
        }
        nanos = padded.parse().ok()?;
        rest = &fraction[digits..];
    }
    let offset_minutes = match rest {
        "Z" | "z" => 0,
        _ if rest.len() == 6 && (rest.starts_with('+') || rest.starts_with('-')) => {
            let sign = if rest.starts_with('-') { -1 } else { 1 };
            sign * (rest[1..3].parse::<i64>().ok()? * 60 + rest[4..6].parse::<i64>().ok()?)
        }
        _ => return None,
    };
    let days = days_from_civil(year, month, day);
    let seconds = days * 86_400 + hour * 3_600 + minute * 60 + second - offset_minutes * 60;
    Some(i128::from(seconds) * 1_000_000_000 + i128::from(nanos))
}

/// Days since 1970-01-01 of a proleptic Gregorian date (Howard Hinnant's algorithm).
fn days_from_civil(year: i64, month: i64, day: i64) -> i64 {
    let year = if month <= 2 { year - 1 } else { year };
    let era = year.div_euclid(400);
    let year_of_era = year - era * 400;
    let month_index = (month + 9) % 12;
    let day_of_year = (153 * month_index + 2) / 5 + day - 1;
    let day_of_era = year_of_era * 365 + year_of_era / 4 - year_of_era / 100 + day_of_year;
    era * 146_097 + day_of_era - 719_468
}

/// SHA-256 over the profile as canonical JSON (keys sorted at every level) and each image's name and content.
pub fn profile_hash(document: &serde_json::Value, images: &[(String, Vec<u8>)]) -> String {
    let mut canonical = String::new();
    write_canonical(document, &mut canonical);
    let mut hasher = Sha256::new();
    hasher.update(canonical.as_bytes());
    let mut sorted: Vec<&(String, Vec<u8>)> = images.iter().collect();
    sorted.sort_by(|a, b| a.0.cmp(&b.0));
    for (name, content) in sorted {
        let digest: [u8; 32] = Sha256::digest(content).into();
        hasher.update(b"\0");
        hasher.update(name.as_bytes());
        hasher.update(b"\0");
        hasher.update(digest);
    }
    let digest: [u8; 32] = hasher.finalize().into();
    hex::encode(digest)
}

fn write_canonical(value: &serde_json::Value, out: &mut String) {
    match value {
        serde_json::Value::Object(map) => {
            let mut keys: Vec<&String> = map.keys().collect();
            keys.sort();
            out.push('{');
            for (index, key) in keys.into_iter().enumerate() {
                if index > 0 {
                    out.push(',');
                }
                out.push_str(&serde_json::Value::String(key.clone()).to_string());
                out.push(':');
                write_canonical(&map[key], out);
            }
            out.push('}');
        }
        serde_json::Value::Array(items) => {
            out.push('[');
            for (index, item) in items.iter().enumerate() {
                if index > 0 {
                    out.push(',');
                }
                write_canonical(item, out);
            }
            out.push(']');
        }
        other => out.push_str(&other.to_string()),
    }
}

/// The wallpaper image a MachineProfile names, if any.
pub fn wallpaper_image(document: &serde_json::Value) -> Option<String> {
    document
        .pointer("/display/guest/wallpaper/image")
        .and_then(serde_json::Value::as_str)
        .map(str::to_string)
}

/// An ISO file name: ends in .iso, no folders, no leading period, no control characters.
pub fn check_iso_name(name: &str) -> Result<(), ClientError> {
    let valid = name.len() > 4
        && name.len() <= 200
        && name.to_ascii_lowercase().ends_with(".iso")
        && !name.contains(['/', '\\', ':', '*', '?', '"', '<', '>', '|'])
        && !name.starts_with('.')
        && !name.chars().any(char::is_control);
    if valid {
        Ok(())
    } else {
        Err(ClientError::InvalidRequest(format!(
            "\"{name}\" is not an ISO file name"
        )))
    }
}

/// A wallpaper image file name: a JPEG, PNG, or BMP name with no folders.
fn check_image_name(name: &str) -> Result<(), ClientError> {
    let lower = name.to_ascii_lowercase();
    let valid = [".jpg", ".jpeg", ".png", ".bmp"]
        .iter()
        .any(|extension| lower.len() > extension.len() && lower.ends_with(extension))
        && !name.contains(['/', '\\', ':', '*', '?', '"', '<', '>', '|'])
        && !name.contains("..")
        && !name.starts_with('.')
        && !name.chars().any(char::is_control);
    if valid {
        Ok(())
    } else {
        Err(ClientError::InvalidRequest(format!(
            "\"{name}\" is not a JPEG, PNG, or BMP file name"
        )))
    }
}

fn write_atomically(path: &Path, content: &[u8]) -> Result<(), ClientError> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(storage)?;
    }
    let mut temporary = path.as_os_str().to_owned();
    temporary.push(".tmp");
    let temporary = PathBuf::from(temporary);
    std::fs::write(&temporary, content).map_err(storage)?;
    std::fs::rename(&temporary, path).map_err(storage)
}

fn remove_if_present(path: &Path) -> Result<(), ClientError> {
    match std::fs::remove_file(path) {
        Err(error) if error.kind() != std::io::ErrorKind::NotFound => Err(storage(error)),
        _ => Ok(()),
    }
}

fn new_id() -> Result<String, ClientError> {
    let mut id = [0u8; 16];
    getrandom::fill(&mut id).map_err(|e| ClientError::Storage(e.to_string()))?;
    Ok(hex::encode(id))
}

pub fn now() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |elapsed| elapsed.as_secs())
}

fn storage(error: std::io::Error) -> ClientError {
    ClientError::Storage(error.to_string())
}

fn not_in_library() -> ClientError {
    ClientError::InvalidRequest("that item is no longer in the library".into())
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn temp_root() -> PathBuf {
        let mut id = [0u8; 8];
        getrandom::fill(&mut id).unwrap();
        std::env::temp_dir().join(format!("hh-library-{}", hex::encode(id)))
    }

    fn add_iso(store: &LibraryStore, name: &str, bytes: usize) -> LibraryIso {
        let partial = store.iso_partial(name).unwrap();
        std::fs::write(&partial, vec![1u8; bytes]).unwrap();
        store.finish_iso(name, &partial).unwrap()
    }

    fn iso_link(host: &str, size_bytes: u64, modified: &str) -> IsoLink {
        IsoLink {
            host_id: host.into(),
            host_name: "TC-PC".into(),
            remote_name: "debian.iso".into(),
            size_bytes,
            remote_modified_at: modified.into(),
            synced_at: 1,
        }
    }

    fn profile_link(host: &str, sent_hash: &str, updated: &str) -> ProfileLink {
        ProfileLink {
            host_id: host.into(),
            host_name: "TC-PC".into(),
            remote_id: "dev-box".into(),
            sent_hash: sent_hash.into(),
            remote_updated_at: updated.into(),
            synced_at: 1,
        }
    }

    #[test]
    fn the_manifest_survives_a_restart() {
        let root = temp_root();
        let store = LibraryStore::new(root.clone());
        let iso = add_iso(&store, "debian.iso", 10);
        store
            .link_iso(&iso.id, iso_link("host-a", 10, "2026-10-23T10:00:00Z"))
            .unwrap();
        let profile = store
            .save_profile(
                None,
                &json!({"name": "Dev box", "os": "windows"}),
                &[("harbor.png".into(), vec![1, 2, 3])],
            )
            .unwrap();

        let reopened = LibraryStore::new(root.clone()).list();

        assert_eq!(reopened.version, MANIFEST_VERSION);
        assert_eq!(reopened.isos[0].name, "debian.iso");
        assert_eq!(reopened.isos[0].size_bytes, 10);
        assert_eq!(reopened.isos[0].links[0].host_id, "host-a");
        assert_eq!(reopened.profiles[0], profile);
        assert!(root.join("images").join("harbor.png").exists());
        assert!(!root.join("library.json.tmp").exists());
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn a_damaged_manifest_is_kept_aside_and_the_library_starts_empty() {
        let root = temp_root();
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(root.join(MANIFEST), b"{ not json").unwrap();

        let store = LibraryStore::new(root.clone());

        assert!(store.list().isos.is_empty());
        assert!(root.join("library.json.bad").exists());
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn an_image_name_is_used_once() {
        let root = temp_root();
        let store = LibraryStore::new(root.clone());
        add_iso(&store, "debian.iso", 3);
        let partial = store.iso_partial("DEBIAN.iso").unwrap();
        std::fs::write(&partial, b"x").unwrap();

        assert!(store.finish_iso("DEBIAN.iso", &partial).is_err());
        assert!(!partial.exists());
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn renaming_moves_the_file_and_removing_deletes_it() {
        let root = temp_root();
        let store = LibraryStore::new(root.clone());
        let iso = add_iso(&store, "debian.iso", 3);

        let renamed = store.rename_iso(&iso.id, "debian 13.iso").unwrap();
        assert_eq!(renamed.name, "debian 13.iso");
        assert!(root.join("isos").join("debian 13.iso").exists());
        assert!(store.rename_iso(&iso.id, "..\\escape.iso").is_err());

        store.remove(&iso.id).unwrap();
        assert!(!root.join("isos").join("debian 13.iso").exists());
        assert!(store.list().isos.is_empty());
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn removing_a_profile_keeps_images_another_profile_names() {
        let root = temp_root();
        let store = LibraryStore::new(root.clone());
        let image = vec![("harbor.png".to_string(), vec![1u8, 2])];
        let first = store
            .save_profile(None, &json!({"name": "A", "os": "windows"}), &image)
            .unwrap();
        let second = store
            .save_profile(None, &json!({"name": "B", "os": "windows"}), &image)
            .unwrap();

        store.remove(&first.id).unwrap();
        assert!(root.join("images").join("harbor.png").exists());
        store.remove(&second.id).unwrap();
        assert!(!root.join("images").join("harbor.png").exists());
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn saving_over_a_profile_keeps_its_links_and_changes_the_hash() {
        let root = temp_root();
        let store = LibraryStore::new(root.clone());
        let saved = store
            .save_profile(None, &json!({"name": "A", "os": "windows"}), &[])
            .unwrap();
        store
            .link_profile(&saved.id, profile_link("host-a", &saved.hash, "x"))
            .unwrap();

        let updated = store
            .save_profile(
                Some(&saved.id),
                &json!({"name": "A", "os": "windows", "description": "more"}),
                &[],
            )
            .unwrap();

        assert_eq!(updated.id, saved.id);
        assert_ne!(updated.hash, saved.hash);
        assert_eq!(updated.description.as_deref(), Some("more"));
        assert_eq!(updated.links.len(), 1);
        assert_eq!(
            store.profile_document(&saved.id).unwrap()["description"],
            "more"
        );
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn the_hash_ignores_key_order_but_not_images() {
        let a = json!({"name": "A", "display": {"guest": {"theme": "dark", "accent": "#112233"}}});
        let b: serde_json::Value = serde_json::from_str(
            r##"{"display": {"guest": {"accent": "#112233", "theme": "dark"}}, "name": "A"}"##,
        )
        .unwrap();

        assert_eq!(profile_hash(&a, &[]), profile_hash(&b, &[]));
        assert_ne!(
            profile_hash(&a, &[("x.png".into(), vec![1])]),
            profile_hash(&a, &[("x.png".into(), vec![2])])
        );
    }

    #[test]
    fn iso_links_compare_name_size_and_time() {
        let link = iso_link("host-a", 10, "2026-10-23T10:00:00Z");
        let host = |size_bytes, modified_at| {
            vec![HostIso {
                name: "Debian.iso",
                size_bytes,
                modified_at,
            }]
        };

        assert_eq!(
            iso_state(&link, &host(10, "2026-10-23T10:00:00.000+00:00")),
            LinkState::InSync
        );
        assert_eq!(
            iso_state(&link, &host(11, "2026-10-23T10:00:00Z")),
            LinkState::HostNewer
        );
        assert_eq!(
            iso_state(&link, &host(10, "2026-10-23T11:00:00Z")),
            LinkState::HostNewer
        );
        assert_eq!(iso_state(&link, &[]), LinkState::MissingOnHost);
    }

    #[test]
    fn profile_links_tell_which_side_changed() {
        let host = |updated_at| {
            vec![HostProfile {
                id: "dev-box",
                updated_at,
            }]
        };
        let link = profile_link("host-a", "h1", "2026-10-23T10:00:00.1234567Z");

        assert_eq!(
            profile_state("h1", &link, &host("2026-10-23T10:00:00.1234567+00:00")),
            LinkState::InSync
        );
        assert_eq!(
            profile_state("h2", &link, &host("2026-10-23T10:00:00.1234567Z")),
            LinkState::LibraryNewer
        );
        assert_eq!(
            profile_state("h1", &link, &host("2026-10-23T12:00:00Z")),
            LinkState::HostNewer
        );
        assert_eq!(
            profile_state("h2", &link, &host("2026-10-23T12:00:00Z")),
            LinkState::BothChanged
        );
        assert_eq!(profile_state("h1", &link, &[]), LinkState::MissingOnHost);
    }

    #[test]
    fn statuses_cover_only_the_given_host() {
        let mut manifest = Manifest::default();
        manifest.profiles.push(LibraryProfile {
            id: "p".into(),
            name: "A".into(),
            os: "windows".into(),
            description: None,
            hash: "h1".into(),
            images: vec![],
            updated_at: 1,
            links: vec![
                profile_link("host-a", "h1", "2026-10-23T10:00:00Z"),
                profile_link("host-b", "h0", "2026-10-23T10:00:00Z"),
            ],
        });

        let states = statuses(
            &manifest,
            "host-b",
            &[],
            &[HostProfile {
                id: "dev-box",
                updated_at: "2026-10-23T10:00:00Z",
            }],
        );

        assert_eq!(
            states,
            vec![LinkStatus {
                item_id: "p".into(),
                host_id: "host-b".into(),
                state: LinkState::LibraryNewer
            }]
        );
    }

    #[test]
    fn timestamps_parse_with_offsets_and_fractions() {
        assert_eq!(instant("1970-01-01T00:00:00Z"), Some(0));
        assert_eq!(instant("1970-01-01T01:00:00+01:00"), Some(0));
        assert_eq!(instant("1970-01-01T00:00:01.5Z"), Some(1_500_000_000));
        assert_eq!(
            instant("2000-03-01T00:00:00Z"),
            Some(951_868_800 * 1_000_000_000)
        );
        assert_eq!(instant("yesterday"), None);
    }

    #[test]
    fn names_that_leave_the_folder_are_refused() {
        let store = LibraryStore::new(temp_root());
        assert!(store.iso_file("..\\x.iso").is_err());
        assert!(store.iso_file("a/b.iso").is_err());
        assert!(store.iso_file(".hidden.iso").is_err());
        assert!(store.image_file("..png").is_err());
        assert!(store.image_file("c:x.png").is_err());
        assert!(store.image_file("harbor night.png").is_ok());
    }
}
