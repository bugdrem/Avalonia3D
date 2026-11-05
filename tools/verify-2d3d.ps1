param([int]$WaitSeconds = 7)

$ErrorActionPreference = "Continue"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W2D {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr hAfter, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, int e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
Add-Type -AssemblyName System.Drawing

$exe = "D:\Avalonia3D\src\Avalonia3DViewer.Desktop\bin\Debug\net10.0\Avalonia3DViewer.exe"
Get-Process -Name "Avalonia3DViewer" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

$proc = Start-Process $exe -PassThru
Start-Sleep -Seconds $WaitSeconds
try { $main = Get-Process -Id $proc.Id -ErrorAction Stop } catch { Write-Output "APP EXITED"; exit 1 }
$h = $main.MainWindowHandle

[void][W2D]::SetWindowPos($h, [IntPtr](-1), 60, 60, 1200, 800, 0x0040)
[void][W2D]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 1500

function Capture([string]$name) {
    $r = New-Object W2D+RECT
    [void][W2D]::GetWindowRect($h, [ref]$r)
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $path = "$env:TEMP\$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    # 注意：函数返回值只能有一个对象，路径信息写宿主流，避免污染返回的 Bitmap
    Write-Host "saved $path"
    return $bmp
}

function Stats($bmp) {
    $colors = @{}
    $samples = 0
    for ($y = 80; $y -lt 760; $y += 4) {
        for ($x = 30; $x -lt 840; $x += 4) {
            $px = $bmp.GetPixel($x, $y)
            $key = "$($px.R),$($px.G),$($px.B)"
            if (-not $colors.ContainsKey($key)) { $colors[$key] = 0 }
            $colors[$key]++
            $samples++
        }
    }
    return @{ Distinct = $colors.Count; Samples = $samples }
}

# ---- 3D ----
$bmpA = Capture "view_3d"
$sA = Stats $bmpA
Write-Output "3D: distinct_colors=$($sA.Distinct) samples=$($sA.Samples)"

# ---- press T (2D) ----
[W2D]::keybd_event(0x54, 0, 0, 0); Start-Sleep -Milliseconds 60; [W2D]::keybd_event(0x54, 0, 2, 0)
Write-Output "T pressed, waiting for transition"
Start-Sleep -Milliseconds 1200

# ---- 2D ----
$bmpB = Capture "view_2d"
$sB = Stats $bmpB
Write-Output "2D: distinct_colors=$($sB.Distinct) samples=$($sB.Samples)"

# ---- pixel diff ratio between 3D and 2D ----
$diff = 0; $total = 0
for ($y = 80; $y -lt 760; $y += 4) {
    for ($x = 30; $x -lt 840; $x += 4) {
        $a = $bmpA.GetPixel($x, $y); $b = $bmpB.GetPixel($x, $y)
        if ([math]::Abs($a.R - $b.R) + [math]::Abs($a.G - $b.G) + [math]::Abs($a.B - $b.B) -gt 24) { $diff++ }
        $total++
    }
}
$ratio = [math]::Round($diff / $total * 100, 1)
Write-Output "changed_pixels=$ratio%"

if ($sA.Distinct -ge 50 -and $sB.Distinct -ge 50 -and $ratio -ge 15) {
    Write-Output "VERDICT: PASS (3D rendered, 2D rendered, views differ)"
} else {
    Write-Output "VERDICT: CHECK (3D=$($sA.Distinct) 2D=$($sB.Distinct) diff=$ratio%)"
}

$bmpA.Dispose(); $bmpB.Dispose()
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
