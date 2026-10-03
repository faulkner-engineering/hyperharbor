use std::net::IpAddr;
use std::sync::Arc;

use mdns_sd::{ResolvedService, ServiceDaemon, ServiceEvent};

use crate::hosts::{
    instance_label, is_guid, is_local_host_name, HostEntry, HostRegistry, HostSource, SERVICE_TYPE,
    TXT_API_VERSION, TXT_HOST_ID,
};

/// Browses for HyperHarbor hosts on a background thread and calls `on_change` whenever the
/// registry changes. The returned daemon must be kept alive for browsing to continue.
pub fn start(
    registry: Arc<HostRegistry>,
    on_change: impl Fn() + Send + 'static,
) -> Result<ServiceDaemon, mdns_sd::Error> {
    let daemon = ServiceDaemon::new()?;
    let receiver = daemon.browse(SERVICE_TYPE)?;

    std::thread::Builder::new()
        .name("mdns-browse".into())
        .spawn(move || {
            while let Ok(event) = receiver.recv() {
                let changed = match event {
                    ServiceEvent::ServiceResolved(service) => {
                        match to_entry(&service, registry.local_host_name()) {
                            Some(entry) => {
                                registry.upsert_discovered(service.get_fullname(), entry)
                            }
                            None => false,
                        }
                    }
                    ServiceEvent::ServiceRemoved(_, full_name) => {
                        registry.remove_discovered(&full_name)
                    }
                    _ => false,
                };

                if changed {
                    on_change();
                }
            }
        })
        .expect("failed to spawn the mDNS browse thread");

    Ok(daemon)
}

/// Longest display name kept from an mDNS instance name.
const MAX_DISPLAY_NAME: usize = 64;

/// What an mDNS announcement says, before validation.
struct Announcement<'a> {
    full_name: &'a str,
    host_name: &'a str,
    port: u16,
    addresses: Vec<IpAddr>,
    host_id: Option<&'a str>,
    api_version: Option<&'a str>,
}

fn to_entry(service: &ResolvedService, local_host_name: &str) -> Option<HostEntry> {
    entry_from(
        Announcement {
            full_name: service.get_fullname(),
            host_name: service.get_hostname(),
            port: service.get_port(),
            addresses: service
                .get_addresses()
                .iter()
                .map(|scoped| scoped.to_ip_addr())
                .collect(),
            host_id: service.get_property_val_str(TXT_HOST_ID),
            api_version: service.get_property_val_str(TXT_API_VERSION),
        },
        local_host_name,
    )
}

