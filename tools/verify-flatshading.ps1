param([int]$WaitSeconds = 8)

$ErrorActionPreference = "Continue"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WF {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, int e);
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
[void][WF]::ShowWindow($h, 3)
[void][WF]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 1600

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$w = $screen.Width; $ht = $screen.Height

function Capture([string]$name) {
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $bmp.Save("$env:TEMP\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    Write-Host "saved $name"
    return $bmp
}

# model pixels: mid-tone grey/blue (background is near-white, grid lines are thin)
function ModelStats($bmp) {
    $set = @{}
    $same = 0; $total = 0
    for ($y = 100; $y -lt ($bmp.Height - 40); $y += 3) {
        for ($x = 10; $x -lt ($bmp.Width - 330) - 4; $x += 3) {
            $p = $bmp.GetPixel($x, $y)
            $lum = ($p.R + $p.G + $p.B) / 3
            if ($lum -lt 70 -or $lum -gt 205) { continue }
            if ([math]::Abs($p.R - $p.G) -gt 45 -or [math]::Abs($p.G - $p.B) -gt 45) { continue }
            $k = "$($p.R),$($p.G),$($p.B)"
            if (-not $set.ContainsKey($k)) { $set[$k] = 1 }
            $q = $bmp.GetPixel($x + 2, $y)
            if ($p.R -eq $q.R -and $p.G -eq $q.G -and $p.B -eq $q.B) { $same++ }
            $total++
        }
    }
    $ratio = if ($total -gt 0) { [math]::Round($same / $total * 100, 1) } else { 0 }
    return @{ Colors = $set.Count; Same = $ratio; Samples = $total }
}

$bmpA = Capture "shade_smooth"
$sA = ModelStats $bmpA
Write-Output "SMOOTH: colors=$($sA.Colors) sameNeighbour=$($sA.Same)% samples=$($sA.Samples)"

[WF]::keybd_event(0x4B, 0, 0, 0); Start-Sleep -Milliseconds 60; [WF]::keybd_event(0x4B, 0, 2, 0)
Write-Output "K pressed (flat shading)"
Start-Sleep -Milliseconds 1200

$bmpB = Capture "shade_flat"
$sB = ModelStats $bmpB
Write-Output "FLAT:   colors=$($sB.Colors) sameNeighbour=$($sB.Same)% samples=$($sB.Samples)"

$verdict = "PASS"
if ($sB.Colors -ge $sA.Colors) { $verdict = "CHECK: flat did not reduce colour count" }
elseif ($sB.Same -le $sA.Same) { $verdict = "CHECK: flat did not increase per-face uniformity" }
Write-Output "VERDICT: $verdict"
Write-Output "SUMMARY smoothColors=$($sA.Colors) flatColors=$($sB.Colors) smoothSame=$($sA.Same)% flatSame=$($sB.Same)%"

$bmpA.Dispose(); $bmpB.Dispose()
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
