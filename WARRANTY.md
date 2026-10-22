# Warranty

HyperHarbor is provided **"as is", without warranty of any kind**, express or implied, including the implied
warranties of merchantability, fitness for a particular purpose, and non-infringement. Faulkner Engineering LLC and the
contributors are not liable for any damages arising from its use, as set out in sections 7 (Disclaimer of Warranty) and
8 (Limitation of Liability) of the [Apache License 2.0](LICENSE). This page explains what that means in practice; the
license is the binding text.

## What HyperHarbor does to your PCs

HyperHarbor administers Hyper-V and parts of Windows on the PCs you install it on. Depending on what you use, it can:

- start, stop, turn off, create, change, move, copy, and delete virtual machines and their disks;
- create local accounts inside your virtual machines and on the host, and change their passwords;
- change host settings such as power plans, Wake-on-LAN, services, startup programs, and installed apps (Lean host);
- install updates of itself, and run backups and restores of virtual machines;
- add a firewall rule, a Windows service, scheduled tasks, and (with your approval) a camera component and a trusted
  certificate for signing Remote Desktop files.

A mistake, a bug, or an interrupted operation can lose data or leave a PC or virtual machine unable to start.

## Before you rely on it

- **Back up anything you cannot afford to lose**, inside and outside your virtual machines, and check that the backup
  restores. HyperHarbor's own backups are not a substitute for an independent copy.
- **Prereleases (beta versions) are for testing.** They may contain defects that stable releases do not.
- **Destructive actions ask for the admin passphrase.** Keep it to yourself, and pair only devices you control.
- **Read the [changelog](CHANGELOG.md)** before updating, for changes you need to act on.

## Support

There is no guaranteed support, response time, or fix. Report problems and ideas through the
[issue tracker](https://github.com/faulkner-engineering/hyperharbor/issues). Report security problems privately through
the repository's Security tab ("Report a vulnerability"), not in a public issue.
