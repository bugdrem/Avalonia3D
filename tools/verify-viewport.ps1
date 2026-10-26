param([int]$WaitSeconds = 6)

$ErrorActionPreference = "Continue"

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Drawing;
public class W8V {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr hAfter, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

Add-Type -AssemblyName System.Drawing

$exe = "D:\Avalonia3D\src\Avalonia3DViewer.Desktop\bin\Debug\net10.0\Avalonia3DViewer.exe"

# clean leftovers
Get-Process -Name "Avalonia3DViewer" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

$proc = Start-Process $exe -PassThru
Start-Sleep -Seconds $WaitSeconds
try { $main = Get-Process -Id $proc.Id -ErrorAction Stop } catch { Write-Output "APP EXITED"; exit 1 }
$h = $main.MainWindowHandle
Write-Output "hwnd=$h"

# move window to a known position for stable cropping
[void][W8V]::SetWindowPos($h, [IntPtr](-1), 60, 60, 1200, 800, 0x0040)
Start-Sleep -Milliseconds 1200

$r = New-Object W8V+RECT
[void][W8V]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
Write-Output "window rect: $($r.Left),$($r.Top) ${w}x${ht}"

$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$out = "$env:TEMP\viewport_check.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Output "screenshot saved: $out"

# analyze viewport region: below toolbar (~y 60..760), left of right panel (x 20..850)
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
$distinct = $colors.Count
$top = $colors.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1
$topShare = [math]::Round($top.Current.Value / $samples * 100, 1)
Write-Output "viewport samples=$samples distinct_colors=$distinct dominant_share=$topShare%"

if ($distinct -ge 50) {
    Write-Output "VERDICT: RENDERED (viewport has rich content)"
} else {
    Write-Output "VERDICT: BLANK (viewport nearly uniform - rendering NOT working)"
}

$g.Dispose(); $bmp.Dispose()
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
