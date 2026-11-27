[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$Configuration = "Release",

    [ValidateNotNullOrEmpty()]
    [string]$ReleaseBranch = "master",

    [switch]$SkipRepositoryChecks,

    [switch]$SkipTests,

    # Thumbprint of a code-signing certificate in Cert:\CurrentUser\My whose subject equals the
    # manifest Publisher. Signs a copy of the bundle for a local install; the upload stays unsigned
    # because the Store signs it.
    [string]$TestCertificateThumbprint,

    # Runs the Windows App Certification Kit on the bundle. Needs an elevated console.
    [switch]$RunWack,

    [switch]$ValidateConfigurationOnly
)

# Builds the Microsoft Store submission from the same sources as the GitHub release:
#   artifacts/store/v<version>/StereoKeeper_<version>_x64_arm64.msixupload  -> Partner Center, Packages
#   artifacts/store/v<version>/listing/                                     -> Partner Center, Store listings, Import
#   artifacts/store/v<version>/listingData-texts.csv                        -> the same listing without the images
# The x64 and arm64 packages hold the same single-file exe as the GitHub release, with the native
# libraries next to it instead of unpacked at run time.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "release-helpers.ps1")

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solutionPath = Join-Path $repositoryRoot "StereoKeeper.sln"
$projectPath = Join-Path $repositoryRoot "StereoKeeper\StereoKeeper.csproj"
$storeRoot = Join-Path $repositoryRoot "packaging\store"
$manifestPath = Join-Path $storeRoot "Package.appxmanifest"
$assetsDirectory = Join-Path $storeRoot "Assets"
$listingSourceDirectory = Join-Path $storeRoot "listing"
$listingSchemaPath = Join-Path $storeRoot "listing-schema.csv"
$listingTextsDirectory = Join-Path $storeRoot "texts"
$localesPath = Join-Path $PSScriptRoot "winget-locales.json"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$storeArtifactsRoot = Join-Path $artifactsRoot "store"

$architectures = [ordered]@{ "win-x64" = "x64"; "win-arm64" = "arm64" }
$manifestNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10"

# The WinGet texts give the package its localized description; the Store listing has its own,
# longer texts in packaging/store/texts.
# Partner Center has no es-419, so Latin American Spanish is listed as es-mx.
$storeLanguageOverrides = @{ "es-419" = "es-mx" }

$listingDeveloper = "Avazbek Olimov"
$listingCopyright = "© 2026 Avazbek Olimov"

# Partner Center limits for the listing texts. Its documentation gives 40 or 30 characters for a
# search term depending on the page; the stricter one is used.
$listingLimits = @{
    ShortDescription = 1000
    Description = 10000
    Features = 20
    Feature = 200
    SearchTerms = 7
    SearchTerm = 30
    SearchTermWords = 21
    Caption = 200
}

# Rows of the listing that hold images. The text-only CSV leaves them out, so importing it keeps
# the screenshots and logos already uploaded by hand.
$listingImageRows = '^((Desktop|Mobile|Xbox|Holographic|SurfaceHub)Screenshot\d+|StoreLogo.*|PromoImage.*|XboxBrandedKeyArt.*|XboxTitledHero.*|XboxFeaturedPromo.*|OptionalPromo.*)$'

function Get-StoreLanguage {
    param([string]$Locale)

    if ($storeLanguageOverrides.ContainsKey($Locale)) {
        return $storeLanguageOverrides[$Locale]
    }

    return $Locale.ToLowerInvariant()
}

