#!/usr/bin/env pwsh
# Publishes yukiosm for one platform and assembles a release package:
#   yukiosm(.exe), runtime/ (native components), assets/, README.md, LICENSE, THIRD-PARTY-NOTICES.md
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

Write-Host "Publishing yukiosm $Version for $Rid"
& dotnet publish (Join-Path $repository 'src' 'YukiOsmRenderer' 'YukiOsmRenderer.csproj') `
    --configuration Release --runtime $Rid --self-contained true --output $bundle `
    -p:PublishSingleFile=true -p:DebugType=embedded -p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed.'
}

Write-Host 'Copying runtime and assets'
Copy-Item -Recurse $NativeRuntime (Join-Path $bundle 'runtime')
Copy-Item -Recurse $Assets (Join-Path $bundle 'assets')
Copy-Item (Join-Path $repository 'README.md'), (Join-Path $repository 'LICENSE'), (Join-Path $repository 'THIRD-PARTY-NOTICES.md') $bundle

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
