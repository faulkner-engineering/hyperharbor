<#
.SYNOPSIS
    Stops the portable HyperHarbor host and tray started from this folder.
    An installed host is the Windows service HyperHarborHost; stop it from Services instead.
#>
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$processes = Get-Process -Name 'HyperHarbor.Host' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($here, [StringComparison]::OrdinalIgnoreCase) }
if ($processes) {
    $processes | Stop-Process -Confirm:$false
    Write-Host "Stopped $($processes.Count) HyperHarbor process(es)."
}
