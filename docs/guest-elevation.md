# Administrator access in guests

How a User gets administrator rights inside their own Windows VM. The account a User signs in with over Remote Desktop
(hh-<username>) is a standard user, so it cannot answer a User Account Control (UAC) prompt, and the guest administrator
(hhadmin) has a host-managed password the User never sees. Phase 28 built the first step, a short administrator window
with a passphrase the User types; this document also records the design for the integrated version, so the next phase
starts from it.

## Phase 28: administrator windows (built)

- Each User has a local administrator account in each Windows VM, hha-<username> (GuestAdminAccountName, next to the
  hh- rule in Users/UserStore.cs), full name "HyperHarbor administrator for <username>" so the UAC prompt explains
  itself. GuestAdminService creates it on the first request through PowerShell Direct with the stored hhadmin
  credential, so VMs set up before this phase need nothing new. It is in Administrators, not in Remote Desktop Users,
  its password never expires, it holds SeDenyNetworkLogonRight and SeDenyRemoteInteractiveLogonRight (it can answer
  UAC and sign in at the console, nothing else), and it is disabled whenever no window is open.
- Opening a window (POST /vms/{id}/guest-admin, elevated) enables the account with a new passphrase (PassphraseGenerator:
  four capitalized common words and a digit, hyphenated) for 10 minutes (PUT /host/guest-admin/settings, 2 to 30).
  Asking again while it is open returns the same passphrase. The window ends at expiry, on DELETE, when the VM stops
  (the sweep every 30 seconds), or with the host process; the host then sets a random password and disables the
  account, at once or as soon as the guest answers again (guest-admin.json lists windows that may still be enabled,
  without the passphrase).
- The client's Administrator access dialog shows the account, the passphrase split into its words, and a countdown.
  It has no copy button: with clipboard redirection on, the device's clipboard is readable inside the VM's session.

## The integrated version (design, not built)

The User should not have to read or type a secret at all. A HyperHarbor guest agent in the VM and a credential provider
in the guest's UAC prompt let the host hand the passphrase straight to Windows after the User approves on their client.

### Parts
- Guest agent: a Windows service in the VM (LocalSystem), installed by provisioning and by the unattended install. It
  talks to the host over Hyper-V sockets (AF_HYPERV), never the network: the host registers the agent's service GUID
  under HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices and listens on it
  (HV_GUID_WILDCARD bound to the VM's ID, so a connection proves which VM it came from); the agent connects to
  HV_GUID_PARENT. No firewall rule, no address, nothing another machine on the LAN can reach.
- Credential provider: a native C++ COM DLL in the guest (ICredentialProvider for CPUS_CREDUI) that adds an
  "Approve in HyperHarbor" tile to the UAC credential prompt, next to the password tiles.

### Flow
1. A program asks for elevation; UAC shows its credential prompt with the HyperHarbor tile.
2. The User picks the tile. The credential provider asks the agent over a local named pipe (ACL: SYSTEM and the
   interactive user) and shows "Waiting for approval in HyperHarbor".
3. The agent asks the host over the Hyper-V socket: VM (from the connection), the Windows session's user, and what it
   can tell about the requesting program.
4. The host raises an approval request on the User's connected client (the device whose Remote Desktop session is in
   that VM, else every device of that User that is online), with the program and the VM. Approval needs the existing
   elevation token or the admin passphrase; Windows Hello on the client can come later.
5. On approval the host opens the same window this phase builds (GuestAdminService.OpenAsync with a host requester),
   sends the account name and passphrase to the agent over the socket, and the agent hands them to the credential
   provider, which submits them to UAC (KERB_INTERACTIVE_UNLOCK_LOGON through GetSerialization). Nothing is shown
   or typed.
6. The host closes the window as soon as the agent reports the prompt finished (or after a short limit, such as 30
   seconds), instead of waiting out the 10 minutes.

### Hooks this phase leaves
- GuestAdminService.OpenAsync takes a GuestAdminRequester, not an HTTP request: a host event opens a window the same
  way, with the device that approved it as the requester.
- The per-User account and its name rule (hha-<username>, GuestAdminAccountName) stay the same, so accounts created by
  phase 28 are the ones the integrated version uses.
- The audit shape (GuestAdminAudit): actions openGuestAdmin and closeGuestAdmin with "account=..., expiresAt=..." and a
  close reason. A host-initiated open must write its own openGuestAdmin entry through IAuditLog before the guest call
  (fail closed, like the audit filter), with a reason such as approved=<device>; the API's open is audited by its route.
- The client's GuestAdminDialog is opened by a button today; the integrated version drives the same dialog (or an
  approval variant) from a host notification, so the client needs a push channel from the host (none exists yet;
  the client polls).

### Open questions
- Shipping native code in the guest: the credential provider is C++ COM loaded by LogonUI and consent.exe, so it needs
  its own code signing, an installer in the guest, and an update path (the agent can update it, but a broken provider
  can break the UAC prompt). Crashes there are serious; it must be small and fail to "no tile".
- Which program asked: credential providers are not told what requested elevation. The agent must work it out best
  effort (the consent.exe parent process and its command line, or the UAC prompt's window text) and the approval
  request must say when it could not.
- Approval fatigue: rate-limit requests per VM and User, make Deny the default action, show the program and VM in the
  request, and time a request out quickly.
- Linux guests: the same approval flow could run sudo through the SSH account manager (a time-limited sudoers entry or
  a one-time password for sudo), with a small agent over vsock instead of a credential provider.
- Whether the window should still exist as a fallback when the agent is not installed (likely yes: older VMs and
  Server Core).
