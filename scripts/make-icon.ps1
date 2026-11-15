param([string] $OutDir)

Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int] $size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Rounded dark tile
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 52 * $s; $d = 2 * $r; $m = 8 * $s; $w = $size - 2 * $m
    $tile.AddArc($m, $m, $d, $d, 180, 90)
    $tile.AddArc($m + $w - $d, $m, $d, $d, 270, 90)
    $tile.AddArc($m + $w - $d, $m + $w - $d, $d, $d, 0, 90)
    $tile.AddArc($m, $m + $w - $d, $d, $d, 90, 90)
    $tile.CloseFigure()
    $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 22, 27, 34))
    $g.FillPath($bg, $tile)

    $blue = [System.Drawing.Color]::FromArgb(255, 41, 151, 255)
    $green = [System.Drawing.Color]::FromArgb(255, 0, 224, 130)

    # Signal arcs on both sides of the mast tip
    $cx = 128 * $s; $cy = 92 * $s
    foreach ($ring in 1..3) {
        $rad = (30 + 26 * ($ring - 1)) * $s
        $pen = New-Object System.Drawing.Pen($green, [Math]::Max(1.6, 12 * $s))
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawArc($pen, $cx - $rad, $cy - $rad, 2 * $rad, 2 * $rad, -40, 80)
        $g.DrawArc($pen, $cx - $rad, $cy - $rad, 2 * $rad, 2 * $rad, 140, 80)
        $pen.Dispose()
    }

    # Mast: a tapered triangle with a dot on top
    $mast = New-Object System.Drawing.Drawing2D.GraphicsPath
    $mast.AddPolygon(@(
        (New-Object System.Drawing.PointF(($cx - 7 * $s), ($cy + 6 * $s))),
        (New-Object System.Drawing.PointF(($cx + 7 * $s), ($cy + 6 * $s))),
        (New-Object System.Drawing.PointF(($cx + 34 * $s), (212 * $s))),
        (New-Object System.Drawing.PointF(($cx - 34 * $s), (212 * $s)))))
    $blueBrush = New-Object System.Drawing.SolidBrush($blue)
    $g.FillPath($blueBrush, $mast)

    # Cut-outs give the mast its lattice look
    foreach ($y in 132, 168) {
        $half = (12 + ($y - 98) * 0.24) * $s
        $g.FillRectangle($bg, $cx - $half, $y * $s, 2 * $half, 14 * $s)
    }

    $dot = 15 * $s
    $g.FillEllipse((New-Object System.Drawing.SolidBrush($green)), $cx - $dot, $cy - $dot, 2 * $dot, 2 * $dot)

    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

# Classic icon entry: BITMAPINFOHEADER + 32-bit pixels (bottom-up) + empty AND mask.
function Get-DibBytes($bmp) {
    $n = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$n); $bw.Write([int]($n * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]($n * $n * 4)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $n - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $n; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $maskRow = [int]([Math]::Ceiling($n / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $n)))
    $bw.Flush()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = @()
foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    if ($size -eq 256) {
        $bmp.Save((Join-Path $OutDir 'radio.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        $images += ,(Get-PngBytes $bmp)
    } else {
        $images += ,(Get-DibBytes $bmp)
    }
    $bmp.Dispose()
}

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $w.Write($image) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'radio.ico'), $ico.ToArray())
"icon written: $($sizes -join ', ')"
