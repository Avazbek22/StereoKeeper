[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$ListingDirectory
)

# Draws the Microsoft Store images from the app icon and the README screenshots:
# - packaging/store/Assets: the package logos. Small sizes come straight from the hand-drawn
#   frames of AppIcon.ico, larger ones are scaled down from AppIcon.png, so the Start menu, the
#   taskbar and the Store show the same logo as the exe.
# - packaging/store/listing: the 300 x 300 Store logo and the screenshots for Partner Center.
#   The Store wants desktop screenshots of at least 1366 x 768, so each README screenshot is
#   centered on a canvas of that size instead of being scaled up.
# Rerun after the icon or the screenshots change and commit the result.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$iconPath = Join-Path $repositoryRoot "StereoKeeper\Assets\AppIcon.ico"
$pngPath = Join-Path $repositoryRoot "StereoKeeper\Assets\AppIcon.png"
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot "packaging\store\Assets"
}
if (-not $ListingDirectory) {
    $ListingDirectory = Join-Path $repositoryRoot "packaging\store\listing"
}

$screenshots = [ordered]@{
    "01-panel.png" = Join-Path $repositoryRoot ".github\assets\app-panel.png"
    "02-screens.png" = Join-Path $repositoryRoot ".github\assets\app-screens.png"
}
$screenshotSize = [Drawing.Size]::new(1366, 768)
$screenshotBackground = [Drawing.Color]::FromArgb(0xE6, 0xEA, 0xEF)

$scales = @(100, 125, 150, 200, 400)
$targetSizes = @(16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256)

function Read-IconFrames {
    param([string]$Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    $count = [BitConverter]::ToUInt16($bytes, 4)
    $frames = @{}
    for ($index = 0; $index -lt $count; $index++) {
        $entry = 6 + 16 * $index
        $size = if ($bytes[$entry] -eq 0) { 256 } else { [int]$bytes[$entry] }
        $length = [BitConverter]::ToInt32($bytes, $entry + 8)
        $offset = [BitConverter]::ToInt32($bytes, $entry + 12)
        $stream = [IO.MemoryStream]::new($bytes, $offset, $length)
        $frames[$size] = [Drawing.Bitmap]::new($stream)
    }

    return $frames
}

function Get-Source {
    param([int]$Pixels)

    # The smallest drawn frame that is at least as large, so nothing is ever scaled up.
    $frame = $script:frames.Keys | Where-Object { $_ -ge $Pixels } | Sort-Object | Select-Object -First 1
    if ($null -ne $frame -and $frame -le 256) {
        return $script:frames[$frame]
    }

    return $script:master
}

# 125% of 50 px is 63 px in the Windows asset tables, not banker's-rounded 62.
function Get-ScaledSize {
    param([int]$Pixels, [int]$Scale)

    return [int][Math]::Round($Pixels * $Scale / 100, [MidpointRounding]::AwayFromZero)
}

function Save-Image {
    param(
        [string]$Name,
        [int]$Width,
        [int]$Height,
        [double]$LogoShare = 1.0,
        [string]$Directory = $OutputDirectory
    )

    $logo = [int][Math]::Round([Math]::Min($Width, $Height) * $LogoShare)
    $source = Get-Source $logo
    $bitmap = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality

        $x = [int](($Width - $logo) / 2)
        $y = [int](($Height - $logo) / 2)
        if ($source.Width -eq $logo) {
            $graphics.DrawImageUnscaled($source, $x, $y)
        }
        else {
            $attributes = [Drawing.Imaging.ImageAttributes]::new()
            $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $graphics.DrawImage(
                $source,
                [Drawing.Rectangle]::new($x, $y, $logo, $logo),
                0, 0, $source.Width, $source.Height,
                [Drawing.GraphicsUnit]::Pixel,
                $attributes)
            $attributes.Dispose()
        }

        $bitmap.Save((Join-Path $Directory $Name), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Save-Screenshot {
    param(
        [string]$Name,
        [string]$SourcePath
    )

    $source = [Drawing.Bitmap]::new($SourcePath)
    $bitmap = [Drawing.Bitmap]::new($screenshotSize.Width, $screenshotSize.Height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        if ($source.Width -gt $screenshotSize.Width -or $source.Height -gt $screenshotSize.Height) {
            throw "Screenshot '$SourcePath' is larger than $($screenshotSize.Width) x $($screenshotSize.Height)."
        }

        $graphics.Clear($screenshotBackground)
        $graphics.DrawImageUnscaled(
            $source,
            [int](($screenshotSize.Width - $source.Width) / 2),
            [int](($screenshotSize.Height - $source.Height) / 2))
        $bitmap.Save((Join-Path $script:screenshotDirectory $Name), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
        $source.Dispose()
    }
}

function Reset-Directory {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Get-ChildItem -LiteralPath $Path -Filter "*.png" | Remove-Item -Force
    }
    else {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
}

$frames = Read-IconFrames $iconPath
$master = [Drawing.Bitmap]::new($pngPath)

$screenshotDirectory = Join-Path $ListingDirectory "Screenshots"
Reset-Directory $OutputDirectory
Reset-Directory $ListingDirectory
Reset-Directory $screenshotDirectory

try {
    foreach ($scale in $scales) {
        $small = Get-ScaledSize 44 $scale
        $medium = Get-ScaledSize 150 $scale
        $store = Get-ScaledSize 50 $scale
        Save-Image "Square44x44Logo.scale-$scale.png" $small $small
        # At 400% the tile is 600 px and its PNG passes the Store's 200 KB limit; Windows scales
        # the 200% tile up on the rare 400% display instead.
        if ($scale -le 200) {
            Save-Image "Square150x150Logo.scale-$scale.png" $medium $medium 0.8
        }
        Save-Image "StoreLogo.scale-$scale.png" $store $store 0.9
    }

    # Taskbar, Start and the title bar pick these by pixel size; the logo has its own colors, so
    # the plated and unplated variants are the same image.
    foreach ($size in $targetSizes) {
        foreach ($form in @("", "_altform-unplated", "_altform-lightunplated")) {
            Save-Image "Square44x44Logo.targetsize-$size$form.png" $size $size
        }
    }

    Save-Image "StoreLogo300x300.png" 300 300 -Directory $ListingDirectory
    foreach ($name in $screenshots.Keys) {
        Save-Screenshot $name $screenshots[$name]
    }
}
finally {
    $master.Dispose()
    foreach ($frame in $frames.Values) {
        $frame.Dispose()
    }
}

$packageImages = @(Get-ChildItem -LiteralPath $OutputDirectory -Filter "*.png")
$tooLarge = @($packageImages | Where-Object { $_.Length -gt 200KB })
if ($tooLarge.Count -gt 0) {
    throw "The Store rejects package images over 200 KB: $($tooLarge.Name -join ', ')."
}
$listingImages = @(Get-ChildItem -LiteralPath $ListingDirectory -Filter "*.png" -Recurse)
Write-Host "Wrote $($packageImages.Count) package images to $OutputDirectory" -ForegroundColor Green
Write-Host "Wrote $($listingImages.Count) listing images to $ListingDirectory" -ForegroundColor Green
