param([int]$WaitSeconds = 7)

$ErrorActionPreference = "Continue"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WS3 {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, int e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, int e);
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
[void][WS3]::ShowWindow($h, 3)
[void][WS3]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 1600

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$w = $screen.Width; $ht = $screen.Height
Write-Output "screen=${w}x${ht}"

function Capture([string]$name) {
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $bmp.Save("$env:TEMP\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    Write-Host "saved $name"
    return $bmp
}

function Key([byte]$vk) {
    [WS3]::keybd_event($vk, 0, 0, 0); Start-Sleep -Milliseconds 60; [WS3]::keybd_event($vk, 0, 2, 0)
}

function DragRect([int]$x0, [int]$y0, [int]$x1, [int]$y1) {
    [void][WS3]::SetCursorPos($x0, $y0)
    Start-Sleep -Milliseconds 160
    [WS3]::mouse_event(2, 0, 0, 0, 0)
    for ($i = 1; $i -le 10; $i++) {
        [void][WS3]::SetCursorPos([int]($x0 + ($x1 - $x0) * $i / 10), [int]($y0 + ($y1 - $y0) * $i / 10))
        Start-Sleep -Milliseconds 25
    }
    [WS3]::mouse_event(4, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 260
}

# sketch fill RGB(76,148,235)
function BlueBox($bmp) {
    $minX = 99999; $maxX = -1; $minY = 99999; $maxY = -1; $n = 0
    for ($y = 80; $y -lt ($bmp.Height - 40); $y += 3) {
        for ($x = 4; $x -lt ($bmp.Width - 330); $x += 3) {
            $p = $bmp.GetPixel($x, $y)
            if ([math]::Abs($p.R - 76) -lt 24 -and [math]::Abs($p.G - 148) -lt 24 -and [math]::Abs($p.B - 235) -lt 24) {
                $n++
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    return @{ N = $n; MinX = $minX; MaxX = $maxX; MinY = $minY; MaxY = $maxY }
}

$cx = [int](($w - 320) / 2); $cy = [int](($ht + 90) / 2)
Write-Output "assumed viewport center=$cx,$cy"

# ---------- 3D baseline ----------
$bmpA = Capture "sk3d_smooth"
$a = BlueBox $bmpA
Write-Output "3D smooth: blue=$($a.N)"

# ---------- 2D ----------
Key 0x54
Start-Sleep -Milliseconds 1200
Write-Output "T pressed"

Key 0x31
Start-Sleep -Milliseconds 350
DragRect ($cx - 430) ($cy - 300) ($cx - 250) ($cy - 150)
Write-Output "rect dragged"

Key 0x32
Start-Sleep -Milliseconds 350
DragRect ($cx - 430) ($cy + 140) ($cx - 300) ($cy + 270)
Write-Output "circle dragged"
Start-Sleep -Milliseconds 600

$bmpB = Capture "sk2d"
$b = BlueBox $bmpB
Write-Output "2D canvas: blue=$($b.N) bbox=($($b.MinX),$($b.MinY))-($($b.MaxX),$($b.MaxY))"

# ---------- 3D ----------
Key 0x54
Start-Sleep -Milliseconds 1500

$bmpC = Capture "sk3d_floor"
$c2 = BlueBox $bmpC
Write-Output "3D after: blue=$($c2.N) bbox=($($c2.MinX),$($c2.MinY))-($($c2.MaxX),$($c2.MaxY))"

$verdict = "PASS"
if ($b.N -lt 20) { $verdict = "CHECK: nothing drawn in 2D" }
elseif ($c2.N -lt 5) { $verdict = "CHECK: sketch not on 3D floor" }
elseif ($a.N -gt 5) { $verdict = "CHECK: baseline blue present" }
Write-Output "VERDICT: $verdict"
Write-Output "SUMMARY 2d=$($b.N) 3d_before=$($a.N) 3d_after=$($c2.N)"

$bmpA.Dispose(); $bmpB.Dispose(); $bmpC.Dispose()
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
