//! Tailscale on this PC. The client reads the Tailscale client that is already installed (it never installs or signs in
//! to anything), finds HyperHarbor hosts among the tailnet's online peers, merges them with the hosts found on the LAN,
//! and decides which address to try first. Everything that decides is a pure function; the local API, the command
//! line, and the certificate probe sit behind small traits so the tests need no Tailscale.

use std::collections::HashMap;
use std::future::Future;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr};
use std::path::PathBuf;
use std::sync::Mutex;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};

use crate::error::ClientError;
use crate::hosts::{HostEntry, HostSource, DEFAULT_PORT};
use crate::paired::PairedHost;

/// The tag clients look at first when they probe the tailnet. The host's setup card shows the command that sets it.
pub const HOST_TAG: &str = "tag:hyperharbor-host";

/// The Tailscale client's local API port when nothing else is configured.
pub const DEFAULT_LOCAL_API_PORT: u16 = 41112;

/// At most this many peers are probed in one scan.
const MAX_PROBES: usize = 32;

/// How long a probe answer is trusted: a host that answered stays known for a while, one that did not is
/// tried again sooner.
const FOUND_TTL: Duration = Duration::from_secs(10 * 60);
const MISSING_TTL: Duration = Duration::from_secs(2 * 60);

/// A request that worked over the tailnet keeps being tried there first for this long, so a PC away from home
/// does not wait for the LAN address to time out on every request.
pub const TAILNET_STICKY: Duration = Duration::from_secs(60);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum TailnetState {
    NotInstalled,
    Unreachable,
    NeedsLogin,
    Stopped,
    Running,
    Other,
}

/// How packets reach a peer. An idle or offline peer reports neither.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum TailnetPath {
    Unknown,
    Direct,
    Relayed,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TailnetNode {
    /// The stable node ID, which survives key rotation.
    pub node_id: String,
    pub host_name: String,
    /// The MagicDNS name without the trailing dot.
    pub dns_name: String,
    pub addresses: Vec<String>,
    pub tags: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TailnetPeer {
    pub node: TailnetNode,
    pub online: bool,
    pub path: TailnetPath,
    /// The relay region while the path is relayed.
    pub relay: Option<String>,
}

impl TailnetPeer {
    pub fn ipv4(&self) -> Option<&str> {
        first_ipv4(&self.node.addresses)
    }

    pub fn tagged(&self) -> bool {
        self.node.tags.iter().any(|tag| tag == HOST_TAG)
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TailnetStatus {
    pub state: TailnetState,
    #[serde(rename = "self")]
    pub self_node: Option<TailnetNode>,
    pub magic_dns_suffix: Option<String>,
    pub peers: Vec<TailnetPeer>,
    pub error: Option<String>,
}

impl TailnetStatus {
    pub fn missing(state: TailnetState, error: Option<String>) -> Self {
        Self {
            state,
            self_node: None,
            magic_dns_suffix: None,
            peers: Vec::new(),
            error,
        }
    }

    pub fn running(&self) -> bool {
        self.state == TailnetState::Running
    }
}

/// What the client keeps on a host list entry for a host found on the tailnet.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TailnetInfo {
    pub addresses: Vec<String>,
    pub dns_name: Option<String>,
    pub node_id: String,
    pub tagged: bool,
    pub online: bool,
    pub path: TailnetPath,
    pub relay: Option<String>,
}

/// Tailscale's ranges: 100.64.0.0/10 and fd7a:115c:a1e0::/48.
pub fn is_tailnet_ip(address: &str) -> bool {
    match address.parse::<IpAddr>() {
        Ok(IpAddr::V4(v4)) => is_tailnet_v4(v4),
        Ok(IpAddr::V6(v6)) => v6
            .to_ipv4_mapped()
            .map_or_else(|| is_tailnet_v6(v6), is_tailnet_v4),
        Err(_) => false,
    }
}

fn is_tailnet_v4(address: Ipv4Addr) -> bool {
    let [a, b, ..] = address.octets();
    a == 100 && (b & 0xC0) == 64
}

fn is_tailnet_v6(address: Ipv6Addr) -> bool {
    address.segments()[..3] == [0xFD7A, 0x115C, 0xA1E0]
}

pub fn first_ipv4(addresses: &[String]) -> Option<&str> {
    addresses
        .iter()
        .find(|address| address.parse::<Ipv4Addr>().is_ok())
        .map(String::as_str)
}

/// The host part of "https://host:port", with brackets removed from an IPv6 address.
fn url_host(url: &str) -> &str {
    let rest = url.split_once("://").map_or(url, |(_, rest)| rest);
    let authority = rest.split('/').next().unwrap_or(rest);
    if let Some(inner) = authority.strip_prefix('[') {
        return inner.split(']').next().unwrap_or(inner);
    }
    authority
        .rsplit_once(':')
        .map_or(authority, |(host, _)| host)
}

pub fn url_is_tailnet(url: &str) -> bool {
    is_tailnet_ip(url_host(url))
}

// ----- Reading the local Tailscale client ----------------------------------------------------------------

fn text<'a>(value: &'a serde_json::Value, name: &str) -> &'a str {
    value.get(name).and_then(|v| v.as_str()).unwrap_or("")
}

fn strings(value: &serde_json::Value, name: &str) -> Vec<String> {
    value
        .get(name)
        .and_then(|v| v.as_array())
        .map(|items| {
            items
                .iter()
                .filter_map(|item| item.as_str().map(str::to_string))
                .collect()
        })
        .unwrap_or_default()
}

fn node_of(value: &serde_json::Value, fallback_id: &str) -> TailnetNode {
    let id = match text(value, "ID") {
        "" => text(value, "StableID"),
        id => id,
    };
    TailnetNode {
        node_id: if id.is_empty() { fallback_id } else { id }.to_string(),
        host_name: text(value, "HostName").to_string(),
        dns_name: text(value, "DNSName").trim_end_matches('.').to_string(),
        addresses: strings(value, "TailscaleIPs"),
        tags: strings(value, "Tags"),
    }
}

/// Reads the JSON of `tailscale status --json` and of the local API's `/localapi/v0/status`. Unknown fields are
/// ignored, because Tailscale adds fields in every release.
pub fn parse_status(json: &str) -> TailnetStatus {
    let Ok(root) = serde_json::from_str::<serde_json::Value>(json) else {
        return TailnetStatus::missing(
            TailnetState::Unreachable,
            Some("Tailscale returned text that is not JSON.".into()),
        );
    };
    if !root.is_object() {
        return TailnetStatus::missing(
            TailnetState::Unreachable,
            Some("Tailscale returned an unexpected status.".into()),
        );
    }

    let state = match text(&root, "BackendState") {
        "Running" => TailnetState::Running,
        "NeedsLogin" | "NoState" => TailnetState::NeedsLogin,
        "Stopped" => TailnetState::Stopped,
        _ => TailnetState::Other,
    };
    let suffix = match text(&root, "MagicDNSSuffix") {
        "" => root
            .get("CurrentTailnet")
            .map_or("", |tailnet| text(tailnet, "MagicDNSSuffix")),
        suffix => suffix,
    };

    let self_node = if state == TailnetState::Running {
        root.get("Self")
            .filter(|node| node.is_object())
            .map(|node| node_of(node, ""))
    } else {
        None
    };

    let mut peers: Vec<TailnetPeer> = root
        .get("Peer")
        .and_then(|peers| peers.as_object())
        .map(|map| {
            map.iter()
                .filter(|(_, peer)| peer.is_object())
                .map(|(key, peer)| peer_of(peer, key))
                .collect()
        })
        .unwrap_or_default();
    peers.sort_by_key(|peer| peer.node.host_name.to_lowercase());

    TailnetStatus {
        state,
        self_node,
        magic_dns_suffix: (!suffix.is_empty()).then(|| suffix.to_string()),
        peers,
        error: None,
    }
}

fn peer_of(value: &serde_json::Value, key: &str) -> TailnetPeer {
    let online = value.get("Online").and_then(|v| v.as_bool()) == Some(true);
    let active = value.get("Active").and_then(|v| v.as_bool()) == Some(true);
    let current = text(value, "CurAddr");
    let relay = text(value, "Relay");

    // Only an active peer has a path in use. Tailscale prints "-" for an idle one, although Relay still names the
    // peer's home relay region then (seen on a real tailnet), so Relay alone does not mean DERP. For an active peer,
    // a set CurAddr is a direct path and otherwise a set Relay is the relay in use.
    let path = if !online || !active {
        TailnetPath::Unknown
    } else if !current.is_empty() {
        TailnetPath::Direct
    } else if !relay.is_empty() {
        TailnetPath::Relayed
    } else {
        TailnetPath::Unknown
    };
    TailnetPeer {
        node: node_of(value, key),
        online,
        path,
        relay: (path == TailnetPath::Relayed).then(|| relay.to_string()),
    }
}