function Find-WindowsSdkTool {
    param([string]$Name)

    $kitsBin = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    $candidates = @(Get-ChildItem -LiteralPath $kitsBin -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$Name" } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })

    if ($candidates.Count -eq 0) {
        throw "$Name was not found. Install the Windows SDK (winget install Microsoft.WindowsSDK.10.0.26100)."
    }

    return $candidates[0]
}

function Read-StoreTexts {
    $locales = Get-Content -Raw -LiteralPath $localesPath -Encoding UTF8 | ConvertFrom-Json
    $texts = [ordered]@{}
    foreach ($property in $locales.PSObject.Properties) {
        $texts[(Get-StoreLanguage $property.Name)] = $property.Value
    }

    return $texts
}

# The Store languages are the Resource entries of Package.appxmanifest, in its order: the package
# says which languages it supports, and the listing gets a column for each. Every one of them needs
# its texts in winget-locales.json. To list the app in another language, add its Resource line.
function Select-ManifestTexts {
    param([Collections.Specialized.OrderedDictionary]$Texts)

    [xml]$manifest = Get-Content -Raw -LiteralPath $manifestPath
    $declared = @($manifest.Package.Resources.Resource | ForEach-Object { $_.Language.ToLowerInvariant() })
    if ($declared.Count -eq 0 -or $declared[0] -ne "en-us") {
        throw "The first manifest language must be en-us; Partner Center treats it as the default."
    }

    $missing = @($declared | Where-Object { -not $Texts.Contains($_) })
    if ($missing.Count -gt 0) {
        throw "winget-locales.json has no texts for the manifest languages: $($missing -join ', ')."
    }

    $selected = [ordered]@{}
    foreach ($language in $declared) {
        $selected[$language] = $Texts[$language]
    }

    return $selected
}

# The Store listing texts, one file per manifest language: packaging/store/texts/<language>.json.
function Read-ListingTexts {
    param([string[]]$Languages)

    $listing = [ordered]@{}
    foreach ($language in $Languages) {
        $path = Join-Path $listingTextsDirectory "$language.json"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "The Store listing has no texts for ${language}: $path is missing."
        }

        $text = Get-Content -Raw -LiteralPath $path -Encoding UTF8 | ConvertFrom-Json
        Assert-ListingTexts -Language $language -Text $text
        $listing[$language] = $text
    }

    return $listing
}

function Assert-ListingTexts {
    param(
        [string]$Language,
        [object]$Text
    )

    $problems = [Collections.Generic.List[string]]::new()
    $check = {
        param([string]$Name, [object]$Value, [int]$Limit)

        if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) {
            $problems.Add("$Name is empty")
        }
        elseif ($Value -ne $Value.Trim()) {
            $problems.Add("$Name has leading or trailing spaces")
        }
        elseif ($Value.Length -gt $Limit) {
            $problems.Add("$Name has $($Value.Length) characters, the limit is $Limit")
        }
    }

    & $check "shortDescription" $Text.shortDescription $listingLimits.ShortDescription
    & $check "description" $Text.description $listingLimits.Description

    $features = @($Text.features)
    if ($features.Count -lt 1 -or $features.Count -gt $listingLimits.Features) {
        $problems.Add("features has $($features.Count) entries, the limit is $($listingLimits.Features)")
    }
    for ($i = 0; $i -lt $features.Count; $i++) {
        & $check "feature $($i + 1)" $features[$i] $listingLimits.Feature
    }

    $terms = @($Text.searchTerms)
    if ($terms.Count -lt 1 -or $terms.Count -gt $listingLimits.SearchTerms) {
        $problems.Add("searchTerms has $($terms.Count) entries, the limit is $($listingLimits.SearchTerms)")
    }
    foreach ($term in $terms) {
        & $check "search term '$term'" $term $listingLimits.SearchTerm
    }
    # Hyphenated parts count as words here, to stay within the limit however Partner Center counts.
    $words = ($terms | ForEach-Object { @(([string]$_) -split '[\s-]+' | Where-Object { $_ }).Count } | Measure-Object -Sum).Sum
    if ($words -gt $listingLimits.SearchTermWords) {
        $problems.Add("search terms have $words words, the limit is $($listingLimits.SearchTermWords)")
    }

    foreach ($caption in @($Text.screenshotCaptions)) {
        & $check "screenshot caption" $caption $listingLimits.Caption
    }

    if ($problems.Count -gt 0) {
        throw "The Store listing texts for $Language break the Partner Center limits: $($problems -join '; ')."
    }
}

function Write-ResourceStrings {
    param(
        [string]$Directory,
        [Collections.Specialized.OrderedDictionary]$Texts
    )

    foreach ($language in $Texts.Keys) {
        $folder = Join-Path $Directory "Strings\$language"
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        $description = [Security.SecurityElement]::Escape([string]$Texts[$language].ShortDescription)
        $resw = @"
<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <data name="AppDisplayName" xml:space="preserve"><value>StereoKeeper</value></data>
  <data name="AppDescription" xml:space="preserve"><value>$description</value></data>
</root>
"@
        [IO.File]::WriteAllText((Join-Path $folder "Resources.resw"), $resw, [Text.UTF8Encoding]::new($false))
    }
}

