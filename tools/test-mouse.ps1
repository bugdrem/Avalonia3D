param([int]$WaitSeconds = 18)

$ErrorActionPreference = "Continue"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W8 {
  public const int HWND_TOPMOST = -1;
  public const int HWND_NOTOPMOST = -2;
  public const uint SWP_SHOWWINDOW = 0x0040;
  public const uint SWP_NOSIZE = 0x0001;
  public const uint SWP_NOMOVE = 0x0002;
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, int e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, int e);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr hAfter, int x, int y, int cx, int cy, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$exe = "D:\Avalonia3D\src\Avalonia3DViewer.Desktop\bin\Debug\net10.0\Avalonia3DViewer.exe"
$logPath = "$env:TEMP\avalonia3d_verify.log"

# 清理上一次测试残留的应用进程（否则多个实例会写入同一日志，污染验证结果）
Get-Process -Name "Avalonia3DViewer" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
if (Test-Path $logPath) { Remove-Item $logPath -Force }
function L($m) { Write-Output "[$(Get-Date -Format HH:mm:ss.fff)] $m" }

$proc = Start-Process $exe -PassThru
Start-Sleep -Seconds $WaitSeconds
try { $main = Get-Process -Id $proc.Id -ErrorAction Stop } catch { L "APP EXITED"; exit 1 }
$h = $main.MainWindowHandle
L "app hwnd=$h"

$r0 = New-Object W8+RECT
[void][W8]::GetWindowRect($h, [ref]$r0)
$bx = [int](($r0.Left + $r0.Right) / 2); $by = [int](($r0.Top + $r0.Bottom) / 2)
$blocker = [W8]::WindowFromPoint($bx, $by)
L "blocker at app center: $blocker (app=$h)"

[void][W8]::SetWindowPos($h, [W8]::HWND_TOPMOST, 40, 40, 1100, 800, [W8]::SWP_SHOWWINDOW)
Start-Sleep -Milliseconds 600

$blockerTopmost = $false
if ($blocker -ne [IntPtr]::Zero -and $blocker -ne $h) {
    [void][W8]::SetWindowPos($blocker, [W8]::HWND_NOTOPMOST, 0, 0, 0, 0, ([W8]::SWP_NOMOVE -bor [W8]::SWP_NOSIZE))
    $blockerTopmost = $true
    Start-Sleep -Milliseconds 500
    L "blocker topmost removed: $blocker"
}

# 重试直至应用窗口位于点击点最顶层（最多 5 次）
$matchOk = $false
for ($attempt = 1; $attempt -le 5 -and -not $matchOk; $attempt++) {
    [void][W8]::SetWindowPos($h, [W8]::HWND_TOPMOST, 40, 40, 1100, 800, [W8]::SWP_SHOWWINDOW)
    [void][W8]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 500
    $r = New-Object W8+RECT
    [void][W8]::GetWindowRect($h, [ref]$r)
    $cx = [int](($r.Left + $r.Right) / 2)
    $cy = [int](($r.Top + $r.Bottom) / 2)
    $atPoint = [W8]::WindowFromPoint($cx, $cy)
    if ($atPoint -eq $h) { $matchOk = $true }
    L "attempt $attempt : center=($cx,$cy) atPoint=$atPoint match=$($atPoint -eq $h)"
}
L "final match=$matchOk"

[W8]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 250
[W8]::mouse_event(2, 0, 0, 0, 0)
Start-Sleep -Milliseconds 150
[W8]::mouse_event(4, 0, 0, 0, 0)
L "click sent"
Start-Sleep -Milliseconds 800

[W8]::SetCursorPos($cx, $cy)
[W8]::mouse_event(2, 0, 0, 0, 0)
for ($i = 1; $i -le 25; $i++) {
    [W8]::SetCursorPos($cx + $i * 9, $cy + $i * 5)
    Start-Sleep -Milliseconds 35
}
[W8]::mouse_event(4, 0, 0, 0, 0)
L "drag sent"
Start-Sleep -Milliseconds 500

