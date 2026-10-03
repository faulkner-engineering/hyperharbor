<#
.SYNOPSIS
    Runs every automated check that CI runs: build, tests, lint, contract drift, and dependency audits.

.DESCRIPTION
    Host:     dotnet build -warnaserror (security analyzers and NuGet audit run here), dotnet test.
    Contract: Redocly lint of docs/api.yaml; regenerated TypeScript types must match the committed file.
    Client:   npm ci, svelte-check, Vitest, npm audit (high and above).
    Rust:     cargo fmt --check, clippy -D warnings, cargo test, cargo audit.

    -Coverage collects host code coverage into TestResults/.
    -SkipAudit skips the network-dependent npm and cargo advisory checks.

    Run from any directory in Windows PowerShell or PowerShell 7:
        powershell -ExecutionPolicy Bypass -File scripts\test-all.ps1 [-HostOnly|-ClientOnly] [-Coverage] [-SkipAudit]
#>
[CmdletBinding()]
param(
    [switch]$HostOnly,
    [switch]$ClientOnly,
    [switch]$Coverage,
    [switch]$SkipAudit
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$client = Join-Path $repo 'client'
$tauri = Join-Path $client 'src-tauri'

# Toolchains are often missing from PATH in non-interactive shells.
$toolPaths = @(
    "$env:ProgramFiles\dotnet",
    "$env:ProgramFiles\nodejs",
    "$env:USERPROFILE\.cargo\bin"
)
$env:Path = (($toolPaths | Where-Object { Test-Path $_ }) + $env:Path) -join ';'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Check([string]$Title, [string]$Directory, [scriptblock]$Command) {
    Write-Host "==> $Title" -ForegroundColor Cyan
    Push-Location $Directory
    try {
        & $Command
        if ($LASTEXITCODE -ne 0) {
            $failures.Add($Title)
            Write-Host "FAILED: $Title (exit code $LASTEXITCODE)" -ForegroundColor Red
        }
    }
    catch {
        $failures.Add($Title)
        Write-Host "FAILED: $Title ($_)" -ForegroundColor Red
    }
    finally {
        Pop-Location
    }
}

if (-not $ClientOnly) {
    $solution = Join-Path $repo 'HyperHarbor.sln'
    Invoke-Check 'Host build' $repo { dotnet build $solution -warnaserror }
    if ($Coverage) {
        Invoke-Check 'Host tests (coverage)' $repo {
            dotnet test $solution --no-build --collect:'XPlat Code Coverage' --results-directory (Join-Path $repo 'TestResults')
        }
    }
    else {
        Invoke-Check 'Host tests' $repo { dotnet test $solution --no-build }
    }
    Invoke-Check 'Contract lint' $repo { npx --yes @redocly/cli lint docs/api.yaml }
}

if (-not $HostOnly) {
    Invoke-Check 'npm ci' $client { npm ci --no-audit --no-fund }
    Invoke-Check 'Generated API types match api.yaml' $client {
        npm run gen:api
        if ($LASTEXITCODE -eq 0) {
            git diff --exit-code -- src/lib/api/types.ts
            if ($LASTEXITCODE -ne 0) {
                Write-Host 'src/lib/api/types.ts is out of date. Run npm run gen:api and commit the result.'
            }
        }
    }
    Invoke-Check 'Frontend type check' $client { npm run check }
    Invoke-Check 'Frontend tests' $client { npm test }
    Invoke-Check 'cargo fmt' $tauri { cargo fmt --check }
    Invoke-Check 'cargo clippy' $tauri { cargo clippy --all-targets -- -D warnings }
    Invoke-Check 'cargo test' $tauri { cargo test }

    if (-not $SkipAudit) {
        Invoke-Check 'npm audit' $client { npm audit --audit-level=high }
        if (Get-Command cargo-audit -ErrorAction SilentlyContinue) {
            Invoke-Check 'cargo audit' $tauri { cargo audit }
        }
        else {
            Write-Host 'cargo-audit is not installed; skipping (cargo install cargo-audit --locked).' -ForegroundColor Yellow
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "Failed checks: $($failures -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host ''
Write-Host 'All checks passed.' -ForegroundColor Green
