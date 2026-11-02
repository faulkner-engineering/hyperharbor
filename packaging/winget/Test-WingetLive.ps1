<#
.SYNOPSIS
    Live test of the winget packages: installs, upgrades, self-updates, and uninstalls the real host and client from
    local manifests and checks the result after each step. Opt-in; it changes the PC (a test PC only).

.DESCRIPTION
    Run as the user who will use HyperHarbor, not elevated: winget then starts the host's installer unelevated and the
    installer asks for elevation itself, as it does for a standard user. Each host install, upgrade, and uninstall shows
    one UAC prompt; the client shows none. Needs LocalManifestFiles turned on once (elevated:
    winget settings --enable LocalManifestFiles) and the HyperHarborHost service not installed.

    -ManifestRoot holds the manifests\f\FaulknerEngineering\HyperHarbor\{Host,Client}\<version> folders that
    New-WingetManifests.ps1 writes, for every version named below. Steps:
      1. Fresh install: host -NewVersion and client -ClientVersion; winget list, the service, the uninstall entry, and
         the client starting.
      2. Uninstall both: the service, the update task, the uninstall entries, and the install folder are gone; the data
         folder stays.
      3. Upgrade: host -PreviousVersion, then -NewVersion over it; both version folders stay and current points at the
         new one. Client -PreviousClientVersion, then -ClientVersion. Then uninstall both.
      4. Self-update (with -SelfUpdateVersion, an install older than the stable release -SelfUpdateTarget): the host
         updates itself from the stable channel (Auto mode, after 10 idle minutes), and the uninstall entry and winget
         list then show -SelfUpdateTarget. Then -NewVersion over it, and uninstall.
    Writes a log (default %TEMP%\hyperharbor-winget-live.log) and exits 1 when any check failed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File packaging\winget\Test-WingetLive.ps1 -ManifestRoot C:\temp\winget `
        -NewVersion 0.2.6 -PreviousVersion 0.2.5 -ClientVersion 0.2.5 -PreviousClientVersion 0.2.4 `
        -SelfUpdateVersion 0.2.5-winget.1 -SelfUpdateTarget 0.2.5
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestRoot,
    [Parameter(Mandatory)][string]$NewVersion,
    [string]$PreviousVersion,
    [Parameter(Mandatory)][string]$ClientVersion,
    [string]$PreviousClientVersion,
    [string]$SelfUpdateVersion,
    [string]$SelfUpdateTarget,
    [int]$SelfUpdateMinutes = 40,
    # Which steps to run; steps 3 and 4 also start and end with nothing installed.
    [int[]]$Steps = @(1, 2, 3, 4),
    [string]$Log = (Join-Path $env:TEMP 'hyperharbor-winget-live.log')
)

# Continue: in Windows PowerShell, Stop turns a native command's stderr line (winget, schtasks) into an exception.
$ErrorActionPreference = 'Continue'
$script:failures = New-Object System.Collections.Generic.List[string]
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\HyperHarborHost'
$clientKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HyperHarbor'
$root = Join-Path $env:ProgramFiles 'HyperHarbor'
$data = Join-Path $env:ProgramData 'HyperHarbor'

function Write-Log([string]$Text) {
    $line = "$(Get-Date -Format 'HH:mm:ss') $Text"
    Write-Host $line
    Add-Content -LiteralPath $Log -Value $line -Encoding UTF8
}

function Test-Check([string]$Name, [bool]$Passed, [string]$Detail = '') {
    if ($Passed) { Write-Log "  PASS $Name $Detail" } else { Write-Log "  FAIL $Name $Detail"; $script:failures.Add($Name) }
}

function Get-Folder([string]$Package, [string]$Version) {
    $folder = Join-Path $ManifestRoot "manifests\f\FaulknerEngineering\HyperHarbor\$Package\$Version"
    if (-not (Test-Path $folder)) { throw "No manifests for $Package $Version in $folder" }
    return $folder
}

function Invoke-Winget([string]$Step, [string[]]$Arguments, [switch]$Elevates) {
    if ($Elevates) { Write-Log "$Step (expect one UAC prompt)" } else { Write-Log $Step }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $output = & winget @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    # winget redraws its progress with carriage returns; keep the last text of each line, and only lines with words.
    $text = ($output -split "`r?`n" | ForEach-Object { ($_ -split "`r")[-1].Trim() } | Where-Object { $_ -match '[A-Za-z]{2}' }) -join ' | '
    Write-Log "  winget $($Arguments -join ' ') -> exit $code in $([int]$watch.Elapsed.TotalSeconds) s: $text"
    return $code
}

