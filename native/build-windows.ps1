#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the native runtime for Windows x64.

.DESCRIPTION
    Builds yukimapnik (Mapnik via vcpkg, static), downloads PostgreSQL, PostGIS, osm2pgsql and
    Node.js, and assembles artifacts\native\win-x64\runtime as described in native\README.md.
    The smoke test executable and a test font are written to artifacts\native\win-x64\tools.

    Requirements: Visual Studio 2022 or later with the C++ desktop workload, git, CMake >= 3.25
    (the one bundled with Visual Studio works). Downloads and the vcpkg checkout are cached in
    artifacts\cache.

.PARAMETER SkipSmokeTest
    Do not run the yukimapnik smoke test (ctest) after building.
#>
[CmdletBinding()]
param(
    [switch]$SkipSmokeTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProgressPreference = 'SilentlyContinue'

$NativeDir = $PSScriptRoot
$RepoRoot = Split-Path $NativeDir -Parent
$CacheDir = Join-Path $RepoRoot 'artifacts\cache'
$DownloadDir = Join-Path $CacheDir 'downloads'
$WorkDir = Join-Path $CacheDir 'build\win-x64'
$OutDir = Join-Path $RepoRoot 'artifacts\native\win-x64'
$RuntimeDir = Join-Path $OutDir 'runtime'
$ToolsDir = Join-Path $OutDir 'tools'
$VcpkgRoot = Join-Path $CacheDir 'vcpkg'
$YukimapnikDir = Join-Path $NativeDir 'yukimapnik'
$Preset = 'win-x64'
$Triplet = 'x64-windows-static-release'

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-Native {
    param([Parameter(Mandatory)][string]$FilePath, [string[]]$Arguments = @())
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath $($Arguments -join ' ')' failed with exit code $LASTEXITCODE"
    }
}

function Read-Versions {
    $versions = [ordered]@{}
    foreach ($line in Get-Content (Join-Path $NativeDir 'versions.env')) {
        if ($line -match '^\s*([A-Z0-9_]+)=(\S*)\s*$') {
            $versions[$Matches[1]] = $Matches[2]
        }
    }
    return $versions
}

function Get-Download {
    param([string]$Url, [string]$Sha256, [string]$FileName)
    New-Item -ItemType Directory -Force $DownloadDir | Out-Null
    $path = Join-Path $DownloadDir $FileName
    if (Test-Path $path) {
        if ((Get-FileHash $path -Algorithm SHA256).Hash -eq $Sha256) {
            return $path
        }
        Write-Warning "Checksum mismatch for cached $FileName, downloading again"
        Remove-Item $path
    }
    Write-Step "Downloading $Url"
    $partial = "$path.partial"
    Invoke-Native curl.exe @('-fsSL', '--retry', '5', '--retry-delay', '5', '-o', $partial, $Url)
    $actual = (Get-FileHash $partial -Algorithm SHA256).Hash
    if ($actual -ne $Sha256) {
        Remove-Item $partial
        throw "SHA256 mismatch for $Url`n  expected $Sha256`n  actual   $actual"
    }
    Move-Item $partial $path
    return $path
}

function Expand-ArchiveFast {
    param([string]$Archive, [string]$Destination, [string[]]$Exclude = @())
    if (Test-Path $Destination) {
        Remove-Item -Recurse -Force $Destination
    }
    New-Item -ItemType Directory -Force $Destination | Out-Null
    $arguments = @('-xf', $Archive, '-C', $Destination)
    foreach ($pattern in $Exclude) {
        $arguments += @('--exclude', $pattern)
    }
    # bsdtar (tar.exe of Windows 10+) extracts zip archives much faster than Expand-Archive.
    Invoke-Native "$env:SystemRoot\System32\tar.exe" $arguments
}

