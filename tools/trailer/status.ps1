# Status bar for drive.mjs: a small always-on-top strip below the client window, outside the recorded area.
# drive.mjs writes "<state>|<text>" to the status file; this shows it until the state is "exit".
# States: ready, rec (red dot and a running timer), work, done, error.
param(
    [Parameter(Mandatory)][string]$File,
    [int]$X,
    [int]$Y,
    [int]$Width = 720
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing @'
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
public class StatusStrip : Form {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    // Never takes focus or clicks, so the scripted mouse and keyboard stay with the client.
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get {
            var p = base.CreateParams;
            p.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020 | 0x00000008; // NOACTIVATE, TOOLWINDOW, TRANSPARENT, TOPMOST
            return p;
        }
    }
}
'@
[StatusStrip]::SetProcessDPIAware() | Out-Null

$form = New-Object StatusStrip
$form.FormBorderStyle = 'None'
$form.ShowInTaskbar = $false
$form.TopMost = $true
$form.StartPosition = 'Manual'
$form.BackColor = [Drawing.Color]::FromArgb(17, 21, 26)
$form.Location = New-Object Drawing.Point($X, $Y)
$form.Size = New-Object Drawing.Size($Width, 52)

$label = New-Object Windows.Forms.Label
$label.Dock = 'Fill'
$label.TextAlign = 'MiddleLeft'
$label.AutoEllipsis = $true
$label.Padding = New-Object Windows.Forms.Padding(14, 0, 0, 0)
$label.Font = New-Object Drawing.Font('Segoe UI Semibold', 11)
$label.ForeColor = [Drawing.Color]::FromArgb(243, 241, 236)
$label.Text = 'Trailer: starting'
$form.Controls.Add($label)

$colors = @{
    ready = [Drawing.Color]::FromArgb(138, 147, 161)
    rec   = [Drawing.Color]::FromArgb(255, 92, 92)
    work  = [Drawing.Color]::FromArgb(227, 155, 46)
    done  = [Drawing.Color]::FromArgb(86, 211, 100)
    error = [Drawing.Color]::FromArgb(255, 123, 114)
}
$script:recSince = $null
$timer = New-Object Windows.Forms.Timer
$timer.Interval = 200
$timer.Add_Tick({
    # A status file untouched for 15 minutes means drive.mjs is gone.
    $written = try { [IO.File]::GetLastWriteTime($File) } catch { [DateTime]::MinValue }
    if (((Get-Date) - $written).TotalMinutes -gt 15) { $form.Close(); return }
    $line = try { [IO.File]::ReadAllText($File).Trim() } catch { $null }
    if (-not $line) { return }
    $state, $text = $line -split '\|', 2
    if ($state -eq 'exit') { $form.Close(); return }
    if ($state -eq 'rec') {
        if (-not $script:recSince) { $script:recSince = Get-Date }
        $elapsed = (Get-Date) - $script:recSince
        $label.Text = [string]::Format([char]0x25CF + ' REC {0}:{1:00}   {2}', [int][Math]::Floor($elapsed.TotalMinutes), $elapsed.Seconds, $text)
    }
    else {
        $label.Text = $text
    }
    $label.ForeColor = if ($colors.ContainsKey($state)) { $colors[$state] } else { $colors.ready }
})
$timer.Start()
[Windows.Forms.Application]::Run($form)
