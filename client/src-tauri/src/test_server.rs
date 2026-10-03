//! A minimal HTTPS server for tests: rustls over std TCP, one request per connection, with replies
//! chosen by a handler. It uses its own self-signed certificate, like a host does.

use std::io::{BufRead, BufReader, Read, Write};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use rcgen::{CertificateParams, KeyPair, PKCS_ECDSA_P256_SHA256};
use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};
use rustls::{ServerConfig, ServerConnection, StreamOwned};
use sha2::{Digest, Sha256};

/// What the server received.
#[derive(Clone, Debug)]
pub struct Request {
    pub method: String,
    pub path: String,
    pub body: String,
}

/// What the server sends back, optionally after a delay.
pub struct Reply {
    pub status: u16,
    pub body: String,
    pub delay: Duration,
}

impl Reply {
    pub fn json(status: u16, body: impl Into<String>) -> Self {
        Self {
            status,
            body: body.into(),
            delay: Duration::ZERO,
        }
    }

    pub fn after(mut self, delay: Duration) -> Self {
        self.delay = delay;
        self
    }
}

type Handler = dyn Fn(&Request) -> Reply + Send + Sync;

pub struct TestServer {
    pub address: SocketAddr,
    pub certificate_der: Vec<u8>,
    pub certificate_pem: String,
    key_der: Vec<u8>,
    requests: Arc<Mutex<Vec<Request>>>,
}

impl TestServer {
    /// Listens on `bind` (for example "127.0.0.1:0") and serves until the test process exits.
    pub fn start(bind: &str, handler: impl Fn(&Request) -> Reply + Send + Sync + 'static) -> Self {
        let key_pair = KeyPair::generate_for(&PKCS_ECDSA_P256_SHA256).unwrap();
        let certificate = CertificateParams::new(vec!["localhost".to_string()])
            .unwrap()
            .self_signed(&key_pair)
            .unwrap();
        Self::serve_with(
            bind,
            certificate.der().to_vec(),
            certificate.pem(),
            key_pair.serialize_der(),
            handler,
        )
    }

    /// Another server with the same certificate, so a client pinned to `self` also trusts it.
    pub fn start_sharing_certificate(
        &self,
        bind: &str,
        handler: impl Fn(&Request) -> Reply + Send + Sync + 'static,
    ) -> Self {
        Self::serve_with(
            bind,
            self.certificate_der.clone(),
            self.certificate_pem.clone(),
            self.key_der.clone(),
            handler,
        )
    }

    fn serve_with(
        bind: &str,
        certificate_der: Vec<u8>,
        certificate_pem: String,
        key_der: Vec<u8>,
        handler: impl Fn(&Request) -> Reply + Send + Sync + 'static,
    ) -> Self {
        let config = Arc::new(
            ServerConfig::builder_with_provider(Arc::new(
                rustls::crypto::aws_lc_rs::default_provider(),
            ))
            .with_safe_default_protocol_versions()
            .unwrap()
            .with_no_client_auth()
            .with_single_cert(
                vec![CertificateDer::from(certificate_der.clone())],
                PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(key_der.clone())),
            )
            .unwrap(),
        );

        let listener = TcpListener::bind(bind).unwrap();
        let address = listener.local_addr().unwrap();
        let requests = Arc::new(Mutex::new(Vec::new()));
        let handler: Arc<Handler> = Arc::new(handler);

        let recorded = requests.clone();
        std::thread::spawn(move || {
            for stream in listener.incoming().flatten() {
                let config = config.clone();
                let handler = handler.clone();
                let recorded = recorded.clone();
                std::thread::spawn(move || serve(stream, config, &*handler, &recorded));
            }
        });

        Self {
            address,
            certificate_der,
            certificate_pem,
            key_der,
            requests,
        }
    }

    pub fn requests(&self) -> Vec<Request> {
        self.requests.lock().unwrap().clone()
    }

    pub fn certificate_hash(&self) -> [u8; 32] {
        Sha256::digest(&self.certificate_der).into()
    }
}

fn serve(
    stream: TcpStream,
    config: Arc<ServerConfig>,
    handler: &Handler,
    recorded: &Mutex<Vec<Request>>,
) {
    let Ok(connection) = ServerConnection::new(config) else {
        return;
    };
    // A client that rejects the certificate fails the handshake on the first read; nothing is recorded.
    let mut reader = BufReader::new(StreamOwned::new(connection, stream));

    let mut request_line = String::new();
    if reader.read_line(&mut request_line).unwrap_or(0) == 0 {
        return;
    }
    let mut parts = request_line.split_whitespace();
    let method = parts.next().unwrap_or_default().to_string();
    let path = parts.next().unwrap_or_default().to_string();

    let mut content_length = 0usize;
    loop {
        let mut header = String::new();
        if reader.read_line(&mut header).unwrap_or(0) == 0 {
            return;
        }
        let header = header.trim_end();
        if header.is_empty() {
            break;
        }
        if let Some((name, value)) = header.split_once(':') {
            if name.eq_ignore_ascii_case("content-length") {
                content_length = value.trim().parse().unwrap_or(0);
            }
        }
    }
    let mut body = vec![0; content_length];
    if reader.read_exact(&mut body).is_err() {
        return;
    }

    let request = Request {
        method,
        path,
        body: String::from_utf8_lossy(&body).into_owned(),
    };
    recorded.lock().unwrap().push(request.clone());

    let reply = handler(&request);
    std::thread::sleep(reply.delay);
    let response = format!(
        "HTTP/1.1 {} Test\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
        reply.status,
        reply.body.len(),
        reply.body
    );
    let stream = reader.get_mut();
    let _ = stream.write_all(response.as_bytes());
    stream.conn.send_close_notify();
    let _ = stream.flush();
}

/// A loopback address with nothing listening, for connection-refused cases.
pub fn closed_address() -> SocketAddr {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    listener.local_addr().unwrap()
}