/// What a source of Tailscale status answered.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Reply {
    Ok(String),
    /// The program does not exist on this PC.
    NotInstalled,
    Failed(String),
}

pub trait StatusSource: Send + Sync {
    fn status(&self) -> impl Future<Output = Reply> + Send;
}

/// The local API over TCP on the loopback address. On Windows the client's own local API is usually a named pipe, so
/// this answers only for setups that expose a port; the command line is the dependable route there.
pub struct HttpSource {
    pub port: u16,
}

impl StatusSource for HttpSource {
    async fn status(&self) -> Reply {
        let client = match reqwest::Client::builder()
            .no_proxy()
            .timeout(Duration::from_secs(2))
            .build()
        {
            Ok(client) => client,
            Err(error) => return Reply::Failed(error.to_string()),
        };
        let response = client
            .get(format!("http://127.0.0.1:{}/localapi/v0/status", self.port))
            .header("Sec-Tailscale", "localapi")
            .send()
            .await;
        match response {
            Ok(response) if response.status().is_success() => match response.text().await {
                Ok(body) => Reply::Ok(body),
                Err(error) => Reply::Failed(error.to_string()),
            },
            Ok(response) => Reply::Failed(format!(
                "The Tailscale local API answered {}.",
                response.status().as_u16()
            )),
            Err(_) => Reply::Failed("The Tailscale local API did not answer.".into()),
        }
    }
}

/// `tailscale status --json`.
pub struct CliSource;

impl CliSource {
    fn candidates() -> Vec<PathBuf> {
        let mut candidates = Vec::new();
        for variable in ["ProgramFiles", "ProgramFiles(x86)"] {
            if let Some(root) = std::env::var_os(variable) {
                candidates.push(PathBuf::from(root).join("Tailscale").join("tailscale.exe"));
            }
        }
        candidates.push(PathBuf::from("tailscale"));
        candidates
    }

    fn run() -> Reply {
        for candidate in Self::candidates() {
            if candidate.is_absolute() && !candidate.exists() {
                continue;
            }
            let mut command = std::process::Command::new(&candidate);
            command
                .args(["status", "--json"])
                .stdin(std::process::Stdio::null());
            #[cfg(windows)]
            {
                use std::os::windows::process::CommandExt;
                command.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
            }
            let Ok(output) = command.output() else {
                continue;
            };
            let body = String::from_utf8_lossy(&output.stdout).into_owned();
            // `status --json` exits 1 with valid JSON while Tailscale is stopped; the JSON is what matters.
            if body.trim_start().starts_with('{') {
                return Reply::Ok(body);
            }
            let error = String::from_utf8_lossy(&output.stderr);
            let line = error.lines().next().unwrap_or("").trim();
            return Reply::Failed(if line.is_empty() {
                format!("tailscale exited with {}.", output.status)
            } else {
                line.chars().take(200).collect()
            });
        }
        Reply::NotInstalled
    }
}

impl StatusSource for CliSource {
    async fn status(&self) -> Reply {
        match tokio::time::timeout(
            Duration::from_secs(10),
            tokio::task::spawn_blocking(Self::run),
        )
        .await
        {
            Ok(Ok(reply)) => reply,
            _ => Reply::Failed("tailscale did not answer in time.".into()),
        }
    }
}

/// The status in a reply, unless the reply is not a usable status.
fn usable(reply: &Reply) -> Option<TailnetStatus> {
    let Reply::Ok(body) = reply else { return None };
    let status = parse_status(body);
    (status.state != TailnetState::Unreachable).then_some(status)
}

/// Sends a few Tailscale pings to an address, so Tailscale settles on a path (direct or relayed) before the status is
/// read: an idle peer reports no path at all. Best effort; the answer is not used, only the status afterwards.
pub async fn warm_path(address: &str) {
    let address = address.to_string();
    let _ = tokio::time::timeout(
        Duration::from_secs(12),
        tokio::task::spawn_blocking(move || {
            for candidate in CliSource::candidates() {
                if candidate.is_absolute() && !candidate.exists() {
                    continue;
                }
                let mut command = std::process::Command::new(&candidate);
                command
                    .args(["ping", "--c", "3", "--timeout", "3s", &address])
                    .stdin(std::process::Stdio::null())
                    .stdout(std::process::Stdio::null())
                    .stderr(std::process::Stdio::null());
                #[cfg(windows)]
                {
                    use std::os::windows::process::CommandExt;
                    command.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
                }
                if command.status().is_ok() {
                    return;
                }
            }
        }),
    )
    .await;
}

/// Reads the status from the local API, then from the command line. "Not installed" is decided by the command line,
/// the last source: a local API that does not answer proves nothing.
pub async fn read_status(http: &impl StatusSource, cli: &impl StatusSource) -> TailnetStatus {
    let reply = http.status().await;
    if let Some(status) = usable(&reply) {
        return status;
    }

    let reply = cli.status().await;
    if let Some(status) = usable(&reply) {
        return status;
    }
    match reply {
        Reply::NotInstalled => TailnetStatus::missing(TailnetState::NotInstalled, None),
        Reply::Failed(error) => TailnetStatus::missing(TailnetState::Unreachable, Some(error)),
        Reply::Ok(body) => parse_status(&body),
    }
}

// ----- Settings ------------------------------------------------------------------------------------------

/// Where this PC finds its Tailscale client. Stored in the app config directory; nothing in it is secret.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TailnetSettings {
    pub local_api_port: u16,
}

impl Default for TailnetSettings {
    fn default() -> Self {
        Self {
            local_api_port: DEFAULT_LOCAL_API_PORT,
        }
    }
}

pub struct TailnetSettingsStore {
    path: Option<PathBuf>,
    settings: Mutex<TailnetSettings>,
}

impl TailnetSettingsStore {
    pub fn new(path: Option<PathBuf>) -> Self {
        let settings = path
            .as_ref()
            .and_then(|path| std::fs::read_to_string(path).ok())
            .and_then(|text| serde_json::from_str(&text).ok())
            .unwrap_or_default();
        Self {
            path,
            settings: Mutex::new(settings),
        }
    }

    pub fn get(&self) -> TailnetSettings {
        *self.settings.lock().unwrap()
    }

    pub fn set(&self, settings: TailnetSettings) -> Result<(), ClientError> {
        if settings.local_api_port == 0 {
            return Err(ClientError::InvalidRequest(
                "The Tailscale local API port must be between 1 and 65535.".into(),
            ));
        }
        *self.settings.lock().unwrap() = settings;
        let Some(path) = &self.path else {
            return Ok(());
        };
        let text = serde_json::to_string_pretty(&settings)
            .map_err(|e| ClientError::Storage(e.to_string()))?;
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(|e| ClientError::Storage(e.to_string()))?;
        }
        std::fs::write(path, text).map_err(|e| ClientError::Storage(e.to_string()))
    }
}

// ----- Finding hosts on the tailnet ----------------------------------------------------------------------

/// Which online peers are worth a probe on the HyperHarbor port. Peers with the host tag come first and, when any
/// exist, are the only ones probed besides peers whose address a paired host used before. A tailnet with no tagged
/// peer probes every online peer, up to a limit.
pub fn probe_targets<'a>(
    status: &'a TailnetStatus,
    known_addresses: &[String],
) -> Vec<&'a TailnetPeer> {
    if !status.running() {
        return Vec::new();
    }
    let online: Vec<&TailnetPeer> = status
        .peers
        .iter()
        .filter(|peer| peer.online && peer.ipv4().is_some())
        .collect();
    let any_tagged = online.iter().any(|peer| peer.tagged());
    let mut targets: Vec<&TailnetPeer> = online
        .into_iter()
        .filter(|peer| {
            !any_tagged
                || peer.tagged()
                || peer
                    .node
                    .addresses
                    .iter()
                    .any(|address| known_addresses.contains(address))
        })
        .collect();
    targets.sort_by_key(|peer| !peer.tagged());
    targets.truncate(MAX_PROBES);
    targets
}

/// Probe answers, kept so a scan every half minute does not open a connection to every peer each time.
#[derive(Default)]
pub struct ProbeCache {
    entries: HashMap<String, (Option<[u8; 32]>, Instant)>,
}