function Import-VsDevEnvironment {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw 'vswhere.exe not found; Visual Studio with the C++ workload is required'
    }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) {
        throw 'No Visual Studio installation with the C++ x64 tools found'
    }
    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    Write-Step "Using Visual Studio at $vs"
    $environment = & $env:ComSpec /c "`"$vcvars`" >nul && set"
    if ($LASTEXITCODE -ne 0) {
        throw "vcvars64.bat failed"
    }
    foreach ($line in $environment) {
        if ($line -match '^([^=]+)=(.*)$') {
            Set-Item -Path "env:$($Matches[1])" -Value $Matches[2]
        }
    }
    return $vs
}

function Get-VcRedistDir([string]$VsDir) {
    $versionFile = Join-Path $VsDir 'VC\Auxiliary\Build\Microsoft.VCRedistVersion.default.txt'
    $version = (Get-Content $versionFile -TotalCount 1).Trim()
    $crt = Get-ChildItem (Join-Path $VsDir "VC\Redist\MSVC\$version\x64") -Directory -Filter 'Microsoft.VC*.CRT' | Select-Object -First 1
    if (-not $crt) {
        throw "Visual C++ runtime redistributable not found for $version"
    }
    return $crt.FullName
}

function Get-PeDependencies([string]$Path) {
    $output = & dumpbin.exe /nologo /dependents $Path
    if ($LASTEXITCODE -ne 0) {
        throw "dumpbin failed for $Path"
    }
    return @($output | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^\S+\.dll$' } |
        ForEach-Object { $_.ToLowerInvariant() } | Sort-Object -Unique)
}

function Test-IsVcRuntime([string]$Dll) {
    return $Dll -match '^(vcruntime|msvcp|concrt|vccorlib|vcomp)\d'
}

function Test-IsSystemDll([string]$Dll) {
    if ($Dll -match '^(api|ext)-ms-') {
        return $true
    }
    if (Test-IsVcRuntime $Dll) {
        return $false
    }
    return Test-Path (Join-Path "$env:SystemRoot\System32" $Dll)
}

# Copies the DLLs from $SourceDir that $Roots import directly or indirectly into $TargetDir.
# DLLs that already exist in $TargetDir are kept.
function Copy-DllClosure {
    param([string[]]$Roots, [string]$SourceDir, [string]$TargetDir)
    $queue = [System.Collections.Generic.Queue[string]]::new()
    foreach ($root in $Roots) {
        $queue.Enqueue($root)
    }
    $seen = @{}
    while ($queue.Count -gt 0) {
        foreach ($dll in Get-PeDependencies $queue.Dequeue()) {
            if ($seen.ContainsKey($dll) -or (Test-IsSystemDll $dll)) {
                continue
            }
            $seen[$dll] = $true
            $target = Join-Path $TargetDir $dll
            if (-not (Test-Path $target)) {
                $source = Join-Path $SourceDir $dll
                if (-not (Test-Path $source)) {
                    continue
                }
                Copy-Item $source $target
            }
            $queue.Enqueue($target)
        }
    }
}

# Copies the Visual C++ runtime DLLs imported by the binaries in $BinaryDirs into $TargetDir.
function Copy-VcRuntime {
    param([string[]]$BinaryDirs, [string]$TargetDir, [string]$RedistDir)
    $needed = @{}
    $files = Get-ChildItem $BinaryDirs -File | Where-Object { $_.Extension -in '.exe', '.dll' }
    foreach ($file in $files) {
        foreach ($dll in Get-PeDependencies $file.FullName) {
            if (Test-IsVcRuntime $dll) {
                $needed[$dll] = $true
            }
        }
    }
    foreach ($dll in $needed.Keys) {
        $target = Join-Path $TargetDir $dll
        if (Test-Path $target) {
            continue
        }
        $source = Join-Path $RedistDir $dll
        if (-not (Test-Path $source)) {
            throw "Visual C++ runtime DLL $dll not found in $RedistDir"
        }
        Write-Host "    app-local $dll -> $TargetDir"
        Copy-Item $source $target
    }
    # Imports of the runtime DLLs themselves (e.g. msvcp140.dll -> vcruntime140.dll).
    foreach ($dll in @($needed.Keys)) {
        foreach ($dependency in Get-PeDependencies (Join-Path $TargetDir $dll)) {
            if ((Test-IsVcRuntime $dependency) -and -not (Test-Path (Join-Path $TargetDir $dependency))) {
                Copy-Item (Join-Path $RedistDir $dependency) $TargetDir
            }
        }
    }
}

# Fails if a binary imports a DLL that is neither next to the executable nor part of Windows.
function Assert-DependenciesResolved {
    param([string[]]$Files, [string]$AppDir)
    $missing = @()
    foreach ($file in $Files) {
        foreach ($dll in Get-PeDependencies $file) {
            if (-not (Test-IsSystemDll $dll) -and -not (Test-Path (Join-Path $AppDir $dll))) {
                $missing += "$file -> $dll"
            }
        }
    }
    if ($missing.Count -gt 0) {
        throw "Unresolved DLL dependencies:`n  $($missing -join "`n  ")"
    }
}

