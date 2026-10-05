# Собирает src/Khors.App/Assets/khors.ico из логотипа KHORS (src/Khors.App/Assets/logo-512.png).
# Размеры 16–256, масштабирование HighQualityBicubic; кадры хранятся в ICO как PNG.
# Запуск (Windows): powershell -ExecutionPolicy Bypass -File tools/brand/make-icon.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot "..\..\src\Khors.App\Assets"
$source = [System.Drawing.Image]::FromFile((Resolve-Path (Join-Path $assets "logo-512.png")))
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @()

foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.DrawImage($source, 0, 0, $size, $size)
    $graphics.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $frames += , @($size, $stream.ToArray())
}
$source.Dispose()

$output = Join-Path $assets "khors.ico"
$writer = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($output))
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($frame in $frames) {
    $dimension = if ($frame[0] -ge 256) { 0 } else { $frame[0] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frame[1].Length); $writer.Write([uint32]$offset)
    $offset += $frame[1].Length
}
foreach ($frame in $frames) { $writer.Write($frame[1]) }
$writer.Dispose()
Write-Host "$output : $($frames.Count) sizes"