impl ProbeCache {
    /// The remembered answer while it is fresh: `Some(None)` is a remembered "no HyperHarbor host here".
    pub fn lookup(&self, address: &str, port: u16, now: Instant) -> Option<Option<[u8; 32]>> {
        let (result, at) = self.entries.get(&format!("{address}:{port}"))?;
        let lifetime = if result.is_some() {
            FOUND_TTL
        } else {
            MISSING_TTL
        };
        (now.saturating_duration_since(*at) < lifetime).then_some(*result)
    }

    pub fn store(&mut self, address: &str, port: u16, result: Option<[u8; 32]>, now: Instant) {
        self.entries
            .insert(format!("{address}:{port}"), (result, now));
    }
}

/// Connects to an address and returns the SHA-256 of the certificate the API presented, or None when nothing
/// that speaks TLS answers on the port.
pub trait CertProbe: Send + Sync {
    fn probe(&self, address: &str, port: u16) -> impl Future<Output = Option<[u8; 32]>> + Send;
}

/// A HyperHarbor host found by probing a peer.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TailnetHost {
    pub addresses: Vec<String>,
    pub port: u16,
    pub dns_name: String,
    pub host_name: String,
    pub node_id: String,
    pub tagged: bool,
    pub path: TailnetPath,
    pub relay: Option<String>,
    /// SHA-256 of the certificate, uppercase hex: the same form as a pairing's pinned fingerprint.
    pub fingerprint: String,
}

/// Probes the chosen peers, using the cache where it is fresh.
pub async fn scan(
    status: &TailnetStatus,
    known_addresses: &[String],
    ports: &[u16],
    probe: &impl CertProbe,
    cache: &Mutex<ProbeCache>,
    now: Instant,
) -> Vec<TailnetHost> {
    let mut jobs = Vec::new();
    for peer in probe_targets(status, known_addresses) {
        let Some(address) = peer.ipv4() else { continue };
        for &port in ports {
            jobs.push((peer, address, port));
        }
    }

    let answers =
        futures_util::future::join_all(jobs.iter().map(|(_, address, port)| async move {
            let cached = cache.lock().unwrap().lookup(address, *port, now);
            match cached {
                Some(answer) => answer,
                None => {
                    let answer = probe.probe(address, *port).await;
                    cache.lock().unwrap().store(address, *port, answer, now);
                    answer
                }
            }
        }))
        .await;

    jobs.iter()
        .zip(answers)
        .filter_map(|((peer, _, port), answer)| {
            answer.map(|hash| TailnetHost {
                addresses: peer.node.addresses.clone(),
                port: *port,
                dns_name: peer.node.dns_name.clone(),
                host_name: peer.node.host_name.clone(),
                node_id: peer.node.node_id.clone(),
                tagged: peer.tagged(),
                path: peer.path,
                relay: peer.relay.clone(),
                fingerprint: hex::encode_upper(hash),
            })
        })
        .collect()
}

/// Adds the hosts found on the tailnet to the host list. A host that is already listed (paired, announced on the LAN,
/// or typed in) gets its Tailscale addresses added to its own entry, so it appears once with both. A paired host
/// that is only reachable over the tailnet is listed too. An unknown host is listed under its Tailscale name.
pub fn merge_tailnet(
    hosts: &mut Vec<HostEntry>,
    found: &[TailnetHost],
    paired: &[PairedHost],
    local_host_name: &str,
) {
    for host in found {
        let fingerprint_owner = paired.iter().find(|candidate| {
            candidate
                .host_certificate_fingerprint
                .eq_ignore_ascii_case(&host.fingerprint)
        });

        let index = if let Some(owner) = fingerprint_owner {
            match hosts.iter().position(|entry| {
                entry
                    .host_id
                    .as_deref()
                    .is_some_and(|id| id.eq_ignore_ascii_case(&owner.host_id))
                    || owner.entry_keys.contains(&entry.key)
            }) {
                Some(index) => index,
                None => {
                    hosts.push(owner.remembered_entry(local_host_name));
                    hosts.len() - 1
                }
            }
        } else if let Some(index) = hosts
            .iter()
            .position(|entry| entry.addresses.iter().any(|a| host.addresses.contains(a)))
        {
            index
        } else {
            let name = if host.dns_name.is_empty() {
                host.host_name.clone()
            } else {
                host.dns_name.clone()
            };
            hosts.push(HostEntry {
                key: format!("tailnet:{name}"),
                display_name: host
                    .dns_name
                    .split('.')
                    .next()
                    .filter(|label| !label.is_empty())
                    .unwrap_or(&host.host_name)
                    .to_string(),
                host_id: None,
                host_name: Some(host.host_name.clone()),
                addresses: Vec::new(),
                port: host.port,
                api_version: None,
                source: HostSource::Tailnet,
                is_local: false,
                paired: false,
                can_wake: false,
                tailscale: None,
            });
            hosts.len() - 1
        };

        let entry = &mut hosts[index];
        for address in &host.addresses {
            if !entry.addresses.contains(address) {
                entry.addresses.push(address.clone());
            }
        }
        entry.tailscale = Some(TailnetInfo {
            addresses: host.addresses.clone(),
            dns_name: (!host.dns_name.is_empty()).then(|| host.dns_name.clone()),
            node_id: host.node_id.clone(),
            tagged: host.tagged,
            online: true,
            path: host.path,
            relay: host.relay.clone(),
        });
    }
}

// ----- Choosing an address -------------------------------------------------------------------------------

/// The order to try a host's base URLs in. The LAN (and loopback) comes first, because it is faster and does not
/// depend on Tailscale; the Tailscale address is the fallback. The address that worked last goes first within its
/// group, except that a Tailscale address that worked a moment ago stays first, so a PC away from home does not wait
/// for a LAN address to time out on every request.
pub fn order_candidates(
    candidates: Vec<String>,
    last_good: Option<&str>,
    tailnet_recent: bool,
) -> Vec<String> {
    let (mut tailnet, mut lan): (Vec<String>, Vec<String>) =
        candidates.into_iter().partition(|url| url_is_tailnet(url));

    let promote = |group: &mut Vec<String>| {
        if let Some(good) = last_good {
            if let Some(index) = group.iter().position(|url| url == good) {
                let url = group.remove(index);
                group.insert(0, url);
                return true;
            }
        }
        false
    };

    let good_is_tailnet = promote(&mut tailnet);
    if !good_is_tailnet {
        promote(&mut lan);
    }

    if good_is_tailnet && tailnet_recent {
        let first = tailnet.remove(0);
        std::iter::once(first).chain(lan).chain(tailnet).collect()
    } else {
        lan.into_iter().chain(tailnet).collect()
    }
}

/// How the last request reached the host, for the connection details.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConnectionRoute {
    /// "lan", "tailscale", or "unknown" (nothing has connected yet).
    pub via: &'static str,
    pub address: Option<String>,
    /// For "tailscale": how packets reach the host.
    pub path: Option<TailnetPath>,
    pub relay: Option<String>,
    /// Shown when the path is relayed.
    pub hint: Option<String>,
}

pub const RELAYED_HINT: &str = "This connection goes through a Tailscale relay, which adds delay. If this PC uses a VPN, exclude Tailscale from it (split tunneling) or allow UDP port 41641, so Tailscale can connect directly.";

/// The own_addresses are this PC's own Tailscale addresses: a host reached through one of them is this PC, and the
/// traffic never leaves it, so there is no path to report.
pub fn describe_route(
    entry: &HostEntry,
    last_good: Option<&str>,
    own_addresses: &[String],
) -> ConnectionRoute {
    let address = last_good.map(|url| url_host(url).to_string());
    let via = match &address {
        Some(address) if is_tailnet_ip(address) => "tailscale",
        Some(_) => "lan",
        None => "unknown",
    };
    let info = entry.tailscale.as_ref();
    let own = address.as_ref().is_some_and(|a| own_addresses.contains(a));
    let (path, relay) = if via == "tailscale" && !own {
        info.map_or((None, None), |info| (Some(info.path), info.relay.clone()))
    } else {
        (None, None)
    };
    ConnectionRoute {
        via,
        address,
        path,
        relay,
        hint: (path == Some(TailnetPath::Relayed)).then(|| RELAYED_HINT.to_string()),
    }
}

// ----- Waking through another host -----------------------------------------------------------------------

/// A paired host this PC can reach, with its list entry (how it was found).
pub struct RelayCandidate<'a> {
    pub paired: &'a PairedHost,
    pub entry: &'a HostEntry,
}

