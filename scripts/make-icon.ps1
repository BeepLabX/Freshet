# Renders the Freshet brand mark (src/Freshet/Assets/BrandMark.xaml, original vector artwork) into a multi-size ICO.
# Run with Windows PowerShell: powershell -STA -File scripts\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskMarks = [System.Windows.Markup.XamlReader]::Parse([System.IO.File]::ReadAllText((Join-Path $taskRoot 'src\Freshet\Assets\BrandMark.xaml')))
$taskOutput = Join-Path $taskRoot 'src\Freshet\Assets\Freshet.ico'
$taskSizes = @(256, 64, 48, 40, 32, 24, 20, 16)

function Render([int]$size) {
    $mark = if ($size -le 32) { $taskMarks['BrandMarkSmall'] } else { $taskMarks['BrandMark'] }
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawImage($mark, (New-Object System.Windows.Rect 0, 0, $size, $size))
    $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}

# 256 px is stored as PNG. Smaller sizes are stored as 32-bit DIBs, which every Windows icon API reads.
function Encode([int]$size) {
    $bitmap = Render $size
    if ($size -ge 256) {
        $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream = New-Object System.IO.MemoryStream
        $encoder.Save($stream)
        return , $stream.ToArray()
    }
    $straight = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap $bitmap, ([System.Windows.Media.PixelFormats]::Bgra32), $null, 0
    $stride = $size * 4
    $pixels = New-Object byte[] ($stride * $size)
    $straight.CopyPixels($pixels, $stride, 0)
    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    $writer.Write([uint32]40); $writer.Write([int32]$size); $writer.Write([int32]($size * 2))
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]0)
    $writer.Write([uint32]($stride * $size + $maskStride * $size)); $writer.Write([int32]0); $writer.Write([int32]0)
    $writer.Write([uint32]0); $writer.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $writer.Write($pixels, $y * $stride, $stride) }
    $writer.Write((New-Object byte[] ($maskStride * $size)))  # Alpha channel carries transparency; AND mask stays empty.
    $writer.Flush()
    return , $stream.ToArray()
}

$images = @($taskSizes | ForEach-Object { , (Encode $_) })
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
