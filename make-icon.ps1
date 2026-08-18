# Draws src\idlemaster.ico - the "IM" mark, the same one the site uses for its
# favicon - at every size Explorer asks for.
#
#   dark rounded square with a faint Ice bezel, "I" and "M" cut as geometric
#   blocks in a top-lit blue, and a row of RAM-stick contacts along the foot.
#
# Run it once when the design changes; the .ico is committed, so build.ps1
# never needs this. In-box System.Drawing only, like everything else here.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$out  = Join-Path $root 'src\idlemaster.ico'

function Rounded([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc($x, $y, 2*$r, 2*$r, 180, 90)
  $p.AddArc($x + $w - 2*$r, $y, 2*$r, 2*$r, 270, 90)
  $p.AddArc($x + $w - 2*$r, $y + $h - 2*$r, 2*$r, 2*$r, 0, 90)
  $p.AddArc($x, $y + $h - 2*$r, 2*$r, 2*$r, 90, 90)
  $p.CloseFigure()
  return $p
}

function Draw-Mark([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.PixelOffsetMode = 'HighQuality'
  $g.Clear([System.Drawing.Color]::Transparent)
  $k = $size / 32.0

  # plate: top-lit dark gradient
  $plate = Rounded 0 0 $size $size (7*$k)
  $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
    (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $size), `
    ([System.Drawing.Color]::FromArgb(0x1c,0x21,0x2c)), ([System.Drawing.Color]::FromArgb(0x0c,0x0e,0x13))
  $g.FillPath($bgBrush, $plate)

  # bezel: a hair of Ice just inside the edge (skipped where it would be a smear)
  if ($size -ge 24) {
    $inset = 0.6 * $k
    $bezel = Rounded $inset $inset ($size - 2*$inset) ($size - 2*$inset) (6.4*$k)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 0x8f,0xc1,0xf0)), ([Math]::Max(1.0, 0.7*$k))
    $g.DrawPath($pen, $bezel)
  }

  # letters, lit from the top
  $ink = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
    (New-Object System.Drawing.PointF 0, (8*$k)), (New-Object System.Drawing.PointF 0, (23*$k)), `
    ([System.Drawing.Color]::FromArgb(0xbf,0xe0,0xfb)), ([System.Drawing.Color]::FromArgb(0x6a,0xab,0xe6))

  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  # I
  $p.AddRectangle((New-Object System.Drawing.RectangleF (6.5*$k), (8.5*$k), (3.2*$k), (14*$k)))
  # M
  $m = @(
    @(12.5, 22.5), @(12.5, 8.5), @(15.7, 8.5), @(19.25, 15.6), @(22.8, 8.5), @(26.0, 8.5),
    @(26.0, 22.5), @(23.0, 22.5), @(23.0, 13.9), @(20.2, 19.4), @(18.3, 19.4), @(15.5, 13.9),
    @(15.5, 22.5)
  )
  $pts = New-Object 'System.Drawing.PointF[]' $m.Count
  for ($i = 0; $i -lt $m.Count; $i++) { $pts[$i] = New-Object System.Drawing.PointF ($m[$i][0]*$k), ($m[$i][1]*$k) }
  $p.StartFigure()
  $p.AddPolygon($pts)
  $g.FillPath($ink, $p)

  # RAM contacts along the foot
  if ($size -ge 24) {
    $cb = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 0x8f,0xc1,0xf0))
    foreach ($x0 in 7.0, 11.5, 16.0, 20.5) {
      $g.FillRectangle($cb, ($x0*$k), (25.0*$k), (3.0*$k), (1.6*$k))
    }
  }

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

# A preview PNG next to the ico is handy for the README and for eyeballing.
$prev = Draw-Mark 256
$prev.Save((Join-Path $root 'docs\icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$prev.Dispose()

Write-Host "wrote $out ($((Get-Item $out).Length) bytes, $($frames.Count) frames) + docs\icon.png" -ForegroundColor Green
