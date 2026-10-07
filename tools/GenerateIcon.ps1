param([string]$OutputPath = (Join-Path $PSScriptRoot '..\Assets\ScreenLight.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
function New-RoundedPath([float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}
$frames = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 24, 32, 48, 64, 128, 256)
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size / 256.0, $size / 256.0)
    $background = New-RoundedPath 8 8 240 240 54
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.Point]::new(0,0), [System.Drawing.Point]::new(256,256), [System.Drawing.ColorTranslator]::FromHtml('#19b9ae'), [System.Drawing.ColorTranslator]::FromHtml('#176b9d'))
    $graphics.FillPath($gradient, $background)
    $white = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 12)
    $white.StartCap = $white.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $monitor = New-RoundedPath 43 61 170 112 15
    $graphics.DrawPath($white, $monitor)
    $graphics.DrawLine($white, 111, 176, 111, 200)
    $graphics.DrawLine($white, 145, 176, 145, 200)
    $graphics.DrawLine($white, 98, 200, 158, 200)
    $gold = [System.Drawing.ColorTranslator]::FromHtml('#ffcf57')
    $ray = [System.Drawing.Pen]::new($gold, 8)
    $ray.StartCap = $ray.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($index in 0..7) {
        $angle = $index * [Math]::PI / 4
        $graphics.DrawLine($ray, [float](128 + 29 * [Math]::Cos($angle)), [float](117 + 29 * [Math]::Sin($angle)), [float](128 + 38 * [Math]::Cos($angle)), [float](117 + 38 * [Math]::Sin($angle)))
    }
    $sun = [System.Drawing.SolidBrush]::new($gold)
    $graphics.FillEllipse($sun, 106, 95, 44, 44)
    $buffer = [System.IO.MemoryStream]::new()
    $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($buffer.ToArray())
    $buffer.Dispose(); $sun.Dispose(); $ray.Dispose(); $monitor.Dispose(); $white.Dispose(); $gradient.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$iconBuffer = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($iconBuffer)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($index = 0; $index -lt $sizes.Count; $index++) {
    $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$index].Length
}
foreach ($frame in $frames) { $writer.Write($frame) }
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($OutputPath), $iconBuffer.ToArray())
$writer.Dispose(); $iconBuffer.Dispose()
Write-Output ('Icon generated: ' + [System.IO.Path]::GetFullPath($OutputPath))
