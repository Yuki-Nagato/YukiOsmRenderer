#Requires -Version 5.1
<#
.SYNOPSIS
    Tests a Windows native runtime (artifacts\native\win-x64\runtime).

.DESCRIPTION
    Creates a temporary PostgreSQL cluster (UTF8, locale C, scram-sha-256 password authentication),
    starts it on a free localhost port, checks PostGIS (GEOS, PROJ, json-c, libxml2), hstore,
    imports a tiny OSM file with osm2pgsql (flex output), checks Node.js and runs the yukimapnik
    smoke test against the database. Everything is stopped and removed afterwards.

.PARAMETER RuntimeDir
    The runtime directory to test.

.PARAMETER ToolsDir
    Directory containing yukimapnik-smoke.exe and fonts\ (default: <RuntimeDir>\..\tools).

.PARAMETER WorkDir
    Directory for the temporary cluster (default: <RuntimeDir>\..\test-work). Removed afterwards
    unless -KeepWorkDir is given.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeDir,
    [string]$ToolsDir,
    [string]$WorkDir,
    [switch]$KeepWorkDir
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RuntimeDir = (Resolve-Path $RuntimeDir).Path
if (-not $ToolsDir) {
    $ToolsDir = Join-Path (Split-Path $RuntimeDir -Parent) 'tools'
}
if (-not $WorkDir) {
    $WorkDir = Join-Path (Split-Path $RuntimeDir -Parent) 'test-work'
}

$script:Failures = 0
function Write-Check([bool]$Condition, [string]$What) {
    if ($Condition) {
        Write-Host "[ OK ] $What" -ForegroundColor Green
    }
    else {
        Write-Host "[FAIL] $What" -ForegroundColor Red
        $script:Failures++
    }
}

function Invoke-Captured {
    param([string]$FilePath, [string[]]$Arguments)
    # Native stderr output must not turn into terminating errors.
    $ErrorActionPreference = 'Continue'
    $output = & $FilePath @Arguments 2>&1 | ForEach-Object { "$_" }
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    return $port
}

# The server started by pg_ctl inherits the output handles of pg_ctl; with pipes (as used by
# Invoke-Captured) the caller would wait until the server exits, so files are used instead.
function Invoke-PgCtlStart {
    param([string[]]$Arguments)
    $stdout = Join-Path $WorkDir 'pg_ctl.out'
    $stderr = Join-Path $WorkDir 'pg_ctl.err'
    $quoted = $Arguments | ForEach-Object { '"' + $_ + '"' }
    $process = Start-Process -FilePath (Join-Path $pgBin 'pg_ctl.exe') -ArgumentList $quoted -NoNewWindow -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    # Start-Process -Wait would also wait for the server, which is a child of pg_ctl.
    $process.WaitForExit()
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = ((Get-Content $stdout, $stderr -ErrorAction SilentlyContinue) -join "`n")
    }
}

$pgBin = Join-Path $RuntimeDir 'pgsql\bin'
$expected = @(
    'pgsql\bin\initdb.exe', 'pgsql\bin\pg_ctl.exe', 'pgsql\bin\postgres.exe', 'pgsql\bin\psql.exe', 'pgsql\bin\pg_restore.exe',
    'pgsql\share\proj\proj.db', 'osm2pgsql\osm2pgsql.exe', 'node\node.exe', 'mapnik\yukimapnik.dll', 'versions.json'
)
foreach ($file in $expected) {
    Write-Check (Test-Path (Join-Path $RuntimeDir $file)) "runtime contains $file"
}
$versions = Get-Content (Join-Path $RuntimeDir 'versions.json') -Raw | ConvertFrom-Json

# PostGIS must find proj.db relative to its PROJ library, without help from the environment.
foreach ($name in 'PROJ_LIB', 'PROJ_DATA', 'PGPASSWORD', 'PGDATA', 'PGHOST', 'PGPORT', 'PGUSER', 'PGDATABASE') {
    Remove-Item "env:$name" -ErrorAction SilentlyContinue
}

if (Test-Path $WorkDir) {
    Remove-Item -Recurse -Force $WorkDir
}
New-Item -ItemType Directory -Force $WorkDir | Out-Null
$dataDir = Join-Path $WorkDir 'data'
$logFile = Join-Path $WorkDir 'postgresql.log'
$user = 'yukiosm'
$password = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ })
$passwordFile = Join-Path $WorkDir 'password.txt'
[System.IO.File]::WriteAllText($passwordFile, $password)
$port = Get-FreePort
$serverStarted = $false

