# Security policy

HyperHarbor runs as a Windows service with administrator rights, administers Hyper-V, and hands out Remote Desktop
credentials to paired devices, so security reports are taken seriously and fixed first.

## Supported versions

Fixes go into the next release on each channel. Update to the newest release before reporting, if you can.

| Version | Supported |
| --- | --- |
| Newest stable release | Yes |
| Newest beta (prerelease) | Yes |
| Anything older | No: update first; both the host and the client update themselves |

## Reporting a vulnerability

**Do not open a public issue.** Report it privately through this repository's
[Security tab](https://github.com/faulkner-engineering/hyperharbor/security/advisories/new) ("Report a vulnerability").

Please include:

- the host and client versions, and the Windows edition and build;
- what an attacker needs (a paired device, a device on the same network, a local account on the host, and so on);
- steps to reproduce, or a proof of concept;
- what they gain.

You can expect an acknowledgement within a week, an assessment and planned fix after that, and credit in the release
notes and the advisory if you want it. Please give us a reasonable time to release a fix before you publish details.

## Scope

In scope: the host (API, pairing, mTLS, elevation and the admin passphrase, the tray pipe, the installer and updater, VM
credential handling), the client (pairing, certificate pinning, stored keys, the files it writes for Remote Desktop, the
elevated helpers), and the release pipeline (update manifests and signatures).

Out of scope: problems that need an attacker who is already an administrator on the host or the client PC, denial of
service from a device on your own network without a paired certificate (pairing request spam is a known limitation), and
vulnerabilities in Windows, Hyper-V, or Remote Desktop themselves (report those to Microsoft).

HyperHarbor comes with no warranty; see [WARRANTY.md](WARRANTY.md).
