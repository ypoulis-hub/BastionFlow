#requires -Version 7
<#
.SYNOPSIS
    Renders the BastionFlow logo (rounded orange square + two white chevrons)
    to a multi-resolution .ico file embedded in the BastionFlow.App .exe.
.DESCRIPTION
    Uses WPF to draw the same geometry that App.xaml's AppLogo DrawingImage
    uses, at sizes 16/24/32/48/64/128/256, and packs them into a Windows ICO
    file with PNG-encoded entries (Vista+ format).

    Run after any logo change. The generated app.ico is referenced by
    src/BastionFlow.App/BastionFlow.App.csproj via <ApplicationIcon>.
#>
[CmdletBinding()]
param(
    [string]$OutPath = (Join-Path $PSScriptRoot '..\src\BastionFlow.App\app.ico'),
    [string]$BgColor = '#F97316'   # orange (matches App.xaml)
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

function New-LogoDrawing([string]$Color) {
    # Canvas: 32x32 logical pixels, will scale to any output size.
    $dg = New-Object System.Windows.Media.DrawingGroup

    # Outer rounded square
    $rectGeom = New-Object System.Windows.Media.RectangleGeometry (
        (New-Object System.Windows.Rect 0, 0, 32, 32), 7, 7)
    $brush = (New-Object System.Windows.Media.BrushConverter).ConvertFromString($Color)
    $dg.Children.Add((New-Object System.Windows.Media.GeometryDrawing $brush, $null, $rectGeom))

    # Subtle top-highlight gradient
    $gradient = New-Object System.Windows.Media.LinearGradientBrush
    $gradient.StartPoint = New-Object System.Windows.Point 0, 0
    $gradient.EndPoint   = New-Object System.Windows.Point 0, 1
    $gradient.GradientStops.Add(
        (New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromArgb(0x33, 0xFF, 0xFF, 0xFF)), 0))
    $gradient.GradientStops.Add(
        (New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromArgb(0x00, 0x00, 0x00, 0x00)), 0.55))
    $dg.Children.Add((New-Object System.Windows.Media.GeometryDrawing $gradient, $null, $rectGeom))

    # Two right-pointing chevrons (>>) in white — same path data as App.xaml.
    $white = [System.Windows.Media.Brushes]::White
    $chevron1 = [System.Windows.Media.Geometry]::Parse('M9,9 L16,16 L9,23 L7,21 L12,16 L7,11 Z')
    $chevron2 = [System.Windows.Media.Geometry]::Parse('M17,9 L24,16 L17,23 L15,21 L20,16 L15,11 Z')
    $dg.Children.Add((New-Object System.Windows.Media.GeometryDrawing $white, $null, $chevron1))
    $dg.Children.Add((New-Object System.Windows.Media.GeometryDrawing $white, $null, $chevron2))

    return $dg
}

function Render-LogoToPng([System.Windows.Media.DrawingGroup]$dg, [int]$size) {
    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap (
        $size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $dv = New-Object System.Windows.Media.DrawingVisual
    $ctx = $dv.RenderOpen()
    $ctx.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size / 32.0), ($size / 32.0)))
    $ctx.DrawDrawing($dg)
    $ctx.Pop()
    $ctx.Close()
    $rtb.Render($dv)

    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms)
    return ,$ms.ToArray()
}

function Write-IcoFile([string]$Path, [hashtable]$PngsBySize) {
    # ICO format (Vista+, supporting PNG-encoded entries):
    #   ICONDIR (6 bytes): Reserved(2)=0, Type(2)=1, Count(2)=N
    #   ICONDIRENTRY (16 bytes) x N
    #   PNG data x N
    $sizes = $PngsBySize.Keys | Sort-Object
    $count = $sizes.Count
    $headerSize = 6 + ($count * 16)

    $fs = [System.IO.File]::Open($Path, 'Create')
    try {
        $bw = New-Object System.IO.BinaryWriter $fs
        $bw.Write([UInt16]0)        # Reserved
        $bw.Write([UInt16]1)        # Type = 1 (icon)
        $bw.Write([UInt16]$count)   # Count

        $offset = $headerSize
        foreach ($size in $sizes) {
            $png = $PngsBySize[$size]
            # Width / Height byte: 0 means 256.
            $dim = if ($size -ge 256) { 0 } else { $size }
            $bw.Write([Byte]$dim)
            $bw.Write([Byte]$dim)
            $bw.Write([Byte]0)         # ColorCount (0 for >= 256 colors)
            $bw.Write([Byte]0)         # Reserved
            $bw.Write([UInt16]1)       # Planes
            $bw.Write([UInt16]32)      # BitsPerPixel
            $bw.Write([UInt32]$png.Length)
            $bw.Write([UInt32]$offset)
            $offset += $png.Length
        }
        foreach ($size in $sizes) {
            $bw.Write($PngsBySize[$size])
        }
        $bw.Flush()
    } finally {
        $fs.Dispose()
    }
}

Write-Host "Rendering BastionFlow logo..." -ForegroundColor Cyan
$dg = New-LogoDrawing -Color $BgColor

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngsBySize = @{}
foreach ($s in $sizes) {
    $pngsBySize[$s] = Render-LogoToPng -dg $dg -size $s
    Write-Host "  ${s}x${s}: $($pngsBySize[$s].Length) bytes"
}

$OutPath = [System.IO.Path]::GetFullPath($OutPath)
$dir = [System.IO.Path]::GetDirectoryName($OutPath)
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

Write-Host "Writing $OutPath ..." -ForegroundColor Cyan
Write-IcoFile -Path $OutPath -PngsBySize $pngsBySize

$icoSize = (Get-Item $OutPath).Length
Write-Host "Done. ICO size: $([math]::Round($icoSize / 1KB, 1)) KB ($($sizes.Count) frames)" -ForegroundColor Green
