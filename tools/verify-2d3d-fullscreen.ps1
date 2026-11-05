param([int]$WaitSeconds = 7)

$ErrorActionPreference = "Continue"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W2M {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, int e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$exe = "D:\Avalonia3D\src\Avalonia3DViewer.Desktop\bin\Debug\net10.0\Avalonia3DViewer.exe"
Get-Process -Name "Avalonia3DViewer" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

$proc = Start-Process $exe -PassThru
Start-Sleep -Seconds $WaitSeconds
try { $main = Get-Process -Id $proc.Id -ErrorAction Stop } catch { Write-Output "APP EXITED"; exit 1 }
$h = $main.MainWindowHandle

[void][W2M]::ShowWindow($h, 3)   # SW_MAXIMIZE
[void][W2M]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 1500

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Output "screen=$($screen.Width)x$($screen.Height)"
$r = New-Object W2M+RECT
[void][W2M]::GetWindowRect($h, [ref]$r)
Write-Output "window=$($r.Left),$($r.Top) $($r.Right - $r.Left)x$($r.Bottom - $r.Top)"

$w = $screen.Width; $ht = $screen.Height

function Capture([string]$name) {
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $path = "$env:TEMP\$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    Write-Host "saved $path"
    return $bmp
}

# viewport region: left of the 320px side panel, below the toolbar
$rx0 = 20; $rx1 = $w - 360; $ry0 = 140; $ry1 = $ht - 90

function Stats($bmp) {
    $colors = @{}
    $samples = 0
    for ($y = $ry0; $y -lt $ry1; $y += 6) {
        for ($x = $rx0; $x -lt $rx1; $x += 6) {
            $px = $bmp.GetPixel($x, $y)
            $key = "$($px.R),$($px.G),$($px.B)"
            if (-not $colors.ContainsKey($key)) { $colors[$key] = 0 }
            $colors[$key]++
            $samples++
        }
    }
    return @{ Distinct = $colors.Count; Samples = $samples }
}

$bmpA = Capture "view_3d"
$sA = Stats $bmpA
Write-Output "3D: distinct=$($sA.Distinct) samples=$($sA.Samples)"

[W2M]::keybd_event(0x54, 0, 0, 0); Start-Sleep -Milliseconds 60; [W2M]::keybd_event(0x54, 0, 2, 0)
Write-Output "T pressed"
Start-Sleep -Milliseconds 1300

$bmpB = Capture "view_2d"
$sB = Stats $bmpB
Write-Output "2D: distinct=$($sB.Distinct) samples=$($sB.Samples)"

$diff = 0; $total = 0
for ($y = $ry0; $y -lt $ry1; $y += 6) {
    for ($x = $rx0; $x -lt $rx1; $x += 6) {
        $a = $bmpA.GetPixel($x, $y); $b = $bmpB.GetPixel($x, $y)
        if ([math]::Abs($a.R - $b.R) + [math]::Abs($a.G - $b.G) + [math]::Abs($a.B - $b.B) -gt 24) { $diff++ }
        $total++
    }
}
$ratio = [math]::Round($diff / $total * 100, 1)
Write-Output "changed=$ratio%"

if ($sA.Distinct -ge 50 -and $sB.Distinct -ge 50 -and $ratio -ge 8) {
    Write-Output "VERDICT: PASS"
} else {
    Write-Output "VERDICT: CHECK 3D=$($sA.Distinct) 2D=$($sB.Distinct) diff=$ratio%"
}

$bmpA.Dispose(); $bmpB.Dispose()
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
