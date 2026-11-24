# Generates AppIcon.ico (16-256 px, PNG-compressed entries) and AppIcon.png (256 px preview) next to this script.
# Design: indigo -> violet rounded tile, white calendar card with binding rings, indigo check mark (deadlines you tick off).
# Re-run after tweaking the colours / geometry below:  powershell -File UniDashboard.App\Assets\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$out = $PSScriptRoot

$c1 = [Drawing.Color]::FromArgb(0x4F, 0x46, 0xE5)   # indigo
$c2 = [Drawing.Color]::FromArgb(0x8B, 0x5C, 0xF6)   # violet

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

# Draws the icon on a 1024-unit design grid, scaled to $size pixels.
function Render([int]$size) {
    $ss = 4                                   # supersample, then downscale: crisp edges at 16-32 px
    $px = $size * $ss
    $bmp = New-Object Drawing.Bitmap $px, $px, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([Drawing.Color]::Transparent)
    $g.ScaleTransform($px / 1024.0, $px / 1024.0)

    # tile
    $tile = RoundRect 32 32 960 960 224
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new(120, 60)), ([Drawing.PointF]::new(900, 980)), $c1, $c2
    $g.FillPath($grad, $tile)

    # card (soft shadow first)
    $shadow = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(46, 20, 10, 80))
    $g.FillPath($shadow, (RoundRect 236 300 552 560 84))
    $g.FillPath([Drawing.Brushes]::White, (RoundRect 236 276 552 560 84))

    # binding rings
    foreach ($x in 380, 600) { $g.FillPath([Drawing.Brushes]::White, (RoundRect $x 196 44 150 22)) }

    # header band on the card (slightly tinted) separates "calendar" from "check"
    $band = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0xE4, 0xE1, 0xFC))
    $clip = RoundRect 236 276 552 560 84
    $g.SetClip($clip)
    $g.FillRectangle($band, 236, 276, 552, 120)
    $g.ResetClip()

    # check mark
    $pen = New-Object Drawing.Pen ($c1), 76
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLines($pen, [Drawing.PointF[]]@([Drawing.PointF]::new(375, 624), [Drawing.PointF]::new(470, 719), [Drawing.PointF]::new(655, 506)))
    $g.Dispose()

    $final = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $fg = [Drawing.Graphics]::FromImage($final)
    $fg.InterpolationMode = 'HighQualityBicubic'; $fg.PixelOffsetMode = 'HighQuality'; $fg.CompositingQuality = 'HighQuality'
    $fg.DrawImage($bmp, 0, 0, $size, $size); $fg.Dispose(); $bmp.Dispose()
    return $final
}

# Classic ICO entries: 32-bit BMP (DIB) for the small sizes - every consumer (shell, compiler resources, GDI+) reads those -
# and PNG only for 256 px, the one size that is conventionally PNG-compressed.
function DibBytes([Drawing.Bitmap]$b) {
    $s = $b.Width; $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]($s * $s * 4)); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {                       # bottom-up BGRA rows
        for ($x = 0; $x -lt $s; $x++) { $c = $b.GetPixel($x, $y); $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A) }
    }
    $maskRow = [int]([math]::Ceiling($s / 32.0) * 4)           # AND mask: all zero (alpha channel does the work)
    $w.Write((New-Object byte[] ($maskRow * $s)))
    $w.Flush(); return , $ms.ToArray()
}
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = Render $s
    if ($s -ge 256) {
        $ms = New-Object IO.MemoryStream; $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        $b.Save((Join-Path $out 'AppIcon.png'), [Drawing.Imaging.ImageFormat]::Png); $data = $ms.ToArray()
    } else { $data = DibBytes $b }
    $b.Dispose(); , $data
}

# ICO container
$ico = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush(); [IO.File]::WriteAllBytes((Join-Path $out 'AppIcon.ico'), $ico.ToArray())
Write-Host "wrote AppIcon.ico ($($sizes -join ', ')) and AppIcon.png"
