use serde::Serialize;

/// Errors returned to the frontend. Serialized as `{ "code": ..., "message": ..., "status": ... }`.
#[derive(Debug, thiserror::Error)]
pub enum ClientError {
    #[error("Unknown host.")]
    UnknownHost,

    #[error("Enter a host name or IP address, optionally followed by :port.")]
    InvalidAddress,

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
