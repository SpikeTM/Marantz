Add-Type -AssemblyName System.Drawing

# -------------------------------------------------------
# Draws the AVR Desktop Control icon at multiple sizes
# and saves a multi-resolution .ico file.
# Design: dark rounded square, gold gradient knob ring,
#         dark inner circle with indicator line, "AVR" text
# -------------------------------------------------------

function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$Size

    # --- Rounded-square background ---
    $cr = [int]($s * 0.18)
    $bgp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bgp.AddArc(0, 0, $cr * 2, $cr * 2, 180, 90)
    $bgp.AddArc($Size - $cr * 2, 0, $cr * 2, $cr * 2, 270, 90)
    $bgp.AddArc($Size - $cr * 2, $Size - $cr * 2, $cr * 2, $cr * 2, 0, 90)
    $bgp.AddArc(0, $Size - $cr * 2, $cr * 2, $cr * 2, 90, 90)
    $bgp.CloseFigure()

    $bgBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 21, 23, 26))
    $g.FillPath($bgBrush, $bgp)

    # --- Knob geometry ---
    $cx = $s / 2.0
    $cy = $s * 0.41
    $outerR = $s * 0.29
    $innerR = $s * 0.22

    # Outer gold ring (path-gradient gives a sphere-like sheen)
    $kRect = New-Object System.Drawing.RectangleF(($cx - $outerR), ($cy - $outerR), ($outerR * 2), ($outerR * 2))
    $kPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $kPath.AddEllipse($kRect)

    $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($kPath)
    $pgb.CenterPoint = New-Object System.Drawing.PointF(($cx - $outerR * 0.28), ($cy - $outerR * 0.28))
    $pgb.CenterColor = [System.Drawing.Color]::FromArgb(255, 241, 231, 208)   # #F1E7D0 warm highlight
    $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 74, 60, 40))   # dark bronze edge
    $g.FillEllipse($pgb, $kRect)

    # Thin rim accent
    $rimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80, 255, 240, 200), [float]([Math]::Max(1, $s * 0.005)))
    $g.DrawEllipse($rimPen, $kRect)

    # Inner dark circle with subtle linear gradient
    $iRect = New-Object System.Drawing.RectangleF(($cx - $innerR), ($cy - $innerR), ($innerR * 2), ($innerR * 2))
    $innerLG = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $iRect,
        [System.Drawing.Color]::FromArgb(255, 42, 45, 51),
        [System.Drawing.Color]::FromArgb(255, 19, 21, 24),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillEllipse($innerLG, $iRect)

    # Indicator line (pointing straight up from center)
    $lineW = [float]([Math]::Max(1.5, $s * 0.014))
    $indPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(230, 216, 198, 162), $lineW)
    $indPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $indPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($indPen, $cx, $cy, $cx, ($cy - $innerR + $s * 0.025))

    # --- "AVR" label (only at 48px and above) ---
    if ($Size -ge 48) {
        $fontSize = [float]([Math]::Max(8, $s * 0.145))
        $font = $null
        foreach ($fname in @("Palatino Linotype", "Georgia", "Times New Roman", "serif")) {
            try {
                $font = New-Object System.Drawing.Font($fname, $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
                break
            }
            catch { }
        }
        if ($null -eq $font) {
            $font = New-Object System.Drawing.Font([System.Drawing.FontFamily]::GenericSerif, $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        }

        $textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 216, 198, 162))
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = [System.Drawing.StringAlignment]::Center

        $textY = $s * 0.795
        $textRect = New-Object System.Drawing.RectangleF(0, $textY, $s, $fontSize * 1.3)

        # Drop shadow
        if ($Size -ge 64) {
            $shadowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(110, 0, 0, 0))
            $shadowRect = New-Object System.Drawing.RectangleF(1, ($textY + 1), $s, $fontSize * 1.3)
            $g.DrawString("AVR", $font, $shadowBrush, $shadowRect, $fmt)
            $shadowBrush.Dispose()
        }
        $g.DrawString("AVR", $font, $textBrush, $textRect, $fmt)

        $font.Dispose()
        $textBrush.Dispose()
        $fmt.Dispose()
    }

    # Clean up
    $g.Dispose()
    $bgBrush.Dispose()
    $pgb.Dispose()
    $innerLG.Dispose()
    $indPen.Dispose()
    $rimPen.Dispose()
    $kPath.Dispose()
    $bgp.Dispose()

    return $bmp
}

# -------------------------------------------------------
# Build a multi-size .ico from an array of Bitmaps.
# Modern Windows ICO embeds PNG chunks per image.
# -------------------------------------------------------
function ConvertTo-Ico {
    param([System.Drawing.Bitmap[]]$Bitmaps, [string]$OutputPath)

    $pngs = foreach ($b in $Bitmaps) {
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
        $ms.Dispose()
    }

    $count = $Bitmaps.Count
    $dataOffset = 6 + 16 * $count   # ICONDIR + N * ICONDIRENTRY

    $stream = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($stream)

    # ICONDIR
    $w.Write([uint16]0)       # reserved
    $w.Write([uint16]1)       # type = icon
    $w.Write([uint16]$count)

    # ICONDIRENTRY for each image
    $offset = $dataOffset
    for ($i = 0; $i -lt $count; $i++) {
        $dim = $Bitmaps[$i].Width
        $w.Write([byte]$(if ($dim -eq 256) { 0 } else { $dim }))  # width  (0 = 256)
        $w.Write([byte]$(if ($dim -eq 256) { 0 } else { $dim }))  # height (0 = 256)
        $w.Write([byte]0)              # color count (0 = true color)
        $w.Write([byte]0)              # reserved
        $w.Write([uint16]1)            # planes
        $w.Write([uint16]32)           # bits per pixel
        $w.Write([uint32]$pngs[$i].Length)
        $w.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }

    # PNG image data
    foreach ($png in $pngs) { $w.Write($png) }

    $w.Flush()
    [System.IO.File]::WriteAllBytes($OutputPath, $stream.ToArray())
    $w.Dispose()
    $stream.Dispose()
}

# -------------------------------------------------------
# Main
# -------------------------------------------------------
$scriptDir = $PSScriptRoot
$resDir = Join-Path $scriptDir "MarantzDesktopControl\Resources"
New-Item -ItemType Directory -Force -Path $resDir | Out-Null

$sizes = @(16, 32, 48, 64, 128, 256)
$bitmaps = @()

foreach ($sz in $sizes) {
    Write-Host "  Drawing ${sz}x${sz}..."
    $bitmaps += New-IconBitmap -Size $sz
}

$icoPath = Join-Path $resDir "app.ico"
Write-Host "  Assembling ICO -> $icoPath"
ConvertTo-Ico -Bitmaps $bitmaps -OutputPath $icoPath

foreach ($b in $bitmaps) { $b.Dispose() }

Write-Host ""
Write-Host "Done!  Icon written to: $icoPath" -ForegroundColor Green
