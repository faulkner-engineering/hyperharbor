<#
.SYNOPSIS
    Records the trailer GIF: a new machine profile, then a VM created from it.

.DESCRIPTION
    Restarts the newest dist\HyperHarbor-Client-*-portable.exe with WebView2's debug port when the port is not
    already open, then runs drive.mjs, which moves the real mouse, records the client, and writes
    out\hyperharbor-trailer.gif. Do not touch the mouse while it runs. See README.md.
        powershell -ExecutionPolicy Bypass -File tools\trailer\make-trailer.ps1 [-Dry] [-Window 40,10,1720,1040]
#>
[CmdletBinding()]
param(
    [switch]$Dry,
    [string]$ProfileName = 'Dev Workstation',
    [string]$Vm = 'dev-01',
    # The client window's outer frame in physical pixels: x, y, width, height.
    [string]$Window = '40,10,1720,1040',
    [int]$Port = 9333,
    [string]$Out = (Join-Path $PSScriptRoot 'out')
)
$ErrorActionPreference = 'Stop'
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) { $node = 'C:\Program Files\nodejs\node.exe' }
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

$debugReady = $false
try { Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$Port/json" -TimeoutSec 3 | Out-Null; $debugReady = $true } catch {}
if (-not $debugReady) {
    $client = Get-ChildItem (Join-Path $repo 'dist\HyperHarbor-Client-*-portable.exe') -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $client) { throw 'No portable client in dist; build one with scripts\package.ps1 -Fast -ClientOnly.' }
    Write-Host "Restarting $($client.Name) with the debug port."
    Get-Process 'HyperHarbor-Client*' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
    # For this launch only: while the port is open, any local process can drive the client.
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port"
    Start-Process $client.FullName
    Remove-Item Env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
    Start-Sleep -Seconds 6
}

$arguments = @((Join-Path $PSScriptRoot 'drive.mjs'), '--profile', $ProfileName, '--vm', $Vm, '--window', $Window,
    '--port', $Port, '--out', $Out)
if ($Dry) { $arguments += '--dry' }
& $node @arguments
if ($LASTEXITCODE -ne 0) { throw "drive.mjs failed ($LASTEXITCODE)." }
