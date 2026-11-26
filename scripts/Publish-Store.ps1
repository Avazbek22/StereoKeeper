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
$localesPath = Join-Path $PSScriptRoot "winget-locales.json"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$storeArtifactsRoot = Join-Path $artifactsRoot "store"

$architectures = [ordered]@{ "win-x64" = "x64"; "win-arm64" = "arm64" }
$manifestNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10"

# Listing languages are the package languages; the WinGet texts are the source for both.
# Partner Center has no es-419, so Latin American Spanish is listed as es-mx.
$storeLanguageOverrides = @{ "es-419" = "es-mx" }

$listingDeveloper = "Avazbek Olimov"
$listingCopyright = "© 2026 Avazbek Olimov"
$maxSearchTerms = 7

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

function Assert-ManifestLanguages {
    param([Collections.Specialized.OrderedDictionary]$Texts)

    [xml]$manifest = Get-Content -Raw -LiteralPath $manifestPath
    $declared = @($manifest.Package.Resources.Resource | ForEach-Object { $_.Language.ToLowerInvariant() })
    $expected = @($Texts.Keys)

    $missing = @($expected | Where-Object { $declared -notcontains $_ })
    $extra = @($declared | Where-Object { $expected -notcontains $_ })
    if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
        throw "Package.appxmanifest languages differ from winget-locales.json. Missing: $($missing -join ', '). Extra: $($extra -join ', ')."
    }

    if ($declared[0] -ne "en-us") {
        throw "The first manifest language must be en-us; Partner Center treats it as the default."
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
        [Collections.Specialized.OrderedDictionary]$Texts
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $listingSourceDirectory "StoreLogo300x300.png") -Destination $Destination
    Copy-Item -LiteralPath (Join-Path $listingSourceDirectory "Screenshots") -Destination $Destination -Recurse
    $screenshots = @(Get-ChildItem -LiteralPath (Join-Path $Destination "Screenshots") -Filter "*.png" | Sort-Object Name)

    $languages = @($Texts.Keys)
    $rows = Import-Csv -LiteralPath $listingSchemaPath -Encoding UTF8
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add((@("Field", "ID", "Type (Type)", "default") + $languages | ForEach-Object { ConvertTo-CsvValue $_ }) -join ",")

    foreach ($row in $rows) {
        $values = foreach ($language in $languages) {
            Get-ListingValue -Field $row.Field -Text $Texts[$language] -Screenshots $screenshots
        }

        $cells = @($row.Field, $row.ID, $row."Type (Type)", "") + @($values)
        $lines.Add(($cells | ForEach-Object { ConvertTo-CsvValue ([string]$_) }) -join ",")
    }

    # Partner Center imports only UTF-8 without a byte order mark, with CRLF line endings.
    $csvPath = Join-Path $Destination "listingData.csv"
    [IO.File]::WriteAllText($csvPath, ($lines -join "`r`n") + "`r`n", [Text.UTF8Encoding]::new($false))
    return $csvPath
}

function Get-ListingValue {
    param(
        [string]$Field,
        [object]$Text,
        [IO.FileInfo[]]$Screenshots
    )

    switch -Regex ($Field) {
        '^Title$' { return "StereoKeeper" }
        '^ShortDescription$' { return ([string]$Text.ShortDescription).Trim() }
        '^Description$' { return ([string]$Text.Description).Trim() }
        '^DevStudio$' { return $listingDeveloper }
        '^CopyrightTrademarkInformation$' { return $listingCopyright }
        '^StoreLogo300x300$' { return "StoreLogo300x300.png" }
        '^DesktopScreenshot(\d+)$' {
            $index = [int]$Matches[1] - 1
            return $(if ($index -lt $Screenshots.Count) { "Screenshots/$($Screenshots[$index].Name)" } else { "" })
        }
        '^SearchTerm(\d+)$' {
            $index = [int]$Matches[1] - 1
            $terms = @(Get-SearchTerms $Text)
            return $(if ($index -lt $terms.Count) { $terms[$index] } else { "" })
        }
        default { return "" }
    }
}

function Get-SearchTerms {
    param([object]$Text)

    # Partner Center allows 7 terms of up to 40 characters and 21 words in total.
    $terms = [Collections.Generic.List[string]]::new()
    $words = 0
    foreach ($tag in @($Text.Tags)) {
        $term = ([string]$tag).Replace("-", " ").Trim()
        $termWords = @($term -split '\s+').Count
        if ($terms.Count -ge $maxSearchTerms -or $term.Length -gt 40 -or $words + $termWords -gt 21) {
            continue
        }

        $terms.Add($term)
        $words += $termWords
    }

    return $terms
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
    $failed = @($report.SelectNodes("//TEST[RESULT='FAIL']") | ForEach-Object { $_.Name })
    if ($result -ne "PASS") {
        throw "WACK result: $result. Failed tests: $($failed -join ', '). Report: $ReportPath"
    }

    Write-Host "WACK passed. Report: $ReportPath" -ForegroundColor Green
}

$versionInfo = Get-ProjectReleaseInfo -ProjectPath $projectPath
$packageVersion = $versionInfo.PackageVersion
if (-not $packageVersion.EndsWith(".0")) {
    throw "Store packages need a version that ends in .0 (the revision is reserved by the Store). Got $packageVersion."
}

$texts = Read-StoreTexts
Assert-ManifestLanguages $texts

if ($ValidateConfigurationOnly) {
    Write-Host "Store configuration is valid:" -ForegroundColor Green
    Write-Host "  Package version : $packageVersion"
    Write-Host "  Languages       : $($texts.Count)"
    return
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

    New-Item -ItemType Directory -Path $layoutDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $publishDirectory -File |
        Where-Object { $_.Extension -in @(".exe", ".dll") } |
        Copy-Item -Destination $layoutDirectory

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

$listingCsv = New-StoreListing -Destination (Join-Path $outputDirectory "listing") -Texts $texts

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
Write-Host "  $listingCsv ($($texts.Count) languages)"
