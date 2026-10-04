HyperHarbor host (portable test build)

Requirements
  - Windows 10 or 11 Pro with Hyper-V enabled
  - No .NET installation is needed

Start
  Right-click Start-HyperHarbor.ps1 and choose "Run with PowerShell".
  The first run asks for administrator rights once, to add a firewall rule
  (TCP 48443, Private networks), add you to Hyper-V Administrators, and create
  the local account paired devices use for VM consoles (hhc-owner). That account
  cannot sign in to Windows, and its password changes on every console request.
  If you were added to that group, sign out and back in, then run it again.

Pair a device
  Install the HyperHarbor client on another PC, select this host, and press Pair.
  The PIN appears in a window from the HyperHarbor tray icon on this PC.

Stop
  Run Stop-HyperHarbor.ps1, or close the service console window and exit the tray.

Data
  Host identity, certificate, and paired devices are stored in
  %ProgramData%\HyperHarbor. Delete that folder to reset the host.

Remove
  Stop the host, then remove the console account by running this from this
  folder in an administrator PowerShell:
    .\HyperHarbor.Host.Service.exe --remove-console
  Then delete this folder and %ProgramData%\HyperHarbor, and remove the
  firewall rule "HyperHarbor Host API" (Windows Defender Firewall, Inbound Rules).

This build is not code signed, so Windows SmartScreen may warn on first run.