try {
    $r = Invoke-Captured (Join-Path $pgBin 'initdb.exe') @('-D', $dataDir, '-U', $user, "--pwfile=$passwordFile", '--auth=scram-sha-256', '-E', 'UTF8', '--locale=C', '--no-instructions')
    Write-Check ($r.ExitCode -eq 0) 'initdb (UTF8, locale C, scram-sha-256)'
    if ($r.ExitCode -ne 0) {
        throw $r.Output
    }

    $r = Invoke-PgCtlStart @('-D', $dataDir, '-l', $logFile, '-w', '-t', '60', '-o', "-p $port -c listen_addresses=127.0.0.1", 'start')
    $serverStarted = $r.ExitCode -eq 0
    Write-Check $serverStarted "pg_ctl start on 127.0.0.1:$port"
    if (-not $serverStarted) {
        throw "$($r.Output)`n$(Get-Content $logFile -Raw -ErrorAction SilentlyContinue)"
    }

    $env:PGPASSWORD = $password
    $psql = Join-Path $pgBin 'psql.exe'
    function Invoke-Sql([string]$Database, [string]$Sql) {
        return Invoke-Captured $psql @('-X', '-q', '-A', '-t', '-v', 'ON_ERROR_STOP=1', '-h', '127.0.0.1', '-p', "$port", '-U', $user, '-d', $Database, '-c', $Sql)
    }

    $r = Invoke-Sql 'postgres' 'CREATE DATABASE smoke'
    Write-Check ($r.ExitCode -eq 0) "CREATE DATABASE smoke $($r.Output)"
    $r = Invoke-Sql 'smoke' 'CREATE EXTENSION postgis'
    Write-Check ($r.ExitCode -eq 0) "CREATE EXTENSION postgis $($r.Output)"
    $r = Invoke-Sql 'smoke' 'CREATE EXTENSION hstore'
    Write-Check ($r.ExitCode -eq 0) "CREATE EXTENSION hstore $($r.Output)"
    $r = Invoke-Sql 'smoke' 'CREATE EXTENSION postgis_topology'
    Write-Check ($r.ExitCode -eq 0) "CREATE EXTENSION postgis_topology $($r.Output)"
    $r = Invoke-Sql 'smoke' 'SELECT postgis_full_version()'
    Write-Check ($r.ExitCode -eq 0 -and $r.Output -match "POSTGIS=`"$([regex]::Escape($versions.postgis))" -and $r.Output -notmatch 'procs need upgrade') "postgis_full_version(): $($r.Output)"
    $r = Invoke-Sql 'smoke' 'SELECT version()'
    Write-Check ($r.ExitCode -eq 0 -and $r.Output -match "PostgreSQL $([regex]::Escape($versions.postgresql))") "version(): $($r.Output)"

    $checks = [ordered]@{
        'PROJ (EPSG:4326 -> EPSG:25832 needs proj.db)' = @('SELECT ST_AsText(ST_SnapToGrid(ST_Transform(ST_SetSRID(ST_MakePoint(9, 51), 4326), 25832), 1))', '^POINT\(500000 564982[4-6]\)$')
        'GEOS (ST_Buffer)' = @('SELECT round(ST_Area(ST_Buffer(ST_MakePoint(0, 0), 10, 64))::numeric)', '^314$')
        'json-c (ST_GeomFromGeoJSON)' = @("SELECT ST_AsText(ST_GeomFromGeoJSON('{""type"":""Point"",""coordinates"":[1,2]}'))", '^POINT\(1 2\)$')
        'libxml2 (ST_GeomFromGML)' = @("SELECT ST_AsText(ST_GeomFromGML('<gml:Point xmlns:gml=""http://www.opengis.net/gml""><gml:coordinates>1,2</gml:coordinates></gml:Point>'))", '^POINT\(1 2\)$')
        'hstore' = @("SELECT ('name=>Yuki, highway=>primary'::hstore) -> 'highway'", '^primary$')
    }
    foreach ($check in $checks.GetEnumerator()) {
        $r = Invoke-Sql 'smoke' $check.Value[0]
        Write-Check ($r.ExitCode -eq 0 -and $r.Output.Trim() -match $check.Value[1]) "$($check.Key): $($r.Output.Trim())"
    }

    # osm2pgsql
    $osm2pgsql = Join-Path $RuntimeDir 'osm2pgsql\osm2pgsql.exe'
    $r = Invoke-Captured $osm2pgsql @('--version')
    Write-Check ($r.ExitCode -eq 0 -and $r.Output -match "osm2pgsql version $([regex]::Escape($versions.osm2pgsql))") "osm2pgsql --version: $(($r.Output -split "`n")[0])"

    $osmFile = Join-Path $WorkDir 'smoke.osm'
    $luaFile = Join-Path $WorkDir 'smoke.lua'
    [System.IO.File]::WriteAllText($osmFile, @'
<?xml version="1.0" encoding="UTF-8"?>
<osm version="0.6" generator="yukiosm-test">
  <node id="1" version="1" lat="51.0" lon="9.0"><tag k="name" v="Smoke"/><tag k="amenity" v="cafe"/></node>
  <node id="2" version="1" lat="51.0" lon="9.001"/>
  <node id="3" version="1" lat="51.001" lon="9.001"/>
  <way id="10" version="1"><nd ref="1"/><nd ref="2"/><nd ref="3"/><tag k="highway" v="residential"/></way>
</osm>
'@)
    [System.IO.File]::WriteAllText($luaFile, @'
local points = osm2pgsql.define_node_table('smoke_points', {
    { column = 'tags', type = 'hstore' },
    { column = 'geom', type = 'point', projection = 3857, not_null = true },
})
local lines = osm2pgsql.define_way_table('smoke_lines', {
    { column = 'highway', type = 'text' },
    { column = 'geom', type = 'linestring', projection = 3857, not_null = true },
})
function osm2pgsql.process_node(object)
    if object.tags.name then
        points:insert({ tags = object.tags, geom = object:as_point() })
    end
end
function osm2pgsql.process_way(object)
    lines:insert({ highway = object.tags.highway, geom = object:as_linestring() })
end
'@)
    $r = Invoke-Captured $osm2pgsql @('--create', '--output=flex', "--style=$luaFile", '-d', 'smoke', '-H', '127.0.0.1', '-P', "$port", '-U', $user, $osmFile)
    Write-Check ($r.ExitCode -eq 0) "osm2pgsql flex import$(if ($r.ExitCode -ne 0) { ": $($r.Output)" })"
    $r = Invoke-Sql 'smoke' "SELECT (SELECT tags -> 'name' FROM smoke_points) || ' ' || (SELECT ST_NPoints(geom) FROM smoke_lines) || ' ' || (SELECT ST_SRID(geom) FROM smoke_points)"
    Write-Check ($r.ExitCode -eq 0 -and $r.Output.Trim() -eq 'Smoke 3 3857') "imported data: $($r.Output.Trim())"

    # Node.js
    $r = Invoke-Captured (Join-Path $RuntimeDir 'node\node.exe') @('--version')
    Write-Check ($r.ExitCode -eq 0 -and $r.Output.Trim() -eq "v$($versions.node)") "node --version: $($r.Output.Trim())"
    $r = Invoke-Captured (Join-Path $RuntimeDir 'node\node.exe') @('-e', 'console.log(require("zlib").gzipSync("x").length > 0 ? "ok" : "bad")')
    Write-Check ($r.ExitCode -eq 0 -and $r.Output.Trim() -eq 'ok') 'node runs a script'

    # yukimapnik
    $smoke = Join-Path $ToolsDir 'yukimapnik-smoke.exe'
    if (Test-Path $smoke) {
        $outDir = Join-Path $WorkDir 'images'
        New-Item -ItemType Directory -Force $outDir | Out-Null
        $conninfo = "host=127.0.0.1 port=$port dbname=smoke user=$user password=$password"
        & $smoke (Join-Path $RuntimeDir 'mapnik\yukimapnik.dll') (Join-Path $ToolsDir 'fonts') --pg $conninfo --out $outDir
        Write-Check ($LASTEXITCODE -eq 0) 'yukimapnik smoke test (render, encode, PostGIS layers, reprojection, text)'
    }
    else {
        Write-Check $false "yukimapnik-smoke.exe not found in $ToolsDir"
    }
}
catch {
    Write-Check $false "unexpected error: $_"
}
finally {
    if ($serverStarted) {
        $r = Invoke-Captured (Join-Path $pgBin 'pg_ctl.exe') @('-D', $dataDir, '-m', 'fast', '-w', 'stop')
        Write-Check ($r.ExitCode -eq 0) 'pg_ctl stop'
    }
    Remove-Item env:PGPASSWORD -ErrorAction SilentlyContinue
    if ($script:Failures -gt 0 -and (Test-Path $logFile)) {
        Write-Host '--- PostgreSQL log ---'
        Get-Content $logFile | Select-Object -Last 40 | Write-Host
    }
    if (-not $KeepWorkDir) {
        Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
    }
}

if ($script:Failures -gt 0) {
    Write-Host "Runtime test FAILED ($script:Failures failures)" -ForegroundColor Red
    exit 1
}
Write-Host 'Runtime test passed' -ForegroundColor Green
