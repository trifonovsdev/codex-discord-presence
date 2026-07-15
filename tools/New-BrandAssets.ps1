[CmdletBinding()]
param([string]$Output = (Join-Path $PSScriptRoot '..\assets\codex-presence.ico'))

Add-Type -AssemblyName System.Drawing
$size = 256
$bitmap = [Drawing.Bitmap]::new($size, $size)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$rectangle = [Drawing.Rectangle]::new(8, 8, 240, 240)
$brush = [Drawing.Drawing2D.LinearGradientBrush]::new($rectangle, [Drawing.Color]::FromArgb(160,147,255), [Drawing.Color]::FromArgb(92,90,238), 135)
$path = [Drawing.Drawing2D.GraphicsPath]::new()
$radius = 58; $diameter = $radius * 2
$path.AddArc(8,8,$diameter,$diameter,180,90); $path.AddArc(248-$diameter,8,$diameter,$diameter,270,90)
$path.AddArc(248-$diameter,248-$diameter,$diameter,$diameter,0,90); $path.AddArc(8,248-$diameter,$diameter,$diameter,90,90); $path.CloseFigure()
$graphics.FillPath($brush,$path)
$pen = [Drawing.Pen]::new([Drawing.Color]::White,18); $pen.StartCap='Round'; $pen.EndCap='Round'; $pen.LineJoin='Round'
$graphics.DrawLines($pen,[Drawing.Point[]]@([Drawing.Point]::new(62,86),[Drawing.Point]::new(98,128),[Drawing.Point]::new(62,170)))
$graphics.DrawLine($pen,120,171,190,171)
$handle = $bitmap.GetHicon(); $icon = [Drawing.Icon]::FromHandle($handle)
$stream = [IO.File]::Create([IO.Path]::GetFullPath($Output)); $icon.Save($stream); $stream.Dispose()
$icon.Dispose(); $pen.Dispose(); $path.Dispose(); $brush.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
Write-Host ([IO.Path]::GetFullPath($Output))
