Add-Type -AssemblyName System.Drawing

$path = "$env:TEMP\view_2d.png"
if (-not (Test-Path $path)) { Write-Output "missing $path"; exit 1 }
$bmp = New-Object System.Drawing.Bitmap($path)
Write-Output "image: $($bmp.Width)x$($bmp.Height)"

$redX = @{}; $blueY = @{}
for ($y = 60; $y -lt ($bmp.Height - 40); $y += 2) {
    for ($x = 4; $x -lt ($bmp.Width - 340); $x += 2) {
        $p = $bmp.GetPixel($x, $y)
        if ($p.R -gt 150 -and $p.R -gt ($p.G + 50) -and $p.R -gt ($p.B + 50)) {
            if (-not $redX.ContainsKey($x)) { $redX[$x] = 0 }
            $redX[$x]++
        }
        if ($p.B -gt 130 -and $p.B -gt ($p.R + 45) -and $p.B -gt ($p.G + 15)) {
            if (-not $blueY.ContainsKey($y)) { $blueY[$y] = 0 }
            $blueY[$y]++
        }
    }
}

$bestRed = $redX.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1
$bestBlue = $blueY.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1

if ($bestRed) { Write-Output "red axis (vertical, x) = $($bestRed.Key)  hits=$($bestRed.Value)" } else { Write-Output "red axis not found" }
if ($bestBlue) { Write-Output "blue axis (horizontal, y) = $($bestBlue.Key)  hits=$($bestBlue.Value)" } else { Write-Output "blue axis not found" }

# 模型像素（深灰实心 + 深色轮廓）范围
$minX = 99999; $maxX = -1; $minY = 99999; $maxY = -1; $count = 0
for ($y = 60; $y -lt ($bmp.Height - 40); $y += 2) {
    for ($x = 4; $x -lt ($bmp.Width - 340); $x += 2) {
        $p = $bmp.GetPixel($x, $y)
        if ($p.R -lt 190 -and [math]::Abs($p.R - $p.G) -lt 18 -and [math]::Abs($p.G - $p.B) -lt 22 -and $p.B -ge $p.R) {
            $count++
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
Write-Output "model bbox: x[$minX..$maxX] y[$minY..$maxY] center=($([int](($minX+$maxX)/2)),$([int](($minY+$maxY)/2))) pixels=$count"
Write-Output "viewport center approx: x=$([int](($bmp.Width - 330)/2)) y=$([int]((60 + $bmp.Height - 40)/2))"
$bmp.Dispose()
