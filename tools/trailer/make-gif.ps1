<#
    Turns a recording into a GIF, or an animated WebP when -Out ends in .webp. drive.mjs runs it with its own cuts; run it by hand to re-cut out\raw.mp4.
    -Cuts lists the segments to keep, each "start-end[@speed]" in seconds, in order; @speed fast-forwards that
    segment (for example "40-95@8" plays the VM creation wait 8 times faster). Omit -Cuts to keep everything.
    Example:
        .\make-gif.ps1 -Cuts '2-38','38-95@8','95-104' -Width 960 -Fps 15
#>
param(
    # Defaults: out\raw.mp4 and out\hyperharbor-trailer.gif, set below (see make-trailer.ps1).
    [string]$In = '',
    [string]$Out = '',
    [string[]]$Cuts = @(),
    [int]$Width = 960,
    [int]$Fps = 15,
    [double]$HoldEnd = 2.5,
    # Plays the whole GIF this many times faster, on top of each cut's own @speed.
    [double]$Speed = 1,
    # sierra2_4a looks smoothest; bayer or none make much smaller files on the flat UI colors.
    [ValidateSet('sierra2_4a', 'bayer', 'none')]
    [string]$Dither = 'sierra2_4a',
    # WebP only (an -Out ending in .webp): 0 to 100.
    [int]$Quality = 75
)
$ErrorActionPreference = 'Stop'
if (-not $In) { $In = Join-Path $PSScriptRoot 'out\raw.mp4' }
if (-not $Out) { $Out = Join-Path $PSScriptRoot 'out\hyperharbor-trailer.gif' }
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
    # PowerShell names are case-insensitive: $segmentSpeed, not $speed, or the loop overwrites -Speed.
    $segmentSpeed = $Speed * $(if ($Matches[3]) { [double]$Matches[3] } else { 1 })
    $filters += "[0:v]trim=start=$($Matches[1]):end=$($Matches[2]),setpts=(PTS-STARTPTS)/$segmentSpeed[c$i]"
    $labels += "[c$i]"
}
$join = if ($labels.Count -gt 1) { "$($labels -join '')concat=n=$($labels.Count):v=1:a=0[joined]" } else { "$($labels[0])null[joined]" }
# Hold the last frame so the finished result is readable before the GIF loops.
$finish = "[joined]tpad=stop_mode=clone:stop_duration=$HoldEnd,fps=$Fps,scale=${Width}:-1:flags=lanczos"
$ditherOption = if ($Dither -eq 'bayer') { 'bayer:bayer_scale=4' } else { $Dither }
$palette = "[a]palettegen=max_colors=256:stats_mode=diff[p];[b][p]paletteuse=dither=${ditherOption}:diff_mode=rectangle"
if ($Out -like '*.webp') {
    # Animated WebP: full color and a fraction of the GIF size; GitHub shows it like any image.
    $graph = (($filters + $join + $finish) -join ';')
    & $ffmpeg -hide_banner -loglevel warning -y -i $In -filter_complex $graph -c:v libwebp_anim -quality $Quality -compression_level 6 -loop 0 $Out
}
else {
    $graph = (($filters + $join + "$finish,split[a][b]") -join ';') + ";$palette"
    & $ffmpeg -hide_banner -loglevel warning -y -i $In -filter_complex $graph -loop 0 $Out
}
if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed ($LASTEXITCODE)." }
$mb = [math]::Round((Get-Item $Out).Length / 1MB, 1)
Write-Host "Saved $Out ($mb MB)"
