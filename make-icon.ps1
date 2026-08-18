# Draws src\idlemaster.ico - the same mark the site uses for its favicon
# (dark rounded square, "ID" in Ice blue) - at every size Explorer asks for.
#
# Run it once when the design changes; the .ico is committed, so build.ps1
# never needs this. In-box System.Drawing only, like everything else here.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$out  = Join-Path $root 'src\idlemaster.ico'

function Draw-Mark([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.PixelOffsetMode = 'HighQuality'
  $g.Clear([System.Drawing.Color]::Transparent)
  $k = $size / 32.0

  # rounded square, rx = 7/32 of the side
  $r = 7 * $k
  $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
  $bg.AddArc(0, 0, 2*$r, 2*$r, 180, 90)
  $bg.AddArc($size - 2*$r, 0, 2*$r, 2*$r, 270, 90)
  $bg.AddArc($size - 2*$r, $size - 2*$r, 2*$r, 2*$r, 0, 90)
  $bg.AddArc(0, $size - 2*$r, 2*$r, 2*$r, 90, 90)
  $bg.CloseFigure()
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x11,0x13,0x18))), $bg)

  # "I" then "D" - the site's SVG path, scaled. Alternate fill cuts the D's hole.
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.FillMode = 'Alternate'
  $p.AddRectangle((New-Object System.Drawing.RectangleF (9*$k), (10.5*$k), (2.6*$k), (11*$k)))
  $p.StartFigure()
  $p.AddLine(14.4*$k, 10.5*$k, 18.6*$k, 10.5*$k)
  $p.AddBezier(18.6*$k, 10.5*$k, 22.2*$k, 10.5*$k, 24.5*$k, 12.6*$k, 24.5*$k, 16.0*$k)
  $p.AddBezier(24.5*$k, 16.0*$k, 24.5*$k, 19.4*$k, 22.2*$k, 21.5*$k, 18.6*$k, 21.5*$k)
  $p.AddLine(18.6*$k, 21.5*$k, 14.4*$k, 21.5*$k)
  $p.CloseFigure()
  $p.StartFigure()
  $p.AddLine(17.0*$k, 12.8*$k, 17.0*$k, 19.2*$k)
  $p.AddLine(17.0*$k, 19.2*$k, 18.4*$k, 19.2*$k)
  $p.AddBezier(18.4*$k, 19.2*$k, 20.5*$k, 19.2*$k, 21.8*$k, 18.0*$k, 21.8*$k, 16.0*$k)
  $p.AddBezier(21.8*$k, 16.0*$k, 21.8*$k, 14.0*$k, 20.5*$k, 12.8*$k, 18.4*$k, 12.8*$k)
  $p.CloseFigure()
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x8f,0xc1,0xf0))), $p)

  $g.Dispose()
  return $bmp
}

# One frame per size. 256 goes in as PNG (the only size Windows wants that
# way); the rest as plain 32-bit DIBs, which every shell since XP reads.
$sizes = 16, 20, 24, 32, 48, 256
$frames = @()
foreach ($s in $sizes) {
  $bmp = Draw-Mark $s
  if ($s -ge 256) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@{ Size = $s; Data = $ms.ToArray() }
  } else {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER with doubled height (colour + mask)
    $bw.Write([int32]40); $bw.Write([int32]$s); $bw.Write([int32]($s*2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int32]0)
    $bw.Write([int32]($s*$s*4)); $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([int32]0); $bw.Write([int32]0)
    # pixels, bottom-up, BGRA
    for ($y = $s - 1; $y -ge 0; $y--) {
      for ($x = 0; $x -lt $s; $x++) {
        $c = $bmp.GetPixel($x, $y)
        $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
      }
    }
    # AND mask: all zero (alpha does the work), rows padded to 32 bits
    $stride = [int](([math]::Ceiling($s / 32.0)) * 4)
    for ($y = 0; $y -lt $s; $y++) { for ($i = 0; $i -lt $stride; $i++) { $bw.Write([byte]0) } }
    $bw.Flush()
    $frames += ,@{ Size = $s; Data = $ms.ToArray() }
  }
  $bmp.Dispose()
}

$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
  $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([int16]1); $w.Write([int16]32)
  $w.Write([int32]$f.Data.Length); $w.Write([int32]$offset)
  $offset += $f.Data.Length
}
foreach ($f in $frames) { $w.Write($f.Data) }
$w.Flush(); $fs.Close()

Write-Host "wrote $out ($((Get-Item $out).Length) bytes, $($frames.Count) frames)" -ForegroundColor Green
