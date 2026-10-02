//! Wake-on-LAN magic packets. A sleeping host cannot answer the API, so the client keeps the
//! adapter details from GET /wake/info and sends the packet itself.

use std::net::{Ipv4Addr, SocketAddrV4, UdpSocket};

use serde::{Deserialize, Serialize};

use crate::error::ClientError;

/// Mirrors the WakeAdapter schema in docs/api.yaml.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WakeAdapter {
    pub name: String,
    pub mac_address: String,
    pub ipv4_address: String,
    pub broadcast_address: String,
}

/// Ports commonly used for magic packets (discard and echo).
const PORTS: [u16; 2] = [9, 7];

/// Six 0xFF bytes followed by the MAC address repeated 16 times.
pub fn magic_packet(mac: [u8; 6]) -> [u8; 102] {
    let mut packet = [0xFFu8; 102];
    for repeat in 0..16 {
        packet[6 + repeat * 6..12 + repeat * 6].copy_from_slice(&mac);
    }
    packet
}

/// Parses AA-BB-CC-DD-EE-FF, AA:BB:CC:DD:EE:FF, or AABBCCDDEEFF.
pub fn parse_mac(text: &str) -> Option<[u8; 6]> {
    let hex: String = text.chars().filter(|c| *c != '-' && *c != ':').collect();
    let bytes = hex::decode(hex).ok()?;
    bytes.try_into().ok()
}

/// Sends magic packets for every adapter to its subnet broadcast address and to the limited
/// broadcast address. Returns how many datagrams were sent.
///
/// Each packet is sent from the local address on the host's subnet. Windows sends
/// 255.255.255.255 out of a single interface chosen by metric, which on a machine with virtual
/// adapters (Hyper-V, VMware, VPN) is often not the LAN, so an unbound socket can miss the host.
pub fn wake(adapters: &[WakeAdapter]) -> Result<usize, ClientError> {
    if adapters.is_empty() {
        return Err(ClientError::NoWakeInfo);
    }

    let locals = local_ipv4_networks();
    let mut sent = 0;
    let mut last_error = None;
    for adapter in adapters {
        let Some(mac) = parse_mac(&adapter.mac_address) else {
            continue;
        };
        let packet = magic_packet(mac);

        let source = adapter
            .ipv4_address
            .parse::<Ipv4Addr>()
            .ok()
            .and_then(|host| source_for(&locals, host))
            .unwrap_or(Ipv4Addr::UNSPECIFIED);
        let socket = match UdpSocket::bind((source, 0)).and_then(|socket| {
            socket.set_broadcast(true)?;
            Ok(socket)
        }) {
            Ok(socket) => socket,
            Err(error) => {
                last_error = Some(error);
                continue;
            }
        };

        let mut targets = vec![Ipv4Addr::BROADCAST];
        if let Ok(subnet) = adapter.broadcast_address.parse::<Ipv4Addr>() {
            targets.insert(0, subnet);
        }

        for target in targets {
            for port in PORTS {
                match socket.send_to(&packet, SocketAddrV4::new(target, port)) {
                    Ok(_) => sent += 1,
                    Err(error) => last_error = Some(error),
                }
            }
        }
    }

    if sent == 0 {
        return Err(ClientError::WakeFailed(
            last_error.map_or_else(|| "no valid MAC address".into(), |e| e.to_string()),
        ));
    }
    Ok(sent)
}

/// Local IPv4 addresses with their netmasks, excluding loopback.
fn local_ipv4_networks() -> Vec<(Ipv4Addr, Ipv4Addr)> {
    if_addrs::get_if_addrs()
        .unwrap_or_default()
        .into_iter()
        .filter_map(|interface| match interface.addr {
            if_addrs::IfAddr::V4(v4) if !v4.ip.is_loopback() => Some((v4.ip, v4.netmask)),
            _ => None,
        })
        .collect()
}

/// The local address on the same subnet as `host`, if this machine has one.
fn source_for(locals: &[(Ipv4Addr, Ipv4Addr)], host: Ipv4Addr) -> Option<Ipv4Addr> {
    locals
        .iter()
        .find(|(ip, mask)| {
            let mask = u32::from(*mask);
            mask != 0 && u32::from(*ip) & mask == u32::from(host) & mask
        })
        .map(|(ip, _)| *ip)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn magic_packet_has_sync_stream_and_sixteen_macs() {
        let mac = [0x90, 0x2E, 0x16, 0x66, 0xC5, 0xAE];
        let packet = magic_packet(mac);

        assert_eq!(&packet[..6], &[0xFF; 6]);
        for repeat in 0..16 {
            assert_eq!(&packet[6 + repeat * 6..12 + repeat * 6], &mac);
        }
    }

    #[test]
    fn parse_mac_accepts_common_formats() {
        let expected = Some([0x90, 0x2E, 0x16, 0x66, 0xC5, 0xAE]);
        assert_eq!(parse_mac("90-2E-16-66-C5-AE"), expected);
        assert_eq!(parse_mac("90:2e:16:66:c5:ae"), expected);
        assert_eq!(parse_mac("902E1666C5AE"), expected);
        assert_eq!(parse_mac("90-2E-16"), None);
        assert_eq!(parse_mac("not a mac"), None);
    }

    #[test]
    fn source_is_the_local_address_on_the_hosts_subnet() {
        // This laptop on 2026-10-02: VMware and Hyper-V adapters plus Wi-Fi on the LAN.
        let mask24: Ipv4Addr = "255.255.255.0".parse().unwrap();
        let locals = vec![
            ("192.168.249.1".parse().unwrap(), mask24),
            (
                "172.25.176.1".parse().unwrap(),
                "255.255.240.0".parse().unwrap(),
            ),
            ("192.168.0.70".parse().unwrap(), mask24),
        ];

        assert_eq!(
            source_for(&locals, "192.168.0.235".parse().unwrap()),
            Some("192.168.0.70".parse().unwrap())
        );
        assert_eq!(source_for(&locals, "10.1.2.3".parse().unwrap()), None);
    }

    #[test]
    fn wake_without_adapters_is_an_error() {
        assert!(matches!(wake(&[]), Err(ClientError::NoWakeInfo)));
    }

    #[test]
    fn wake_sends_to_subnet_and_limited_broadcast() {
        let adapter = WakeAdapter {
            name: "Ethernet".into(),
            mac_address: "90-2E-16-66-C5-AE".into(),
            ipv4_address: "127.0.0.2".into(),
            broadcast_address: "127.255.255.255".into(),
        };
        // Two targets times two ports. Sending to broadcast addresses works without a listener.
        assert_eq!(wake(&[adapter]).unwrap(), 4);
    }
}