# The host's scheduled tasks and their folder. A standard user cannot list SYSTEM tasks (schtasks says the path is not
# found, the task cache keys are unreadable and can outlive the task), but the Task Scheduler API answers "not found"
# (0x80070002) only for a task that does not exist.
function Get-HostTasks {
    $service = New-Object -ComObject Schedule.Service
    $service.Connect()
    try { $folder = $service.GetFolder('\HyperHarbor') } catch { return @() }
    $found = @('(folder)')
    foreach ($name in 'Update', 'Console sleep', 'Console wake') {
        try { $null = $folder.GetTask($name); $found += $name }
        catch { if ($_.Exception.Message -notmatch '0x80070002') { $found += $name } }
    }
    return $found
}

function Get-HostEntry { Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue }

function Get-CurrentTarget {
    $item = Get-Item -LiteralPath (Join-Path $root 'current') -ErrorAction SilentlyContinue
    if ($item -and $item.Target) { return Split-Path -Leaf (@($item.Target)[0]) }
    return $null
}

function Get-WingetListVersion([string]$Name) {
    $lines = & winget list --name $Name --exact --accept-source-agreements --disable-interactivity 2>&1 | Out-String
    $match = [regex]::Match($lines, "(?m)^$([regex]::Escape($Name))\s+\S+\s+(\S+)")
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}

# -Released: a version published before the winget work (0.2.5 and older), whose entry has no quiet uninstall yet.
function Test-HostInstalled([string]$Version, [switch]$Released) {
    $service = Get-Service HyperHarborHost -ErrorAction SilentlyContinue
    Test-Check 'service running' ($service -and $service.Status -eq 'Running') "($($service.Status))"
    $entry = Get-HostEntry
    Test-Check 'uninstall entry DisplayVersion' ($entry -and $entry.DisplayVersion -eq $Version) "($($entry.DisplayVersion))"
    if (-not $Released) {
        Test-Check 'uninstall entry QuietUninstallString' ($entry -and $entry.QuietUninstallString -like '*HyperHarbor.Host.exe" uninstall --quiet') "($($entry.QuietUninstallString))"
        Test-Check 'uninstall entry URLInfoAbout' ($entry -and $entry.URLInfoAbout -eq 'https://faulkner.engineering/hyperharbor')
    }
    Test-Check 'current junction' ((Get-CurrentTarget) -eq $Version) "($(Get-CurrentTarget))"
    $listed = Get-WingetListVersion 'HyperHarbor Host'
    Test-Check 'winget list host version' ($listed -eq $Version) "($listed)"
    $tasks = @(Get-HostTasks)
    Test-Check 'update task' ($tasks -contains 'Update') "($($tasks -join ', '))"
}

function Test-HostRemoved {
    Test-Check 'service removed' (-not (Get-Service HyperHarborHost -ErrorAction SilentlyContinue))
    $tasks = @(Get-HostTasks)
    Test-Check 'scheduled tasks and their folder removed' ($tasks.Count -eq 0) "($($tasks -join ', '))"
    Test-Check 'uninstall entry removed' (-not (Test-Path -LiteralPath $uninstallKey))
    Test-Check 'install folder removed' (-not (Test-Path -LiteralPath $root))
    Test-Check 'data folder kept' (Test-Path -LiteralPath $data)
    Test-Check 'winget list has no host' ($null -eq (Get-WingetListVersion 'HyperHarbor Host'))
}

function Test-ClientInstalled([string]$Version) {
    $entry = Get-ItemProperty -LiteralPath $clientKey -ErrorAction SilentlyContinue
    Test-Check 'client uninstall entry' ($entry -and $entry.DisplayVersion -eq $Version) "($($entry.DisplayVersion))"
    $listed = Get-WingetListVersion 'HyperHarbor'
    Test-Check 'winget list client version' ($listed -eq $Version) "($listed)"
    $exe = Join-Path $env:LOCALAPPDATA 'HyperHarbor\hyperharbor-client.exe'
    $process = Start-Process $exe -PassThru
    Start-Sleep -Seconds 10
    $process.Refresh()
    Test-Check 'client starts and shows its window' ((-not $process.HasExited) -and $process.MainWindowHandle -ne 0)
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}

function Uninstall-Both {
    $code = Invoke-Winget 'Uninstall the host' @('uninstall', '--name', 'HyperHarbor Host', '--exact', '--disable-interactivity') -Elevates
    Test-Check 'host uninstall exit code' ($code -eq 0) "($code)"
    Test-HostRemoved
    $code = Invoke-Winget 'Uninstall the client' @('uninstall', '--name', 'HyperHarbor', '--exact', '--disable-interactivity')
    Test-Check 'client uninstall exit code' ($code -eq 0) "($code)"
    # The client's QuietUninstallString runs the uninstaller in place (_?=), so it is done when winget returns (a client
    # installed before 0.2.6 has none, and its uninstaller returns early). Only uninstall.exe, which cannot delete
    # itself while running, goes a moment later.
    $folder = Join-Path $env:LOCALAPPDATA 'HyperHarbor'
    Test-Check 'client uninstall entry removed when winget returns' (-not (Test-Path -LiteralPath $clientKey))
    Test-Check 'client removed when winget returns' (-not (Test-Path -LiteralPath (Join-Path $folder 'hyperharbor-client.exe')))
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ((Test-Path -LiteralPath (Join-Path $folder 'uninstall.exe')) -and $watch.Elapsed.TotalSeconds -lt 40) { Start-Sleep -Milliseconds 250 }
    Test-Check 'client uninstall.exe removed afterwards' (-not (Test-Path -LiteralPath (Join-Path $folder 'uninstall.exe'))) "($([Math]::Round($watch.Elapsed.TotalSeconds, 1)) s)"
}

