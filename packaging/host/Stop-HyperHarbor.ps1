<#
.SYNOPSIS
    Stops the portable HyperHarbor host (service and tray).
#>
foreach ($name in 'HyperHarbor.Host.Tray', 'HyperHarbor.Host.Service') {
    $processes = Get-Process -Name $name -ErrorAction SilentlyContinue
    if ($processes) {
        $processes | Stop-Process -Confirm:$false
        Write-Host "Stopped $name."
    }
}
