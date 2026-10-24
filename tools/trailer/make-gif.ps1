<#
    Turns a recording into a GIF. drive.mjs runs it with its own cuts; run it by hand to re-cut out\raw.mp4.
    -Cuts lists the segments to keep, each "start-end[@speed]" in seconds, in order; @speed fast-forwards that
    segment (for example "40-95@8" plays the VM creation wait 8 times faster). Omit -Cuts to keep everything.
    Example:
        .\make-gif.ps1 -Cuts '2-38','38-95@8','95-104' -Width 960 -Fps 15
#>
param(
    [string]$In = (Join-Path $PSScriptRoot 'out\raw.mp4'),
    [string]$Out = (Join-Path $PSScriptRoot 'out\hyperharbor-trailer.gif'),
    [string[]]$Cuts = @(),
    [int]$Width = 960,
    [int]$Fps = 15,
    [double]$HoldEnd = 2.5
)
$ErrorActionPreference = 'Stop'
# FFMPEG, then ffmpeg on PATH, then the winget package (winget install Gyan.FFmpeg).
$ffmpeg = if ($env:FFMPEG) { $env:FFMPEG } else { (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source }
if (-not $ffmpeg) {
    $ffmpeg = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Gyan.FFmpeg*\*\bin\ffmpeg.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $ffmpeg) { throw 'ffmpeg not found: install it (winget install Gyan.FFmpeg) or set FFMPEG.' }

$filters = @()
$labels = @()
# powershell -File passes "a,b" as one string; accept both forms.
$Cuts = @($Cuts | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if ($Cuts.Count -eq 0) { $Cuts = @('0-99999') }
for ($i = 0; $i -lt $Cuts.Count; $i++) {
    if ($Cuts[$i] -notmatch '^([\d.]+)-([\d.]+)(?:@([\d.]+))?$') { throw "Bad cut '$($Cuts[$i])'; use start-end or start-end@speed." }
    $speed = if ($Matches[3]) { $Matches[3] } else { '1' }
    $filters += "[0:v]trim=start=$($Matches[1]):end=$($Matches[2]),setpts=(PTS-STARTPTS)/$speed[c$i]"
    $labels += "[c$i]"
}
$join = if ($labels.Count -gt 1) { "$($labels -join '')concat=n=$($labels.Count):v=1:a=0[joined]" } else { "$($labels[0])null[joined]" }
# Hold the last frame so the finished result is readable before the GIF loops.
$finish = "[joined]tpad=stop_mode=clone:stop_duration=$HoldEnd,fps=$Fps,scale=${Width}:-1:flags=lanczos,split[a][b]"
$palette = '[a]palettegen=max_colors=256:stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a:diff_mode=rectangle'
$graph = (($filters + $join + $finish) -join ';') + ";$palette"

& $ffmpeg -hide_banner -loglevel warning -y -i $In -filter_complex $graph -loop 0 $Out
if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed ($LASTEXITCODE)." }
$mb = [math]::Round((Get-Item $Out).Length / 1MB, 1)
Write-Host "Saved $Out ($mb MB)"
