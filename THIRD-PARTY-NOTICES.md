# Third-party notices

## Kopia

HyperHarbor's backup feature runs [Kopia](https://kopia.io) 0.23.1, which is embedded unmodified in
HyperHarbor.Host.exe and extracted beside the installed version. Kopia is licensed under the Apache License 2.0:
<https://github.com/kopia/kopia/blob/master/LICENSE>. The release used is pinned by SHA-256 in `tools/tools.json`
and downloaded from the project's GitHub release by `scripts/fetch-tools.ps1`.
