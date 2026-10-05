<#
.SYNOPSIS
    Installs the HyperHarbor host from this folder as a Windows service.

.DESCRIPTION
    Asks for elevation once, then:
      - copies this build to <InstallRoot>\versions\<version> and points the junction
        <InstallRoot>\current at it (InstallRoot defaults to %ProgramFiles%\HyperHarbor),
      - registers the HyperHarborHost service (LocalSystem, automatic start, restarts after a failure)
        against <InstallRoot>\current\HyperHarbor.Host.Service.exe, and records you as the user whose
        tray may connect to it,
      - adds the inbound firewall rule for the API port (Private networks only),
      - creates the standard local account paired devices use for VM consoles (hhc-<user>) if missing,
      - starts the service.
    It then starts the tray and registers it to start when you sign in.

    Data stays in %ProgramData%\HyperHarbor, so pairings, settings, and the audit trail of a portable
    host carry over. A running portable host is stopped first. Running this again from a newer build
    installs that build beside the current one and switches to it; the previous version stays on disk.
#>
[CmdletBinding()]
param(
    [int]$Port = 48443,

    # For tests only: the default location under Program Files is writable only by administrators,
    # which the service (LocalSystem) relies on.
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'HyperHarbor'),

    # Internal: performs the elevated steps.
    [switch]$ElevatedInstall,
    [string]$ForUserSid,
    [string]$ResultFile
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$serviceName = 'HyperHarborHost'
$parametersKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName\Parameters"
$ruleName = 'HyperHarbor Host API'
$consoleAccounts = Join-Path $env:ProgramData 'HyperHarbor\console-accounts.json.protected'
$current = Join-Path $InstallRoot 'current'
$serviceExe = Join-Path $current 'HyperHarbor.Host.Service.exe'
$trayExe = Join-Path $current 'HyperHarbor.Host.Tray.exe'

function Get-PackageVersion {
    $file = Join-Path $here 'version.json'
    if (-not (Test-Path $file)) {
        throw "version.json is missing from $here. Install from a package built by scripts\package.ps1."
    }
    $version = (Get-Content $file -Raw | ConvertFrom-Json).version
    if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw "version.json holds '$version', which is not a version number (major.minor.patch)."
    }
    return $version
}

