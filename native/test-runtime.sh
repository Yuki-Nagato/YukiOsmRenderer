#!/usr/bin/env bash
# YukiOsmRenderer - tests a Linux native runtime (artifacts/native/linux-<arch>/runtime).
#
# Usage: test-runtime.sh <runtime dir> [--tools <dir>] [--work-dir <dir>] [--keep-work-dir]
#
# Creates a temporary PostgreSQL cluster (UTF8, locale C, scram-sha-256 password authentication),
# starts it on a free localhost port, checks PostGIS (GEOS, PROJ, json-c, libxml2), hstore,
# imports a tiny OSM file with osm2pgsql (flex output), checks Node.js and runs the yukimapnik
# smoke test against the database, the yukimaplibre smoke test with SwiftShader and the Java runtime.
# Everything is stopped and removed afterwards.
# Must run as an unprivileged user (PostgreSQL refuses to run as root). Only needs glibc and bash.
set -uo pipefail

usage() {
    echo "usage: $0 <runtime dir> [--tools <dir>] [--work-dir <dir>] [--keep-work-dir]" >&2
    exit 2
}

[[ $# -ge 1 ]] || usage
runtime=$(cd "$1" && pwd) || usage
shift
tools="$(dirname "$runtime")/tools"
work="$(dirname "$runtime")/test-work"
keep_work=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --tools) tools=$2; shift 2 ;;
        --work-dir) work=$2; shift 2 ;;
        --keep-work-dir) keep_work=1; shift ;;
        *) usage ;;
    esac
done

if [[ $(id -u) -eq 0 ]]; then
    echo "error: run as an unprivileged user, PostgreSQL refuses to run as root" >&2
    exit 2
fi

failures=0
check() { # <condition exit code> <description>
    if [[ $1 -eq 0 ]]; then
        printf '[ OK ] %s\n' "$2"
    else
        printf '[FAIL] %s\n' "$2"
        failures=$((failures + 1))
    fi
}

json_value() { # <key>: reads a string value from versions.json
    sed -n "s/^ *\"$1\": *\"\\([^\"]*\\)\".*/\\1/p" "$runtime/versions.json" | head -n 1
}

for file in pgsql/bin/initdb pgsql/bin/pg_ctl pgsql/bin/postgres pgsql/bin/psql pgsql/bin/pg_restore \
    pgsql/share/proj/proj.db osm2pgsql/bin/osm2pgsql node/bin/node mapnik/libyukimapnik.so \
    maplibre/libyukimaplibre.so maplibre/libvulkan.so.1 jre/bin/java versions.json; do
    [[ -e $runtime/$file ]]
    check $? "runtime contains $file"
done

# Nothing from the environment may help resolving libraries or PROJ data.
unset LD_LIBRARY_PATH LD_PRELOAD PROJ_LIB PROJ_DATA PGPASSWORD PGDATA PGHOST PGPORT PGUSER PGDATABASE

