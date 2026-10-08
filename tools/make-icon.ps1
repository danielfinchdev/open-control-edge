# Generates src\OpenControlEdge\Assets\app.ico: black rounded square with three concentric progress rings
# (orange 300°, green 220°, yellow 140°, the colours of the panel's rings) on dark tracks. From 16 to 24 px only
# the outer two, thicker, so the mark still reads in the taskbar and the tray.
# Sizes <= 64 are stored as classic 32-bit DIBs (widest API compatibility), 256 as PNG.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\OpenControlEdge\Assets\app.ico'))

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

    $bg = New-RoundedPath 0 0 $s $s ([Math]::Max(3.0, $s * 56 / 256))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 0, 0))), $bg)

    # Design at 256 px: radii 96 / 64 / 32, stroke 24. Small sizes: radii 92 / 50, stroke 34 (in 256 units).
    $small = $s -le 24
    $rings = if ($small) {
        @(@{ R = 92; Sweep = 300; Color = @(0xE8, 0x49, 0x1D) }, @{ R = 50; Sweep = 220; Color = @(0x22, 0xC5, 0x5E) })
    } else {
        @(@{ R = 96; Sweep = 300; Color = @(0xE8, 0x49, 0x1D) }, @{ R = 64; Sweep = 220; Color = @(0x22, 0xC5, 0x5E) },
          @{ R = 32; Sweep = 140; Color = @(0xE5, 0xE6, 0x19) })
    }
    $unit = $s / 256.0
    $stroke = [float]([Math]::Max(1.6, ($(if ($small) { 34 } else { 24 })) * $unit))
    foreach ($ring in $rings) {
        $r = [float]($ring.R * $unit)
        $c = [float]($s / 2.0)
        $rect = New-Object System.Drawing.RectangleF ($c - $r), ($c - $r), (2 * $r), (2 * $r)
        $track = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x2A, 0x2A, 0x2A)), $stroke
        $g.DrawEllipse($track, $rect)
        $arc = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, $ring.Color[0], $ring.Color[1], $ring.Color[2])), $stroke
        $arc.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $arc.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawArc($arc, $rect, -90, $ring.Sweep)
    }
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
