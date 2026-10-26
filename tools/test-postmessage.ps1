param([int]$WaitSeconds = 18)

$ErrorActionPreference = "Continue"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W7 {
  public const uint WM_MOUSEMOVE = 0x0200;
  public const uint WM_LBUTTONDOWN = 0x0201;
  public const uint WM_LBUTTONUP = 0x0202;
  public const uint WM_RBUTTONDOWN = 0x0204;
  public const uint WM_RBUTTONUP = 0x0205;
  public const int MK_LBUTTON = 0x0001;
  public const int MK_RBUTTON = 0x0002;
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, int w, int l);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$exe = "D:\Avalonia3D\src\Avalonia3DViewer.Desktop\bin\Debug\net10.0\Avalonia3DViewer.exe"
$logPath = "$env:TEMP\avalonia3d_debug.log"
if (Test-Path $logPath) { Remove-Item $logPath -Force }
function L($m) { Write-Output "[$(Get-Date -Format HH:mm:ss.fff)] $m" }

$proc = Start-Process $exe -PassThru
Start-Sleep -Seconds $WaitSeconds
try { $main = Get-Process -Id $proc.Id -ErrorAction Stop } catch { L "APP EXITED"; exit 1 }
$h = $main.MainWindowHandle
L "app hwnd=$h"
[void][W7]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 500

# target: client-space center
$cr = New-Object W7+RECT
[void][W7]::GetClientRect($h, [ref]$cr)
$cw = $cr.Right - $cr.Left
$ch = $cr.Bottom - $cr.Top
L "client size: ${cw}x${ch}"
$tx = [int]($cw / 2)
$ty = [int]($ch / 2)
L "target client point: ($tx,$ty)"

# sync physical cursor to that screen point
$wr = New-Object W7+RECT
[void][W7]::GetWindowRect($h, [ref]$wr)
$screenX = $wr.Left + $tx
$screenY = $wr.Top + $ty
[void][W7]::SetCursorPos($screenX, $screenY)

function MakeL($x, $y) { return ($y -shl 16) -bor ($x -band 0xFFFF) }

# 1) plain MOVE first (Avalonia needs mouse-enter tracking)
[void][W7]::PostMessage($h, [W7]::WM_MOUSEMOVE, 0, (MakeL $tx $ty))
Start-Sleep -Milliseconds 100
# 2) press
[void][W7]::PostMessage($h, [W7]::WM_LBUTTONDOWN, [W7]::MK_LBUTTON, (MakeL $tx $ty))
Start-Sleep -Milliseconds 100
# 3) drag moves (step, sync physical cursor)
for ($i = 1; $i -le 20; $i++) {
    $mx = $tx + $i * 10
    $my = $ty + $i * 6
    [void][W7]::PostMessage($h, [W7]::WM_MOUSEMOVE, [W7]::MK_LBUTTON, (MakeL $mx $my))
    [void][W7]::SetCursorPos($wr.Left + $mx, $wr.Top + $my)
    Start-Sleep -Milliseconds 30
}
# 4) release
$ex = $tx + 200; $ey = $ty + 120
[void][W7]::PostMessage($h, [W7]::WM_LBUTTONUP, 0, (MakeL $ex $ey))
[void][W7]::SetCursorPos($wr.Left + $ex, $wr.Top + $ey)
L "postmessage click+drag sent"

Start-Sleep -Seconds 3
L "===== APP DEBUG LOG ====="
if (Test-Path $logPath) {
    Get-Content $logPath | ForEach-Object { L "  $_" }
} else {
    L "  (no log)"
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
L "===== DONE ====="
