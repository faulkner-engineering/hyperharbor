# HyperHarbor

Host/client app that manages Hyper-V VMs on a home PC and connects to them in one click from any device. Think "Moonlight for Hyper-V."

## Architecture
- host/ (.NET 8): Worker Service + Kestrel API (mTLS), WinForms tray app, Core library
  - Hyper-V via CIM/WMI (root\virtualization\v2) and PowerShell SDK (PowerShell Direct)
- client/ (Tauri v2, Rust + TypeScript): discovery, pairing, VM dashboard, RDP launcher
- Discovery: mDNS _hyperharbor._tcp on LAN; manual add by IP/hostname otherwise
- Pairing: PIN shown in tray, certificate exchange and pinning, mTLS afterward
- Credentials: per-device local VM account, password rotated via PowerShell Direct on each connect, pushed to client cred store, removed after connect
- Wake-on-LAN: readiness check with auto-fix, Test Wake, relay mode later

## Roadmap (one phase per session, read-only first)
1. Solution skeleton + OpenAPI contract
2. List VMs and state (read-only)
3. Start/stop + Kestrel API on localhost
4. mDNS discovery + client VM list
5. PIN pairing + mTLS
6. Wake-on-LAN
7. One-click RDP with ephemeral credentials

## Rules
- MVP scope only. Do not implement later phases early.
- Professional code and comments. No slang. No em dashes.
- Small commits with clear messages. Run tests before committing.
- Never store VM passwords in plaintext. Never bind the API to 0.0.0.0 without mTLS.
- Ask before any destructive Hyper-V operation.