[CmdletBinding()]
param(
    # Interface language codes (en-US, ja-JP, ...) to draw; all 30 when left out.
    [string[]]$Languages,

    [string]$OutputDirectory
)

# Draws the Microsoft Store screenshots in every interface language: the real panel, rendered
# off-screen by tools/StoreScreenshots with the README's demo data (Sony headphones; Discord,
# Chrome and Teams on the microphone). Each language gets the two images of the README:
#   packaging/store/listing/Screenshots/<store language>/01-panel.png    call mode, stereo back, Always stereo
#   packaging/store/listing/Screenshots/<store language>/02-screens.png  progress, settings, help
# Rerun after a change to the panel or its texts, look through the images and commit them.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "release-helpers.ps1")

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repositoryRoot "tools\StoreScreenshots\StoreScreenshots.csproj"
$buildDirectory = Join-Path $repositoryRoot "artifacts\.store-screenshots"
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot "packaging\store\listing\Screenshots"
}

if (Test-Path -LiteralPath $buildDirectory) {
    Remove-Item -LiteralPath $buildDirectory -Recurse -Force
}

Invoke-NativeCommand "dotnet" @("build", $projectPath, "--configuration", "Release", "--output", $buildDirectory, "--nologo", "-v:q")

# A full run replaces every language, so a language dropped from the app leaves no stale images.
if (-not $Languages -and (Test-Path -LiteralPath $OutputDirectory)) {
    Get-ChildItem -LiteralPath $OutputDirectory | Remove-Item -Recurse -Force
}

# One process per language: the text renderer remembers which font it fell back to for a
# character, so after Chinese the Japanese kana would come out in a Chinese font.
if (-not $Languages) {
    $Languages = @(Select-String -LiteralPath (Join-Path $repositoryRoot "StereoKeeper\Localization\AppLanguageCatalog.cs") `
        -Pattern 'new\(AppLanguage\.\w+, "([^"]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value })
}

# "-Languages ja-JP,ko-KR" arrives as one string from "pwsh -File".
$Languages = @($Languages | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($language in $Languages) {
    Invoke-NativeCommand (Join-Path $buildDirectory "StoreScreenshots.exe") @($OutputDirectory, $language)
}
Remove-Item -LiteralPath $buildDirectory -Recurse -Force

$images = @(Get-ChildItem -LiteralPath $OutputDirectory -Filter "*.png" -Recurse)
$size = ($images | Measure-Object -Property Length -Sum).Sum
Write-Host "Wrote $($images.Count) screenshots ($([Math]::Round($size / 1MB, 1)) MiB) to $OutputDirectory" -ForegroundColor Green
