<#
.SYNOPSIS
    Runs the signed-in session live tests: the checks in a real Windows guest session that used to be done by hand.

.DESCRIPTION
    Starts two processes that talk through a private temporary folder:
      - the client agent (cargo test session_live, client/src-tauri/src/session_live.rs), which opens Remote Desktop
        with the client's own rdp::launch for each request;
      - SessionLiveTests (Host.Tests), which runs the live host harness, connects, checks the guest, and resizes and
        reads the session windows.

    Needs an active desktop on this machine (stay connected to it; a minimized or disconnected session stops mstsc
    from drawing and resizing), RDP trust set up for this account in the client (otherwise mstsc stops at its security
    dialog), and a Windows VM made from the release profile (Brave as the default browser, Edge kept out of the way):
        -VmId <id>     reuses one, for example from an earlier run with -Keep.
        -Iso <name>    creates HyperHarbor-Test from an image in the harness ISO folder (about 25 minutes) and deletes
                       it afterwards unless -Keep is given.
    -IsoFolder and -VmFolder set HH_HARNESS_ISO_FOLDER and HH_HARNESS_VM_FOLDER.

    Run from Windows PowerShell:
        powershell -ExecutionPolicy Bypass -File scripts\live-session.ps1 -VmId <id>
        powershell -ExecutionPolicy Bypass -File scripts\live-session.ps1 -Iso Win11.iso -Keep

    The progress log is %TEMP%\hyperharbor-session-progress.log, and the agent's output goes to
    %TEMP%\hyperharbor-session-agent.log.
#>
[CmdletBinding()]
param(
    [string]$VmId,
    [string]$Iso,
    [switch]$Keep,
    [string]$IsoFolder,
    [string]$VmFolder
)

$ErrorActionPreference = 'Stop'
if (-not $VmId -and -not $Iso) {
    throw 'Give -VmId of a VM made from the release profile, or -Iso to create one.'
}

$repo = Split-Path -Parent $PSScriptRoot
$tauri = Join-Path $repo 'client\src-tauri'
$toolPaths = @("$env:ProgramFiles\dotnet", "$env:USERPROFILE\.cargo\bin")
$env:Path = (@($toolPaths | Where-Object { Test-Path $_ }) + $env:Path) -join ';'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
# The host runs powershell.exe 5.1 for PowerShell Direct; a PowerShell 7 module path breaks it (docs/development.md).
$env:PSModulePath = @(
    (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'WindowsPowerShell\Modules'),
    "$env:ProgramFiles\WindowsPowerShell\Modules",
    "$env:SystemRoot\system32\WindowsPowerShell\v1.0\Modules"
) -join ';'

# The folder carries launch requests from the test to the agent. Passwords in them are protected with DPAPI for this
# account (never plain text); each request is deleted as soon as it is read, and the folder goes at the end.
$folder = Join-Path $env:TEMP ("hyperharbor-session-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
$env:HH_SESSION_LIVE = '1'
$env:HH_SESSION_DIR = $folder
if ($VmId) { $env:HH_LIVE_GUEST_VM = $VmId } else { Remove-Item Env:HH_LIVE_GUEST_VM -ErrorAction SilentlyContinue }
if ($Iso) { $env:HH_SPIKE_ISO = $Iso }
if ($Keep) { $env:HH_MACHINE_KEEP = '1' }
if ($IsoFolder) { $env:HH_HARNESS_ISO_FOLDER = $IsoFolder }
if ($VmFolder) { $env:HH_HARNESS_VM_FOLDER = $VmFolder }

$agentLog = Join-Path $env:TEMP 'hyperharbor-session-agent.log'
$agent = $null
try {
    Write-Host 'Building the client agent...'
    Push-Location $tauri
    try {
        cargo test --lib --no-run --quiet
        if ($LASTEXITCODE -ne 0) { throw 'The client agent did not build.' }
    }
    finally {
        Pop-Location
    }

    $agent = Start-Process -FilePath 'cargo' -WorkingDirectory $tauri -NoNewWindow -PassThru `
        -RedirectStandardOutput $agentLog -RedirectStandardError "$agentLog.err" `
        -ArgumentList 'test', '--lib', '--quiet', 'session_live::serve_session_launches', '--', '--ignored', '--nocapture', '--exact'

    Write-Host 'Running SessionLiveTests...'
    dotnet test (Join-Path $repo 'host\tests\Host.Tests') --filter 'FullyQualifiedName~SessionLiveTests' --logger 'console;verbosity=detailed'
    $result = $LASTEXITCODE
}
finally {
    New-Item -ItemType File -Path (Join-Path $folder 'done') -Force | Out-Null
    if ($agent -and -not $agent.WaitForExit(30000)) {
        # cargo runs the test binary as a child, so end the tree.
        taskkill.exe /PID $agent.Id /T /F | Out-Null
    }
    Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
}

exit $result
