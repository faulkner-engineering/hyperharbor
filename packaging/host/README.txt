HyperHarbor host

Requirements
  - Windows 10 or 11 Pro with Hyper-V enabled
  - No .NET installation is needed

Install (recommended)
  Right-click Install-HyperHarbor.ps1 and choose "Run with PowerShell".
  It asks for administrator rights once, then installs the host to
  %ProgramFiles%\HyperHarbor and runs it as the Windows service HyperHarborHost,
  which starts with Windows. The tray starts when you sign in. The installer also
  adds a firewall rule (TCP 48443, Private networks) and creates the local account
  paired devices use for VM consoles (hhc-owner). That account cannot sign in to
  Windows, and its password changes on every console request.
  Run the installer from a newer build to update; the previous version stays on disk.

Run without installing (portable)
  Right-click Start-HyperHarbor.ps1 and choose "Run with PowerShell".
  The first run asks for administrator rights once, to add the firewall rule,
  add you to Hyper-V Administrators, and create the console account.
  If you were added to that group, sign out and back in, then run it again.
  Stop it with Stop-HyperHarbor.ps1, or close the service console window and exit
  the tray. The portable host does not run while the service is installed.

Pair a device
  Install the HyperHarbor client on another PC, select this host, and press Pair.
  The PIN appears in a window from the HyperHarbor tray icon on this PC.

Data
  Host identity, certificate, paired devices, and logs are stored in
  %ProgramData%\HyperHarbor, shared by the installed and portable host.
  Delete that folder to reset the host.

Remove
  Run Uninstall-HyperHarbor.ps1 (add -RemoveData to delete %ProgramData%\HyperHarbor too).
  For the portable host: stop it, then run this from its folder in an administrator PowerShell:
    .\HyperHarbor.Host.Service.exe --remove-console
  Then delete the folder and %ProgramData%\HyperHarbor, and remove the firewall
  rule "HyperHarbor Host API" (Windows Defender Firewall, Inbound Rules).

This build is not code signed, so Windows SmartScreen may warn on first run.