/// The paired hosts to ask, best first, to send the magic packet for a sleeping `target` on its LAN. A relay must be
/// another host, must be reachable now (announced on the LAN, found on the tailnet, or typed in; a host that is only
/// remembered is not), and must be on the target's network: one of its adapters has the target adapter's broadcast
/// address. Those found over a direct tailnet path come first, then the rest by name.
pub fn rank_wake_relays<'a>(
    target: &PairedHost,
    candidates: &[RelayCandidate<'a>],
) -> Vec<&'a PairedHost> {
    let networks: Vec<&str> = target
        .wake_adapters
        .iter()
        .map(|adapter| adapter.broadcast_address.as_str())
        .collect();

    let mut ranked: Vec<(&RelayCandidate<'a>, (u8, u8))> = candidates
        .iter()
        .filter(|candidate| {
            !candidate
                .paired
                .host_id
                .eq_ignore_ascii_case(&target.host_id)
        })
        .filter(|candidate| candidate.entry.source != HostSource::Remembered)
        .filter(|candidate| {
            candidate
                .paired
                .wake_adapters
                .iter()
                .any(|adapter| networks.contains(&adapter.broadcast_address.as_str()))
        })
        .map(|candidate| {
            let path = match candidate.entry.tailscale.as_ref().map(|info| info.path) {
                Some(TailnetPath::Direct) => 0,
                Some(TailnetPath::Unknown) | None => 1,
                Some(TailnetPath::Relayed) => 2,
            };
            let source = match candidate.entry.source {
                HostSource::Discovered => 0,
                HostSource::Tailnet => 1,
                _ => 2,
            };
            (candidate, (path, source))
        })
        .collect();
    ranked.sort_by(|a, b| {
        a.1.cmp(&b.1).then_with(|| {
            a.0.paired
                .display_name
                .to_lowercase()
                .cmp(&b.0.paired.display_name.to_lowercase())
        })
    });
    ranked
        .into_iter()
        .map(|(candidate, _)| candidate.paired)
        .collect()
}

// ----- The reachability test -----------------------------------------------------------------------------

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum StepStatus {
    Pass,
    Warn,
    Fail,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReachabilityStep {
    pub id: &'static str,
    pub title: &'static str,
    pub status: StepStatus,
    pub detail: Option<String>,
}

/// What the API answered when asked over the Tailscale address.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ApiCheck {
    /// Not paired, so the authenticated request was not tried.
    NotTried,
    Ok,
    /// 403 with a Tailscale node problem code; carries the host's message.
    NodeRefused(String),
    Failed(String),
}

/// What the TLS probe of the host's API port found.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CertCheck {
    NoAnswer,
    /// The certificate is the one pinned at pairing.
    Pinned,
    /// A different certificate than the pinned one.
    Different,
    /// The host is not paired yet, so there is nothing to compare with.
    Unpaired,
}

pub struct ReachabilityInput<'a> {
    pub status: &'a TailnetStatus,
    /// The host's Tailscale address, when this PC knows one.
    pub address: Option<&'a str>,
    pub port: u16,
    pub certificate: CertCheck,
    pub api: ApiCheck,
}

fn step(
    id: &'static str,
    title: &'static str,
    status: StepStatus,
    detail: Option<String>,
) -> ReachabilityStep {
    ReachabilityStep {
        id,
        title,
        status,
        detail,
    }
}

/// The checks in order, ending at the first failure.
pub fn build_reachability(input: &ReachabilityInput<'_>) -> Vec<ReachabilityStep> {
    let mut steps = Vec::new();

    let running = input.status.running();
    steps.push(step(
        "tailscale",
        "Tailscale on this PC",
        if running {
            StepStatus::Pass
        } else {
            StepStatus::Fail
        },
        (!running).then(|| match input.status.state {
            TailnetState::NotInstalled => "Tailscale is not installed on this PC.".to_string(),
            TailnetState::NeedsLogin => "Sign in to Tailscale on this PC.".to_string(),
            TailnetState::Stopped => "Tailscale is turned off on this PC.".to_string(),
            _ => input
                .status
                .error
                .clone()
                .unwrap_or_else(|| "Tailscale on this PC is not ready.".to_string()),
        }),
    ));
    if !running {
        return steps;
    }

    // A host on this PC is not a peer of its own tailnet node, and nothing crosses a network to reach it.
    let own = input.address.is_some_and(|address| {
        input
            .status
            .self_node
            .as_ref()
            .is_some_and(|node| node.addresses.iter().any(|a| a == address))
    });
    if own {
        steps.push(step(
            "peer",
            "Host on the tailnet",
            StepStatus::Pass,
            Some("The host is this PC, so no network is crossed. Test from another device to check the path.".into()),
        ));
        return finish_reachability(steps, input);
    }

    let peer = input.address.and_then(|address| {
        input
            .status
            .peers
            .iter()
            .find(|peer| peer.node.addresses.iter().any(|a| a == address))
    });
    let Some(peer) = peer else {
        steps.push(step(
            "peer",
            "Host on the tailnet",
            StepStatus::Fail,
            Some("Tailscale on this PC does not list this host. Check that the host runs Tailscale and is signed in to the same tailnet.".into()),
        ));
        return steps;
    };
    steps.push(step(
        "peer",
        "Host on the tailnet",
        StepStatus::Pass,
        Some(if peer.node.dns_name.is_empty() {
            peer.node.host_name.clone()
        } else {
            peer.node.dns_name.clone()
        }),
    ));

    steps.push(step(
        "online",
        "Host online",
        if peer.online {
            StepStatus::Pass
        } else {
            StepStatus::Fail
        },
        (!peer.online).then(|| {
            "Tailscale reports the host as offline. It may be asleep or not running Tailscale."
                .to_string()
        }),
    ));
    if !peer.online {
        return steps;
    }

    steps.push(match peer.path {
        TailnetPath::Direct => step(
            "path",
            "Connection path",
            StepStatus::Pass,
            Some("Direct".into()),
        ),
        TailnetPath::Relayed => step(
            "path",
            "Connection path",
            StepStatus::Warn,
            Some(format!(
                "Relayed{}. {RELAYED_HINT}",
                peer.relay
                    .as_deref()
                    .map(|r| format!(" through {r}"))
                    .unwrap_or_default()
            )),
        ),
        TailnetPath::Unknown => step(
            "path",
            "Connection path",
            StepStatus::Warn,
            Some("Not decided yet: Tailscale picks the path when the first packet is sent.".into()),
        ),
    });

    finish_reachability(steps, input)
}

/// The steps that follow the path: the API answers, the certificate matches, and a signed-in request works.
fn finish_reachability(
    mut steps: Vec<ReachabilityStep>,
    input: &ReachabilityInput<'_>,
) -> Vec<ReachabilityStep> {
    let answered = !matches!(input.certificate, CertCheck::NoAnswer);
    steps.push(step(
        "api",
        "HyperHarbor answers",
        if answered { StepStatus::Pass } else { StepStatus::Fail },
        Some(if answered {
            format!("Port {}", input.port)
        } else {
            format!("Nothing answered on port {}. Check that HyperHarbor runs on the host and that the Windows firewall allows the port for the Tailscale network.", input.port)
        }),
    ));
    if !answered {
        return steps;
    }

    match input.certificate {
        CertCheck::Pinned => steps.push(step(
            "identity",
            "Host identity",
            StepStatus::Pass,
            Some("The certificate matches the one pinned when you paired.".into()),
        )),
        CertCheck::Different => {
            steps.push(step(
                "identity",
                "Host identity",
                StepStatus::Fail,
                Some("The host presented a different certificate than the one pinned at pairing. Do not continue unless you reinstalled the host; then pair again.".into()),
            ));
            return steps;
        }
        _ => steps.push(step(
            "identity",
            "Host identity",
            StepStatus::Warn,
            Some("Not paired yet, so the certificate cannot be compared.".into()),
        )),
    }

    match &input.api {
        ApiCheck::Ok => steps.push(step("access", "Signed-in request", StepStatus::Pass, None)),
        ApiCheck::NodeRefused(message) => steps.push(step(
            "access",
            "Signed-in request",
            StepStatus::Fail,
            Some(message.clone()),
        )),
        ApiCheck::Failed(message) => steps.push(step(
            "access",
            "Signed-in request",
            StepStatus::Fail,
            Some(message.clone()),
        )),
        ApiCheck::NotTried => {}
    }
    steps
}

// ----- Keeping the scan up to date -----------------------------------------------------------------------

