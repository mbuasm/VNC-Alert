param(
    [string]$Png = 'src/MaszynyAlert.App/Assets/VncAlert.png',
    [string]$Ico = 'src/MaszynyAlert.App/Assets/VncAlert.ico'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$colors = @{
    Edge   = [Drawing.Color]::FromArgb(0x8A, 0x96, 0xA4)
    Bezel  = [Drawing.Color]::FromArgb(0xCD, 0xD4, 0xDC)
    Neck   = [Drawing.Color]::FromArgb(0xA9, 0xB3, 0xBF)
    Screen = [Drawing.Color]::FromArgb(0x0B, 0x12, 0x20)
    Bar    = [Drawing.Color]::FromArgb(0x5A, 0xB4, 0xF5)
    Alarm  = [Drawing.Color]::FromArgb(0xFF, 0x4D, 0x4F)
}

# Małe rozmiary: prostokąty w pikselach (x, y, szerokość, wysokość). Ostatni słupek = alarm.
$pixelLayouts = @{
    16 = @{ Edge = 0, 2, 16, 11; Bezel = 1, 3, 14, 9; Screen = 2, 4, 12, 7; Neck = 6, 13, 4, 1; Foot = 4, 14, 8, 1; Radius = 1
            Bars = @((3, 6, 2, 4), (7, 7, 2, 3), (11, 5, 2, 5)) }
    20 = @{ Edge = 0, 2, 20, 14; Bezel = 1, 3, 18, 12; Screen = 2, 4, 16, 10; Neck = 8, 16, 4, 1; Foot = 5, 17, 10, 1; Radius = 1
            Bars = @((3, 10, 2, 3), (7, 7, 2, 6), (11, 9, 2, 4), (15, 5, 2, 8)) }
    24 = @{ Edge = 0, 3, 24, 16; Bezel = 1, 4, 22, 14; Screen = 2, 5, 20, 12; Neck = 10, 19, 4, 1; Foot = 6, 20, 12, 1; Radius = 1
            Bars = @((3, 12, 3, 4), (8, 9, 3, 7), (13, 11, 3, 5), (18, 7, 3, 9)) }
    32 = @{ Edge = 0, 4, 32, 21; Bezel = 1, 5, 30, 19; Screen = 3, 7, 26, 15; Neck = 13, 25, 6, 2; Foot = 8, 27, 16, 2; Radius = 2
            Bars = @((5, 15, 4, 5), (11, 12, 4, 8), (17, 14, 4, 6), (23, 9, 4, 11)) }
}

function Fill-Pixels([Drawing.Graphics]$g, [Drawing.Color]$color, [int[]]$r) {
    $brush = [Drawing.SolidBrush]::new($color)
    try { $g.FillRectangle($brush, $r[0], $r[1], $r[2], $r[3]) } finally { $brush.Dispose() }
}

function Draw-PixelIcon([Drawing.Bitmap]$bitmap, [hashtable]$l) {
    $g = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::None
        Fill-Pixels $g $colors.Edge $l.Edge
        Fill-Pixels $g $colors.Bezel $l.Bezel
        Fill-Pixels $g $colors.Screen $l.Screen
        Fill-Pixels $g $colors.Neck $l.Neck
        Fill-Pixels $g $colors.Edge $l.Foot
        for ($i = 0; $i -lt $l.Bars.Count; $i++) {
            $color = if ($i -eq $l.Bars.Count - 1) { $colors.Alarm } else { $colors.Bar }
            Fill-Pixels $g $color $l.Bars[$i]
        }
    } finally { $g.Dispose() }
    # Zaokrąglenie rogów obudowy: narożne piksele przezroczyste, sąsiad po przekątnej w kolorze krawędzi.
    $x0 = $l.Edge[0]; $y0 = $l.Edge[1]; $x1 = $x0 + $l.Edge[2] - 1; $y1 = $y0 + $l.Edge[3] - 1
    foreach ($corner in @(($x0, $y0, 1, 1), ($x1, $y0, -1, 1), ($x0, $y1, 1, -1), ($x1, $y1, -1, -1))) {
        $cx, $cy, $dx, $dy = $corner
        for ($k = 0; $k -lt $l.Radius; $k++) {
            $bitmap.SetPixel($cx + $k * $dx, $cy, [Drawing.Color]::Transparent)
            $bitmap.SetPixel($cx, $cy + $k * $dy, [Drawing.Color]::Transparent)
        }
        $bitmap.SetPixel($cx + $dx, $cy + $dy, $colors.Edge)
    }
}

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-VectorIcon([Drawing.Bitmap]$bitmap, [int]$size) {
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $disposables = [Collections.Generic.List[IDisposable]]::new()
    try {
        $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.ScaleTransform($size / 256.0, $size / 256.0)
        $brush = { param($c) $b = [Drawing.SolidBrush]::new($c); $disposables.Add($b); $b }
        # Krawędź co najmniej 1 px w każdym rozmiarze.
        $edgeWidth = [Math]::Max(3.0, 256.0 / $size)
        $edgePen = [Drawing.Pen]::new($colors.Edge, $edgeWidth); $disposables.Add($edgePen)

        $neck = [Drawing.Drawing2D.GraphicsPath]::new(); $disposables.Add($neck)
        $neck.AddPolygon([Drawing.PointF[]]@([Drawing.PointF]::new(112, 184), [Drawing.PointF]::new(144, 184),
            [Drawing.PointF]::new(150, 206), [Drawing.PointF]::new(106, 206)))
        $g.FillPath((& $brush $colors.Neck), $neck)
        $foot = New-RoundedRect 78 204 100 10 5; $disposables.Add($foot)
        $g.FillPath((& $brush $colors.Bezel), $foot); $g.DrawPath($edgePen, $foot)

        $bezel = New-RoundedRect 14 38 228 146 12; $disposables.Add($bezel)
        $g.FillPath((& $brush $colors.Bezel), $bezel); $g.DrawPath($edgePen, $bezel)
        $screen = New-RoundedRect 24 48 208 126 4; $disposables.Add($screen)
        $g.FillPath((& $brush $colors.Screen), $screen)

        $heights = 0.4, 0.75, 0.55, 1.0, 0.7
        $x0 = 40.0; $x1 = 216.0; $top = 70.0; $base = 158.0
        # Słupki wyrównane do pełnych pikseli, żeby w 48–64 px miały ostre krawędzie.
        $snap = { param($v) [Math]::Round($v * $size / 256.0) * 256.0 / $size }
        $slot = ($x1 - $x0) / $heights.Count; $barWidth = & $snap ($slot * 0.6)
        for ($i = 0; $i -lt $heights.Count; $i++) {
            $left = & $snap ($x0 + $i * $slot + ($slot - $barWidth) / 2)
            $barTop = & $snap ($base - $heights[$i] * ($base - $top))
            $color = if ($i -eq $heights.Count - 1) { $colors.Alarm } else { $colors.Bar }
            $bar = New-RoundedRect $left $barTop $barWidth ((& $snap $base) - $barTop) 4; $disposables.Add($bar)
            $g.FillPath((& $brush $color), $bar)
        }
    } finally {
        foreach ($d in $disposables) { $d.Dispose() }
        $g.Dispose()
    }
}

function New-IconBitmap([int]$size) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    if ($pixelLayouts.ContainsKey($size)) { Draw-PixelIcon $bitmap $pixelLayouts[$size] } else { Draw-VectorIcon $bitmap $size }
    return $bitmap
}

$master = New-IconBitmap 1024
try { $master.Save([IO.Path]::GetFullPath($Png), [Drawing.Imaging.ImageFormat]::Png) } finally { $master.Dispose() }

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$frames = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    $stream = [IO.MemoryStream]::new()
    try {
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $frames.Add($stream.ToArray())
    } finally { $stream.Dispose(); $bitmap.Dispose() }
}
$writer = [IO.BinaryWriter]::new([IO.File]::Create([IO.Path]::GetFullPath($Ico)))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = [byte]($sizes[$i] % 256)
        $writer.Write($dimension); $writer.Write($dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose() }
Write-Output "Created $Png (1024 px) and $Ico ($($sizes -join ', ') px)."
