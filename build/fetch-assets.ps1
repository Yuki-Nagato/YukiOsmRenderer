#!/usr/bin/env pwsh
# Downloads and assembles the platform independent assets of YukiOsmRenderer into artifacts/assets:
# OpenStreetMap Carto, the carto compiler, fonts, the preview page with Leaflet and the external data dump.
# Requires PowerShell 7. The carto compiler must have been installed with `npm ci` in assets/carto-compiler.

[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot '..' 'artifacts' 'assets'),
    [string]$Cache = (Join-Path $PSScriptRoot '..' 'artifacts' 'cache' 'assets'),
    # Skip the external data (water polygons etc.), e.g. for quick local builds that only need the style.
    [switch]$SkipExternalData
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Output = [IO.Path]::GetFullPath($Output)
$Cache = [IO.Path]::GetFullPath($Cache)

$cartoVersion = 'v6.1.0'
$cartoSha256 = '691aa2fc00a2986a9b1b9a41ed278b62b0bd5a1f535e4774cc92abba6d60cba2'
$leafletVersion = '1.9.4'
$leafletSha256 = 'aaec1d5c3239a613a53e996087629aca1483cb2f0438b11b8a335c6cede4c16b'
$planetilerVersion = '0.10.2'
$planetilerSha256 = 'f310bd0413e2e4512b27f4046d418664e8e1d3bf31603c2a70e23de06c167e4d'
$shortbreadSchemaSha256 = '711fb88522c98e2ccfc49e5aadc99bc95d80a9d8b8e16a2603a90eac13441392'
$versatilesStyleVersion = '8.0.0'
$versatilesStyleSha256 = '3a898ff7dde45073de9a4491152a704ea96b9ea2d0f7bab34afe73579582f348'
$versatilesSpritesSha256 = 'eacafb4afc345b879200779a472eca476cc3a1caf4a525c25e2a093f173ba898'
$versatilesFontsVersion = '3.0.0'
$versatilesNotoSansSha256 = 'f58be6be2eb3d127b6ff1c90d828b69ff4807d1d88cd13a43930943fda0e334e'
# Themes of the vector engine; the first is the default.
$vectorThemes = @('classic', 'colorful', 'colorful-dark', 'natural', 'muted', 'gray', 'gray-dark', 'toner')

# Downloads the first working URL (mirrors are tried in order) into the cache and verifies the checksum.
function Get-CachedFile([string[]]$Urls, [string]$Name, [string]$Sha256) {
    $path = Join-Path $Cache 'downloads' $Name
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
        foreach ($url in $Urls) {
            Write-Host "Downloading $url"
            & curl -fsSL --retry 3 --retry-delay 5 -A 'YukiOsmRenderer' -o "$path.tmp" $url
            if ($LASTEXITCODE -eq 0) {
                Move-Item -Force "$path.tmp" $path
                break
            }
            Write-Warning "Download of $url failed."
        }
        if (-not (Test-Path $path)) {
            throw "Could not download $Name from any of: $($Urls -join ', ')"
        }
    }
    if ($Sha256) {
        $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $Sha256) {
            Remove-Item $path
            throw "Checksum mismatch for $Name (expected $Sha256, got $actual)."
        }
    }
    return $path
}

function Reset-Directory([string]$Path) {
    if (Test-Path $Path) {
        Remove-Item -Recurse -Force $Path
    }
    New-Item -ItemType Directory -Force $Path | Out-Null
}

New-Item -ItemType Directory -Force $Output, $Cache | Out-Null

# OpenStreetMap Carto ---------------------------------------------------------------------------------------------
$cartoArchive = Get-CachedFile "https://github.com/openstreetmap-carto/openstreetmap-carto/archive/refs/tags/$cartoVersion.tar.gz" "openstreetmap-carto-$cartoVersion.tar.gz" $cartoSha256
$cartoSource = Join-Path $Cache "openstreetmap-carto-$($cartoVersion.TrimStart('v'))"
if (-not (Test-Path $cartoSource)) {
    & tar -xzf $cartoArchive -C $Cache
    if ($LASTEXITCODE -ne 0) {
        throw 'Extracting OpenStreetMap Carto failed.'
    }
}
$carto = Join-Path $Output 'carto'
Reset-Directory $carto
foreach ($item in 'project.mml', 'style', 'symbols', 'patterns', 'openstreetmap-carto-flex.lua', 'indexes.sql', 'functions.sql', 'common-values.sql', 'LICENSE.txt') {
    Copy-Item -Recurse (Join-Path $cartoSource $item) $carto
}
Write-Host "OpenStreetMap Carto $cartoVersion -> $carto"