function Write-PackageManifest {
    param(
        [string]$Destination,
        [string]$Version,
        [string]$Architecture
    )

    [xml]$manifest = Get-Content -Raw -LiteralPath $manifestPath
    $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace("m", $manifestNamespace)
    $identity = $manifest.SelectSingleNode("/m:Package/m:Identity", $namespaces)
    $identity.SetAttribute("Version", $Version)
    $identity.SetAttribute("ProcessorArchitecture", $Architecture)

    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($Destination, $settings)
    try {
        $manifest.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

function New-StoreListing {
    param(
        [string]$Destination,
        [Collections.Specialized.OrderedDictionary]$Listing
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $listingSourceDirectory "StoreLogo300x300.png") -Destination $Destination

    # Each language shows the panel in its own language (New-StoreScreenshots.ps1).
    $languages = @($Listing.Keys)
    $screenshots = @{}
    foreach ($language in $languages) {
        $source = Join-Path $listingSourceDirectory "Screenshots\$language"
        $images = @(Get-ChildItem -LiteralPath $source -Filter "*.png" -ErrorAction SilentlyContinue | Sort-Object Name)
        if ($images.Count -eq 0) {
            throw "No Store screenshots for $language in $source. Run scripts\New-StoreScreenshots.ps1."
        }

        $target = Join-Path $Destination "Screenshots\$language"
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        $images | Copy-Item -Destination $target
        $screenshots[$language] = @($images | ForEach-Object { "Screenshots/$language/$($_.Name)" })
    }

    $rows = Import-Csv -LiteralPath $listingSchemaPath -Encoding UTF8
    $header = (@("Field", "ID", "Type (Type)", "default") + $languages | ForEach-Object { ConvertTo-CsvValue $_ }) -join ","
    $lines = [Collections.Generic.List[string]]::new()
    $textLines = [Collections.Generic.List[string]]::new()
    $lines.Add($header)
    $textLines.Add($header)

    foreach ($row in $rows) {
        $values = foreach ($language in $languages) {
            Get-ListingValue -Field $row.Field -Text $Listing[$language] -Screenshots $screenshots[$language]
        }

        $cells = @($row.Field, $row.ID, $row."Type (Type)", "") + @($values)
        $line = ($cells | ForEach-Object { ConvertTo-CsvValue ([string]$_) }) -join ","
        $lines.Add($line)
        if ($row.Field -notmatch $listingImageRows) {
            $textLines.Add($line)
        }
    }

    # Partner Center imports only UTF-8 without a byte order mark, with CRLF line endings.
    $encoding = [Text.UTF8Encoding]::new($false)
    $csvPath = Join-Path $Destination "listingData.csv"
    [IO.File]::WriteAllText($csvPath, ($lines -join "`r`n") + "`r`n", $encoding)

    # Partner Center often fails to import the whole folder, and a CSV imported on its own fails
    # on the image paths. This copy has no image rows at all, so it can be imported again after
    # the screenshots and logos were added by hand without touching them.
    $textCsvPath = Join-Path (Split-Path -Parent $Destination) "listingData-texts.csv"
    [IO.File]::WriteAllText($textCsvPath, ($textLines -join "`r`n") + "`r`n", $encoding)
    return $csvPath
}

function Get-ListingValue {
    param(
        [string]$Field,
        [object]$Text,
        [string[]]$Screenshots
    )

    switch -Regex ($Field) {
        '^Title$' { return "StereoKeeper" }
        '^ShortDescription$' { return ConvertTo-ListingText $Text.shortDescription }
        '^Description$' { return ConvertTo-ListingText $Text.description }
        '^Feature(\d+)$' { return Get-ListingItem $Text.features $Matches[1] }
        '^SearchTerm(\d+)$' { return Get-ListingItem $Text.searchTerms $Matches[1] }
        '^DesktopScreenshotCaption(\d+)$' { return Get-ListingItem $Text.screenshotCaptions $Matches[1] }
        '^DevStudio$' { return $listingDeveloper }
        '^CopyrightTrademarkInformation$' { return $listingCopyright }
        '^StoreLogo300x300$' { return "StoreLogo300x300.png" }
        '^DesktopScreenshot(\d+)$' {
            $index = [int]$Matches[1] - 1
            return $(if ($index -lt $Screenshots.Count) { $Screenshots[$index] } else { "" })
        }
        default { return "" }
    }
}

# Partner Center rejects bare LF line breaks anywhere in the import file, also inside a field.
function ConvertTo-ListingText {
    param([string]$Value)

    return ($Value.Trim() -replace '\r?\n', "`r`n")
}

# The n-th entry (1-based) of a list in the listing texts, or an empty cell after its end.
function Get-ListingItem {
    param(
        [object[]]$Items,
        [string]$Number
    )

    $index = [int]$Number - 1
    $list = @($Items)
    return $(if ($index -lt $list.Count) { [string]$list[$index] } else { "" })
}

function ConvertTo-CsvValue {
    param([string]$Value)

    if ($Value -match '[",\r\n]') {
        return '"' + $Value.Replace('"', '""') + '"'
    }

    return $Value
}

function Invoke-Wack {
    param(
        [string]$PackagePath,
        [string]$ReportPath
    )

    $appCert = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\App Certification Kit\appcert.exe"
    if (-not (Test-Path -LiteralPath $appCert -PathType Leaf)) {
        throw "The Windows App Certification Kit was not found: $appCert"
    }

    Invoke-NativeCommand $appCert @("reset")
    Invoke-NativeCommand $appCert @("test", "-appxpackagepath", $PackagePath, "-reportoutputpath", $ReportPath)

    [xml]$report = Get-Content -Raw -LiteralPath $ReportPath
    $result = $report.REPORT.OVERALL_RESULT
    $failed = @($report.SelectNodes("//TEST[RESULT='FAIL']") | ForEach-Object { $_.GetAttribute("NAME") })
    if ($result -ne "PASS") {
        throw "WACK result: $result. Failed tests: $($failed -join ', '). Report: $ReportPath"
    }

    # Optional tests (blocked executables, for Windows in S mode) may fail without failing the run.
    if ($failed.Count -gt 0) {
        Write-Warning "WACK optional tests failed: $($failed -join ', '). Report: $ReportPath"
    }

    Write-Host "WACK passed. Report: $ReportPath" -ForegroundColor Green
}

$versionInfo = Get-ProjectReleaseInfo -ProjectPath $projectPath
$packageVersion = $versionInfo.PackageVersion
if (-not $packageVersion.EndsWith(".0")) {
    throw "Store packages need a version that ends in .0 (the revision is reserved by the Store). Got $packageVersion."
}

$texts = Select-ManifestTexts (Read-StoreTexts)
$listing = Read-ListingTexts -Languages @($texts.Keys)

if ($ValidateConfigurationOnly) {
    Write-Host "Store configuration is valid:" -ForegroundColor Green
    Write-Host "  Package version : $packageVersion"
    Write-Host "  Languages       : $($texts.Keys -join ', ')"
    return
}

if ($RunWack) {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "-RunWack needs an elevated console: the certification kit installs and starts the package."
    }
}

if ($SkipRepositoryChecks) {
    Write-Warning "Repository branch and cleanliness checks were explicitly skipped."
}
else {
    Assert-ReleaseRepositoryState -RepositoryRoot $repositoryRoot -ReleaseBranch $ReleaseBranch
}

$makeAppx = Find-WindowsSdkTool "makeappx.exe"
$makePri = Find-WindowsSdkTool "makepri.exe"

$outputDirectory = Join-Path $storeArtifactsRoot $versionInfo.ReleaseTag
$stagingDirectory = Join-Path $storeArtifactsRoot ".staging"
if (-not $outputDirectory.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Store output must remain inside '$artifactsRoot'."
}

foreach ($directory in @($outputDirectory, $stagingDirectory)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}

$packagesDirectory = Join-Path $stagingDirectory "packages"
New-Item -ItemType Directory -Path $outputDirectory, $packagesDirectory -Force | Out-Null

if (-not $SkipTests) {
    Invoke-NativeCommand "dotnet" @("test", $solutionPath, "--configuration", $Configuration, "--nologo")
}

$priConfig = Join-Path $stagingDirectory "priconfig.xml"
Invoke-NativeCommand $makePri @("createconfig", "/cf", $priConfig, "/dq", "en-US", "/pv", "10.0.0", "/o")

# One resources.pri per package: the default config splits languages and scales into resource
# packs, which only a Visual Studio bundle build produces.
[xml]$priConfigXml = Get-Content -Raw -LiteralPath $priConfig
$packagingNode = $priConfigXml.resources.SelectSingleNode("packaging")
if ($null -ne $packagingNode) {
    [void]$priConfigXml.resources.RemoveChild($packagingNode)
    $priConfigXml.Save($priConfig)
}

foreach ($runtimeIdentifier in $architectures.Keys) {
    $architecture = $architectures[$runtimeIdentifier]
    $publishDirectory = Join-Path $stagingDirectory "publish-$architecture"
    $layoutDirectory = Join-Path $stagingDirectory "layout-$architecture"

    Invoke-NativeCommand "dotnet" @(
        "publish",
        $projectPath,
        "--configuration", $Configuration,
        "--runtime", $runtimeIdentifier,
        "--self-contained", "true",
        "--output", $publishDirectory,
        "--nologo",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=false",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-p:ContinuousIntegrationBuild=true"
    )

    # Only the exe and its native libraries go into the package. Anything else the publish starts
    # producing one day (a config file, a content folder) would be silently left out, so stop.
    # The native libraries ship their own .pdb files despite DebugType=None; those stay out.
    $published = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Where-Object { $_.Extension -ne ".pdb" })
    $unexpected = @($published | Where-Object { $_.DirectoryName -ne $publishDirectory -or $_.Extension -notin @(".exe", ".dll") })
    if ($unexpected.Count -gt 0) {
        throw "The $architecture publish has files the package does not take: $($unexpected.FullName -join ', ')."
    }

    New-Item -ItemType Directory -Path $layoutDirectory -Force | Out-Null
    $published | Copy-Item -Destination $layoutDirectory

    $executable = Get-Item -LiteralPath (Join-Path $layoutDirectory "StereoKeeper.exe")
    if ($executable.VersionInfo.FileVersion -ne $packageVersion) {
        throw "Unexpected FileVersion in the $architecture exe: $($executable.VersionInfo.FileVersion)."
    }

    Copy-Item -LiteralPath $assetsDirectory -Destination (Join-Path $layoutDirectory "Assets") -Recurse
    Write-PackageManifest -Destination (Join-Path $layoutDirectory "AppxManifest.xml") -Version $packageVersion -Architecture $architecture

    # resources.pri carries the localized name and description and maps the logo variants.
    Write-ResourceStrings -Directory $layoutDirectory -Texts $texts
    Invoke-NativeCommand $makePri @(
        "new",
        "/pr", $layoutDirectory,
        "/cf", $priConfig,
        "/mn", (Join-Path $layoutDirectory "AppxManifest.xml"),
        "/of", (Join-Path $layoutDirectory "resources.pri"),
        "/o")
    Remove-Item -LiteralPath (Join-Path $layoutDirectory "Strings") -Recurse -Force

    $packagePath = Join-Path $packagesDirectory "StereoKeeper_$($packageVersion)_$architecture.msix"
    Invoke-NativeCommand $makeAppx @("pack", "/o", "/h", "SHA256", "/d", $layoutDirectory, "/p", $packagePath)
}

