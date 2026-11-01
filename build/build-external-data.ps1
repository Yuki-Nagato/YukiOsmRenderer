#!/usr/bin/env pwsh
# Builds the external data used by OpenStreetMap Carto (water polygons, Antarctic ice sheet) with the upstream
# scripts/get-external-data.py inside a PostGIS container and exports it as a pg_dump archive, so that the
# renderer can load it offline with pg_restore. Requires Docker.

[CmdletBinding()]
param(
    # Extracted OpenStreetMap Carto source tree (contains scripts/get-external-data.py and external-data.yml).
    [Parameter(Mandatory)]
    [string]$CartoDirectory,
    # Receives external-data.dump and external-data.json; downloaded shapefiles are kept in downloads/.
    [Parameter(Mandatory)]
    [string]$Output,
    # Must match the PostgreSQL major version bundled in runtime/pgsql.
    [string]$Image = 'postgis/postgis:17-3.6'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$CartoDirectory = (Resolve-Path $CartoDirectory).Path
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force (Join-Path $Output 'downloads') | Out-Null
$container = "yukiosm-external-data-$PID"

function Invoke-Docker {
    & docker @args
    if ($LASTEXITCODE -ne 0) {
        throw "docker $($args -join ' ') failed with exit code $LASTEXITCODE."
    }
}

try {
    Invoke-Docker run -d --name $container `
        -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=gis `
        -v "${CartoDirectory}:/carto:ro" -v "$(Join-Path $Output 'downloads'):/downloads" `
        $Image -c fsync=off -c synchronous_commit=off -c full_page_writes=off -c maintenance_work_mem=1GB | Out-Null
    Write-Host 'Waiting for PostgreSQL'
    # The image restarts the server once after initialization, so require several successful checks in a row.
    $ready = 0
    for ($i = 0; $i -lt 120 -and $ready -lt 3; $i++) {
        Start-Sleep -Seconds 2
        & docker exec $container pg_isready -U postgres -d gis -q
        $ready = if ($LASTEXITCODE -eq 0) { $ready + 1 } else { 0 }
    }
    if ($ready -lt 3) {
        throw 'PostgreSQL did not start.'
    }
    Invoke-Docker exec $container psql -U postgres -d gis -q -c 'CREATE EXTENSION IF NOT EXISTS postgis;'

    Write-Host 'Installing ogr2ogr and Python dependencies in the container'
    Invoke-Docker exec -e DEBIAN_FRONTEND=noninteractive $container bash -c 'apt-get update -qq && apt-get install -y -qq --no-install-recommends gdal-bin python3-psycopg2 python3-yaml python3-requests ca-certificates > /dev/null'

    Write-Host 'Running get-external-data.py'
    # Downloads from osmdata.openstreetmap.de occasionally fail; downloaded files and loaded tables are reused on retries.
    for ($attempt = 1; ; $attempt++) {
        & docker exec -w /carto $container python3 scripts/get-external-data.py `
            --config /carto/external-data.yml --data /downloads --cache `
            --database gis --host localhost --username postgres
        if ($LASTEXITCODE -eq 0) {
            break
        }
        if ($attempt -ge 3) {
            throw "get-external-data.py failed with exit code $LASTEXITCODE."
        }
        Write-Warning "get-external-data.py failed (attempt $attempt); retrying in 30 seconds"
        Start-Sleep -Seconds 30
    }

    Write-Host 'Exporting the tables'
    Invoke-Docker exec $container pg_dump -U postgres -d gis --format=custom --compress=6 --no-owner --no-privileges `
        --table=public.simplified_water_polygons --table=public.water_polygons `
        --table=public.icesheet_polygons --table=public.icesheet_outlines `
        --table=public.external_data --file=/tmp/external-data.dump
    Invoke-Docker cp "${container}:/tmp/external-data.dump" (Join-Path $Output 'external-data.dump.tmp')
    Move-Item -Force (Join-Path $Output 'external-data.dump.tmp') (Join-Path $Output 'external-data.dump')

    # The newest Last-Modified of the downloaded sources identifies the snapshot.
    $version = (& docker exec $container psql -U postgres -d gis -At -c "SELECT to_char(max(to_timestamp(last_modified, 'Dy, DD Mon YYYY HH24:MI:SS')), 'YYYY-MM-DD') FROM external_data")
    if ($LASTEXITCODE -ne 0 -or -not $version) {
        $version = (Get-Date -AsUTC).ToString('yyyy-MM-dd')
    }
    @{ version = "$version".Trim() } | ConvertTo-Json | Set-Content -NoNewline -Encoding utf8NoBOM (Join-Path $Output 'external-data.json')
    Write-Host "External data $version -> $(Join-Path $Output 'external-data.dump')"
}
finally {
    & docker rm -f $container 2>&1 | Out-Null
}