# carto compiler ------------------------------------------------------------------------------------------------
$compilerSource = Join-Path $repository 'assets' 'carto-compiler'
$cartoPackage = Join-Path $compilerSource 'node_modules' 'carto' 'package.json'
if (-not (Test-Path $cartoPackage)) {
    throw "The carto compiler is not installed. Run 'npm ci' in $compilerSource first."
}
$compiler = Join-Path $Output 'carto-compiler'
Reset-Directory $compiler
Copy-Item (Join-Path $compilerSource 'package.json') $compiler
if (Test-Path (Join-Path $compilerSource 'package-lock.json')) {
    Copy-Item (Join-Path $compilerSource 'package-lock.json') $compiler
}
Copy-Item -Recurse (Join-Path $compilerSource 'node_modules') $compiler
$cartoCompilerVersion = (Get-Content $cartoPackage -Raw | ConvertFrom-Json).version
Write-Host "carto $cartoCompilerVersion -> $compiler"

# Fonts: the same set as scripts/get-fonts.py of OpenStreetMap Carto, plus all regional variants of Noto Sans CJK ------
$fonts = Join-Path $Output 'fonts'
Reset-Directory $fonts
$notoRegularBoldItalic = @('NotoSans')
$notoRegularBold = @(
    'NotoSansAdlamUnjoined', 'NotoSansArabicUI', 'NotoSansArmenian', 'NotoSansBalinese', 'NotoSansBamum', 'NotoSansBengaliUI',
    'NotoSansCanadianAboriginal', 'NotoSansCham', 'NotoSansCherokee', 'NotoSansDevanagariUI', 'NotoSansEthiopic', 'NotoSansGeorgian',
    'NotoSansGujaratiUI', 'NotoSansGurmukhiUI', 'NotoSansHebrew', 'NotoSansJavanese', 'NotoSansKannadaUI', 'NotoSansKayahLi',
    'NotoSansKhmerUI', 'NotoSansLaoUI', 'NotoSansLisu', 'NotoSansMalayalamUI', 'NotoSansMyanmarUI', 'NotoSansOlChiki',
    'NotoSansOriyaUI', 'NotoSansSinhalaUI', 'NotoSansSundanese', 'NotoSansSymbols', 'NotoSansTaiTham', 'NotoSansTamilUI',
    'NotoSansTeluguUI', 'NotoSansThaana', 'NotoSansThaiUI', 'NotoSerifTibetan')
$notoRegularBlack = @('NotoSansSyriac')
$notoRegular = @(
    'NotoSansBatak', 'NotoSansBuginese', 'NotoSansBuhid', 'NotoSansChakma', 'NotoSansCoptic', 'NotoSansHanunoo', 'NotoSansLepcha',
    'NotoSansLimbu', 'NotoSansMandaic', 'NotoSansMongolian', 'NotoSansNewTaiLue', 'NotoSansNKo', 'NotoSansOsage', 'NotoSansOsmanya',
    'NotoSansSamaritan', 'NotoSansSaurashtra', 'NotoSansShavian', 'NotoSansSymbols2', 'NotoSansTagalog', 'NotoSansTagbanwa',
    'NotoSansTaiLe', 'NotoSansTaiViet', 'NotoSansTifinagh', 'NotoSansVai', 'NotoSansYi')
