<#
.SYNOPSIS
    Removes the installed HyperHarbor host.

.DESCRIPTION
    Asks for elevation once, then stops and removes the HyperHarborHost service, removes the console
    account (hhc-<user>) and the firewall rule, and deletes <InstallRoot> (default
    %ProgramFiles%\HyperHarbor). It also stops the tray and removes it from your sign-in.

    Data in %ProgramData%\HyperHarbor (pairings, settings, audit trail) is kept unless -RemoveData is set,
    so installing again, or running the portable host, picks up where this left off.
#>
[CmdletBinding()]
param(
    [switch]$RemoveData,

    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'HyperHarbor'),

    # Internal: performs the elevated steps.
    [switch]$ElevatedUninstall,
    [string]$ResultFile
)

$ErrorActionPreference = 'Stop'
$serviceName = 'HyperHarborHost'
$ruleName = 'HyperHarbor Host API'
$dataDirectory = Join-Path $env:ProgramData 'HyperHarbor'

if ($ElevatedUninstall) {
    try {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($service) {
            if ($service.Status -ne 'Stopped') {
                Stop-Service -Name $serviceName -Force
                $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
            }
            sc.exe delete $serviceName | Out-Null
            Write-Host "Removed the $serviceName service."
        }
        Get-Process -Name 'HyperHarbor.Host.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -Confirm:$false

        $serviceExe = Join-Path $InstallRoot 'current\HyperHarbor.Host.Service.exe'
        if (Test-Path $serviceExe) {
            & $serviceExe --remove-console
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "The console account was not removed (exit code $LASTEXITCODE)."
            }
        }

        Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule

        if (Test-Path $InstallRoot) {
            # Remove the junctions first: Remove-Item -Recurse on a junction would follow it.
            Get-ChildItem -Path $InstallRoot -Force |
                Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
                ForEach-Object { [IO.Directory]::Delete($_.FullName) }
            Remove-Item -Path $InstallRoot -Recurse -Force
            Write-Host "Deleted $InstallRoot."
        }

        if ($RemoveData -and (Test-Path $dataDirectory)) {
            Remove-Item -Path $dataDirectory -Recurse -Force
            Write-Host "Deleted $dataDirectory."
        }
        Set-Content -Path $ResultFile -Value 'ok'
    }
    catch {
        Set-Content -Path $ResultFile -Value $_.Exception.Message
        Write-Warning $_.Exception.Message
        Start-Sleep -Seconds 5
    }
    return
}

$resultFile = Join-Path ([IO.Path]::GetTempPath()) "hyperharbor-uninstall-$([Guid]::NewGuid().ToString('N')).txt"
$arguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$($MyInvocation.MyCommand.Path)`"",
    '-ElevatedUninstall', '-InstallRoot', "`"$InstallRoot`"", '-ResultFile', "`"$resultFile`""
)
if ($RemoveData) {
    $arguments += '-RemoveData'
}
try {
    Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait
}
catch {
    Write-Warning 'Removal was cancelled.'
    return
}

$result = if (Test-Path $resultFile) { (Get-Content $resultFile -Raw).Trim() } else { 'The elevated step did not report a result.' }
Remove-Item $resultFile -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'HyperHarbor Tray' -ErrorAction SilentlyContinue
if ($result -ne 'ok') {
    Write-Warning "Removal did not finish: $result"
    return
}
Write-Host 'HyperHarbor is removed.' -ForegroundColor Green
