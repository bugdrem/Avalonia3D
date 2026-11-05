Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W2T {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, int e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
Add-Type -AssemblyName System.Drawing

$exe = "D:\Avalonia3D\src\Avalonia3DViewer.Desktop\bin\Debug\net10.0\Avalonia3DViewer.exe"
Get-Process -Name "Avalonia3DViewer" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
$proc = Start-Process $exe -PassThru
Start-Sleep -Seconds 7
$h = (Get-Process -Id $proc.Id).MainWindowHandle
[void][W2T]::ShowWindow($h, 3)
Start-Sleep -Milliseconds 1200

$r = New-Object W2T+RECT
[void][W2T]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top

function Snap([string]$n) {
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save("$env:TEMP\$n.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    return $bmp
}

$before = Snap "t0_3d"
[W2T]::keybd_event(0x54, 0, 0, 0); Start-Sleep -Milliseconds 40; [W2T]::keybd_event(0x54, 0, 2, 0)
Start-Sleep -Milliseconds 110
$mid = Snap "t1_mid"
Start-Sleep -Milliseconds 900
$after = Snap "t2_2d"

function DiffPct($a, $b) {
    $d = 0; $t = 0
    for ($y = 80; $y -lt ($a.Height - 60); $y += 8) {
        for ($x = 20; $x -lt ($a.Width - 340); $x += 8) {
            $p = $a.GetPixel($x, $y); $q = $b.GetPixel($x, $y)
            if ([math]::Abs($p.R-$q.R) + [math]::Abs($p.G-$q.G) + [math]::Abs($p.B-$q.B) -gt 16) { $d++ }
            $t++
        }
    }
    return [math]::Round($d / $t * 100, 1)
}

$d1 = DiffPct $before $mid
$d2 = DiffPct $mid $after
$d3 = DiffPct $before $after
Write-Output "3D<->mid=$d1%  mid<->2D=$d2%  3D<->2D=$d3%"
if ($d1 -gt 2 -and $d2 -gt 2 -and $d1 -lt $d3 -and $d2 -lt $d3) {
    Write-Output "VERDICT: ANIMATED (mid frame is between the two endpoints)"
} else {
    Write-Output "VERDICT: CHECK (mid frame may be identical to an endpoint)"
}

$before.Dispose(); $mid.Dispose(); $after.Dispose()
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
