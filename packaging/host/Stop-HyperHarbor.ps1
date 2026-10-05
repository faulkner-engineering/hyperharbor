<#
.SYNOPSIS
    Stops the portable HyperHarbor host (service and tray) started from this folder.
    An installed host is a Windows service; stop it from Services (HyperHarborHost) instead.
#>
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
foreach ($name in 'HyperHarbor.Host.Tray', 'HyperHarbor.Host.Service') {
    $processes = Get-Process -Name $name -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($here, [StringComparison]::OrdinalIgnoreCase) }
    if ($processes) {
        $processes | Stop-Process -Confirm:$false
        Write-Host "Stopped $name."
    }
}
