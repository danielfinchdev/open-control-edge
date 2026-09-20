# Generates src\EdgeWidget\Assets\app.ico: black rounded square with an orange progress arc.
# Sizes <= 64 are stored as classic 32-bit DIBs (widest API compatibility), 256 as PNG.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\EdgeWidget\Assets\app.ico'))

Add-Type -AssemblyName System.Drawing
$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
$entries = @()

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $bg = New-RoundedPath 0 0 $s $s ([Math]::Max(3.0, $s * 0.24))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 0, 0))), $bg)

    $stroke = [float][Math]::Max(1.8, $s * 0.12)
    $inset = [float]($s * 0.2 + $stroke / 2)
    $rect = New-Object System.Drawing.RectangleF $inset, $inset, ([float]($s - 2 * $inset)), ([float]($s - 2 * $inset))
    $track = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x3A, 0x3A, 0x3A)), $stroke
    $g.DrawEllipse($track, $rect)
    $arc = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0xD9, 0x77, 0x57)), $stroke
    $arc.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arc.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($arc, $rect, -90, 250)
    $g.Dispose()

    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $data = $ms.ToArray()
    } else {
        $lock = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $s, $s), [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $pixels = New-Object byte[] ($s * $s * 4)
        [System.Runtime.InteropServices.Marshal]::Copy($lock.Scan0, $pixels, 0, $pixels.Length)
        $bmp.UnlockBits($lock)
        $maskRow = [int]([Math]::Floor(($s + 31) / 32) * 4)
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s * 2))
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
        $bw.Write([UInt32]($pixels.Length + $maskRow * $s))
        $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($row = $s - 1; $row -ge 0; $row--) { $bw.Write($pixels, $row * $s * 4, $s * 4) }
        $bw.Write((New-Object byte[] ($maskRow * $s)))
        $bw.Flush()
        $data = $ms.ToArray()
    }
    $bmp.Dispose()
    $entries += , @{ Size = $s; Data = $data }
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$e.Data.Length); $w.Write([UInt32]$offset)
    $offset += $e.Data.Length
}
foreach ($e in $entries) { $w.Write($e.Data) }
$w.Close()
Write-Output "Icon written: $Out ($((Get-Item $Out).Length) bytes)"