function Initialize-Vcpkg([hashtable]$Versions) {
    $manifest = Get-Content (Join-Path $YukimapnikDir 'vcpkg.json') -Raw | ConvertFrom-Json
    if ($manifest.'builtin-baseline' -ne $Versions.VCPKG_COMMIT) {
        throw "builtin-baseline in vcpkg.json ($($manifest.'builtin-baseline')) differs from VCPKG_COMMIT in versions.env ($($Versions.VCPKG_COMMIT))"
    }
    if (-not (Test-Path (Join-Path $VcpkgRoot '.git'))) {
        Write-Step "Cloning vcpkg"
        Invoke-Native git @('clone', '--quiet', $Versions.VCPKG_REPOSITORY, $VcpkgRoot)
    }
    $head = (& git -C $VcpkgRoot rev-parse HEAD).Trim()
    if ($head -ne $Versions.VCPKG_COMMIT) {
        Write-Step "Checking out vcpkg $($Versions.VCPKG_COMMIT)"
        $ErrorActionPreference = 'Continue'
        & git -C $VcpkgRoot cat-file -e "$($Versions.VCPKG_COMMIT)^{commit}" 2>$null
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) {
            Invoke-Native git @('-C', $VcpkgRoot, 'fetch', '--quiet', 'origin')
        }
        Invoke-Native git @('-C', $VcpkgRoot, 'checkout', '--quiet', '--force', $Versions.VCPKG_COMMIT)
    }
    $marker = Join-Path $VcpkgRoot 'yukiosm-bootstrapped.txt'
    $vcpkgExe = Join-Path $VcpkgRoot 'vcpkg.exe'
    if (-not (Test-Path $vcpkgExe) -or -not (Test-Path $marker) -or (Get-Content $marker -Raw).Trim() -ne $Versions.VCPKG_COMMIT) {
        Write-Step "Bootstrapping vcpkg"
        Invoke-Native (Join-Path $VcpkgRoot 'bootstrap-vcpkg.bat') @('-disableMetrics')
        Set-Content -Path $marker -Value $Versions.VCPKG_COMMIT
    }
    $env:VCPKG_ROOT = $VcpkgRoot
    $env:VCPKG_DISABLE_METRICS = '1'
    $env:VCPKG_DOWNLOADS = Join-Path $CacheDir 'vcpkg-downloads'
    if (-not $env:VCPKG_DEFAULT_BINARY_CACHE) {
        $env:VCPKG_DEFAULT_BINARY_CACHE = Join-Path $CacheDir 'vcpkg-binary'
    }
    New-Item -ItemType Directory -Force $env:VCPKG_DOWNLOADS, $env:VCPKG_DEFAULT_BINARY_CACHE | Out-Null
}

function Build-Yukimapnik {
    Write-Step "Building yukimapnik (vcpkg installs Mapnik on the first run, this takes a while)"
    Push-Location $YukimapnikDir
    try {
        Invoke-Native cmake @('--preset', $Preset)
        Invoke-Native cmake @('--build', '--preset', $Preset)
        if (-not $SkipSmokeTest) {
            Invoke-Native ctest @('--preset', $Preset)
        }
    }
    finally {
        Pop-Location
    }
}

function Copy-License([string]$Source, [string]$Component, [string]$Name = $null) {
    $dir = Join-Path $RuntimeDir "licenses\$Component"
    New-Item -ItemType Directory -Force $dir | Out-Null
    if (-not $Name) {
        $Name = Split-Path $Source -Leaf
    }
    Copy-Item $Source (Join-Path $dir $Name)
}

