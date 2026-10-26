# Builds a multi-size .ico from a square PNG whose artwork is a rounded squircle
# on a light background. The inside of the squircle is kept; everything outside
# (background + outer drop shadow) is masked to transparent.
param(
    [Parameter(Mandatory = $true)][string]$SourcePng,
    [Parameter(Mandatory = $true)][string]$OutputIco,
    [string]$PreviewPng = '',
    [double]$InsetRatio = 0.0703,
    [double]$TileRatio = 0.8594,
    [double]$RadiusRatio = 0.2031
)

Add-Type -AssemblyName System.Drawing

function New-RoundedPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R)
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $R * 2
    $p.AddArc($X, $Y, $d, $d, 180, 90)
    $p.AddArc(($X + $W - $d), $Y, $d, $d, 270, 90)
    $p.AddArc(($X + $W - $d), ($Y + $H - $d), $d, $d, 0, 90)
    $p.AddArc($X, ($Y + $H - $d), $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$source = [System.Drawing.Image]::FromFile((Resolve-Path $SourcePng).Path)
$size = $source.Width
if ($source.Width -ne $source.Height) { throw 'Source PNG must be square' }

$masked = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$mg = [System.Drawing.Graphics]::FromImage($masked)
$mg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$mg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$mg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$mg.Clear([System.Drawing.Color]::Transparent)

[float]$inset = $size * $InsetRatio
[float]$tile = $size * $TileRatio
[float]$radius = $size * $RadiusRatio
$clip = New-RoundedPath -X $inset -Y $inset -W $tile -H $tile -R $radius
$mg.SetClip($clip)
$mg.DrawImage($source, 0, 0, $size, $size)
$mg.ResetClip()
$mg.Dispose()
$source.Dispose()

# Keep a large reference copy next to the icon for future regeneration.
$pngPath = [System.IO.Path]::ChangeExtension($OutputIco, '.png')
$masked.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($s in $sizes) {
    $bmp = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($masked, 0, 0, $s, $s)
    $g.Dispose()

    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@{ Size = $s; Bytes = $ms.ToArray() }
    if ($s -eq 256 -and $PreviewPng -ne '') {
        $bmp.Save((Join-Path (Get-Location) $PreviewPng), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $ms.Dispose()
    $bmp.Dispose()
}

$dir = Split-Path -Parent $OutputIco
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$file = [System.IO.File]::Create($OutputIco)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$frames.Count)
$offset = 6 + (16 * $frames.Count)
foreach ($f in $frames) {
    $b = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $writer.Write([byte]$b)
    $writer.Write([byte]$b)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$f.Bytes.Length)
    $writer.Write([UInt32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $writer.Write([byte[]]$f.Bytes) }
$writer.Dispose()
$file.Dispose()
$masked.Dispose()

Write-Host "Wrote $OutputIco ($((Get-Item $OutputIco).Length) bytes) + $pngPath"
