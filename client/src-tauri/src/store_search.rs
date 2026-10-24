//! Opens an extension store's own search in the default browser. The stores have no public search API, so the
//! user searches there and pastes the extension's link back into the profile editor. Only the two store search
//! pages can be opened, with the query percent-encoded into the path.
#![allow(unsafe_code)]

use serde::Deserialize;
use windows::core::{w, HSTRING, PCWSTR};
use windows::Win32::UI::Shell::{ShellExecuteExW, SHELLEXECUTEINFOW};
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

use crate::error::ClientError;

#[derive(Debug, Clone, Copy, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum ExtensionStore {
    Chrome,
    Edge,
}

/// The store's search page for `query`.
pub fn search_url(store: ExtensionStore, query: &str) -> Result<String, ClientError> {
    let query = query.trim();
    if query.is_empty() || query.chars().count() > 100 || query.chars().any(char::is_control) {
        return Err(ClientError::InvalidRequest(
            "search for 1 to 100 characters".into(),
        ));
    }
    let encoded: String = query
        .bytes()
        .map(|byte| {
            if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_' | b'~') {
                (byte as char).to_string()
            } else {
                format!("%{byte:02X}")
            }
        })
        .collect();
    Ok(match store {
        ExtensionStore::Chrome => format!("https://chromewebstore.google.com/search/{encoded}"),
        ExtensionStore::Edge => {
            format!("https://microsoftedge.microsoft.com/addons/search/{encoded}")
        }
    })
}

/// Opens `url` with the default browser.
pub fn open(url: &str) -> Result<(), ClientError> {
    let file = HSTRING::from(url);
    let mut info = SHELLEXECUTEINFOW {
        cbSize: std::mem::size_of::<SHELLEXECUTEINFOW>() as u32,
        lpVerb: w!("open"),
        lpFile: PCWSTR(file.as_ptr()),
        nShow: SW_SHOWNORMAL.0,
        ..Default::default()
    };
    // SAFETY: `info` is fully initialised and its strings outlive the call; no process handle is requested.
    unsafe { ShellExecuteExW(&mut info) }.map_err(|e| {
        ClientError::InvalidRequest(format!(
            "the browser could not be opened: {}",
            e.message().trim()
        ))
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_query_is_encoded_into_the_stores_search_page() {
        assert_eq!(
            search_url(ExtensionStore::Chrome, " dark reader ").unwrap(),
            "https://chromewebstore.google.com/search/dark%20reader"
        );
        assert_eq!(
            search_url(ExtensionStore::Edge, "a/b?c#d").unwrap(),
            "https://microsoftedge.microsoft.com/addons/search/a%2Fb%3Fc%23d"
        );
    }

    #[test]
    fn empty_long_and_control_queries_are_refused() {
        assert!(search_url(ExtensionStore::Chrome, "  ").is_err());
        assert!(search_url(ExtensionStore::Chrome, &"x".repeat(101)).is_err());
        assert!(search_url(ExtensionStore::Chrome, "a\nb").is_err());
    }
}
