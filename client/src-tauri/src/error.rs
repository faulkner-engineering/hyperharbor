use serde::Serialize;

/// Errors returned to the frontend. Serialized as `{ "code": ..., "message": ..., "status": ... }`.
#[derive(Debug, thiserror::Error)]
pub enum ClientError {
    #[error("Unknown host.")]
    UnknownHost,

    #[error("Enter a host name or IP address, optionally followed by :port.")]
    InvalidAddress,

    #[error("The virtual machine ID is not valid.")]
    InvalidVmId,

    #[error("Pair this device with the host to see its virtual machines.")]
    PairingRequired,

    #[error("No pairing is in progress for this host. Start pairing again.")]
    NoPendingPairing,

    #[error("The host's identity could not be verified. Pairing was not saved. Try again, and check that you are on a trusted network.")]
    PairingVerificationFailed,

    #[error("This host has not shared its Wake-on-LAN details yet. Connect to it once while it is awake.")]
    NoWakeInfo,

    #[error("The wake signal could not be sent: {0}")]
    WakeFailed(String),

    #[error("Remote Desktop could not be started: {0}")]
    RdpFailed(String),

    #[error("This device cannot reach {0} on the Remote Desktop port. VMs on an internal or NAT switch are only reachable from the host; use an External switch to connect from other devices.")]
    VmUnreachable(String),

    #[error("The host could not be reached: {0}")]
    Unreachable(String),

    #[error("{message}")]
    Api { status: u16, message: String },

    #[error("Unexpected response from the host: {0}")]
    InvalidResponse(String),

    #[error("Could not save data: {0}")]
    Storage(String),
}

impl ClientError {
    fn code(&self) -> &'static str {
        match self {
            ClientError::UnknownHost => "unknownHost",
            ClientError::InvalidAddress => "invalidAddress",
            ClientError::InvalidVmId => "invalidVmId",
            ClientError::PairingRequired => "pairingRequired",
            ClientError::NoPendingPairing => "noPendingPairing",
            ClientError::PairingVerificationFailed => "pairingVerificationFailed",
            ClientError::NoWakeInfo => "noWakeInfo",
            ClientError::WakeFailed(_) => "wakeFailed",
            ClientError::RdpFailed(_) => "rdpFailed",
            ClientError::VmUnreachable(_) => "vmUnreachable",
            ClientError::Unreachable(_) => "unreachable",
            ClientError::Api { .. } => "api",
            ClientError::InvalidResponse(_) => "invalidResponse",
            ClientError::Storage(_) => "storage",
        }
    }

    fn status(&self) -> Option<u16> {
        match self {
            ClientError::Api { status, .. } => Some(*status),
            _ => None,
        }
    }
}

impl Serialize for ClientError {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        use serde::ser::SerializeStruct;
        let mut state = serializer.serialize_struct("ClientError", 3)?;
        state.serialize_field("code", self.code())?;
        state.serialize_field("message", &self.to_string())?;
        state.serialize_field("status", &self.status())?;
        state.end()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// One value of every variant. The match has no wildcard, so a new variant does not compile
    /// until it is added here, and then the frontend test below checks its code.
    fn every_variant() -> Vec<ClientError> {
        let all = vec![
            ClientError::UnknownHost,
            ClientError::InvalidAddress,
            ClientError::InvalidVmId,
            ClientError::PairingRequired,
            ClientError::NoPendingPairing,
            ClientError::PairingVerificationFailed,
            ClientError::NoWakeInfo,
            ClientError::WakeFailed(String::new()),
            ClientError::RdpFailed(String::new()),
            ClientError::VmUnreachable(String::new()),
            ClientError::Unreachable(String::new()),
            ClientError::Api {
                status: 500,
                message: String::new(),
            },
            ClientError::InvalidResponse(String::new()),
            ClientError::Storage(String::new()),
        ];
        for error in &all {
            match error {
                ClientError::UnknownHost
                | ClientError::InvalidAddress
                | ClientError::InvalidVmId
                | ClientError::PairingRequired
                | ClientError::NoPendingPairing
                | ClientError::PairingVerificationFailed
                | ClientError::NoWakeInfo
                | ClientError::WakeFailed(_)
                | ClientError::RdpFailed(_)
                | ClientError::VmUnreachable(_)
                | ClientError::Unreachable(_)
                | ClientError::Api { .. }
                | ClientError::InvalidResponse(_)
                | ClientError::Storage(_) => {}
            }
        }
        all
    }

    #[test]
    fn every_code_is_known_to_the_frontend() {
        let client_ts = include_str!("../../src/lib/api/client.ts");
        let union = client_ts
            .split("code:")
            .nth(1)
            .and_then(|rest| rest.split(';').next())
            .expect("ClientError.code union in client.ts");

        for error in every_variant() {
            let code = format!("\"{}\"", error.code());
            assert!(union.contains(&code), "client.ts does not list {code}");
        }
    }

    /// The frontend reads exactly these fields (ClientError in src/lib/api/client.ts).
    #[test]
    fn serializes_code_message_and_status() {
        let api = serde_json::to_value(ClientError::Api {
            status: 409,
            message: "Start the VM first.".into(),
        })
        .unwrap();
        let other = serde_json::to_value(ClientError::InvalidVmId).unwrap();

        assert_eq!(
            api,
            serde_json::json!({ "code": "api", "message": "Start the VM first.", "status": 409 })
        );
        assert_eq!(
            other,
            serde_json::json!({
                "code": "invalidVmId",
                "message": "The virtual machine ID is not valid.",
                "status": null
            })
        );
    }
}
