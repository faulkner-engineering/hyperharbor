<#
.SYNOPSIS
    Collects Wake-on-LAN diagnostics that HyperHarbor's readiness checks cannot see.

.DESCRIPTION
    Read-only report (run normally, or as administrator for the NIC power settings):
      - physical adapters and their MAC addresses
      - Hyper-V external switches bound to a physical adapter
      - network adapter power management and advanced wake/energy settings
      - available sleep states, sleep and hibernate timeouts
      - recent sleep and wake events and the last wake source

    -Listen (requires administrator): listens on UDP 9 and 7 while the PC is awake and reports
    every magic packet that arrives. Press Wake in the HyperHarbor client during the listen
    window. If nothing arrives, the network is dropping the packet before it reaches this PC.
    A temporary firewall rule is added for the duration and removed afterwards.

.EXAMPLE
    .\Diagnose-Wake.ps1 > wake-report.txt
    .\Diagnose-Wake.ps1 -Listen -Seconds 120
#>
[CmdletBinding()]
param(
    [switch]$Listen,
    [int]$Seconds = 120
)

$ErrorActionPreference = 'Continue'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

function Section([string]$Title) {
    Write-Output ''
    Write-Output "=== $Title ==="
}

function Format-Mac([string]$Mac) {
    ($Mac -replace '[-:]', '').ToUpperInvariant()
}

if ($Listen) {
    if (-not $isAdmin) {
        Write-Warning 'Listening needs administrator rights (for a temporary firewall rule). Run PowerShell as administrator.'
        return
    }

    $localMacs = Get-NetAdapter -Physical | ForEach-Object { Format-Mac $_.MacAddress }
    $ruleName = 'HyperHarbor Wake diagnostics (temporary)'
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol UDP -LocalPort 7, 9 -Action Allow | Out-Null
    $clients = @(9, 7) | ForEach-Object {
        $client = New-Object System.Net.Sockets.UdpClient
        $client.Client.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::Socket, [System.Net.Sockets.SocketOptionName]::ReuseAddress, $true)
        $client.Client.Bind((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, $_)))
        $client
    }

    try {
        Write-Output "Listening on UDP 9 and 7 for $Seconds seconds. Press Wake in the HyperHarbor client now."
        $deadline = (Get-Date).AddSeconds($Seconds)
        $received = 0
        while ((Get-Date) -lt $deadline) {
            foreach ($client in $clients) {
                while ($client.Available -gt 0) {
                    $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
                    $bytes = $client.Receive([ref]$remote)
                    $received++
                    $isMagic = $bytes.Length -ge 102 -and ($bytes[0..5] | Where-Object { $_ -ne 0xFF }).Count -eq 0
                    $target = if ($isMagic) { ($bytes[6..11] | ForEach-Object { $_.ToString('X2') }) -join '' } else { '' }
                    $match = if (-not $isMagic) { 'not a magic packet' } elseif ($localMacs -contains $target) { 'matches this PC' } else { 'for a DIFFERENT MAC' }
                    Write-Output ("{0:HH:mm:ss} from {1} to port {2}: {3} bytes, target MAC {4} ({5})" -f (Get-Date), $remote, ($client.Client.LocalEndPoint.Port), $bytes.Length, $target, $match)
                }
            }
            Start-Sleep -Milliseconds 200
        }

        if ($received -eq 0) {
            Write-Output 'No packets arrived. The network did not deliver the wake signal to this PC (see the notes at the end).'
        }
    }
    finally {
        $clients | ForEach-Object { $_.Close() }
        Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    }

    Write-Output ''
    Write-Output 'If nothing arrived: wireless access points and mesh systems often drop broadcast traffic from'
    Write-Output 'Wi-Fi clients. Try waking from a wired client, or disable "AP isolation" or broadcast filtering.'
    return
}

Write-Output "HyperHarbor Wake-on-LAN diagnostics for $env:COMPUTERNAME at $(Get-Date -Format s)"
Write-Output "Running as administrator: $isAdmin"

Section 'Physical network adapters'
$physical = Get-NetAdapter -Physical | Sort-Object Name
$physical | Format-Table Name, InterfaceDescription, MacAddress, Status, LinkSpeed, MediaType -AutoSize | Out-String -Width 200

Section 'Hyper-V external switches'
if (Get-Command Get-VMSwitch -ErrorAction SilentlyContinue) {
    $external = Get-VMSwitch -SwitchType External -ErrorAction SilentlyContinue
    if ($external) {
        $external | Format-Table Name, NetAdapterInterfaceDescription, AllowManagementOS -AutoSize | Out-String -Width 200
        Write-Output 'NOTE: A physical adapter bound to a Hyper-V external switch often cannot wake the PC.'
        Write-Output '      If the wired adapter is listed above, test Wake-on-LAN with the switch removed or'
        Write-Output '      bound to a different adapter.'
    }
    else {
        Write-Output 'None.'
    }
}
else {
    Write-Output 'Hyper-V PowerShell module not available.'
}

Section 'Adapter power management'
foreach ($adapter in $physical) {
    try {
        Get-NetAdapterPowerManagement -Name $adapter.Name -ErrorAction Stop |
            Format-List Name, WakeOnMagicPacket, WakeOnPattern, DeviceSleepOnDisconnect, AllowComputerToTurnOffDevice |
            Out-String -Width 200
    }
    catch {
        Write-Output "$($adapter.Name): not available ($($_.Exception.Message.Trim()))"
    }
}

Section 'Adapter advanced settings related to wake and power saving'
foreach ($adapter in $physical) {
    Write-Output "-- $($adapter.Name)"
    Get-NetAdapterAdvancedProperty -Name $adapter.Name -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -match 'wake|wol|magic|pattern|energy|eee|green|power|shutdown|sleep|link speed' } |
        Format-Table DisplayName, DisplayValue -AutoSize | Out-String -Width 200
}

Section 'Devices allowed to wake the PC'
powercfg /devicequery wake_armed

Section 'Available sleep states'
powercfg /a

Section 'Sleep and hibernate timeouts on AC power (seconds, 0 = never)'
foreach ($setting in 'STANDBYIDLE', 'HIBERNATEIDLE', 'HYBRIDSLEEP') {
    $line = powercfg /q SCHEME_CURRENT SUB_SLEEP $setting | Select-String 'Current AC Power Setting Index'
    Write-Output ("{0,-14} {1}" -f $setting, (($line -split ':')[-1]).Trim())
}

Section 'Last wake source'
powercfg /lastwake

Section 'Recent sleep and wake events (last 3 days)'
$filter = @{ LogName = 'System'; StartTime = (Get-Date).AddDays(-3); Id = 1, 42, 107, 506, 507 }
Get-WinEvent -FilterHashtable $filter -MaxEvents 30 -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -in 'Microsoft-Windows-Kernel-Power', 'Microsoft-Windows-Power-Troubleshooter' } |
    ForEach-Object {
        $first = ($_.Message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 3) -join ' | '
        Write-Output ("{0:yyyy-MM-dd HH:mm:ss}  {1,4}  {2}" -f $_.TimeCreated, $_.Id, $first)
    }

Write-Output ''
Write-Output 'Notes: Windows cannot read BIOS/UEFI settings. If everything above looks right, check the'
Write-Output 'firmware for "Wake on LAN", "Power On By PCI-E", and "ErP" or "Deep Sleep" (which must be off).'
