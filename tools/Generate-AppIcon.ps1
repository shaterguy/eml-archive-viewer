param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath {
    param(
        [single]$X,
        [single]$Y,
        [single]$Width,
        [single]$Height,
        [single]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng {
    param([int]$Size)

    $scale = $Size / 256.0
    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $s = { param([double]$Value) [single]($Value * $scale) }

        $shadowPath = New-RoundedPath (& $s 21) (& $s 25) (& $s 218) (& $s 218) (& $s 36)
        $shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(70, 0, 21, 51))
        $graphics.FillPath($shadowBrush, $shadowPath)
        $shadowBrush.Dispose()
        $shadowPath.Dispose()

        $backgroundPath = New-RoundedPath (& $s 17) (& $s 17) (& $s 218) (& $s 218) (& $s 36)
        $backgroundBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.PointF]::new((& $s 30), (& $s 20)),
            [System.Drawing.PointF]::new((& $s 225), (& $s 235)),
            [System.Drawing.Color]::FromArgb(255, 24, 132, 224),
            [System.Drawing.Color]::FromArgb(255, 4, 45, 104))
        $graphics.FillPath($backgroundBrush, $backgroundPath)
        $backgroundPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(220, 2, 40, 93), (& $s 3))
        $graphics.DrawPath($backgroundPen, $backgroundPath)
        $backgroundPen.Dispose()
        $backgroundBrush.Dispose()
        $backgroundPath.Dispose()

        $cabinetPath = New-RoundedPath (& $s 53) (& $s 43) (& $s 151) (& $s 111) (& $s 10)
        $cabinetBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.PointF]::new((& $s 53), (& $s 43)),
            [System.Drawing.PointF]::new((& $s 204), (& $s 154)),
            [System.Drawing.Color]::FromArgb(255, 56, 190, 244),
            [System.Drawing.Color]::FromArgb(255, 9, 86, 174))
        $graphics.FillPath($cabinetBrush, $cabinetPath)
        $cabinetPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(235, 199, 242, 255), (& $s 2))
        $graphics.DrawPath($cabinetPen, $cabinetPath)
        $cabinetPen.Dispose()
        $cabinetBrush.Dispose()
        $cabinetPath.Dispose()

        foreach ($drawerY in 51, 83, 115) {
            $drawerPath = New-RoundedPath (& $s 60) (& $s $drawerY) (& $s 137) (& $s 26) (& $s 5)
            $drawerBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(185, 12, 88, 180))
            $graphics.FillPath($drawerBrush, $drawerPath)
            $drawerPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(170, 157, 231, 255), (& $s 1.2))
            $graphics.DrawPath($drawerPen, $drawerPath)
            $drawerBrush.Dispose()
            $drawerPen.Dispose()
            $drawerPath.Dispose()

            $labelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(240, 239, 249, 255))
            $graphics.FillRectangle($labelBrush, (& $s 106), (& $s ($drawerY + 7)), (& $s 45), (& $s 10))
            $labelBrush.Dispose()
        }

        $envelopeShadow = New-RoundedPath (& $s 37) (& $s 104) (& $s 151) (& $s 94) (& $s 9)
        $envelopeShadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(75, 0, 24, 62))
        $graphics.TranslateTransform((& $s 3), (& $s 5))
        $graphics.FillPath($envelopeShadowBrush, $envelopeShadow)
        $graphics.ResetTransform()
        $envelopeShadowBrush.Dispose()
        $envelopeShadow.Dispose()

        $envelopePath = New-RoundedPath (& $s 37) (& $s 99) (& $s 151) (& $s 94) (& $s 9)
        $envelopeBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.PointF]::new((& $s 37), (& $s 99)),
            [System.Drawing.PointF]::new((& $s 188), (& $s 193)),
            [System.Drawing.Color]::FromArgb(255, 255, 255, 255),
            [System.Drawing.Color]::FromArgb(255, 213, 232, 248))
        $graphics.FillPath($envelopeBrush, $envelopePath)
        $envelopePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 84, 145, 199), (& $s 2))
        $graphics.DrawPath($envelopePen, $envelopePath)

        $flap = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $flap.AddPolygon([System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new((& $s 39), (& $s 105)),
            [System.Drawing.PointF]::new((& $s 112), (& $s 158)),
            [System.Drawing.PointF]::new((& $s 186), (& $s 105))))
        $flapBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 247, 252, 255))
        $graphics.FillPath($flapBrush, $flap)
        $graphics.DrawPath($envelopePen, $flap)
        $flapBrush.Dispose()
        $flap.Dispose()

        $bottomPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(210, 96, 155, 205), (& $s 2))
        $graphics.DrawLine($bottomPen, (& $s 39), (& $s 190), (& $s 91), (& $s 145))
        $graphics.DrawLine($bottomPen, (& $s 186), (& $s 190), (& $s 136), (& $s 145))
        $bottomPen.Dispose()
        $envelopePen.Dispose()
        $envelopeBrush.Dispose()
        $envelopePath.Dispose()

        $lensShadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(80, 0, 17, 50))
        $graphics.FillEllipse($lensShadowBrush, (& $s 135), (& $s 126), (& $s 78), (& $s 78))
        $lensShadowBrush.Dispose()

        $handlePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 7, 54, 126), (& $s 17))
        $handlePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $handlePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawLine($handlePen, (& $s 191), (& $s 188), (& $s 226), (& $s 224))
        $handlePen.Dispose()

        $handleHighlight = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 44, 151, 230), (& $s 9))
        $handleHighlight.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $handleHighlight.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawLine($handleHighlight, (& $s 193), (& $s 186), (& $s 226), (& $s 220))
        $handleHighlight.Dispose()

        $outerLensBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 229, 243, 253))
        $graphics.FillEllipse($outerLensBrush, (& $s 127), (& $s 117), (& $s 80), (& $s 80))
        $outerLensBrush.Dispose()
        $outerLensPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 4, 45, 102), (& $s 5))
        $graphics.DrawEllipse($outerLensPen, (& $s 127), (& $s 117), (& $s 80), (& $s 80))
        $outerLensPen.Dispose()

        $lensBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.PointF]::new((& $s 139), (& $s 126)),
            [System.Drawing.PointF]::new((& $s 194), (& $s 188)),
            [System.Drawing.Color]::FromArgb(220, 159, 231, 255),
            [System.Drawing.Color]::FromArgb(230, 34, 151, 222))
        $graphics.FillEllipse($lensBrush, (& $s 136), (& $s 126), (& $s 62), (& $s 62))
        $lensBrush.Dispose()
        $lensPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(245, 26, 99, 169), (& $s 2))
        $graphics.DrawEllipse($lensPen, (& $s 136), (& $s 126), (& $s 62), (& $s 62))
        $lensPen.Dispose()

        $glintBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(175, 255, 255, 255))
        $graphics.FillEllipse($glintBrush, (& $s 149), (& $s 136), (& $s 18), (& $s 11))
        $glintBrush.Dispose()

        $memory = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            return ,$memory.ToArray()
        }
        finally {
            $memory.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$directory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($directory)) {
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
}

$sizes = @(16, 32, 48, 256)
$images = foreach ($size in $sizes) {
    [PSCustomObject]@{
        Size = $size
        Bytes = (New-IconPng -Size $size)
    }
}

$stream = [System.IO.File]::Create($OutputPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$images.Count)

    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { [byte]0 } else { [byte]$image.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$image.Bytes.Length)
        $writer.Write([UInt32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Bytes)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

Write-Host "Generated application icon: $OutputPath"
