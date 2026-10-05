<#
.SYNOPSIS
    End-to-end test: the Rust client pairs with the real host service over loopback.

.DESCRIPTION
    Builds Host.Service, then runs the ignored e2e_loopback Rust test. That test starts the service
    on 127.0.0.1 with a temporary data directory and a private tray pipe, acts as the tray to read
    the PIN, and checks pairing (wrong PIN first), mTLS calls, certificate pinning, and unpairing.
    Nothing listens beyond loopback and no real data directory is touched. Without Hyper-V the
    VM list returns 503, which the test accepts.

    Run from any directory in Windows PowerShell or PowerShell 7:
        powershell -ExecutionPolicy Bypass -File scripts\e2e.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

$toolPaths = @(
    "$env:ProgramFiles\dotnet",
    "$env:USERPROFILE\.cargo\bin"
)
$env:Path = (($toolPaths | Where-Object { Test-Path $_ }) + $env:Path) -join ';'
$env:DOTNET_NOLOGO = '1'

$project = Join-Path $repo 'host\src\Host.Service\Host.Service.csproj'
dotnet build $project -c Debug
if ($LASTEXITCODE -ne 0) { throw "Host build failed with exit code $LASTEXITCODE." }

$env:HH_E2E_SERVICE_EXE = Join-Path $repo 'host\src\Host.Service\bin\Debug\net8.0-windows\HyperHarbor.Host.exe'
if (-not (Test-Path $env:HH_E2E_SERVICE_EXE)) { throw "Not found: $env:HH_E2E_SERVICE_EXE" }

Push-Location (Join-Path $repo 'client\src-tauri')
try {
    cargo test e2e_loopback -- --ignored --nocapture
    if ($LASTEXITCODE -ne 0) { throw "End-to-end test failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