$bundleName = "StereoKeeper_$($packageVersion)_x64_arm64"
$bundlePath = Join-Path $outputDirectory "$bundleName.msixbundle"
Invoke-NativeCommand $makeAppx @("bundle", "/o", "/bv", $packageVersion, "/d", $packagesDirectory, "/p", $bundlePath)

# An .msixupload is a zip that holds the bundle; Partner Center takes it as is.
$uploadPath = Join-Path $outputDirectory "$bundleName.msixupload"
$zipPath = [IO.Path]::ChangeExtension($uploadPath, ".zip")
Compress-Archive -LiteralPath $bundlePath -DestinationPath $zipPath -CompressionLevel Optimal -Force
Move-Item -LiteralPath $zipPath -Destination $uploadPath -Force

$listingCsv = New-StoreListing -Destination (Join-Path $outputDirectory "listing") -Listing $listing

if ($TestCertificateThumbprint) {
    $signTool = Find-WindowsSdkTool "signtool.exe"
    $localDirectory = Join-Path $outputDirectory "local-test"
    New-Item -ItemType Directory -Path $localDirectory -Force | Out-Null
    $signedBundle = Join-Path $localDirectory "$bundleName.msixbundle"
    Copy-Item -LiteralPath $bundlePath -Destination $signedBundle
    Invoke-NativeCommand $signTool @("sign", "/fd", "SHA256", "/sha1", $TestCertificateThumbprint, $signedBundle)
    Write-Host "Signed for a local install: $signedBundle"
}

if ($RunWack) {
    Invoke-Wack -PackagePath $bundlePath -ReportPath (Join-Path $outputDirectory "wack-report.xml")
}

Remove-Item -LiteralPath $stagingDirectory -Recurse -Force

Write-Host
Write-Host "Store submission $($versionInfo.ReleaseTag) is ready:" -ForegroundColor Green
Write-Host "  $uploadPath ($([Math]::Round((Get-Item -LiteralPath $uploadPath).Length / 1MB, 2)) MiB)"
Write-Host "  $listingCsv ($($texts.Keys -join ', '))"