/// Everything the last refresh found. The host list is built from it without waiting for the network.
#[derive(Clone)]
pub struct Snapshot {
    pub status: TailnetStatus,
    pub hosts: Vec<TailnetHost>,
}

pub struct TailnetService {
    pub settings: TailnetSettingsStore,
    cache: Mutex<ProbeCache>,
    snapshot: Mutex<Snapshot>,
}

impl TailnetService {
    pub fn new(settings: TailnetSettingsStore) -> Self {
        Self {
            settings,
            cache: Mutex::new(ProbeCache::default()),
            snapshot: Mutex::new(Snapshot {
                status: TailnetStatus::missing(TailnetState::NotInstalled, None),
                hosts: Vec::new(),
            }),
        }
    }

    pub fn snapshot(&self) -> Snapshot {
        self.snapshot.lock().unwrap().clone()
    }

    /// Reads Tailscale and probes the peers. Returns true when the visible result changed.
    pub async fn refresh_with(
        &self,
        http: &impl StatusSource,
        cli: &impl StatusSource,
        probe: &impl CertProbe,
        paired: &[PairedHost],
        now: Instant,
    ) -> bool {
        let status = read_status(http, cli).await;
        let known: Vec<String> = paired
            .iter()
            .flat_map(|host| host.addresses.iter().cloned())
            .filter(|address| is_tailnet_ip(address))
            .collect();
        let mut ports = vec![DEFAULT_PORT];
        for host in paired {
            if !ports.contains(&host.port) {
                ports.push(host.port);
            }
        }
        let hosts = scan(&status, &known, &ports, probe, &self.cache, now).await;

        let mut snapshot = self.snapshot.lock().unwrap();
        let changed = snapshot.status != status || snapshot.hosts != hosts;
        *snapshot = Snapshot { status, hosts };
        changed
    }

    /// Reads only the status, ignoring the cache of probes: for the Tailscale dialog.
    pub async fn read_now(
        &self,
        http: &impl StatusSource,
        cli: &impl StatusSource,
    ) -> TailnetStatus {
        let status = read_status(http, cli).await;
        self.snapshot.lock().unwrap().status = status.clone();
        status
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::wake::WakeAdapter;

    // Shaped like `tailscale status --json` from Tailscale 1.7x, reduced to the fields read here.
    const STATUS: &str = r#"{
      "Version": "1.76.0",
      "BackendState": "Running",
      "MagicDNSSuffix": "tail1234.ts.net",
      "Self": { "ID": "nSELF", "HostName": "LAPTOP", "DNSName": "laptop.tail1234.ts.net.", "TailscaleIPs": ["100.64.0.9"] },
      "Peer": {
        "nodekey:a": { "ID": "nDIRECT", "HostName": "gaming-pc", "DNSName": "gaming-pc.tail1234.ts.net.", "TailscaleIPs": ["100.64.0.1", "fd7a:115c:a1e0::1"], "Tags": ["tag:hyperharbor-host"], "Online": true, "Active": true, "CurAddr": "203.0.113.5:41641", "Relay": "nyc" },
        "nodekey:b": { "ID": "nRELAY", "HostName": "den-pc", "DNSName": "den-pc.tail1234.ts.net.", "TailscaleIPs": ["100.64.0.2"], "Tags": ["tag:hyperharbor-host"], "Online": true, "Active": true, "CurAddr": "", "Relay": "fra" },
        "nodekey:c": { "ID": "nPHONE", "HostName": "phone", "DNSName": "phone.tail1234.ts.net.", "TailscaleIPs": ["100.64.0.3"], "Online": true, "Active": false, "CurAddr": "", "Relay": "ord" },
        "nodekey:d": { "ID": "nOFF", "HostName": "old-pc", "DNSName": "old-pc.tail1234.ts.net.", "TailscaleIPs": ["100.64.0.4"], "Tags": ["tag:hyperharbor-host"], "Online": false, "Active": true, "CurAddr": "198.51.100.9:41641" }
      }
    }"#;

    fn status() -> TailnetStatus {
        parse_status(STATUS)
    }

