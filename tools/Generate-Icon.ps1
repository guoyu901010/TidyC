param(
    [string]$OutputIcon = (Join-Path $PSScriptRoot '..\src\TidyC.App\Assets\TidyC.ico'),
    [string]$OutputPreview = (Join-Path $PSScriptRoot '..\src\TidyC.App\Assets\TidyC.png')
)

Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param([single]$X, [single]$Y, [single]$Width, [single]$Height, [single]$Radius)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $Radius * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc(($X + $Width - $d), $Y, $d, $d, 270, 90)
    $path.AddArc(($X + $Width - $d), ($Y + $Height - $d), $d, $d, 0, 90)
    $path.AddArc($X, ($Y + $Height - $d), $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::Transparent)

    [single]$inset = [Math]::Max(1, [int]($Size * 0.03))
    [single]$w = $Size - (2 * $inset)
    $rect = [System.Drawing.RectangleF]::new($inset, $inset, $w, $w)
    [single]$radius = $Size * 0.22

    $shape = New-RoundedRectanglePath -X $rect.X -Y $rect.Y -Width $rect.Width -Height $rect.Height -Radius $radius
    $startColor = [System.Drawing.Color]::FromArgb(255, 76, 194, 255)
    $endColor = [System.Drawing.Color]::FromArgb(255, 0, 103, 192)
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $startColor, $endColor, [single]45.0)
    $graphics.FillPath($gradient, $shape)

    if ($Size -ge 32) {
        [single]$hHeight = $rect.Height * 0.46
        $highlightPath = New-RoundedRectanglePath -X $rect.X -Y $rect.Y -Width $rect.Width -Height ($hHeight * 2) -Radius $radius
        $highlight = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(26, 255, 255, 255))
        $graphics.FillPath($highlight, $highlightPath)
        $highlight.Dispose()
        $highlightPath.Dispose()
    }

    [single]$fontSize = $Size * 0.58
    $font = [System.Drawing.Font]::new('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = [System.Drawing.StringFormat]::new()
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textRect = [System.Drawing.RectangleF]::new(0, [single]($Size * 0.015), [single]$Size, [single]$Size)
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $graphics.DrawString('T', $font, $white, $textRect, $format)

    if ($Size -ge 24) {
        [single]$dot = $Size * 0.20
        [single]$dotX = $Size - $dot - ($Size * 0.10)
        [single]$dotY = $Size - $dot - ($Size * 0.10)
        $ring = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 19, 19, 19))
        $graphics.FillEllipse($ring, ($dotX - ($dot * 0.10)), ($dotY - ($dot * 0.10)), ($dot * 1.20), ($dot * 1.20))
        $green = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 63, 208, 122))
        $graphics.FillEllipse($green, $dotX, $dotY, $dot, $dot)
        $ring.Dispose()
        $green.Dispose()
    }

    $white.Dispose()
    $format.Dispose()
    $font.Dispose()
    $gradient.Dispose()
    $shape.Dispose()
    $graphics.Dispose()
    return $bitmap
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()

foreach ($size in $sizes) {
    $bmp = New-IconBitmap -Size $size
    $stream = [System.IO.MemoryStream]::new()
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,@{ Size = $size; Bytes = $stream.ToArray() }
    if ($size -eq 256) {
        $bmp.Save($OutputPreview, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $stream.Dispose()
    $bmp.Dispose()
}

$iconDirectory = Split-Path -Parent $OutputIcon
if (-not (Test-Path $iconDirectory)) { New-Item -ItemType Directory -Path $iconDirectory -Force | Out-Null }

$file = [System.IO.File]::Create($OutputIcon)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$frames.Count)

$offset = 6 + (16 * $frames.Count)
foreach ($frame in $frames) {
    $sizeByte = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
    $writer.Write([byte]$sizeByte)
    $writer.Write([byte]$sizeByte)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$frame.Bytes.Length)
    $writer.Write([UInt32]$offset)
    $offset += $frame.Bytes.Length
}

foreach ($frame in $frames) {
    $writer.Write([byte[]]$frame.Bytes)
}

$writer.Flush()
$writer.Dispose()
$file.Dispose()

Write-Host "Wrote $OutputIcon ($((Get-Item $OutputIcon).Length) bytes) and $OutputPreview"