/// Builds a host entry from an announcement. Anyone on the LAN can announce, so values that become
/// keys, labels, or URLs are checked: an invalid host ID or version is dropped, the label is cleaned,
/// and an announcement with port 0 is ignored.
fn entry_from(announcement: Announcement<'_>, local_host_name: &str) -> Option<HostEntry> {
    if announcement.port == 0 {
        return None;
    }

    let host_id = announcement
        .host_id
        .filter(|id| is_guid(id))
        .map(str::to_string);
    let api_version = announcement
        .api_version
        .filter(|version| {
            !version.is_empty()
                && version.len() <= 32
                && version
                    .chars()
                    .all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '-' | '+'))
        })
        .map(str::to_string);
    let raw_host_name = announcement.host_name.trim_end_matches('.');
    // Windows machine names may contain underscores, so this is looser than a DNS host name.
    let host_name = (raw_host_name.len() <= 253
        && raw_host_name.split('.').all(|label| {
            !label.is_empty()
                && label.len() <= 63
                && label
                    .chars()
                    .all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '_'))
        }))
    .then(|| raw_host_name.to_string());

    let mut addresses: Vec<String> = announcement
        .addresses
        .iter()
        .filter(|ip| !ip.is_loopback() && !ip.is_unspecified() && !ip.is_multicast())
        .map(|ip| ip.to_string())
        .collect();
    // IPv4 first, then lexical, so the list is stable between announcements.
    addresses.sort_by_key(|address| (address.contains(':'), address.clone()));
    addresses.dedup();

    let display_name: String = instance_label(announcement.full_name)
        .chars()
        .filter(|c| !c.is_control())
        .take(MAX_DISPLAY_NAME)
        .collect();

    Some(HostEntry {
        key: match &host_id {
            Some(id) => format!("mdns:{id}"),
            None => format!("mdns:{}", announcement.full_name),
        },
        display_name,
        is_local: is_local_host_name(raw_host_name, local_host_name),
        host_id,
        host_name,
        addresses,
        port: announcement.port,
        api_version,
        source: HostSource::Discovered,
        paired: false,
        can_wake: false,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const HOST_ID: &str = "6f1c2d3e-4a5b-4c6d-8e7f-9a0b1c2d3e4f";

    fn announcement<'a>() -> Announcement<'a> {
        Announcement {
            full_name: "TC-PC._hyperharbor._tcp.local.",
            host_name: "TC-PC.local.",
            port: 48443,
            addresses: vec![
                "fe80::1".parse().unwrap(),
                "192.168.1.20".parse().unwrap(),
                "127.0.0.1".parse().unwrap(),
                "192.168.1.20".parse().unwrap(),
            ],
            host_id: Some(HOST_ID),
            api_version: Some("1.1.0"),
        }
    }

    #[test]
    fn announcement_becomes_a_keyed_entry() {
        let entry = entry_from(announcement(), "OTHER-PC").unwrap();

        assert_eq!(entry.key, format!("mdns:{HOST_ID}"));
        assert_eq!(entry.display_name, "TC-PC");
        assert_eq!(entry.host_name.as_deref(), Some("TC-PC.local"));
        assert_eq!(entry.addresses, vec!["192.168.1.20", "fe80::1"]);
        assert_eq!(entry.api_version.as_deref(), Some("1.1.0"));
        assert!(!entry.is_local);
    }

    #[test]
    fn own_host_name_is_local() {
        assert!(entry_from(announcement(), "TC-PC").unwrap().is_local);
    }

    #[test]
    fn invalid_host_id_falls_back_to_the_service_name() {
        for host_id in [
            "not-a-guid",
            "",
            "../../x",
            "6f1c2d3e-4a5b-4c6d-8e7f-9a0b1c2d3e4f\n",
        ] {
            let entry = entry_from(
                Announcement {
                    host_id: Some(host_id),
                    ..announcement()
                },
                "OTHER-PC",
            )
            .unwrap();
            assert_eq!(entry.host_id, None, "{host_id:?}");
            assert_eq!(entry.key, "mdns:TC-PC._hyperharbor._tcp.local.");
        }
    }

    #[test]
    fn hostile_text_values_are_dropped_or_cleaned() {
        let long_version = "1".repeat(33);
        let entry = entry_from(
            Announcement {
                full_name: "Evil\r\nHost\u{7}._hyperharbor._tcp.local.",
                host_name: "bad host/name.local.",
                api_version: Some(&long_version),
                ..announcement()
            },
            "OTHER-PC",
        )
        .unwrap();

        assert_eq!(entry.display_name, "EvilHost");
        assert_eq!(entry.host_name, None);
        assert_eq!(entry.api_version, None);
    }

    #[test]
    fn display_name_is_capped() {
        let full_name = format!("{}._hyperharbor._tcp.local.", "x".repeat(500));
        let entry = entry_from(
            Announcement {
                full_name: &full_name,
                ..announcement()
            },
            "OTHER-PC",
        )
        .unwrap();

        assert_eq!(entry.display_name.chars().count(), MAX_DISPLAY_NAME);
    }

    #[test]
    fn port_zero_is_ignored() {
        assert!(entry_from(
            Announcement {
                port: 0,
                ..announcement()
            },
            "OTHER-PC"
        )
        .is_none());
    }

    #[test]
    fn unusable_addresses_are_dropped() {
        let entry = entry_from(
            Announcement {
                addresses: vec![
                    "0.0.0.0".parse().unwrap(),
                    "224.0.0.251".parse().unwrap(),
                    "::1".parse().unwrap(),
                ],
                ..announcement()
            },
            "OTHER-PC",
        )
        .unwrap();

        assert!(entry.addresses.is_empty());
    }

    /// Live check against hosts on the network. Run with: cargo test live_browse -- --ignored --nocapture
    #[test]
    #[ignore]
    fn live_browse() {
        let local = gethostname::gethostname().to_string_lossy().into_owned();
        let registry = Arc::new(HostRegistry::new(local, None));
        let daemon = start(registry.clone(), || {}).expect("start browsing");

        std::thread::sleep(std::time::Duration::from_secs(5));
        let hosts = registry.list();
        for host in &hosts {
            println!("{host:#?}");
        }
        let _ = daemon.shutdown();
        assert!(!hosts.is_empty(), "no HyperHarbor hosts were discovered");
    }
}
