#!/usr/bin/env pwsh
# Publishes yukiosm and yukielevation for one platform and assembles a release package:
#   yukiosm(.exe), yukielevation(.exe), runtime/ (native components), assets/, README.md, LICENSE, THIRD-PARTY-NOTICES.md
# Requires the outputs of native/build-*.ps1|sh (artifacts/native/<rid>/runtime) and build/fetch-assets.ps1 (artifacts/assets).

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64')]
    [string]$Rid,
    [string]$Version = '0.0.0-dev',
    [string]$NativeRuntime = (Join-Path $PSScriptRoot '..' 'artifacts' 'native' $Rid 'runtime'),
    [string]$Assets = (Join-Path $PSScriptRoot '..' 'artifacts' 'assets'),
    [string]$Output = (Join-Path $PSScriptRoot '..' 'artifacts' 'bundle'),
    # Only assemble the directory, without creating the archive.
    [switch]$NoArchive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$NativeRuntime = [IO.Path]::GetFullPath($NativeRuntime)
$Assets = [IO.Path]::GetFullPath($Assets)
$Output = [IO.Path]::GetFullPath($Output)
foreach ($required in $NativeRuntime, (Join-Path $Assets 'manifest.json')) {
    if (-not (Test-Path $required)) {
        throw "Missing $required; build the native runtime and the assets first."
    }
}

$name = "yukiosm-$Version-$Rid"
$bundle = Join-Path $Output $name
if (Test-Path $bundle) {
    Remove-Item -Recurse -Force $bundle
}
New-Item -ItemType Directory -Force $bundle | Out-Null

foreach ($project in 'YukiOsmRenderer', 'YukiElevation') {
    Write-Host "Publishing $project $Version for $Rid"
    & dotnet publish (Join-Path $repository 'src' $project "$project.csproj") `
        --configuration Release --runtime $Rid --self-contained true --output $bundle `
        -p:PublishSingleFile=true -p:DebugType=embedded -p:Version=$Version
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish of $project failed."
    }
}

Write-Host 'Copying runtime and assets'
Copy-Item -Recurse $NativeRuntime (Join-Path $bundle 'runtime')
Copy-Item -Recurse $Assets (Join-Path $bundle 'assets')
Copy-Item (Join-Path $repository 'README.md'), (Join-Path $repository 'LICENSE'), (Join-Path $repository 'THIRD-PARTY-NOTICES.md') $bundle

# yukielevation carries GDAL from the MaxRev.Gdal packages; GDAL's license text is in the package of its data.
$elevationLicenses = Join-Path $bundle 'runtime' 'licenses' 'yukielevation'
New-Item -ItemType Directory -Force $elevationLicenses | Out-Null
Copy-Item (Join-Path $repository 'src' 'YukiElevation' 'LICENSES.txt') (Join-Path $elevationLicenses 'README.txt')
$packageAssets = Get-Content (Join-Path $repository 'src' 'YukiElevation' 'obj' 'project.assets.json') -Raw | ConvertFrom-Json
$gdalPackage = ($packageAssets.libraries.PSObject.Properties | Where-Object Name -like 'MaxRev.Gdal.Core/*').Value.path
$gdalLicense = $packageAssets.packageFolders.PSObject.Properties.Name |
    ForEach-Object { Join-Path $_ $gdalPackage 'runtimes' 'any' 'native' 'gdal-data' 'LICENSE.TXT' } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $gdalLicense) {
    throw 'The license text of GDAL was not found in the MaxRev.Gdal.Core package.'
}
Copy-Item $gdalLicense (Join-Path $elevationLicenses 'gdal.txt')

if (-not $NoArchive) {
    if ($Rid -like 'win-*') {
        $archive = Join-Path $Output "$name.zip"
        if (Test-Path $archive) {
            Remove-Item $archive
        }
        Compress-Archive -Path $bundle -DestinationPath $archive -CompressionLevel Optimal
    }
    else {
        # tar keeps the executable bits and symbolic links of the Linux runtime.
        $archive = Join-Path $Output "$name.tar.gz"
        & tar -czf $archive -C $Output $name
        if ($LASTEXITCODE -ne 0) {
            throw 'tar failed.'
        }
    }
    Write-Host "Package: $archive"
}
Write-Host "Bundle: $bundle"