function Get-DirectorySize([string]$Path) {
    return (Get-ChildItem $Path -Recurse -File | Measure-Object Length -Sum).Sum
}

$versions = Read-Versions
$vsDir = Import-VsDevEnvironment
$redistDir = Get-VcRedistDir $vsDir
Initialize-Vcpkg $versions
Build-Yukimapnik

$postgresqlZip = Get-Download $versions.POSTGRESQL_WINDOWS_URL $versions.POSTGRESQL_WINDOWS_SHA256 (Split-Path $versions.POSTGRESQL_WINDOWS_URL -Leaf)
$postgisZip = Get-Download $versions.POSTGIS_WINDOWS_URL $versions.POSTGIS_WINDOWS_SHA256 (Split-Path $versions.POSTGIS_WINDOWS_URL -Leaf)
$osm2pgsqlZip = Get-Download $versions.OSM2PGSQL_WINDOWS_URL $versions.OSM2PGSQL_WINDOWS_SHA256 (Split-Path $versions.OSM2PGSQL_WINDOWS_URL -Leaf)
$nodeZip = Get-Download $versions.NODE_WIN_X64_URL $versions.NODE_WIN_X64_SHA256 (Split-Path $versions.NODE_WIN_X64_URL -Leaf)

Write-Step "Extracting archives"
$extractDir = Join-Path $WorkDir 'extract'
Expand-ArchiveFast $postgresqlZip (Join-Path $extractDir 'postgresql') @('pgsql/pgAdmin 4', 'pgsql/StackBuilder', 'pgsql/doc', 'pgsql/include', 'pgsql/symbols')
Expand-ArchiveFast $postgisZip (Join-Path $extractDir 'postgis')
Expand-ArchiveFast $osm2pgsqlZip (Join-Path $extractDir 'osm2pgsql')
Expand-ArchiveFast $nodeZip (Join-Path $extractDir 'node')

if (Test-Path $RuntimeDir) {
    Remove-Item -Recurse -Force $RuntimeDir
}
New-Item -ItemType Directory -Force $RuntimeDir | Out-Null

# PostgreSQL ---------------------------------------------------------------------------------------
Write-Step "Assembling PostgreSQL $($versions.POSTGRESQL_VERSION)"
$edbDir = Join-Path $extractDir 'postgresql\pgsql'
$pgDir = Join-Path $RuntimeDir 'pgsql'
$pgBin = Join-Path $pgDir 'bin'
$pgLib = Join-Path $pgDir 'lib'
New-Item -ItemType Directory -Force $pgDir | Out-Null
foreach ($dir in 'bin', 'lib', 'share') {
    Copy-Item -Recurse (Join-Path $edbDir $dir) (Join-Path $pgDir $dir)
}
# StackBuilder (wxWidgets), import libraries, pgxs/pkg-config files and test helpers are not needed at runtime.
Remove-Item -Force (Join-Path $pgBin 'stackbuilder.exe'), (Join-Path $pgBin 'test_cloexec.exe'), (Join-Path $pgBin 'testplug.dll') -ErrorAction SilentlyContinue
Get-ChildItem $pgBin -Filter 'wx*.dll' | Remove-Item
Get-ChildItem $pgDir -Recurse -File -Include '*.lib', '*.a', '*.pdb' | Remove-Item
foreach ($dir in 'lib\pgxs', 'lib\pkgconfig', 'share\doc', 'share\man') {
    $path = Join-Path $pgDir $dir
    if (Test-Path $path) {
        Remove-Item -Recurse -Force $path
    }
}
# Procedural languages and transforms that need a separate Perl, Python or Tcl installation.
Get-ChildItem $pgLib -File | Where-Object { $_.Name -match '(^pl(perl|python3|tcl)|_pl(perl|python3))\.dll$' } | Remove-Item
Get-ChildItem (Join-Path $pgDir 'share\extension') -File |
    Where-Object { $_.Name -match '^(pl(perl|python3|tcl)u?|[a-z]+_pl(perl|python3)u?)(\.control$|--)' } | Remove-Item
Copy-License (Join-Path $edbDir 'server_license.txt') 'postgresql' 'LICENSE.txt'
Copy-License (Join-Path $edbDir 'commandlinetools_3rd_party_licenses.txt') 'postgresql' 'third-party-licenses.txt'

