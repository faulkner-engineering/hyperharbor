<#
.SYNOPSIS
    Starts the portable HyperHarbor host (service and tray) from this folder.

.DESCRIPTION
    On first run, asks for elevation once to:
      - add an inbound firewall rule for the API port (Private networks only), and
      - add the current user to the Hyper-V Administrators group.
    Group membership takes effect after you sign out and back in.

    The service runs in its own console window so its log is visible. Close that window or
    run Stop-HyperHarbor.ps1 to stop it.
#>
[CmdletBinding()]
param(
    [int]$Port = 48443,

    # Internal: performs the elevated setup steps.
    [switch]$ElevatedSetup,
    [string]$ForUser
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$ruleName = 'HyperHarbor Host API'
$hyperVAdminsSid = 'S-1-5-32-578'

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

    Start-Sleep -Seconds 3
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

if (-not $inHyperVAdmins -or -not $hasRule) {
    Write-Host 'First-run setup needs administrator rights (firewall rule and Hyper-V Administrators group).'
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$($MyInvocation.MyCommand.Path)`"",
        '-ElevatedSetup', '-Port', $Port, '-ForUser', "`"$($identity.Name)`""
    )
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
}

$service = Join-Path $here 'HyperHarbor.Host.Service.exe'
$tray = Join-Path $here 'HyperHarbor.Host.Tray.exe'

if (Get-Process -Name 'HyperHarbor.Host.Service' -ErrorAction SilentlyContinue) {
    Write-Host 'The HyperHarbor service is already running.'
}
else {
    Start-Process -FilePath $service -WorkingDirectory $here -ArgumentList "--Api:Port=$Port"
    Write-Host "Started the HyperHarbor service on port $Port."
}

if (-not (Get-Process -Name 'HyperHarbor.Host.Tray' -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath $tray -WorkingDirectory $here
    Write-Host 'Started the HyperHarbor tray app.'
}

$publicNetworks = Get-NetConnectionProfile | Where-Object NetworkCategory -ne 'Private'
foreach ($network in $publicNetworks) {
    Write-Warning "Network '$($network.Name)' is $($network.NetworkCategory). Other devices can only connect over Private networks."
}