$fontFiles = [Collections.Generic.List[object]]::new()
foreach ($font in $notoRegularBoldItalic + $notoRegularBold + $notoRegularBlack + $notoRegular) {
    $styles = @('Regular')
    if ($notoRegularBoldItalic -contains $font -or $notoRegularBold -contains $font) {
        $styles += 'Bold'
    }
    if ($notoRegularBoldItalic -contains $font) {
        $styles += 'Italic'
    }
    if ($notoRegularBlack -contains $font) {
        $styles += 'Black'
    }
    foreach ($style in $styles) {
        $fontFiles.Add(@{ Url = "https://github.com/notofonts/noto-fonts/raw/main/hinted/ttf/$font/$font-$style.ttf"; Name = "$font-$style.ttf" })
    }
}
# One collection per weight with the JP, KR, SC, TC and HK faces ("Noto Sans CJK SC Regular" etc.). These pan-CJK
# fonts select regional glyph forms through the OpenType 'locl' feature: Mapnik shapes Han text as zh-Hans by default
# (also on tile.openstreetmap.org), so the renderer sets the shaping language of labels for the other variants.
foreach ($weight in 'Regular', 'Bold') {
    $fontFiles.Add(@{ Url = "https://github.com/notofonts/noto-cjk/raw/main/Sans/OTC/NotoSansCJK-$weight.ttc"; Name = "NotoSansCJK-$weight.ttc" })
}
$fontFiles | ForEach-Object -ThrottleLimit 8 -Parallel {
    $path = Join-Path $using:Cache 'downloads' 'fonts' $_.Name
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
        & curl -fsSL --retry 5 --retry-delay 5 -A 'get-fonts.py/osm-carto' -o "$path.tmp" $_.Url
        if ($LASTEXITCODE -ne 0) {
            throw "Download of $($_.Url) failed."
        }
        Move-Item -Force "$path.tmp" $path
    }
    Copy-Item $path $using:fonts
}

$extract = Join-Path $Cache 'extract'
Reset-Directory $extract
$emoji = Get-CachedFile @('https://archive.org/download/noto-emoji/Noto_Emoji.zip') 'Noto_Emoji.zip' '4cbf97fe2a97b6f54214e40cd2161f202c9a797cd63c4f0c5b9e273eb19c2fb8'
Expand-Archive $emoji (Join-Path $extract 'emoji')
Copy-Item (Join-Path $extract 'emoji' 'static' 'NotoEmoji-Regular.ttf'), (Join-Path $extract 'emoji' 'static' 'NotoEmoji-Bold.ttf') $fonts
# The URL used by OpenStreetMap Carto's get-fonts.py comes first, followed by other OSDN mirrors.
$hanazono = Get-CachedFile @(
    'https://mirrors.dotsrc.org/osdn/hanazono-font/68253/hanazono-20170904.zip',
    'https://ftp.iij.ad.jp/pub/osdn.jp/hanazono-font/68253/hanazono-20170904.zip',
    'https://ftp.jaist.ac.jp/pub/sourceforge.jp/hanazono-font/68253/hanazono-20170904.zip',
    'https://mirrors.gigenet.com/OSDN/hanazono-font/68253/hanazono-20170904.zip'
) 'hanazono-20170904.zip' '571cd4a09ae7da0c642d640fc2442c050aa450ebb0587a95cdd097d41a9c9572'
Expand-Archive $hanazono (Join-Path $extract 'hanazono')
Copy-Item (Join-Path $extract 'hanazono' 'HanaMinA.ttf'), (Join-Path $extract 'hanazono' 'HanaMinB.ttf') $fonts
$fontLicenses = Join-Path $fonts 'licenses'
New-Item -ItemType Directory -Force $fontLicenses | Out-Null
Copy-Item (Get-CachedFile 'https://raw.githubusercontent.com/notofonts/noto-fonts/main/LICENSE' 'noto-fonts-LICENSE.txt' $null) (Join-Path $fontLicenses 'Noto-OFL.txt')
Copy-Item (Get-CachedFile 'https://raw.githubusercontent.com/notofonts/noto-cjk/main/Sans/LICENSE' 'noto-cjk-LICENSE.txt' $null) (Join-Path $fontLicenses 'NotoSansCJK-OFL.txt')
Get-ChildItem (Join-Path $extract 'hanazono') -File | Where-Object { $_.Name -match '(?i)license|readme' } | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $fontLicenses "Hanazono-$($_.Name)")
}
Remove-Item -Recurse -Force $extract
Write-Host "$((Get-ChildItem $fonts -File).Count) font files -> $fonts"