# PostGIS ------------------------------------------------------------------------------------------
Write-Step "Merging PostGIS $($versions.POSTGIS_VERSION) (postgis, postgis_topology)"
$bundleDir = Get-ChildItem (Join-Path $extractDir 'postgis') -Directory | Select-Object -First 1 -ExpandProperty FullName
$postgisShare = Join-Path $pgDir 'share'
$postgisModules = @('postgis-3.dll', 'postgis_topology-3.dll')
foreach ($module in $postgisModules) {
    Copy-Item (Join-Path $bundleDir "lib\$module") $pgLib
}
foreach ($extension in 'postgis', 'postgis_topology') {
    Get-ChildItem (Join-Path $bundleDir 'share\extension') -File |
        Where-Object { $_.Name -eq "$extension.control" -or $_.Name -like "$extension--*.sql" } |
        Copy-Item -Destination (Join-Path $postgisShare 'extension')
}
$postgisContrib = Join-Path $bundleDir 'share\contrib\postgis-3.6'
$contribTarget = Join-Path $postgisShare 'contrib\postgis-3.6'
New-Item -ItemType Directory -Force $contribTarget | Out-Null
Get-ChildItem $postgisContrib -File -Filter '*.sql' |
    Where-Object { $_.Name -notmatch '^(rtpostgis|raster|sfcgal|tiger|uninstall_rtpostgis)' } |
    Copy-Item -Destination $contribTarget
# PROJ looks for its database in <libproj dir>\..\share\proj, i.e. pgsql\share\proj. Datum grids are omitted.
$projTarget = Join-Path $postgisShare 'proj'
New-Item -ItemType Directory -Force $projTarget | Out-Null
Get-ChildItem (Join-Path $postgisContrib 'proj') -File | Where-Object { $_.Extension -ne '.tif' } | Copy-Item -Destination $projTarget
foreach ($tool in 'shp2pgsql.exe', 'pgsql2shp.exe') {
    Copy-Item (Join-Path $bundleDir "bin\$tool") $pgBin
}
$postgisRoots = @($postgisModules | ForEach-Object { Join-Path $pgLib $_ }) + @('shp2pgsql.exe', 'pgsql2shp.exe' | ForEach-Object { Join-Path $pgBin $_ })
Copy-DllClosure -Roots $postgisRoots -SourceDir (Join-Path $bundleDir 'bin') -TargetDir $pgBin
Copy-License (Join-Path $bundleDir 'LICENSE') 'postgis' 'LICENSE.txt'
Copy-License (Join-Path $bundleDir 'README.md') 'postgis' 'README.md'
Copy-License (Join-Path $bundleDir 'version.txt') 'postgis' 'bundled-library-versions.txt'

# osm2pgsql ----------------------------------------------------------------------------------------
Write-Step "Assembling osm2pgsql $($versions.OSM2PGSQL_VERSION)"
$osm2pgsqlSource = Join-Path $extractDir 'osm2pgsql\osm2pgsql-bin'
$osm2pgsqlDir = Join-Path $RuntimeDir 'osm2pgsql'
New-Item -ItemType Directory -Force $osm2pgsqlDir | Out-Null
Get-ChildItem $osm2pgsqlSource -File | Where-Object { $_.Extension -in '.exe', '.dll', '.style' -or $_.Name -eq 'proj.db' } |
    Copy-Item -Destination $osm2pgsqlDir
Copy-License (Join-Path $osm2pgsqlSource 'COPYING') 'osm2pgsql' 'COPYING.txt'
Copy-License (Join-Path $osm2pgsqlSource 'README.md') 'osm2pgsql' 'README.md'

# Node.js ------------------------------------------------------------------------------------------
Write-Step "Assembling Node.js $($versions.NODE_VERSION)"
$nodeSource = Get-ChildItem (Join-Path $extractDir 'node') -Directory | Select-Object -First 1 -ExpandProperty FullName
$nodeDir = Join-Path $RuntimeDir 'node'
New-Item -ItemType Directory -Force $nodeDir | Out-Null
# Only the executable is needed; npm/npx/corepack are not used at runtime.
Copy-Item (Join-Path $nodeSource 'node.exe') $nodeDir
Copy-License (Join-Path $nodeSource 'LICENSE') 'node' 'LICENSE.txt'

