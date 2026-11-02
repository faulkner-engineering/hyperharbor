<#
.SYNOPSIS
    Runs inside Windows Sandbox: installs and uninstalls the HyperHarbor host and client with winget in a clean Windows,
    as the winget-pkgs validation does, and writes what it finds to result.txt beside this script.

.DESCRIPTION
    Started by winget-pkgs Tools\SandboxTest.ps1 with this script's folder mapped. The folder holds this script,
    manifests\Host\<version> and manifests\Client\<version> (one version each), and serve\ with any installer whose
    manifest points at http://127.0.0.1:8765/ (a test build; the sandbox cannot reach the host's loopback, so the
    script serves it inside). The folder must be inside the winget-pkgs clone, or SandboxTest.ps1 asks before mapping it
    and fails without a console. SandboxTest.ps1 needs PowerShell 7. packaging\winget\README.md has the commands.
#>
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$log = Join-Path $root 'result.txt'
# The log is written inside the sandbox and copied to the mapped folder, which the host reads while it grows.
$localLog = 'C:\hh-sandbox-result.txt'
function L([string]$Text) {
    [IO.File]::AppendAllText($localLog, "$(Get-Date -Format HH:mm:ss) $Text
")
    Copy-Item -LiteralPath $localLog -Destination $log -Force
}
function Arp {
    Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object DisplayName -like '*HyperHarbor*' |
        ForEach-Object { "$($_.PSChildName) | $($_.DisplayName) | $($_.DisplayVersion) | $($_.Publisher) | quiet: $($_.QuietUninstallString)" }
}
function Windows { Get-Process | Where-Object { $_.MainWindowTitle } | ForEach-Object { "$($_.ProcessName): $($_.MainWindowTitle)" } }
# Not named Winget: PowerShell names ignore case, so that function would call itself instead of winget.exe.
function Invoke-Winget([string]$Step, [string[]]$Arguments) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $output = & winget.exe @Arguments 2>&1 | Out-String
    $text = ($output -split "`r?`n" | ForEach-Object { ($_ -split "`r")[-1].Trim() } | Where-Object { $_ -match '[A-Za-z]{2}' }) -join ' | '
    L "$Step -> exit $LASTEXITCODE in $([int]$watch.Elapsed.TotalSeconds) s: $text"
}

Remove-Item -LiteralPath $localLog -ErrorAction SilentlyContinue
L "sandbox run, winget $(winget.exe --version), $((Get-CimInstance Win32_OperatingSystem).Caption) $([Environment]::OSVersion.Version)"
$server = Start-Job -ArgumentList (Join-Path $root 'serve') -ScriptBlock {
    param($Folder)
    $listener = [Net.HttpListener]::new()
    $listener.Prefixes.Add('http://127.0.0.1:8765/')
    $listener.Start()
    while ($true) {
        $context = $listener.GetContext()
        $file = Join-Path $Folder ([Uri]::UnescapeDataString($context.Request.Url.AbsolutePath.TrimStart('/')))
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            $context.Response.ContentLength64 = (Get-Item -LiteralPath $file).Length
            $stream = [IO.File]::OpenRead($file)
            $stream.CopyTo($context.Response.OutputStream)
            $stream.Close()
        }
        else { $context.Response.StatusCode = 404 }
        $context.Response.Close()
    }
}
Start-Sleep -Seconds 3

$hostManifest = @(Get-ChildItem (Join-Path $root 'manifests\Host') -Directory)[0].FullName
$clientManifest = @(Get-ChildItem (Join-Path $root 'manifests\Client') -Directory)[0].FullName
L "Host $(Split-Path -Leaf $hostManifest)"
Invoke-Winget 'winget install host' @('install', '--manifest', $hostManifest, '--accept-package-agreements', '--disable-interactivity')
L "service: $((Get-Service HyperHarborHost -ErrorAction SilentlyContinue).Status)"
Arp | ForEach-Object { L "entry: $_" }
L "winget list: $(winget.exe list --name 'HyperHarbor Host' --accept-source-agreements --disable-interactivity | Select-Object -Last 1)"
L "current: $((Get-Item "$env:ProgramFiles\HyperHarbor\current" -ErrorAction SilentlyContinue).Target)"
Windows | ForEach-Object { L "window: $_" }
Invoke-Winget 'winget uninstall host' @('uninstall', '--name', 'HyperHarbor Host', '--exact', '--disable-interactivity')
L "after uninstall: service $([bool](Get-Service HyperHarborHost -ErrorAction SilentlyContinue)), entry $(Test-Path HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\HyperHarborHost), folder $(Test-Path "$env:ProgramFiles\HyperHarbor"), data $(Test-Path "$env:ProgramData\HyperHarbor"), task folder $(try { $s = New-Object -ComObject Schedule.Service; $s.Connect(); $null = $s.GetFolder('\HyperHarbor'); $true } catch { $false })"
Windows | ForEach-Object { L "window: $_" }

L "Client $(Split-Path -Leaf $clientManifest)"
Invoke-Winget 'winget install client' @('install', '--manifest', $clientManifest, '--accept-package-agreements', '--disable-interactivity')
Arp | ForEach-Object { L "entry: $_" }
$client = Start-Process "$env:LOCALAPPDATA\HyperHarbor\hyperharbor-client.exe" -PassThru
Start-Sleep -Seconds 15
$client.Refresh()
L "client running: $(-not $client.HasExited), window: $($client.MainWindowHandle -ne 0)"
Windows | ForEach-Object { L "window: $_" }
if (-not $client.HasExited) { Stop-Process -Id $client.Id -Force }
Invoke-Winget 'winget uninstall client' @('uninstall', '--name', 'HyperHarbor', '--exact', '--disable-interactivity')
Start-Sleep -Seconds 10
L "after uninstall: entry $(Test-Path HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HyperHarbor), folder $(Test-Path "$env:LOCALAPPDATA\HyperHarbor\hyperharbor-client.exe")"
Stop-Job $server
L 'done'
