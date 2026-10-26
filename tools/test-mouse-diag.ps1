param(
    [string]$LogPath = "$env:TEMP\avalonia3d_debug.log",
    [int]$WaitSeconds = 18
)

$ErrorActionPreference = "Continue"
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W3 {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, int e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte b, byte scan, uint flags, int extra);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int nCmdShow);
  [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
  [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
  [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }
}
"@

$exe = "D:\Avalonia3D\src\Avalonia3DViewer.Desktop\bin\Debug\net10.0\Avalonia3DViewer.exe"
$log = "$env:TEMP\mouse_diag.log"
if (Test-Path $log) { Remove-Item $log -Force }
function L($m) { Add-Content $log "[$(Get-Date -Format HH:mm:ss.fff)] $m" }

$proc = Start-Process $exe -PassThru
Start-Sleep -Seconds $WaitSeconds
try { $main = Get-Process -Id $proc.Id -ErrorAction Stop } catch { L "APP EXITED"; Get-Content $log; exit 1 }
$h = $main.MainWindowHandle
L "app hwnd=$h"

$consoleHwnd = [W3]::GetConsoleWindow()
if ($consoleHwnd -ne [IntPtr]::Zero) { [void][W3]::ShowWindow($consoleHwnd, 6) }
Start-Sleep -Milliseconds 400
[void][W3]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 600
L "foreground match: $([W3]::GetForegroundWindow() -eq $h)"

$r = New-Object W3+RECT
[void][W3]::GetWindowRect($h, [ref]$r)
$cx = [int](($r.Left + $r.Right) / 2)
$cy = [int](($r.Top + $r.Bottom) / 2)
L "rect=$($r.Left),$($r.Top)-$($r.Right),$($r.Bottom) center=($cx,$cy)"

# ---- WindowFromPoint：点击位置实际属于哪个窗口 ----
$pt = New-Object W3+POINT
$pt.X = $cx; $pt.Y = $cy
$atPoint = [W3]::WindowFromPoint($cx, $cy)
$sb = New-Object System.Text.StringBuilder 256
[void][W3]::GetClassName($atPoint, $sb, 256)
$pid2 = 0
[void][W3]::GetWindowThreadProcessId($atPoint, [ref]$pid2)
$procName = (Get-Process -Id $pid2 -ErrorAction SilentlyContinue).ProcessName
L "WindowFromPoint($cx,$cy) -> hwnd=$atPoint class=$($sb.ToString()) pid=$pid2 ($procName)"

# ---- 安装低级鼠标钩子 ----
$script:events = New-Object System.Collections.ArrayList
$cb = [W3+LowLevelProc]{
    param($nCode, $wParam, $lParam)
    if ($nCode -ge 0) {
        $data = [System.Runtime.InteropServices.Marshal]::PtrToStructure($lParam, [W3+MSLLHOOKSTRUCT])
        $msg = "MOUSE msg=$wParam at ($($data.pt.X),$($data.pt.Y))"
        if ($wParam -eq 0x0201 -or $wParam -eq 0x0202) {  # LBUTTONDOWN/UP
            $hw = [W3]::WindowFromPoint($data.pt.X, $data.pt.Y)
            $msg += " windowAtPoint=$hw"
            $script:events.Add($msg) | Out-Null
        }
    }
    return [W3]::CallNextHookEx([IntPtr]::Zero, $nCode, $wParam, $lParam)
}
$hookId = [W3]::SetWindowsHookEx(14, $cb, [IntPtr]::Zero, 0)
L "hook installed: $($hookId -ne [IntPtr]::Zero)"

# ---- 执行点击与拖拽（同时泵消息） ----
$deadline = (Get-Date).AddSeconds(10)
$step = 0
while ((Get-Date) -lt $deadline -and $step -lt 200) {
    [System.Windows.Forms.Application]::DoEvents()
    if ($step -eq 5) {
        [W3]::SetCursorPos($cx, $cy)
        L "cursor moved to ($cx,$cy)"
    }
    if ($step -eq 8) {
        [W3]::mouse_event(2, 0, 0, 0, 0); L "LEFT DOWN sent"
    }
    if ($step -eq 12) {
        [W3]::mouse_event(4, 0, 0, 0, 0); L "LEFT UP sent (click complete)"
    }
    if ($step -eq 20) {
        [W3]::SetCursorPos($cx, $cy); [W3]::mouse_event(2, 0, 0, 0, 0); L "drag start"
    }
    if ($step -gt 20 -and $step -lt 60 -and ($step % 3 -eq 0)) {
        [W3]::SetCursorPos($cx + ($step-20) * 8, $cy + ($step-20) * 5)
    }
    if ($step -eq 60) {
        [W3]::mouse_event(4, 0, 0, 0, 0); L "drag end"
    }
    if ($step -eq 70) {
        # 键盘测试：按 F（适应视图）
        [W3]::keybd_event(0x46, 0, 0, 0); Start-Sleep -Milliseconds 50
        [W3]::keybd_event(0x46, 0, 2, 0); L "KEY F sent"
    }
    $step++
    Start-Sleep -Milliseconds 50
}
[void][W3]::UnhookWindowsHookEx($hookId)
L "hook events captured:"
foreach ($e in $script:events) { L "  $e" }

Start-Sleep -Seconds 2
L "===== APP DEBUG LOG ====="
if (Test-Path $LogPath) { Get-Content $LogPath | ForEach-Object { L "  $_" } } else { L "  (app log missing - no pointer/key events reached Avalonia)" }

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
Get-Content $log
