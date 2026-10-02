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
pub fn wake(adapters: &[WakeAdapter]) -> Result<usize, ClientError> {
    if adapters.is_empty() {
        return Err(ClientError::NoWakeInfo);
    }

    let socket = UdpSocket::bind((Ipv4Addr::UNSPECIFIED, 0))
        .map_err(|e| ClientError::WakeFailed(e.to_string()))?;
    socket
        .set_broadcast(true)
        .map_err(|e| ClientError::WakeFailed(e.to_string()))?;

    let mut sent = 0;
    let mut last_error = None;
    for adapter in adapters {
        let Some(mac) = parse_mac(&adapter.mac_address) else {
            continue;
        };
        let packet = magic_packet(mac);

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
