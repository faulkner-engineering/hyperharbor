<#
.SYNOPSIS
    Starts a portable HyperHarbor host (service and tray) from this folder, for development and testing.

.DESCRIPTION
    To install the host, double-click HyperHarbor.Host.exe instead (or run HyperHarbor.Host.exe install).

    On first run, asks for elevation once to:
      - add an inbound firewall rule for the API port (Private networks only),
      - add the current user to Hyper-V Administrators, and
      - create the standard local account paired devices use for VM consoles (hhc-<user>).
        It cannot sign in to Windows; the host rotates its password on every console request.
    Group membership takes effect after you sign out and back in.

    The elevated steps run as whichever administrator approves the prompt, so the files they write are
    given to the current user. When the host cannot open a file in its data folder (written by another
    account before this was fixed), the script asks for elevation again to restore access.

    The host runs in its own console window so its log is visible. Close that window or
    run Stop-HyperHarbor.ps1 to stop it.
#>
[CmdletBinding()]
param(
    [int]$Port = 48443,

    # Internal: performs the elevated setup steps.
    [switch]$ElevatedSetup,
    [string]$ForUser,
    [string]$ForUserSid,
    [switch]$RepairData
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$ruleName = 'HyperHarbor Host API'
$hyperVAdminsSid = 'S-1-5-32-578'
$dataFolder = Join-Path $env:ProgramData 'HyperHarbor'
$consoleAccounts = Join-Path $dataFolder 'console-accounts.json.protected'
$hostExe = Join-Path $here 'HyperHarbor.Host.exe'

# The data files the current account cannot open (the host refuses to start while there are any).
function Get-UnreadableDataFile {
    if (-not (Test-Path $dataFolder)) {
        return @()
    }

    $files = Get-ChildItem -LiteralPath $dataFolder -File -Recurse -Force -ErrorAction SilentlyContinue -ErrorVariable listErrors
    $unreadable = @($listErrors | ForEach-Object { $_.TargetObject })
    foreach ($file in $files) {
        try {
            [IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite, Delete').Dispose()
        }
        catch [UnauthorizedAccessException] {
            $unreadable += $file.FullName
        }
        catch {
            # Locked or removed meanwhile; access is not the problem.
        }
    }

    return $unreadable
}

if ($ElevatedSetup) {
    if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port `
            -Action Allow -Profile Private | Out-Null
        Write-Host "Added firewall rule '$ruleName' for TCP $Port (Private networks)."
    }

    $group = Get-LocalGroup -SID $hyperVAdminsSid
    if (-not (Get-LocalGroupMember -Group $group | Where-Object Name -eq $ForUser)) {
        Add-LocalGroupMember -Group $group -Member $ForUser
        Write-Host "Added $ForUser to $($group.Name). Sign out and back in for this to take effect."
    }

    # Setup also gives the files already in the data folder back to this user (--for-user).
    if ($RepairData -or -not (Test-Path $consoleAccounts)) {
        $setupArguments = @('--setup-console')
        if ($ForUserSid) {
            $setupArguments += "--for-user=$ForUserSid"
        }

        $setup = Start-Process -FilePath $hostExe -ArgumentList $setupArguments -Wait -PassThru
        if ($setup.ExitCode -ne 0) {
            Write-Warning "Console access was not set up (exit code $($setup.ExitCode)). Use Set up console access in the HyperHarbor Host window."
        }
    }

    Start-Sleep -Seconds 3
    return
}

# The installed service and a portable host would share the port, the tray pipe, and the data.
if (Get-Service -Name 'HyperHarborHost' -ErrorAction SilentlyContinue) {
    Write-Warning 'HyperHarbor is installed as a service on this PC. Run "HyperHarbor.Host.exe uninstall" first to use the portable host.'
    return
}

# Hyper-V must be enabled on this PC.
if (-not (Get-Service -Name vmms -ErrorAction SilentlyContinue)) {
    Write-Warning 'Hyper-V is not enabled on this PC. Enable it, restart, and run this script again.'
    return
}

# One-time setup that needs administrator rights.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$inHyperVAdmins = $identity.Groups.Value -contains $hyperVAdminsSid
$hasRule = [bool](Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)
$hasConsole = Test-Path $consoleAccounts
$unreadable = @(Get-UnreadableDataFile)

if (-not $inHyperVAdmins -or -not $hasRule -or -not $hasConsole -or $unreadable.Count -gt 0) {
    if ($unreadable.Count -gt 0) {
        Write-Host "This account cannot open $($unreadable.Count) file(s) in $dataFolder, for example $($unreadable[0]). Setup restores access."
    }
    else {
        Write-Host 'First-run setup needs administrator rights (firewall rule, Hyper-V Administrators group, and the console account).'
    }

    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$($MyInvocation.MyCommand.Path)`"",
        '-ElevatedSetup', '-Port', $Port, '-ForUser', "`"$($identity.Name)`"", '-ForUserSid', $identity.User.Value
    )
    if ($unreadable.Count -gt 0) {
        $arguments += '-RepairData'
    }

    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait
    }
    catch {
        Write-Warning 'Setup was cancelled. Other devices may not be able to connect, and VMs may not be visible.'
    }

    if (-not $inHyperVAdmins) {
        Write-Warning 'Sign out and back in so Hyper-V Administrators membership applies, then run this script again.'
        return
    }

    $unreadable = @(Get-UnreadableDataFile)
    if ($unreadable.Count -gt 0) {
        Write-Warning "This account still cannot open $($unreadable[0]), so the host would not start. Approve the setup prompt, then run this script again."
        return
    }
}

# The host and the tray are the same executable; the command line tells them apart.
$running = Get-CimInstance Win32_Process -Filter "Name = 'HyperHarbor.Host.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($here, [StringComparison]::OrdinalIgnoreCase) }
$hostRunning = $running | Where-Object { $_.CommandLine -notmatch '--tray' }
$trayRunning = $running | Where-Object { $_.CommandLine -match '--tray' }

if ($hostRunning) {
    Write-Host 'The HyperHarbor host is already running.'
}
else {
    Start-Process -FilePath $hostExe -WorkingDirectory $here -ArgumentList 'run', "--Api:Port=$Port"
    Write-Host "Started the HyperHarbor host on port $Port."
}

if (-not $trayRunning) {
    Start-Process -FilePath $hostExe -WorkingDirectory $here -ArgumentList '--tray'
    Write-Host 'Started the HyperHarbor tray app.'
}

$publicNetworks = Get-NetConnectionProfile | Where-Object NetworkCategory -ne 'Private'
foreach ($network in $publicNetworks) {
    Write-Warning "Network '$($network.Name)' is $($network.NetworkCategory). Other devices can only connect over Private networks."
}