# ---- right-drag (pan) ----
[W8]::SetCursorPos($cx, $cy)
[W8]::mouse_event(8, 0, 0, 0, 0)
for ($i = 1; $i -le 15; $i++) {
    [W8]::SetCursorPos($cx - $i * 6, $cy - $i * 4)
    Start-Sleep -Milliseconds 35
}
[W8]::mouse_event(16, 0, 0, 0, 0)
L "right-drag (pan) sent"
Start-Sleep -Milliseconds 500

# ---- wheel zoom ----
for ($k = 1; $k -le 3; $k++) {
    [W8]::SetCursorPos($cx, $cy)
    [W8]::mouse_event(0x0800, 0, 0, 120, 0)
    Start-Sleep -Milliseconds 200
}
for ($k = 1; $k -le 2; $k++) {
    [W8]::SetCursorPos($cx, $cy)
    [W8]::mouse_event(0x0800, 0, 0, -120, 0)
    Start-Sleep -Milliseconds 200
}
L "wheel sent"
Start-Sleep -Milliseconds 500

# ---- G: toggle move mode, then left-drag moves the MODEL ----
[W8]::keybd_event(0x47, 0, 0, 0); Start-Sleep -Milliseconds 60; [W8]::keybd_event(0x47, 0, 2, 0)
Start-Sleep -Milliseconds 500
L "G pressed (move mode on)"

[W8]::SetCursorPos($cx, $cy)
[W8]::mouse_event(2, 0, 0, 0, 0)
for ($i = 1; $i -le 20; $i++) {
    [W8]::SetCursorPos($cx + $i * 8, $cy + $i * 4)
    Start-Sleep -Milliseconds 35
}
[W8]::mouse_event(4, 0, 0, 0, 0)
L "move-drag sent"
Start-Sleep -Milliseconds 800

# ---- Shift + move-drag: axis lock ----
[W8]::keybd_event(0xA0, 0, 0, 0)
Start-Sleep -Milliseconds 120
[W8]::SetCursorPos($cx, $cy)
[W8]::mouse_event(2, 0, 0, 0, 0)
for ($i = 1; $i -le 15; $i++) {
    [W8]::SetCursorPos($cx + $i * 7, $cy - $i * 5)
    Start-Sleep -Milliseconds 35
}
[W8]::mouse_event(4, 0, 0, 0, 0)
[W8]::keybd_event(0xA0, 0, 2, 0)
L "shift move-drag sent (axis lock)"
Start-Sleep -Milliseconds 800

# ---- Esc: exit move mode, left-drag must ROTATE again ----
[W8]::keybd_event(0x1B, 0, 0, 0); Start-Sleep -Milliseconds 60; [W8]::keybd_event(0x1B, 0, 2, 0)
Start-Sleep -Milliseconds 500
L "Esc pressed (move mode off)"

[W8]::SetCursorPos($cx, $cy)
[W8]::mouse_event(2, 0, 0, 0, 0)
for ($i = 1; $i -le 10; $i++) {
    [W8]::SetCursorPos($cx + $i * 6, $cy)
    Start-Sleep -Milliseconds 35
}
[W8]::mouse_event(4, 0, 0, 0, 0)
L "post-esc drag sent (should rotate)"
Start-Sleep -Seconds 2

L "===== APP DEBUG LOG ====="
if (Test-Path $logPath) {
    Get-Content $logPath | ForEach-Object { L "  $_" }
} else {
    L "  (no log)"
}

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue

if ($blockerTopmost) {
    [void][W8]::SetWindowPos($blocker, [W8]::HWND_TOPMOST, 0, 0, 0, 0, ([W8]::SWP_NOMOVE -bor [W8]::SWP_NOSIZE))
    L "blocker topmost restored"
}
L "===== DONE ====="
