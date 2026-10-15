$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sourcePath = Join-Path $PSScriptRoot 'assets/icon.png'
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    $sourcePath = Join-Path $PSScriptRoot 'assets/characters/whale/idle.png'
}
$iconPath = Join-Path $PSScriptRoot 'app.ico'
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Cannot find $sourcePath"
}

$source = [System.Drawing.Bitmap]::new($sourcePath)
$thumb = [System.Drawing.Bitmap]::new(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($thumb)
$stream = [System.IO.MemoryStream]::new()
try {
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.DrawImage($source,
        [System.Drawing.Rectangle]::new(0, 0, 256, 256),
        [System.Drawing.Rectangle]::new(0, 0, $source.Width, $source.Height),
        [System.Drawing.GraphicsUnit]::Pixel)
    $thumb.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $png = $stream.ToArray()

    $file = [System.IO.File]::Create($iconPath)
    try {
        $writer = [System.IO.BinaryWriter]::new($file)
        $writer.Write([uint16]0)       # reserved
        $writer.Write([uint16]1)       # icon
        $writer.Write([uint16]1)       # one image
        $writer.Write([byte]0)         # 256 px wide
        $writer.Write([byte]0)         # 256 px high
        $writer.Write([byte]0)         # color count
        $writer.Write([byte]0)         # reserved
        $writer.Write([uint16]1)       # color planes
        $writer.Write([uint16]32)      # bits per pixel
        $writer.Write([uint32]$png.Length)
        $writer.Write([uint32]22)      # PNG data offset
        $writer.Write($png)
        $writer.Flush()
    }
    finally { $file.Dispose() }
}
finally {
    $graphics.Dispose()
    $thumb.Dispose()
    $source.Dispose()
    $stream.Dispose()
}
Write-Host "Created $iconPath"
