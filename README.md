<picture>
  <source media="(prefers-color-scheme: dark)" srcset="hyperharbor-brand/png/lockup-horizontal-on-dark@2x.png">
  <source media="(prefers-color-scheme: light)" srcset="hyperharbor-brand/png/lockup-horizontal@2x.png">
  <img alt="HyperHarbor" src="hyperharbor-brand/png/lockup-horizontal@2x.png" width="420">
</picture>

# HyperHarbor

Manage Hyper-V virtual machines on a home PC and connect to them in one click from any device.

## Repository layout

| Path | Contents |
| --- | --- |
| `docs/api.yaml` | OpenAPI 3.1 contract between host and client |
| `host/src/Shared.Contracts` | DTOs mirroring the API contract |
| `host/src/Host.Core` | Hyper-V and host services |
| `host/src/Host.Service` | Windows service hosting the API |
| `host/src/Host.Tray` | Tray application (pairing PIN, status) |
| `host/tests/Host.Tests` | Host unit tests |
| `client/` | Tauri v2 client (Rust + Svelte/TypeScript) |

## Requirements

- Windows 10/11 Pro with Hyper-V (host)
- .NET SDK 8.0
- Rust (MSVC toolchain) and Node.js 20 or later (client)

## Build

```powershell
dotnet build HyperHarbor.sln
dotnet test HyperHarbor.sln

cd client
npm install
npm run tauri dev
```

## Brand

The logo, app and tray icons, colors, and usage rules are in [hyperharbor-brand/](hyperharbor-brand/README.md).