# Points $current at $target. The new junction is created beside it first, so a failure leaves the old one.
function Set-CurrentVersion([string]$target) {
    $next = "$current.next"
    if (Test-Path $next) {
        [IO.Directory]::Delete($next)
    }
    New-Item -ItemType Junction -Path $next -Target $target | Out-Null

    if (Test-Path $current) {
        if (-not ((Get-Item $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "$current is a folder, not a junction. Move it away and run the installer again."
        }
        # Removes only the junction; Remove-Item would delete the files it points to.
        [IO.Directory]::Delete($current)
    }
    Rename-Item -Path $next -NewName (Split-Path -Leaf $current)
}

function Wait-ForPort([int]$Port, [TimeSpan]$Timeout) {
    $deadline = [DateTime]::UtcNow + $Timeout
    while ([DateTime]::UtcNow -lt $deadline) {
        $client = New-Object Net.Sockets.TcpClient
        try {
            $client.Connect('127.0.0.1', $Port)
            return $true
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
        finally {
            $client.Dispose()
        }
    }
    return $false
}

if ($ElevatedInstall) {
    try {
        $version = Get-PackageVersion
        $target = Join-Path $InstallRoot "versions\$version"

        # Stop the installed service, any portable host, and every tray (the tray started at logon runs from current).
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -ne 'Stopped') {
            Write-Host "Stopping the $serviceName service."
            Stop-Service -Name $serviceName -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        }
        Get-Process -Name 'HyperHarbor.Host.Service', 'HyperHarbor.Host.Tray' -ErrorAction SilentlyContinue |
            Stop-Process -Force -Confirm:$false

        # Copy the build unless this script already runs from the version folder.
        New-Item -ItemType Directory -Force (Join-Path $InstallRoot 'versions') | Out-Null
        if ((Resolve-Path $here).Path.TrimEnd('\') -ne $target.TrimEnd('\')) {
            robocopy $here $target /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) {
                throw "Copying the host to $target failed (robocopy exit code $LASTEXITCODE)."
            }
        }
        Set-CurrentVersion $target
        Write-Host "Installed HyperHarbor $version in $target."

        $binaryPath = "`"$serviceExe`""
        if ($Port -ne 48443) {
            $binaryPath += " --Api:Port=$Port"
        }
        if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
            New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName 'HyperHarbor Host' `
                -Description 'Manages Hyper-V VMs for paired HyperHarbor clients.' -StartupType Automatic | Out-Null
            Write-Host "Registered the $serviceName service."
        }
        else {
            sc.exe config $serviceName binPath= $binaryPath start= auto obj= LocalSystem | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Updating the $serviceName service failed (sc.exe exit code $LASTEXITCODE)." }
        }
        # Restart after a crash: 10 s, 30 s, then every 60 s; the count resets after a day.
        sc.exe failure $serviceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Setting the $serviceName recovery actions failed (sc.exe exit code $LASTEXITCODE)." }

        New-Item -Path $parametersKey -Force | Out-Null
        Set-ItemProperty -Path $parametersKey -Name 'TrayUserSid' -Value $ForUserSid

        if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port `
                -Action Allow -Profile Private | Out-Null
            Write-Host "Added firewall rule '$ruleName' for TCP $Port (Private networks)."
        }

        if (-not (Test-Path $consoleAccounts)) {
            & $serviceExe --setup-console
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Console access was not set up (exit code $LASTEXITCODE). Use Set up console access in the HyperHarbor Host window."
            }
        }

        Start-Service -Name $serviceName
        (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
        if (-not (Wait-ForPort -Port $Port -Timeout ([TimeSpan]::FromSeconds(60)))) {
            throw "The service is running but does not answer on TCP $Port. See $env:ProgramData\HyperHarbor\logs."
        }
        Write-Host "The $serviceName service is running on port $Port."
        Set-Content -Path $ResultFile -Value 'ok'
    }
    catch {
        Set-Content -Path $ResultFile -Value $_.Exception.Message
        Write-Warning $_.Exception.Message
        Start-Sleep -Seconds 5
    }
    return
}

if (-not (Get-Service -Name vmms -ErrorAction SilentlyContinue)) {
    Write-Warning 'Hyper-V is not enabled on this PC. Enable it, restart, and run this script again.'
    return
}

$version = Get-PackageVersion
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$resultFile = Join-Path ([IO.Path]::GetTempPath()) "hyperharbor-install-$([Guid]::NewGuid().ToString('N')).txt"
$arguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$($MyInvocation.MyCommand.Path)`"",
    '-ElevatedInstall', '-Port', $Port, '-InstallRoot', "`"$InstallRoot`"",
    '-ForUserSid', $identity.User.Value, '-ResultFile', "`"$resultFile`""
)
Write-Host "Installing HyperHarbor $version. Windows asks for administrator rights once."
try {
    Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait
}
catch {
    Write-Warning 'Installation was cancelled.'
    return
}

$result = if (Test-Path $resultFile) { (Get-Content $resultFile -Raw).Trim() } else { 'The elevated installer did not report a result.' }
Remove-Item $resultFile -ErrorAction SilentlyContinue
if ($result -ne 'ok') {
    Write-Warning "Installation failed: $result"
    return
}

# The tray runs as you, from the stable current path, at every sign-in.
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HyperHarbor Tray' -Value "`"$trayExe`""
Start-Process -FilePath $trayExe -WorkingDirectory $current
Write-Host "HyperHarbor $version is installed. The tray starts when you sign in." -ForegroundColor Green

$publicNetworks = Get-NetConnectionProfile | Where-Object NetworkCategory -ne 'Private'
foreach ($network in $publicNetworks) {
    Write-Warning "Network '$($network.Name)' is $($network.NetworkCategory). Other devices can only connect over Private networks."
}
