# Native helper for drive.mjs. Reads one command per line from stdin and answers each with one line:
#   origin                 -> "x y": screen position (physical pixels) of the client window's content area
#   place x y w h          -> moves and sizes the client window (physical pixels, outer frame)
#   move x y ms            -> glides the cursor to (x, y) over ms milliseconds with ease-in-out
#   click                  -> left click at the current position
#   front                  -> brings the client window to the front
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
'@
[Native]::SetProcessDPIAware() | Out-Null
$proc = Get-Process | Where-Object { $_.MainWindowTitle -eq 'HyperHarbor' -and $_.ProcessName -like 'HyperHarbor-Client*' } | Select-Object -First 1
if (-not $proc) { [Console]::Out.WriteLine('error The HyperHarbor client window is not open.'); exit 1 }
$hwnd = $proc.MainWindowHandle

function Front {
    [Native]::ShowWindow($hwnd, 9) | Out-Null
    # An Alt tap lets a background process take the foreground.
    [Native]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [Native]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [Native]::SetForegroundWindow($hwnd) | Out-Null
}

while ($null -ne ($line = [Console]::In.ReadLine())) {
    $a = $line.Trim() -split '\s+'
    switch ($a[0]) {
        'origin' {
            $p = New-Object Native+POINT
            [Native]::ClientToScreen($hwnd, [ref]$p) | Out-Null
            [Console]::Out.WriteLine("$($p.X) $($p.Y)")
        }
        'place' {
            [Native]::ShowWindow($hwnd, 9) | Out-Null
            [Native]::MoveWindow($hwnd, [int]$a[1], [int]$a[2], [int]$a[3], [int]$a[4], $true) | Out-Null
            [Console]::Out.WriteLine('ok')
        }
        'front' { Front; [Console]::Out.WriteLine('ok') }
        'move' {
            $s = New-Object Native+POINT
            [Native]::GetCursorPos([ref]$s) | Out-Null
            $tx = [int]$a[1]; $ty = [int]$a[2]; $ms = [int]$a[3]
            $steps = [Math]::Max(1, [int]($ms / 10))
            for ($i = 1; $i -le $steps; $i++) {
                $t = $i / $steps
                $e = if ($t -lt 0.5) { 2 * $t * $t } else { 1 - [Math]::Pow(-2 * $t + 2, 2) / 2 }
                [Native]::SetCursorPos([int]($s.X + ($tx - $s.X) * $e), [int]($s.Y + ($ty - $s.Y) * $e)) | Out-Null
                Start-Sleep -Milliseconds 10
            }
            [Console]::Out.WriteLine('ok')
        }
        'click' {
            [Native]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 60
            [Native]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero)
            [Console]::Out.WriteLine('ok')
        }
        default { [Console]::Out.WriteLine("error unknown command $($a[0])") }
    }
}
