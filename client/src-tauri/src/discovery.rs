use std::sync::Arc;

use mdns_sd::{ResolvedService, ServiceDaemon, ServiceEvent};

use crate::hosts::{
    instance_label, is_local_host_name, HostEntry, HostRegistry, HostSource, SERVICE_TYPE,
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
                        let entry = to_entry(&service, registry.local_host_name());
                        registry.upsert_discovered(service.get_fullname(), entry)
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

fn to_entry(service: &ResolvedService, local_host_name: &str) -> HostEntry {
    let host_id = service
        .get_property_val_str(TXT_HOST_ID)
        .map(str::to_string);
    let full_name = service.get_fullname();

    let mut addresses: Vec<String> = service
        .get_addresses()
        .iter()
        .map(|scoped| scoped.to_ip_addr())
        .filter(|ip| !ip.is_loopback())
        .map(|ip| ip.to_string())
        .collect();
    // IPv4 first, then lexical, so the list is stable between announcements.
    addresses.sort_by_key(|address| (address.contains(':'), address.clone()));

    HostEntry {
        key: match &host_id {
            Some(id) => format!("mdns:{id}"),
            None => format!("mdns:{full_name}"),
        },
        display_name: instance_label(full_name),
        host_id,
        host_name: Some(service.get_hostname().trim_end_matches('.').to_string()),
        addresses,
        port: service.get_port(),
        api_version: service
            .get_property_val_str(TXT_API_VERSION)
            .map(str::to_string),
        source: HostSource::Discovered,
        is_local: is_local_host_name(service.get_hostname(), local_host_name),
        paired: false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

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
