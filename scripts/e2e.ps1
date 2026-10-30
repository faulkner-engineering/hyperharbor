<#
.SYNOPSIS
    End-to-end tests that need no Hyper-V: the ones CI runs, and that must pass locally before a pull request.

.DESCRIPTION
    1. Host end-to-end tests ([E2EFact], Category=E2E in Host.Tests): the real kopia.exe, the real host service on
       loopback, and rclone's S3 server standing in for a cloud target. The script fetches the pinned tools
       (scripts\fetch-tools.ps1) and sets HH_E2E=1 and the tool paths; with HH_E2E=1 a missing tool fails a test
       instead of skipping it.
    2. The Rust client against the real host service (the ignored e2e_loopback test): it starts the service on
       127.0.0.1 with a temporary data directory and a private tray pipe, acts as the tray to read the PIN, and checks
       pairing (wrong PIN first), mTLS calls, certificate pinning, and unpairing. Without Hyper-V the VM list returns
       503, which the test accepts.
    3. Compatibility with the last stable release (previousRelease in tools\test-tools.json): the same Rust test of
       this client against that release's published host, and of that release's client (built from its tag in a
       temporary worktree) against this host.

    Nothing listens beyond loopback and no real data directory is touched. Tests that need Hyper-V or a VM stay opt-in
    live tests (docs/development.md, Live testing).

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
# @() keeps one match an array; a lone string would be joined onto PATH without a separator.
$env:Path = (@($toolPaths | Where-Object { Test-Path $_ }) + $env:Path) -join ';'
$env:DOTNET_NOLOGO = '1'

# The last line fetch-tools prints is the path of kopia.exe.
$env:HH_KOPIA_EXE = (& (Join-Path $PSScriptRoot 'fetch-tools.ps1') | Select-Object -Last 1)
if (-not (Test-Path $env:HH_KOPIA_EXE)) { throw "fetch-tools did not produce kopia.exe: $env:HH_KOPIA_EXE" }
# rclone serve s3 stands in for an S3 backup target (tools\test-tools.json; never shipped).
$env:HH_RCLONE_EXE = (& (Join-Path $PSScriptRoot 'fetch-tools.ps1') -Tool rclone | Select-Object -Last 1)
if (-not (Test-Path $env:HH_RCLONE_EXE)) { throw "fetch-tools did not produce rclone.exe: $env:HH_RCLONE_EXE" }
$env:HH_E2E = '1'

$solution = Join-Path $repo 'HyperHarbor.sln'
dotnet build $solution -c Debug
if ($LASTEXITCODE -ne 0) { throw "Host build failed with exit code $LASTEXITCODE." }

dotnet test $solution --no-build -c Debug --filter 'Category=E2E'
if ($LASTEXITCODE -ne 0) { throw "Host end-to-end tests failed with exit code $LASTEXITCODE." }

$env:HH_E2E_SERVICE_EXE = Join-Path $repo 'host\src\Host.Service\bin\Debug\net8.0-windows\HyperHarbor.Host.exe'
if (-not (Test-Path $env:HH_E2E_SERVICE_EXE)) { throw "Not found: $env:HH_E2E_SERVICE_EXE" }

$tauri = Join-Path $repo 'client\src-tauri'
Push-Location $tauri
try {
    cargo test e2e_loopback -- --ignored --nocapture
    if ($LASTEXITCODE -ne 0) { throw "End-to-end test failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

# 3. Compatibility with the last stable release (tools\test-tools.json, previousRelease), both ways: this client against
#    its host, and its client against this host. Clients and hosts update separately, so either pair occurs.
$previous = (Get-Content (Join-Path $repo 'tools\test-tools.json') -Raw | ConvertFrom-Json).previousRelease
$currentHost = $env:HH_E2E_SERVICE_EXE
$env:HH_E2E_SERVICE_EXE = (& (Join-Path $PSScriptRoot 'fetch-tools.ps1') -Tool previousRelease | Select-Object -Last 1)
Write-Host "==> This client against the $($previous.version) host" -ForegroundColor Cyan
Push-Location $tauri
try {
    cargo test e2e_loopback -- --ignored --nocapture
    if ($LASTEXITCODE -ne 0) { throw "This client failed against the $($previous.version) host (exit code $LASTEXITCODE)." }
}
finally {
    Pop-Location
}

Write-Host "==> The $($previous.version) client against this host" -ForegroundColor Cyan
$env:HH_E2E_SERVICE_EXE = $currentHost
# CI checks out one commit without tags, so the tag is fetched when it is missing.
git -C $repo rev-parse --verify --quiet "refs/tags/$($previous.tag)" | Out-Null
if ($LASTEXITCODE -ne 0) {
    git -C $repo fetch --no-tags --depth 1 origin "refs/tags/$($previous.tag):refs/tags/$($previous.tag)"
    if ($LASTEXITCODE -ne 0) { throw "Could not fetch $($previous.tag)." }
}
$worktree = Join-Path ([IO.Path]::GetTempPath()) "hyperharbor-e2e-$($previous.tag)-$PID"
git -C $repo worktree add --detach $worktree $previous.tag
if ($LASTEXITCODE -ne 0) { throw "Could not check out $($previous.tag)." }
# Sharing the build folder lets the old client reuse the dependencies already built.
$env:CARGO_TARGET_DIR = Join-Path $tauri 'target'
Push-Location (Join-Path $worktree 'client\src-tauri')
try {
    cargo test e2e_loopback -- --ignored --nocapture
    if ($LASTEXITCODE -ne 0) { throw "The $($previous.version) client failed against this host (exit code $LASTEXITCODE)." }
}
finally {
    Pop-Location
    Remove-Item Env:\CARGO_TARGET_DIR
    git -C $repo worktree remove --force $worktree
}
