//! Remote Desktop and console windows this app launched that are still open, by host. While a host has any,
//! the app checks in with it about once a minute: after a Wake-on-LAN wake nobody is at the host, so it stays
//! awake only while paired devices keep using it, and a session to a VM does not pass through the host.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;

/// How often hosts with open sessions are told they are still in use (the host waits 10 minutes).
pub const CHECK_IN_INTERVAL: Duration = Duration::from_secs(60);

#[derive(Clone, Default)]
pub struct ActiveSessions {
    counts: Arc<Mutex<HashMap<String, usize>>>,
}

impl ActiveSessions {
    /// Counts a session to the host with this key until the guard is dropped (when mstsc exits).
    pub fn begin(&self, key: &str) -> SessionGuard {
        *self
            .counts
            .lock()
            .unwrap()
            .entry(key.to_string())
            .or_insert(0) += 1;
        SessionGuard {
            sessions: self.clone(),
            key: key.to_string(),
        }
    }

    /// Keys of the hosts that have open sessions.
    pub fn keys(&self) -> Vec<String> {
        self.counts.lock().unwrap().keys().cloned().collect()
    }
}

pub struct SessionGuard {
    sessions: ActiveSessions,
    key: String,
}

impl Drop for SessionGuard {
    fn drop(&mut self) {
        let mut counts = self.sessions.counts.lock().unwrap();
        if let Some(count) = counts.get_mut(&self.key) {
            *count -= 1;
            if *count == 0 {
                counts.remove(&self.key);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_host_is_listed_until_its_last_session_ends() {
        let sessions = ActiveSessions::default();
        let first = sessions.begin("host-a");
        let second = sessions.begin("host-a");
        let other = sessions.begin("host-b");

        let mut keys = sessions.keys();
        keys.sort();
        assert_eq!(keys, ["host-a", "host-b"]);

        drop(first);
        drop(other);
        assert_eq!(sessions.keys(), ["host-a"]);

        drop(second);
        assert!(sessions.keys().is_empty());
    }
}
