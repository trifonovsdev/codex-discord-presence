[CmdletBinding()]
param([string]$Output = (Join-Path $PSScriptRoot '..\assets\codex-presence.ico'))

Add-Type -AssemblyName System.Drawing
$size = 256
$bitmap = [Drawing.Bitmap]::new($size, $size)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$rectangle = [Drawing.Rectangle]::new(8, 8, 240, 240)
$brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(18,18,18))
$path = [Drawing.Drawing2D.GraphicsPath]::new()
$radius = 58; $diameter = $radius * 2
$path.AddArc(8,8,$diameter,$diameter,180,90); $path.AddArc(248-$diameter,8,$diameter,$diameter,270,90)
$path.AddArc(248-$diameter,248-$diameter,$diameter,$diameter,0,90); $path.AddArc(8,248-$diameter,$diameter,$diameter,90,90); $path.CloseFigure()
$graphics.FillPath($brush,$path)
$border = [Drawing.Pen]::new([Drawing.Color]::FromArgb(58,58,58),2)
$graphics.DrawPath($border,$path)
$pen = [Drawing.Pen]::new([Drawing.Color]::White,18); $pen.StartCap='Round'; $pen.EndCap='Round'; $pen.LineJoin='Round'
$graphics.DrawLines($pen,[Drawing.Point[]]@([Drawing.Point]::new(58,82),[Drawing.Point]::new(100,128),[Drawing.Point]::new(58,174)))
$graphics.DrawLine($pen,122,174,194,174)
$liveBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(46,207,145))
$graphics.FillEllipse($liveBrush,187,41,28,28)
$handle = $bitmap.GetHicon(); $icon = [Drawing.Icon]::FromHandle($handle)
$stream = [IO.File]::Create([IO.Path]::GetFullPath($Output)); $icon.Save($stream); $stream.Dispose()
$icon.Dispose(); $liveBrush.Dispose(); $pen.Dispose(); $border.Dispose(); $path.Dispose(); $brush.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
Write-Host ([IO.Path]::GetFullPath($Output))