# Preview page ----------------------------------------------------------------------------------------------------
$web = Join-Path $Output 'web'
Reset-Directory $web
Copy-Item (Join-Path $repository 'assets' 'web' '*') $web -Recurse
$leafletZip = Get-CachedFile "https://github.com/Leaflet/Leaflet/releases/download/v$leafletVersion/leaflet.zip" "leaflet-$leafletVersion.zip" $leafletSha256
$leafletExtract = Join-Path $Cache "leaflet-$leafletVersion"
Reset-Directory $leafletExtract
Expand-Archive $leafletZip $leafletExtract
$leaflet = Join-Path $web 'leaflet'
New-Item -ItemType Directory -Force $leaflet | Out-Null
Copy-Item (Join-Path $leafletExtract 'dist' 'leaflet.js'), (Join-Path $leafletExtract 'dist' 'leaflet.css') $leaflet
Copy-Item -Recurse (Join-Path $leafletExtract 'dist' 'images') $leaflet
Copy-Item (Get-CachedFile "https://raw.githubusercontent.com/Leaflet/Leaflet/v$leafletVersion/LICENSE" "leaflet-$leafletVersion-LICENSE" $null) (Join-Path $leaflet 'LICENSE')
Write-Host "Preview page with Leaflet $leafletVersion -> $web"

# External data ---------------------------------------------------------------------------------------------------
$externalDataVersion = 'none'
if (-not $SkipExternalData) {
    $externalCache = Join-Path $Cache 'external-data'
    $dump = Join-Path $externalCache 'external-data.dump'
    if (-not (Test-Path $dump)) {
        & (Join-Path $PSScriptRoot 'build-external-data.ps1') -CartoDirectory $cartoSource -Output $externalCache
    }
    $externalData = Join-Path $Output 'external-data'
    Reset-Directory $externalData
    Copy-Item $dump $externalData
    $externalDataVersion = (Get-Content (Join-Path $externalCache 'external-data.json') -Raw | ConvertFrom-Json).version
    Write-Host "External data $externalDataVersion -> $externalData"
}
else {
    Write-Warning 'External data skipped; the result cannot import data.'
}

# Vector engine ---------------------------------------------------------------------------------------------------
$vector = Join-Path $Output 'vector'
Reset-Directory $vector
$vectorLicenses = Join-Path $vector 'licenses'
New-Item -ItemType Directory -Force $vectorLicenses | Out-Null

