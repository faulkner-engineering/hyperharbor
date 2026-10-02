use serde::Serialize;

/// Errors returned to the frontend. Serialized as `{ "code": ..., "message": ... }`.
#[derive(Debug, thiserror::Error)]
pub enum ClientError {
    #[error("Unknown host.")]
    UnknownHost,

    #[error("Enter a host name or IP address, optionally followed by :port.")]
    InvalidAddress,

    #[error("Pairing is required before this host's virtual machines can be shown.")]
    PairingRequired,

    #[error("The host could not be reached: {0}")]
    Unreachable(String),

    #[error("{title}: {detail}")]
    Api {
        status: u16,
        title: String,
        detail: String,
    },

    #[error("Unexpected response from the host: {0}")]
    InvalidResponse(String),

    #[error("Could not save hosts: {0}")]
    Storage(String),
}

impl ClientError {
    fn code(&self) -> &'static str {
        match self {
            ClientError::UnknownHost => "unknownHost",
            ClientError::InvalidAddress => "invalidAddress",
            ClientError::PairingRequired => "pairingRequired",
            ClientError::Unreachable(_) => "unreachable",
            ClientError::Api { .. } => "api",
            ClientError::InvalidResponse(_) => "invalidResponse",
            ClientError::Storage(_) => "storage",
        }
    }
}

impl Serialize for ClientError {
    fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        use serde::ser::SerializeStruct;
        let mut state = serializer.serialize_struct("ClientError", 2)?;
        state.serialize_field("code", self.code())?;
        state.serialize_field("message", &self.to_string())?;
        state.end()
    }
}
