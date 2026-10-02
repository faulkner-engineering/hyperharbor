use std::time::Duration;

use serde::Deserialize;

use crate::error::ClientError;
use crate::hosts::HostEntry;

const API_BASE_PATH: &str = "/api/v1";
const REQUEST_TIMEOUT: Duration = Duration::from_secs(5);

/// HTTP client for the host API.
pub struct ApiClient {
    http: reqwest::Client,
}

#[derive(Deserialize)]
struct ProblemDetails {
    title: Option<String>,
    detail: Option<String>,
}

impl ApiClient {
    pub fn new() -> Self {
        let http = reqwest::Client::builder()
            .timeout(REQUEST_TIMEOUT)
            .build()
            .expect("failed to build HTTP client");
        Self { http }
    }

    /// Returns the VM list exactly as the host serialized it; the frontend types it from docs/api.yaml.
    pub async fn list_vms(&self, host: &HostEntry) -> Result<serde_json::Value, ClientError> {
        let url = format!("{}{API_BASE_PATH}/vms", base_url(host)?);
        let response = self
            .http
            .get(&url)
            .send()
            .await
            .map_err(|e| ClientError::Unreachable(e.without_url().to_string()))?;

        let status = response.status();
        if status.is_success() {
            return response
                .json::<serde_json::Value>()
                .await
                .map_err(|e| ClientError::InvalidResponse(e.without_url().to_string()));
        }

        let problem = response.json::<ProblemDetails>().await.ok();
        Err(ClientError::Api {
            status: status.as_u16(),
            title: problem
                .as_ref()
                .and_then(|p| p.title.clone())
                .unwrap_or_else(|| {
                    status
                        .canonical_reason()
                        .unwrap_or("Request failed")
                        .to_string()
                }),
            detail: problem.and_then(|p| p.detail).unwrap_or_default(),
        })
    }
}

/// Until pairing and mTLS exist, the host API only listens on loopback, so only a host on this
/// machine can be called.
pub fn base_url(host: &HostEntry) -> Result<String, ClientError> {
    if !host.is_local {
        return Err(ClientError::PairingRequired);
    }
    Ok(format!("http://127.0.0.1:{}", host.port))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::hosts::ManualHost;

    #[test]
    fn base_url_uses_loopback_for_local_hosts() {
        let host = ManualHost::parse("localhost:49000").unwrap().to_entry("pc");
        assert_eq!(base_url(&host).unwrap(), "http://127.0.0.1:49000");
    }

    #[test]
    fn base_url_requires_pairing_for_remote_hosts() {
        let host = ManualHost::parse("192.168.1.10").unwrap().to_entry("pc");
        assert!(matches!(base_url(&host), Err(ClientError::PairingRequired)));
    }
}