function Install-Host([string]$Step, [string]$Version) {
    $code = Invoke-Winget $Step @('install', '--manifest', (Get-Folder Host $Version), '--accept-package-agreements', '--disable-interactivity') -Elevates
    Test-Check "host $Version install exit code" ($code -eq 0) "($code)"
}

function Install-Client([string]$Step, [string]$Version) {
    $code = Invoke-Winget $Step @('install', '--manifest', (Get-Folder Client $Version), '--accept-package-agreements', '--disable-interactivity')
    Test-Check "client $Version install exit code" ($code -eq 0) "($code)"
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this as the user, not elevated: the host installer elevates itself, as it does for winget users.'
}
if (Get-Service HyperHarborHost -ErrorAction SilentlyContinue) { throw 'The HyperHarborHost service is installed. Uninstall it first.' }

Write-Log "winget live test (steps $($Steps -join ', ')): new $NewVersion, previous $PreviousVersion, client $ClientVersion, self-update $SelfUpdateVersion -> $SelfUpdateTarget"
Write-Log "winget $(winget --version)"

if ($Steps -contains 1) {
    Write-Log '1. Fresh install'
    Install-Host 'Install the host' $NewVersion
    Test-HostInstalled $NewVersion
    Install-Client 'Install the client' $ClientVersion
    Test-ClientInstalled $ClientVersion
}

if ($Steps -contains 2) {
    Write-Log '2. Uninstall'
    Uninstall-Both
}

if ($PreviousVersion -and $Steps -contains 3) {
    Write-Log '3. Upgrade'
    Install-Host 'Install the previous host' $PreviousVersion
    Test-HostInstalled $PreviousVersion -Released
    $code = Invoke-Winget "Upgrade the host to $NewVersion" @('upgrade', '--manifest', (Get-Folder Host $NewVersion), '--accept-package-agreements', '--disable-interactivity') -Elevates
    Test-Check 'host upgrade exit code' ($code -eq 0) "($code)"
    Test-HostInstalled $NewVersion
    $versions = @(Get-ChildItem (Join-Path $root 'versions') -Directory | ForEach-Object Name)
    Test-Check 'both versions kept for rollback' (($versions -contains $PreviousVersion) -and ($versions -contains $NewVersion)) "($($versions -join ', '))"
    if ($PreviousClientVersion) {
        Install-Client 'Install the previous client' $PreviousClientVersion
        $code = Invoke-Winget "Upgrade the client to $ClientVersion" @('upgrade', '--manifest', (Get-Folder Client $ClientVersion), '--accept-package-agreements', '--disable-interactivity')
        Test-Check 'client upgrade exit code' ($code -eq 0) "($code)"
        Test-ClientInstalled $ClientVersion
    }
    else {
        Install-Client 'Install the client' $ClientVersion
    }
    Uninstall-Both
}

if ($SelfUpdateVersion -and $Steps -contains 4) {
    Write-Log "4. Self-update from $SelfUpdateVersion to $SelfUpdateTarget"
    Install-Host 'Install the older host' $SelfUpdateVersion
    Test-HostInstalled $SelfUpdateVersion
    Write-Log "  Waiting up to $SelfUpdateMinutes minutes for the host to update itself (stable channel, after 10 idle minutes)"
    $deadline = (Get-Date).AddMinutes($SelfUpdateMinutes)
    $seen = $null
    while ((Get-Date) -lt $deadline) {
        $current = Get-CurrentTarget
        $entry = (Get-HostEntry).DisplayVersion
        if ("$current $entry" -ne $seen) { Write-Log "  current $current, uninstall entry $entry"; $seen = "$current $entry" }
        if ($current -eq $SelfUpdateTarget -and $entry -eq $SelfUpdateTarget) { break }
        Start-Sleep -Seconds 30
    }
    Test-HostInstalled $SelfUpdateTarget
    Install-Host "Install $NewVersion over the self-updated host" $NewVersion
    Test-HostInstalled $NewVersion
    $code = Invoke-Winget 'Uninstall the host' @('uninstall', '--name', 'HyperHarbor Host', '--exact', '--disable-interactivity') -Elevates
    Test-Check 'host uninstall exit code' ($code -eq 0) "($code)"
    Test-HostRemoved
}

if ($script:failures.Count -gt 0) {
    Write-Log "FAILED: $($script:failures.Count) checks: $($script:failures -join '; ')"
    exit 1
}
Write-Log 'All checks passed.'