# yukimapnik ---------------------------------------------------------------------------------------
Write-Step "Installing yukimapnik"
$buildDir = Join-Path $WorkDir 'yukimapnik'
$mapnikDir = Join-Path $RuntimeDir 'mapnik'
Invoke-Native cmake @('--install', $buildDir, '--component', 'runtime', '--prefix', $mapnikDir)
if (Test-Path $ToolsDir) {
    Remove-Item -Recurse -Force $ToolsDir
}
Invoke-Native cmake @('--install', $buildDir, '--component', 'smoke', '--prefix', $ToolsDir)
$vcpkgShare = Join-Path $WorkDir "vcpkg_installed\$Triplet\share"
foreach ($port in Get-ChildItem $vcpkgShare -Directory) {
    $copyright = Join-Path $port.FullName 'copyright'
    if ((Test-Path $copyright) -and $port.Name -notlike 'vcpkg-*' -and $port.Name -ne 'pkgconf') {
        Copy-License $copyright 'mapnik' "$($port.Name).txt"
    }
}
Copy-License (Join-Path $NativeDir 'licenses\mapnik.txt') 'mapnik' 'README.txt'

# Visual C++ runtime -------------------------------------------------------------------------------
Write-Step "Checking Visual C++ runtime and DLL dependencies"
Copy-VcRuntime -BinaryDirs @($pgBin, $pgLib) -TargetDir $pgBin -RedistDir $redistDir
Copy-VcRuntime -BinaryDirs @($osm2pgsqlDir) -TargetDir $osm2pgsqlDir -RedistDir $redistDir
Copy-VcRuntime -BinaryDirs @($mapnikDir) -TargetDir $mapnikDir -RedistDir $redistDir
Copy-VcRuntime -BinaryDirs @($nodeDir) -TargetDir $nodeDir -RedistDir $redistDir
if (Get-ChildItem $RuntimeDir -Recurse -File | Where-Object { Test-IsVcRuntime $_.Name.ToLowerInvariant() }) {
    Copy-License (Join-Path $NativeDir 'licenses\vcruntime.txt') 'vcruntime' 'README.txt'
}

$peFiles = { param($dir) @(Get-ChildItem $dir -File | Where-Object { $_.Extension -in '.exe', '.dll' } | ForEach-Object FullName) }
Assert-DependenciesResolved -Files ((& $peFiles $pgBin) + (& $peFiles $pgLib)) -AppDir $pgBin
Assert-DependenciesResolved -Files (& $peFiles $osm2pgsqlDir) -AppDir $osm2pgsqlDir
Assert-DependenciesResolved -Files (& $peFiles $mapnikDir) -AppDir $mapnikDir
Assert-DependenciesResolved -Files (& $peFiles $nodeDir) -AppDir $nodeDir

# versions.json ------------------------------------------------------------------------------------
$versionInfo = [ordered]@{
    rid = 'win-x64'
    postgresql = $versions.POSTGRESQL_VERSION
    postgis = $versions.POSTGIS_VERSION
    osm2pgsql = $versions.OSM2PGSQL_VERSION
    mapnik = $versions.MAPNIK_VERSION
    node = $versions.NODE_VERSION
    mapnikPlugins = 'static'
    sources = [ordered]@{
        postgresql = $versions.POSTGRESQL_WINDOWS_URL
        postgis = $versions.POSTGIS_WINDOWS_URL
        osm2pgsql = $versions.OSM2PGSQL_WINDOWS_URL
        mapnik = "vcpkg $($versions.VCPKG_REPOSITORY)@$($versions.VCPKG_COMMIT)"
        node = $versions.NODE_WIN_X64_URL
    }
}
$json = $versionInfo | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText((Join-Path $RuntimeDir 'versions.json'), $json.Replace("`r`n", "`n") + "`n")

$size = Get-DirectorySize $RuntimeDir
Write-Step ("Runtime ready: {0} ({1:N1} MiB)" -f $RuntimeDir, ($size / 1MB))