# Planetiler and its Shortbread schema (unchanged; the renderer points its sources at local files when building).
$planetiler = Join-Path $vector 'planetiler'
New-Item -ItemType Directory -Force $planetiler | Out-Null
Copy-Item (Get-CachedFile "https://github.com/onthegomap/planetiler/releases/download/v$planetilerVersion/planetiler.jar" "planetiler-$planetilerVersion.jar" $planetilerSha256) (Join-Path $planetiler 'planetiler.jar')
Copy-Item (Get-CachedFile "https://raw.githubusercontent.com/onthegomap/planetiler/v$planetilerVersion/planetiler-custommap/src/main/resources/samples/shortbread.yml" `
    "planetiler-$planetilerVersion-shortbread.yml" $shortbreadSchemaSha256) (Join-Path $vector 'shortbread.yml')
Copy-Item (Get-CachedFile "https://raw.githubusercontent.com/onthegomap/planetiler/v$planetilerVersion/LICENSE" "planetiler-$planetilerVersion-LICENSE" $null) (Join-Path $vectorLicenses 'Planetiler-LICENSE.txt')

# Styles, generated with the VersaTiles style library, and their sprites.
$styleArchive = Get-CachedFile "https://github.com/versatiles-org/versatiles-style/releases/download/v$versatilesStyleVersion/versatiles-style.tar.gz" `
    "versatiles-style-$versatilesStyleVersion.tar.gz" $versatilesStyleSha256
$styleLibrary = Join-Path $Cache "versatiles-style-$versatilesStyleVersion"
Reset-Directory $styleLibrary
& tar -xzf $styleArchive -C $styleLibrary
if ($LASTEXITCODE -ne 0) {
    throw 'Extracting the VersaTiles style library failed.'
}
$fontStacks = & node (Join-Path $PSScriptRoot 'vector-styles.cjs') (Join-Path $styleLibrary 'versatiles-style.js') (Join-Path $vector 'styles') @vectorThemes
if ($LASTEXITCODE -ne 0) {
    throw 'Generating the vector styles failed.'
}
$sprites = Join-Path $vector 'sprites'
New-Item -ItemType Directory -Force $sprites | Out-Null
& tar -xzf (Get-CachedFile "https://github.com/versatiles-org/versatiles-style/releases/download/v$versatilesStyleVersion/sprites.tar.gz" `
    "versatiles-sprites-$versatilesStyleVersion.tar.gz" $versatilesSpritesSha256) -C $sprites
if ($LASTEXITCODE -ne 0) {
    throw 'Extracting the sprites failed.'
}
Copy-Item (Get-CachedFile "https://raw.githubusercontent.com/versatiles-org/versatiles-style/v$versatilesStyleVersion/LICENSE.md" "versatiles-style-$versatilesStyleVersion-LICENSE.md" $null) `
    (Join-Path $vectorLicenses 'VersaTiles-style-LICENSE.md')

# Glyphs of the font stacks the styles use. CJK characters are drawn from Noto Sans CJK instead (with the selected
# regional variant), so the ranges that only contain CJK characters are left out.
$glyphArchive = Get-CachedFile "https://github.com/versatiles-org/versatiles-fonts/releases/download/v$versatilesFontsVersion/noto_sans.tar.gz" `
    "versatiles-fonts-$versatilesFontsVersion-noto_sans.tar.gz" $versatilesNotoSansSha256
$glyphSource = Join-Path $Cache "versatiles-fonts-$versatilesFontsVersion"
Reset-Directory $glyphSource
& tar -xzf $glyphArchive -C $glyphSource
if ($LASTEXITCODE -ne 0) {
    throw 'Extracting the glyphs failed.'
}
$cjkOnly = @(@(0x1100, 0x11FF), @(0x2F00, 0x9FFF), @(0xAC00, 0xD7FF), @(0xF900, 0xFAFF))
$glyphs = Join-Path $vector 'glyphs'
foreach ($font in $fontStacks | Where-Object { $_ }) {
    $source = Join-Path $glyphSource $font
    if (-not (Test-Path $source)) {
        throw "The styles use the font stack '$font', which noto_sans.tar.gz does not contain."
    }
    $target = Join-Path $glyphs $font
    New-Item -ItemType Directory -Force $target | Out-Null
    foreach ($file in Get-ChildItem $source -Filter '*.pbf') {
        $first, $last = $file.BaseName -split '-' | ForEach-Object { [int]$_ }
        if (-not ($cjkOnly | Where-Object { $first -ge $_[0] -and $last -le $_[1] })) {
            Copy-Item $file.FullName $target
        }
    }
}
Copy-Item (Get-CachedFile "https://raw.githubusercontent.com/versatiles-org/versatiles-fonts/v$versatilesFontsVersion/LICENSE" "versatiles-fonts-$versatilesFontsVersion-LICENSE" $null) `
    (Join-Path $vectorLicenses 'VersaTiles-fonts-LICENSE.txt')

# Ocean polygons for Planetiler: the same snapshot as the external data of OpenStreetMap Carto.
$waterPolygonsVersion = 'none'
if (-not $SkipExternalData) {
    $waterPolygons = Join-Path $Cache 'external-data' 'downloads' 'water-polygons-split-3857.zip'
    if (-not (Test-Path $waterPolygons)) {
        throw "$waterPolygons is missing; delete the external data cache to download it again."
    }
    Copy-Item $waterPolygons $vector
    $waterPolygonsVersion = [DateTimeOffset]::Parse((Get-Content "$waterPolygons.lastmod" -Raw).Trim(), [Globalization.CultureInfo]::InvariantCulture).ToString('yyyy-MM-dd')
}
Write-Host "Vector engine: Planetiler $planetilerVersion, VersaTiles style $versatilesStyleVersion ($($vectorThemes -join ', ')), water polygons $waterPolygonsVersion -> $vector"

[ordered]@{
    openStreetMapCarto = $cartoVersion
    carto = $cartoCompilerVersion
    mapnikApi = '3.0.22'
    externalData = $externalDataVersion
    leaflet = $leafletVersion
    planetiler = $planetilerVersion
    waterPolygons = $waterPolygonsVersion
    versaTilesStyle = $versatilesStyleVersion
} | ConvertTo-Json | Set-Content -NoNewline -Encoding utf8NoBOM (Join-Path $Output 'manifest.json')
Write-Host "Assets ready in $Output"
