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
