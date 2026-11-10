# Draws the original Freshet app icon (vector, no third-party artwork) and writes a multi-size ICO.
# Run with Windows PowerShell: powershell -STA -File scripts\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $taskRoot 'src\Freshet\Assets\Freshet.ico'
$taskSizes = @(256, 64, 48, 40, 32, 24, 20, 16)

function New-IconPng([int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size / 256.0), ($size / 256.0)))
    $fill = New-Object System.Windows.Media.LinearGradientBrush (
        [System.Windows.Media.Color]::FromRgb(0x2F, 0xB8, 0xE8), [System.Windows.Media.Color]::FromRgb(0x1D, 0x5F, 0xE0), 55.0)
    $inset = if ($size -le 24) { 8 } else { 16 }
    $radius = if ($size -le 24) { 48 } else { 58 }
    $dc.DrawRoundedRectangle($fill, $null, (New-Object System.Windows.Rect $inset, $inset, (256 - 2 * $inset), (256 - 2 * $inset)), $radius, $radius)
    # Small sizes get thicker strokes and a single wave so the mark stays legible.
    $small = $size -le 32
    $stroke = if ($small) { 30 } else { 20 }
    $white = New-Object System.Windows.Media.Pen ([System.Windows.Media.Brushes]::White), $stroke
    $white.StartLineCap = 'Round'; $white.EndLineCap = 'Round'; $white.LineJoin = 'Round'
    $arrow = if ($small) { 'M128,58 L128,142 M88,98 L128,58 L168,98' } else { 'M128,56 L128,138 M94,90 L128,56 L162,90' }
    $dc.DrawGeometry($null, $white, [System.Windows.Media.Geometry]::Parse($arrow))
    $wave = if ($small) { 'M54,188 C82,164 106,164 128,184 C150,204 174,204 202,180' } else { 'M52,176 C80,152 104,152 128,172 C152,192 176,192 204,168' }
    $dc.DrawGeometry($null, $white, [System.Windows.Media.Geometry]::Parse($wave))
    if (-not $small) {
        $soft = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb(0x99, 0xFF, 0xFF, 0xFF))
        $softPen = New-Object System.Windows.Media.Pen $soft, 14
        $softPen.StartLineCap = 'Round'; $softPen.EndLineCap = 'Round'
        $dc.DrawGeometry($null, $softPen, [System.Windows.Media.Geometry]::Parse('M70,212 C94,194 112,194 128,208 C146,222 164,222 186,206'))
    }
    $dc.Pop(); $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return , $stream.ToArray()
}

# ICO container: header, one directory entry per size, then PNG payloads (Windows Vista+).
$images = @($taskSizes | ForEach-Object { , (New-IconPng $_) })
$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$taskSizes.Count)
$offset = 6 + 16 * $taskSizes.Count
for ($i = 0; $i -lt $taskSizes.Count; $i++) {
    $dimension = if ($taskSizes[$i] -ge 256) { 0 } else { $taskSizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()
[System.IO.File]::WriteAllBytes($taskOutput, $file.ToArray())
Get-Item -LiteralPath $taskOutput | Select-Object FullName, Length