unresolved=0
for file in "$runtime"/pgsql/bin/* "$runtime"/pgsql/lib/*.so* "$runtime"/osm2pgsql/bin/* "$runtime"/node/bin/node "$runtime"/mapnik/*.so \
    "$runtime"/maplibre/*.so*; do
    [[ -f $file && $(head -c 4 "$file" | od -An -c | tr -d ' \n') == '177ELF' ]] || continue
    missing=$(ldd "$file" 2>&1 | grep 'not found')
    if [[ -n $missing ]]; then
        unresolved=$((unresolved + 1))
        echo "$file: $missing"
    fi
done
check ${unresolved:-0} "all shared library dependencies resolve without LD_LIBRARY_PATH"

pg_bin="$runtime/pgsql/bin"
user=yukiosm
password=$(head -c 18 /dev/urandom | od -An -tx1 | tr -d ' \n')
rm -rf "$work"
mkdir -p "$work"
data="$work/data"
log_file="$work/postgresql.log"
printf '%s' "$password" > "$work/password.txt"

# A port is free if nothing accepts connections on it.
port=0
for _ in $(seq 1 50); do
    candidate=$((20000 + RANDOM % 30000))
    if ! (exec 3<>"/dev/tcp/127.0.0.1/$candidate") 2>/dev/null; then
        port=$candidate
        break
    fi
done

server_started=0
cleanup() {
    if [[ $server_started -eq 1 ]]; then
        "$pg_bin/pg_ctl" -D "$data" -m fast -w stop > /dev/null
        check $? "pg_ctl stop"
    fi
    if [[ $failures -gt 0 && -f $log_file ]]; then
        echo '--- PostgreSQL log ---'
        tail -n 40 "$log_file"
    fi
    [[ $keep_work -eq 1 ]] || rm -rf "$work"
    if [[ $failures -gt 0 ]]; then
        echo "Runtime test FAILED ($failures failures)"
        exit 1
    fi
    echo 'Runtime test passed'
}
trap cleanup EXIT

run() { # <description> <command>...: runs a command and records the result
    local description=$1 output
    shift
    output=$("$@" 2>&1)
    local rc=$?
    if [[ $rc -eq 0 ]]; then
        check 0 "$description"
    else
        check $rc "$description: $output"
    fi
    return $rc
}

run 'initdb (UTF8, locale C, scram-sha-256)' "$pg_bin/initdb" -D "$data" -U "$user" --pwfile="$work/password.txt" \
    --auth=scram-sha-256 -E UTF8 --locale=C --no-instructions || exit 1
# Unix sockets are disabled: the compiled-in default directory (/tmp) may not exist or be writable.
if run "pg_ctl start on 127.0.0.1:$port" "$pg_bin/pg_ctl" -D "$data" -l "$log_file" -w -t 60 \
    -o "-p $port -c listen_addresses=127.0.0.1 -c unix_socket_directories=''" start; then
    server_started=1
else
    exit 1
fi

export PGPASSWORD=$password
sql() { # <database> <sql>
    "$pg_bin/psql" -X -q -A -t -v ON_ERROR_STOP=1 -h 127.0.0.1 -p "$port" -U "$user" -d "$1" -c "$2" 2>&1
}
expect_sql() { # <description> <database> <sql> <extended regex>
    local output
    output=$(sql "$2" "$3")
    local rc=$?
    [[ $rc -eq 0 ]] && grep -Eq "$4" <<< "$output"
    check $? "$1${output:+: $output}"
}

expect_sql 'CREATE DATABASE smoke' postgres 'CREATE DATABASE smoke' '.*'
expect_sql 'CREATE EXTENSION postgis' smoke 'CREATE EXTENSION postgis' '.*'
expect_sql 'CREATE EXTENSION hstore' smoke 'CREATE EXTENSION hstore' '.*'
expect_sql 'CREATE EXTENSION postgis_topology' smoke 'CREATE EXTENSION postgis_topology' '.*'
expect_sql 'postgis_full_version()' smoke 'SELECT postgis_full_version()' "POSTGIS=\"$(json_value postgis)"
expect_sql 'version()' smoke 'SELECT version()' "^PostgreSQL $(json_value postgresql)"
expect_sql 'PROJ (EPSG:4326 -> EPSG:25832 needs proj.db)' smoke \
    'SELECT ST_AsText(ST_SnapToGrid(ST_Transform(ST_SetSRID(ST_MakePoint(9, 51), 4326), 25832), 1))' '^POINT\(500000 564982[4-6]\)$'
expect_sql 'GEOS (ST_Buffer)' smoke 'SELECT round(ST_Area(ST_Buffer(ST_MakePoint(0, 0), 10, 64))::numeric)' '^314$'
expect_sql 'json-c (ST_GeomFromGeoJSON)' smoke \
    "SELECT ST_AsText(ST_GeomFromGeoJSON('{\"type\":\"Point\",\"coordinates\":[1,2]}'))" '^POINT\(1 2\)$'
expect_sql 'libxml2 (ST_GeomFromGML)' smoke \
    "SELECT ST_AsText(ST_GeomFromGML('<gml:Point xmlns:gml=\"http://www.opengis.net/gml\"><gml:coordinates>1,2</gml:coordinates></gml:Point>'))" '^POINT\(1 2\)$'
expect_sql 'hstore' smoke "SELECT ('name=>Yuki, highway=>primary'::hstore) -> 'highway'" '^primary$'

# osm2pgsql
osm2pgsql="$runtime/osm2pgsql/bin/osm2pgsql"
output=$("$osm2pgsql" --version 2>&1)
grep -q "osm2pgsql version $(json_value osm2pgsql)" <<< "$output"
check $? "osm2pgsql --version: $(head -n 1 <<< "$output")"

cat > "$work/smoke.osm" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<osm version="0.6" generator="yukiosm-test">
  <node id="1" version="1" lat="51.0" lon="9.0"><tag k="name" v="Smoke"/><tag k="amenity" v="cafe"/></node>
  <node id="2" version="1" lat="51.0" lon="9.001"/>
  <node id="3" version="1" lat="51.001" lon="9.001"/>
  <way id="10" version="1"><nd ref="1"/><nd ref="2"/><nd ref="3"/><tag k="highway" v="residential"/></way>
</osm>
EOF
cat > "$work/smoke.lua" <<'EOF'
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
EOF
output=$("$osm2pgsql" --create --output=flex --style="$work/smoke.lua" -d smoke -H 127.0.0.1 -P "$port" -U "$user" "$work/smoke.osm" 2>&1)
rc=$?
check $rc "osm2pgsql flex import$([[ $rc -ne 0 ]] && printf ': %s' "$output")"
expect_sql 'imported data' smoke \
    "SELECT (SELECT tags -> 'name' FROM smoke_points) || ' ' || (SELECT ST_NPoints(geom) FROM smoke_lines) || ' ' || (SELECT ST_SRID(geom) FROM smoke_points)" \
    '^Smoke 3 3857$'

# Node.js
output=$("$runtime/node/bin/node" --version 2>&1)
[[ $output == "v$(json_value node)" ]]
check $? "node --version: $output"
output=$("$runtime/node/bin/node" -e 'console.log(require("zlib").gzipSync("x").length > 0 ? "ok" : "bad")' 2>&1)
[[ $output == ok ]]
check $? 'node runs a script'

# yukimapnik
if [[ -x $tools/yukimapnik-smoke ]]; then
    mkdir -p "$work/images"
    "$tools/yukimapnik-smoke" "$runtime/mapnik/libyukimapnik.so" "$tools/fonts" \
        --pg "host=127.0.0.1 port=$port dbname=smoke user=$user password=$password" --out "$work/images"
    check $? 'yukimapnik smoke test (render, encode, PostGIS layers, reprojection, text)'
else
    check 1 "yukimapnik-smoke not found in $tools"
fi

# yukimaplibre with SwiftShader (also renders on several threads at once)
if [[ -x $tools/yukimaplibre-smoke ]]; then
    output=$("$tools/yukimaplibre-smoke" "$runtime/maplibre/libyukimaplibre.so" "$runtime/maplibre/libvulkan.so.1" 2>&1)
    rc=$?
    [[ $rc -eq 0 ]] && grep -q "MapLibre Native $(json_value maplibreNative)" <<< "$output"
    check $? "yukimaplibre smoke test (SwiftShader, concurrent maps)$([[ $rc -ne 0 ]] && printf ': %s' "$output")"
else
    check 1 "yukimaplibre-smoke not found in $tools"
fi

# Java runtime for Planetiler
output=$("$runtime/jre/bin/java" -version 2>&1)
grep -q "Temurin-$(json_value temurin)" <<< "$output"
check $? "java -version: $(head -n 1 <<< "$output")"