    fn peer<'a>(status: &'a TailnetStatus, name: &str) -> &'a TailnetPeer {
        status
            .peers
            .iter()
            .find(|p| p.node.host_name == name)
            .unwrap()
    }

    // ---- Parsing ----

    #[test]
    fn status_reads_self_and_peers() {
        let status = status();
        assert_eq!(status.state, TailnetState::Running);
        assert_eq!(status.magic_dns_suffix.as_deref(), Some("tail1234.ts.net"));
        assert_eq!(
            status.self_node.as_ref().unwrap().dns_name,
            "laptop.tail1234.ts.net"
        );
        assert_eq!(status.peers.len(), 4);
        assert_eq!(peer(&status, "gaming-pc").ipv4(), Some("100.64.0.1"));
        assert!(peer(&status, "gaming-pc").tagged());
        assert!(!peer(&status, "phone").tagged());
    }

    #[test]
    fn status_tells_direct_relayed_idle_and_offline_apart() {
        let status = status();
        assert_eq!(peer(&status, "gaming-pc").path, TailnetPath::Direct);
        assert_eq!(peer(&status, "gaming-pc").relay, None);
        assert_eq!(peer(&status, "den-pc").path, TailnetPath::Relayed);
        assert_eq!(peer(&status, "den-pc").relay.as_deref(), Some("fra"));
        assert_eq!(peer(&status, "phone").path, TailnetPath::Unknown);
        assert_eq!(peer(&status, "old-pc").path, TailnetPath::Unknown);
        assert!(!peer(&status, "old-pc").online);
    }

    #[test]
    fn status_maps_the_backend_state_and_hides_self_unless_running() {
        for (backend, expected) in [
            ("NeedsLogin", TailnetState::NeedsLogin),
            ("Stopped", TailnetState::Stopped),
            ("Starting", TailnetState::Other),
        ] {
            let json =
                format!(r#"{{"BackendState":"{backend}","Self":{{"ID":"n1","HostName":"pc"}}}}"#);
            let status = parse_status(&json);
            assert_eq!(status.state, expected);
            assert!(status.self_node.is_none());
        }
    }

    #[test]
    fn status_rejects_text_that_is_not_a_status() {
        assert_eq!(parse_status("not json").state, TailnetState::Unreachable);
        assert_eq!(parse_status("[]").state, TailnetState::Unreachable);
    }

    #[test]
    fn tailnet_addresses_are_the_cgnat_and_ula_ranges() {
        for (address, expected) in [
            ("100.64.0.1", true),
            ("100.127.255.254", true),
            ("100.128.0.1", false),
            ("100.63.0.1", false),
            ("192.168.0.2", false),
            ("fd7a:115c:a1e0::5", true),
            ("fd7a:115c:a1e1::5", false),
            ("::ffff:100.64.0.7", true),
            ("pc.local", false),
        ] {
            assert_eq!(is_tailnet_ip(address), expected, "{address}");
        }
        assert!(url_is_tailnet("https://100.64.0.1:48443"));
        assert!(url_is_tailnet("https://[fd7a:115c:a1e0::1]:48443"));
        assert!(!url_is_tailnet("https://192.168.0.5:48443"));
        assert!(!url_is_tailnet("https://pc.lan:48443"));
    }

    // ---- Sources, with the local API mocked ----

    struct Fake(Reply);

    impl StatusSource for Fake {
        async fn status(&self) -> Reply {
            self.0.clone()
        }
    }

    #[tokio::test]
    async fn the_local_api_is_preferred() {
        let status = read_status(
            &Fake(Reply::Ok(STATUS.into())),
            &Fake(Reply::Ok("{}".into())),
        )
        .await;
        assert_eq!(status.peers.len(), 4);
    }

    #[tokio::test]
    async fn the_command_line_answers_when_the_local_api_does_not() {
        let status = read_status(
            &Fake(Reply::Failed("down".into())),
            &Fake(Reply::Ok(STATUS.into())),
        )
        .await;
        assert_eq!(status.state, TailnetState::Running);
    }

    #[tokio::test]
    async fn not_installed_is_decided_by_the_command_line_only() {
        let missing = read_status(
            &Fake(Reply::Failed("down".into())),
            &Fake(Reply::NotInstalled),
        )
        .await;
        assert_eq!(missing.state, TailnetState::NotInstalled);

        let broken = read_status(
            &Fake(Reply::Failed("down".into())),
            &Fake(Reply::Failed("daemon not running".into())),
        )
        .await;
        assert_eq!(broken.state, TailnetState::Unreachable);
        assert_eq!(broken.error.as_deref(), Some("daemon not running"));
    }

    /// A stand-in for the Tailscale local API: answers one request on a loopback port and returns what it was sent.
    async fn serve_once(
        status_line: &'static str,
        body: &'static str,
    ) -> (u16, tokio::task::JoinHandle<String>) {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let handle = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let mut buffer = vec![0u8; 4096];
            let read = socket.read(&mut buffer).await.unwrap();
            let response = format!(
                "HTTP/1.1 {status_line}\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                body.len()
            );
            socket.write_all(response.as_bytes()).await.unwrap();
            String::from_utf8_lossy(&buffer[..read]).into_owned()
        });
        (port, handle)
    }

    #[tokio::test]
    async fn the_http_source_reads_the_status_from_the_configured_port() {
        let (port, request) = serve_once("200 OK", STATUS).await;

        let status = read_status(&HttpSource { port }, &Fake(Reply::NotInstalled)).await;

        assert_eq!(status.state, TailnetState::Running);
        assert_eq!(status.peers.len(), 4);
        let request = request.await.unwrap();
        assert!(
            request.starts_with("GET /localapi/v0/status HTTP/1.1"),
            "{request}"
        );
        assert!(
            request.to_lowercase().contains("sec-tailscale: localapi"),
            "{request}"
        );
    }

    #[tokio::test]
    async fn an_http_error_falls_through_to_the_command_line() {
        let (port, _request) = serve_once("403 Forbidden", "access denied").await;

        let status = read_status(&HttpSource { port }, &Fake(Reply::Ok(STATUS.into()))).await;

        assert_eq!(status.state, TailnetState::Running);
    }

    #[tokio::test]
    async fn a_closed_port_is_not_proof_that_tailscale_is_missing() {
        // Nothing listens on port 1.
        let status = read_status(
            &HttpSource { port: 1 },
            &Fake(Reply::Failed("daemon not running".into())),
        )
        .await;

        assert_eq!(status.state, TailnetState::Unreachable);
    }

    // ---- Probe targets, cache, scan ----

    struct FakeProbe {
        hosts: HashMap<String, [u8; 32]>,
        calls: Mutex<Vec<String>>,
    }

    impl CertProbe for FakeProbe {
        async fn probe(&self, address: &str, port: u16) -> Option<[u8; 32]> {
            self.calls.lock().unwrap().push(format!("{address}:{port}"));
            self.hosts.get(address).copied()
        }
    }

    fn probe_with(hosts: &[(&str, u8)]) -> FakeProbe {
        FakeProbe {
            hosts: hosts
                .iter()
                .map(|(a, b)| (a.to_string(), [*b; 32]))
                .collect(),
            calls: Mutex::new(Vec::new()),
        }
    }

    #[test]
    fn tagged_peers_are_probed_and_offline_or_untagged_ones_are_not() {
        let status = status();
        let names: Vec<&str> = probe_targets(&status, &[])
            .iter()
            .map(|p| p.node.host_name.as_str())
            .collect();
        assert_eq!(names, vec!["den-pc", "gaming-pc"]);
    }

    #[test]
    fn an_untagged_peer_is_probed_when_a_paired_host_used_its_address() {
        let status = status();
        let names: Vec<&str> = probe_targets(&status, &["100.64.0.3".to_string()])
            .iter()
            .map(|p| p.node.host_name.as_str())
            .collect();
        assert!(names.contains(&"phone"));
    }

    #[test]
    fn without_any_tagged_peer_every_online_peer_is_probed() {
        let json = STATUS.replace("tag:hyperharbor-host", "tag:other");
        let status = parse_status(&json);
        assert_eq!(probe_targets(&status, &[]).len(), 3);
    }

    #[test]
    fn nothing_is_probed_while_tailscale_is_not_running() {
        assert!(probe_targets(&parse_status(r#"{"BackendState":"Stopped"}"#), &[]).is_empty());
    }

    #[test]
    fn the_cache_keeps_a_found_host_longer_than_a_missing_one() {
        let start = Instant::now();
        let mut cache = ProbeCache::default();
        cache.store("100.64.0.1", 48443, Some([1; 32]), start);
        cache.store("100.64.0.2", 48443, None, start);

        let soon = start + Duration::from_secs(60);
        assert_eq!(cache.lookup("100.64.0.1", 48443, soon), Some(Some([1; 32])));
        assert_eq!(cache.lookup("100.64.0.2", 48443, soon), Some(None));

        let later = start + Duration::from_secs(5 * 60);
        assert_eq!(
            cache.lookup("100.64.0.1", 48443, later),
            Some(Some([1; 32]))
        );
        assert_eq!(cache.lookup("100.64.0.2", 48443, later), None);

        let much_later = start + Duration::from_secs(11 * 60);
        assert_eq!(cache.lookup("100.64.0.1", 48443, much_later), None);
        assert_eq!(cache.lookup("100.64.0.1", 5000, soon), None);
    }

    #[tokio::test]
    async fn a_scan_returns_the_peers_that_answer_and_uses_the_cache() {
        let status = status();
        let probe = probe_with(&[("100.64.0.1", 0xAA)]);
        let cache = Mutex::new(ProbeCache::default());
        let now = Instant::now();

        let found = scan(&status, &[], &[48443], &probe, &cache, now).await;
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].dns_name, "gaming-pc.tail1234.ts.net");
        assert_eq!(found[0].fingerprint, "AA".repeat(32));
        assert_eq!(found[0].path, TailnetPath::Direct);
        assert!(found[0].tagged);
        assert_eq!(probe.calls.lock().unwrap().len(), 2);

        // A second scan inside the cache lifetime opens no connection.
        scan(
            &status,
            &[],
            &[48443],
            &probe,
            &cache,
            now + Duration::from_secs(30),
        )
        .await;
        assert_eq!(probe.calls.lock().unwrap().len(), 2);
    }

    #[tokio::test]
    async fn the_service_reports_a_change_only_once() {
        let service = TailnetService::new(TailnetSettingsStore::new(None));
        let probe = probe_with(&[("100.64.0.1", 0xAA)]);
        let now = Instant::now();
        let http = Fake(Reply::Failed("down".into()));
        let cli = Fake(Reply::Ok(STATUS.into()));

        assert!(service.refresh_with(&http, &cli, &probe, &[], now).await);
        assert!(!service.refresh_with(&http, &cli, &probe, &[], now).await);
        assert_eq!(service.snapshot().hosts.len(), 1);
    }

    // ---- Merging ----

    fn lan_entry(host_id: &str, addresses: &[&str]) -> HostEntry {
        HostEntry {
            key: format!("mdns:{host_id}"),
            display_name: "GAMING-PC".into(),
            host_id: Some(host_id.into()),
            host_name: Some("GAMING-PC".into()),
            addresses: addresses.iter().map(|a| a.to_string()).collect(),
            port: 48443,
            api_version: Some("1.23.0".into()),
            source: HostSource::Discovered,
            is_local: false,
            paired: false,
            can_wake: false,
            tailscale: None,
        }
    }

    fn paired_host(host_id: &str, fingerprint: &str) -> PairedHost {
        PairedHost {
            host_id: host_id.into(),
            device_id: "dev".into(),
            host_certificate_fingerprint: fingerprint.into(),
            display_name: "GAMING-PC".into(),
            entry_keys: vec![format!("mdns:{host_id}")],
            wake_adapters: Vec::new(),
            wake_refreshed_at: None,
            addresses: vec!["192.168.0.235".into()],
            port: 48443,
            host_name: Some("GAMING-PC".into()),
        }
    }

    fn found(fingerprint_byte: u8) -> TailnetHost {
        let status = status();
        let peer = peer(&status, "gaming-pc");
        TailnetHost {
            addresses: peer.node.addresses.clone(),
            port: 48443,
            dns_name: peer.node.dns_name.clone(),
            host_name: peer.node.host_name.clone(),
            node_id: peer.node.node_id.clone(),
            tagged: true,
            path: TailnetPath::Direct,
            relay: None,
            fingerprint: hex::encode_upper([fingerprint_byte; 32]),
        }
    }

    #[test]
    fn a_host_on_both_networks_appears_once_with_both_addresses() {
        let paired = [paired_host("ID-1", &"AA".repeat(32))];
        let mut hosts = vec![lan_entry("ID-1", &["192.168.0.235"])];

        merge_tailnet(&mut hosts, &[found(0xAA)], &paired, "LAPTOP");

        assert_eq!(hosts.len(), 1);
        assert_eq!(hosts[0].key, "mdns:ID-1");
        assert_eq!(
            hosts[0].addresses,
            vec!["192.168.0.235", "100.64.0.1", "fd7a:115c:a1e0::1"]
        );
        let info = hosts[0].tailscale.as_ref().unwrap();
        assert_eq!(info.path, TailnetPath::Direct);
        assert!(info.tagged);
    }

    #[test]
    fn a_paired_host_that_is_only_on_the_tailnet_is_listed_under_its_pairing() {
        let paired = [paired_host("ID-1", &"AA".repeat(32))];
        let mut hosts = Vec::new();

        merge_tailnet(&mut hosts, &[found(0xAA)], &paired, "LAPTOP");

        assert_eq!(hosts.len(), 1);
        assert_eq!(hosts[0].key, "mdns:ID-1");
        assert_eq!(hosts[0].host_id.as_deref(), Some("ID-1"));
        assert!(hosts[0].addresses.contains(&"192.168.0.235".to_string()));
        assert!(hosts[0].addresses.contains(&"100.64.0.1".to_string()));
    }

    #[test]
    fn a_host_that_was_typed_in_by_its_tailscale_address_is_not_listed_twice() {
        let mut manual = lan_entry("x", &["100.64.0.1"]);
        manual.key = "manual:100.64.0.1:48443".into();
        manual.host_id = None;
        manual.source = HostSource::Manual;
        let mut hosts = vec![manual];

        merge_tailnet(&mut hosts, &[found(0xBB)], &[], "LAPTOP");

        assert_eq!(hosts.len(), 1);
        assert!(hosts[0].tailscale.is_some());
    }

    #[test]
    fn an_unknown_host_is_listed_by_its_tailscale_name() {
        let mut hosts = Vec::new();

        merge_tailnet(&mut hosts, &[found(0xBB)], &[], "LAPTOP");

        assert_eq!(hosts.len(), 1);
        assert_eq!(hosts[0].source, HostSource::Tailnet);
        assert_eq!(hosts[0].display_name, "gaming-pc");
        assert_eq!(hosts[0].key, "tailnet:gaming-pc.tail1234.ts.net");
        assert!(!hosts[0].paired);
    }

    #[test]
    fn merging_twice_changes_nothing() {
        let paired = [paired_host("ID-1", &"AA".repeat(32))];
        let mut hosts = vec![lan_entry("ID-1", &["192.168.0.235"])];
        merge_tailnet(&mut hosts, &[found(0xAA)], &paired, "LAPTOP");
        let once = hosts.clone();
        merge_tailnet(&mut hosts, &[found(0xAA)], &paired, "LAPTOP");
        assert_eq!(hosts, once);
    }

    // ---- Address preference ----

    const LAN: &str = "https://192.168.0.235:48443";
    const LAN_B: &str = "https://192.168.0.236:48443";
    const TAIL: &str = "https://100.64.0.1:48443";

    fn urls(items: &[&str]) -> Vec<String> {
        items.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn the_lan_address_is_tried_before_the_tailscale_address() {
        assert_eq!(
            order_candidates(urls(&[TAIL, LAN]), None, false),
            urls(&[LAN, TAIL])
        );
    }

    #[test]
    fn the_address_that_worked_last_goes_first_within_its_group() {
        assert_eq!(
            order_candidates(urls(&[LAN, LAN_B, TAIL]), Some(LAN_B), false),
            urls(&[LAN_B, LAN, TAIL])
        );
    }

    #[test]
    fn a_tailscale_address_that_worked_a_moment_ago_stays_first() {
        assert_eq!(
            order_candidates(urls(&[LAN, TAIL]), Some(TAIL), true),
            urls(&[TAIL, LAN])
        );
    }

    #[test]
    fn an_old_tailscale_success_does_not_beat_the_lan() {
        assert_eq!(
            order_candidates(urls(&[LAN, TAIL]), Some(TAIL), false),
            urls(&[LAN, TAIL])
        );
    }

    #[test]
    fn every_address_is_kept() {
        let out = order_candidates(
            urls(&[TAIL, LAN, "https://100.64.0.2:48443"]),
            Some("https://100.64.0.2:48443"),
            true,
        );
        assert_eq!(out.len(), 3);
        assert_eq!(out[0], "https://100.64.0.2:48443");
    }

    #[test]
    fn a_host_on_this_pc_has_no_path_and_passes_without_crossing_a_network() {
        let status = status();
        let own = status.self_node.as_ref().unwrap().addresses[0].clone();
        let steps =
            build_reachability(&input(&status, Some(&own), CertCheck::Pinned, ApiCheck::Ok));
        assert_eq!(
            ids(&steps),
            vec![
                ("tailscale", StepStatus::Pass),
                ("peer", StepStatus::Pass),
                ("api", StepStatus::Pass),
                ("identity", StepStatus::Pass),
                ("access", StepStatus::Pass),
            ]
        );

        let mut entry = lan_entry("ID-1", &["192.168.0.235", &own]);
        entry.tailscale = None;
        let route = describe_route(
            &entry,
            Some(&format!("https://{own}:48443")),
            std::slice::from_ref(&own),
        );
        assert_eq!(route.via, "tailscale");
        assert_eq!(route.path, None);
        assert_eq!(route.hint, None);
    }

    #[test]
    fn the_route_says_lan_or_tailscale_and_hints_when_relayed() {
        let mut entry = lan_entry("ID-1", &["192.168.0.235", "100.64.0.1"]);
        entry.tailscale = Some(TailnetInfo {
            addresses: vec!["100.64.0.1".into()],
            dns_name: None,
            node_id: "n".into(),
            tagged: true,
            online: true,
            path: TailnetPath::Relayed,
            relay: Some("fra".into()),
        });

        let lan = describe_route(&entry, Some(LAN), &[]);
        assert_eq!(lan.via, "lan");
        assert_eq!(lan.hint, None);

        let tail = describe_route(&entry, Some(TAIL), &[]);
        assert_eq!(tail.via, "tailscale");
        assert_eq!(tail.path, Some(TailnetPath::Relayed));
        assert_eq!(tail.relay.as_deref(), Some("fra"));
        assert!(tail.hint.as_deref().unwrap().contains("split tunneling"));

        assert_eq!(describe_route(&entry, None, &[]).via, "unknown");

        entry.tailscale.as_mut().unwrap().path = TailnetPath::Direct;
        assert_eq!(describe_route(&entry, Some(TAIL), &[]).hint, None);
    }

    // ---- Wake relay ----

    fn adapter(broadcast: &str) -> WakeAdapter {
        WakeAdapter {
            name: "Ethernet".into(),
            mac_address: "90-2E-16-66-C5-AE".into(),
            ipv4_address: "192.168.0.235".into(),
            broadcast_address: broadcast.into(),
        }
    }

    fn relay_host(id: &str, name: &str, broadcast: &str) -> PairedHost {
        let mut host = paired_host(id, "00");
        host.display_name = name.into();
        host.wake_adapters = vec![adapter(broadcast)];
        host
    }

    fn entry_for(host: &PairedHost, source: HostSource, path: Option<TailnetPath>) -> HostEntry {
        let mut entry = lan_entry(&host.host_id, &[]);
        entry.source = source;
        entry.tailscale = path.map(|path| TailnetInfo {
            addresses: vec![],
            dns_name: None,
            node_id: "n".into(),
            tagged: true,
            online: true,
            path,
            relay: None,
        });
        entry
    }

    #[test]
    fn the_wake_relay_is_chosen_on_the_sleeping_hosts_network() {
        let target = relay_host("T", "Target", "192.168.0.255");
        let same_net = relay_host("A", "Den", "192.168.0.255");
        let other_net = relay_host("B", "Office", "10.0.0.255");
        let entries = [
            entry_for(&same_net, HostSource::Tailnet, Some(TailnetPath::Direct)),
            entry_for(&other_net, HostSource::Tailnet, Some(TailnetPath::Direct)),
        ];
        let candidates = [
            RelayCandidate {
                paired: &same_net,
                entry: &entries[0],
            },
            RelayCandidate {
                paired: &other_net,
                entry: &entries[1],
            },
        ];

        let ranked = rank_wake_relays(&target, &candidates);

        assert_eq!(
            ranked
                .iter()
                .map(|h| h.host_id.as_str())
                .collect::<Vec<_>>(),
            vec!["A"]
        );
    }

    #[test]
    fn the_target_and_hosts_that_are_not_reachable_are_never_relays() {
        let target = relay_host("T", "Target", "192.168.0.255");
        let asleep = relay_host("A", "Asleep", "192.168.0.255");
        let entries = [
            entry_for(&target, HostSource::Tailnet, Some(TailnetPath::Direct)),
            entry_for(&asleep, HostSource::Remembered, None),
        ];
        let candidates = [
            RelayCandidate {
                paired: &target,
                entry: &entries[0],
            },
            RelayCandidate {
                paired: &asleep,
                entry: &entries[1],
            },
        ];

        assert!(rank_wake_relays(&target, &candidates).is_empty());
    }

    #[test]
    fn a_direct_relay_comes_before_a_relayed_one_and_then_by_name() {
        let target = relay_host("T", "Target", "192.168.0.255");
        let relayed = relay_host("A", "Alpha", "192.168.0.255");
        let direct_b = relay_host("B", "Bravo", "192.168.0.255");
        let direct_c = relay_host("C", "Charlie", "192.168.0.255");
        let entries = [
            entry_for(&relayed, HostSource::Tailnet, Some(TailnetPath::Relayed)),
            entry_for(&direct_c, HostSource::Tailnet, Some(TailnetPath::Direct)),
            entry_for(&direct_b, HostSource::Tailnet, Some(TailnetPath::Direct)),
        ];
        let candidates = [
            RelayCandidate {
                paired: &relayed,
                entry: &entries[0],
            },
            RelayCandidate {
                paired: &direct_c,
                entry: &entries[1],
            },
            RelayCandidate {
                paired: &direct_b,
                entry: &entries[2],
            },
        ];

        let ranked = rank_wake_relays(&target, &candidates);

        assert_eq!(
            ranked
                .iter()
                .map(|h| h.host_id.as_str())
                .collect::<Vec<_>>(),
            vec!["B", "C", "A"]
        );
    }

    // ---- Reachability ----

    fn input<'a>(
        status: &'a TailnetStatus,
        address: Option<&'a str>,
        certificate: CertCheck,
        api: ApiCheck,
    ) -> ReachabilityInput<'a> {
        ReachabilityInput {
            status,
            address,
            port: 48443,
            certificate,
            api,
        }
    }

    fn ids(steps: &[ReachabilityStep]) -> Vec<(&'static str, StepStatus)> {
        steps.iter().map(|s| (s.id, s.status)).collect()
    }

    #[test]
    fn a_direct_reachable_paired_host_passes_every_step() {
        let status = status();
        let steps = build_reachability(&input(
            &status,
            Some("100.64.0.1"),
            CertCheck::Pinned,
            ApiCheck::Ok,
        ));
        assert_eq!(
            ids(&steps),
            vec![
                ("tailscale", StepStatus::Pass),
                ("peer", StepStatus::Pass),
                ("online", StepStatus::Pass),
                ("path", StepStatus::Pass),
                ("api", StepStatus::Pass),
                ("identity", StepStatus::Pass),
                ("access", StepStatus::Pass),
            ]
        );
    }

    #[test]
    fn a_relayed_path_warns_with_the_split_tunneling_hint() {
        let status = status();
        let steps = build_reachability(&input(
            &status,
            Some("100.64.0.2"),
            CertCheck::Pinned,
            ApiCheck::Ok,
        ));
        let path = steps.iter().find(|s| s.id == "path").unwrap();
        assert_eq!(path.status, StepStatus::Warn);
        assert!(path.detail.as_deref().unwrap().contains("through fra"));
        assert!(path.detail.as_deref().unwrap().contains("split tunneling"));
    }

    #[test]
    fn the_test_stops_at_the_first_failure() {
        let stopped = parse_status(r#"{"BackendState":"Stopped"}"#);
        assert_eq!(
            ids(&build_reachability(&input(
                &stopped,
                Some("100.64.0.1"),
                CertCheck::Pinned,
                ApiCheck::Ok
            ))),
            vec![("tailscale", StepStatus::Fail)]
        );

        let status = status();
        assert_eq!(
            ids(&build_reachability(&input(
                &status,
                Some("100.64.0.77"),
                CertCheck::Pinned,
                ApiCheck::Ok
            ))),
            vec![("tailscale", StepStatus::Pass), ("peer", StepStatus::Fail)]
        );
        assert_eq!(
            ids(&build_reachability(&input(
                &status,
                Some("100.64.0.4"),
                CertCheck::Pinned,
                ApiCheck::Ok
            ))),
            vec![
                ("tailscale", StepStatus::Pass),
                ("peer", StepStatus::Pass),
                ("online", StepStatus::Fail)
            ]
        );
        let silent = build_reachability(&input(
            &status,
            Some("100.64.0.1"),
            CertCheck::NoAnswer,
            ApiCheck::NotTried,
        ));
        assert_eq!(silent.last().unwrap().id, "api");
        assert_eq!(silent.last().unwrap().status, StepStatus::Fail);
    }

    #[test]
    fn a_different_certificate_or_a_refused_node_fails_with_a_clear_message() {
        let status = status();
        let different = build_reachability(&input(
            &status,
            Some("100.64.0.1"),
            CertCheck::Different,
            ApiCheck::Ok,
        ));
        assert_eq!(different.last().unwrap().id, "identity");
        assert_eq!(different.last().unwrap().status, StepStatus::Fail);

        let refused = build_reachability(&input(
            &status,
            Some("100.64.0.1"),
            CertCheck::Pinned,
            ApiCheck::NodeRefused("This device is paired from a different Tailscale node.".into()),
        ));
        let access = refused.last().unwrap();
        assert_eq!(access.id, "access");
        assert_eq!(access.status, StepStatus::Fail);
        assert!(access
            .detail
            .as_deref()
            .unwrap()
            .contains("different Tailscale node"));
    }

    #[test]
    fn an_unpaired_host_warns_about_identity_and_skips_the_signed_in_request() {
        let status = status();
        let steps = build_reachability(&input(
            &status,
            Some("100.64.0.1"),
            CertCheck::Unpaired,
            ApiCheck::NotTried,
        ));
        assert_eq!(steps.last().unwrap().id, "identity");
        assert_eq!(steps.last().unwrap().status, StepStatus::Warn);
    }

    /// Reads the Tailscale installed on this PC. Run with: cargo test live_local_tailscale -- --ignored --nocapture
    #[tokio::test]
    #[ignore = "needs Tailscale installed on this PC"]
    async fn live_local_tailscale() {
        let status = read_status(
            &HttpSource {
                port: DEFAULT_LOCAL_API_PORT,
            },
            &CliSource,
        )
        .await;
        println!("{status:#?}");
        assert_eq!(status.state, TailnetState::Running);
        let me = status.self_node.expect("a running client has a node");
        assert!(
            me.addresses.iter().any(|a| is_tailnet_ip(a)),
            "{:?}",
            me.addresses
        );
        assert!(!me.dns_name.is_empty());
    }

    // ---- Settings ----

    #[test]
    fn settings_persist_and_reject_port_zero() {
        let path =
            std::env::temp_dir().join(format!("hyperharbor-tailnet-{}.json", std::process::id()));
        let _ = std::fs::remove_file(&path);
        let store = TailnetSettingsStore::new(Some(path.clone()));
        assert_eq!(store.get().local_api_port, DEFAULT_LOCAL_API_PORT);

        store
            .set(TailnetSettings {
                local_api_port: 5555,
            })
            .unwrap();
        assert_eq!(
            TailnetSettingsStore::new(Some(path.clone()))
                .get()
                .local_api_port,
            5555
        );
        assert!(store.set(TailnetSettings { local_api_port: 0 }).is_err());
        let _ = std::fs::remove_file(&path);
    }

    /// Needs Tailscale and at least one other online device on the tailnet. Run with:
    /// cargo test live_peer_path -- --ignored --nocapture
    #[tokio::test]
    #[ignore = "needs Tailscale and another online device"]
    async fn live_peer_path() {
        let before = read_status(
            &HttpSource {
                port: DEFAULT_LOCAL_API_PORT,
            },
            &CliSource,
        )
        .await;
        let peer = before
            .peers
            .iter()
            .find(|peer| peer.online)
            .expect("an online peer");
        let address = peer.ipv4().expect("an IPv4 address").to_string();
        println!("before: {:?} {:?}", peer.path, peer.relay);

        warm_path(&address).await;

        let after = read_status(
            &HttpSource {
                port: DEFAULT_LOCAL_API_PORT,
            },
            &CliSource,
        )
        .await;
        let peer = after
            .peers
            .iter()
            .find(|p| p.ipv4() == Some(address.as_str()))
            .unwrap();
        println!("after: {:?} {:?}", peer.path, peer.relay);
        assert_ne!(peer.path, TailnetPath::Unknown);
    }
}
