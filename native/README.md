# Native components

YukiOsmRenderer bundles the native programs that make up the OpenStreetMap rendering stack.
This directory contains the Mapnik and MapLibre Native wrapper libraries and the scripts that build or collect all
native components for each supported platform.

| Component      | Version         | Windows x64                       | Linux x64 / arm64 (glibc ≥ 2.31)       |
|----------------|-----------------|-----------------------------------|----------------------------------------|
| PostgreSQL     | 17.11           | EDB binary archive (17.11-5)      | built from source (Debian 11)          |
| PostGIS        | 3.6.2           | OSGeo Windows bundle              | built from source (Debian 11)          |
| osm2pgsql      | 2.3.1           | official Windows binaries         | built from source (Debian 11, LuaJIT)  |
| Mapnik         | 4.0.7           | vcpkg, linked into `yukimapnik`   | vcpkg, linked into `yukimapnik`        |
| Node.js        | 24.22.2         | official binaries                 | official binaries                      |
| MapLibre Native| android-v13.7.1 | patched, linked into `yukimaplibre` | patched, linked into `yukimaplibre` (manylinux_2_28, GCC 14) |
| SwiftShader    | master 2026-10-14 | built from source               | built from source (manylinux_2_28)     |
| Java runtime   | Temurin 25.0.5+8 | official binaries                | official binaries                      |

All versions, download URLs and SHA256 checksums are pinned in [`versions.env`](versions.env); vcpkg is pinned
by the commit `VCPKG_COMMIT`, which must equal `builtin-baseline` in
[`yukimapnik/vcpkg.json`](yukimapnik/vcpkg.json) and [`yukimaplibre/vcpkg.json`](yukimaplibre/vcpkg.json).
MapLibre Native and SwiftShader are pinned by commit; the patches applied to MapLibre Native are in
[`yukimaplibre/patches`](yukimaplibre/patches) and the submodules it needs in
[`yukimaplibre/submodules.txt`](yukimaplibre/submodules.txt).

## Runtime layout

Every build produces `artifacts/native/<rid>/runtime/` (`<rid>` is `win-x64`, `linux-x64` or
`linux-arm64`), which is copied next to the `yukiosm` executable in a release:

```
runtime/
  pgsql/       PostgreSQL with PostGIS (postgis, postgis_topology) and hstore: bin/ (initdb, pg_ctl, postgres,
               psql, pg_restore, shp2pgsql, ...), lib/, share/ (share/proj contains proj.db for PostGIS)
  osm2pgsql/   osm2pgsql executable (bin/osm2pgsql on Linux, osm2pgsql.exe on Windows) and its libraries
  mapnik/      yukimapnik shared library (yukimapnik.dll / libyukimapnik.so) and its dependencies,
               plus input/ when Mapnik input plugins are built as shared libraries
  node/        Node.js (node.exe on Windows, bin/node on Linux)
  maplibre/    yukimaplibre (yukimaplibre.dll / libyukimaplibre.so) and SwiftShader named like the Vulkan loader
               (vulkan-1.dll / libvulkan.so.1)
  jre/         Java runtime for Planetiler (bin/java)
  licenses/    license texts of all bundled components
  versions.json
```

On Linux all executables and libraries use `$ORIGIN`-relative RPATHs, so the runtime directory can be moved
anywhere. Nothing outside of it is required apart from glibc ≥ 2.31 (libstdc++ and libgcc_s are bundled as
well). The runtime contains no symbolic links.

Details that matter to the code using the runtime:

- **Mapnik plugins are linked statically** into yukimapnik (only the `postgis` input plugin is built), so there
  is no `mapnik/input/` directory; call `ym_initialize(NULL, fontsDir, ...)`. yukimapnik has no dependencies
  besides the operating system (static CRT on Windows, static libstdc++ and only `ym_*` exported on Linux).
- **Mapnik is built with PROJ** (with an embedded `proj.db`, unaffected by `PROJ_DATA`/`PROJ_LIB`), because
  Mapnik 4 without PROJ only accepts the SRS strings `epsg:3857`/`epsg:4326` and rejects the
  `+proj=merc ...` / `+proj=longlat ...` definitions used by OpenStreetMap Carto.
- **PostGIS finds `pgsql/share/proj/proj.db` by itself** (relative to its PROJ library). If `PROJ_DATA` is set
  for the server anyway, set `PROJ_LIB` to the same directory: the Windows PostGIS bundle uses PROJ 8.2,
  which only reads `PROJ_LIB`. Do not pass these variables to osm2pgsql on Windows, which ships its own PROJ 9
  and `proj.db` (osm2pgsql only uses EPSG:3857 and does not need PROJ).
- **Linux**: the compiled-in Unix socket directory of PostgreSQL is `/tmp`; start the server with
  `-c unix_socket_directories=<dir>` (or `''` and TCP only).
- **Windows**: `pg_ctl start` leaves the server running with the standard handles of `pg_ctl`. Do not capture
  them through pipes (use `-l <logfile>` and files/no redirection), otherwise reading the output blocks until the
  server exits. The Visual C++ runtime is deployed app-local in `pgsql/bin` and `osm2pgsql/`.
- Procedural languages that need external interpreters (PL/Perl, PL/Python, PL/Tcl) are not included.
- `node/` only contains the Node.js executable (no npm/npx/corepack).
- **yukimaplibre** links MapLibre Native and its dependencies statically (static CRT on Windows, static libstdc++
  and only `yml_*` exported on Linux). MapLibre Native renders with Vulkan; call `yml_load_vulkan` with the
  bundled SwiftShader before creating maps, which also makes MapLibre use it instead of a Vulkan loader of the
  system. Each map has its own thread and run loop; `yml_set_worker_threads` sizes the pool that parses and lays
  out tiles for all maps. The patches fix concurrent rendering, select faces of font collections (Noto Sans CJK
  `.ttc`) and share font data between maps.
- **SwiftShader** is built without window system integration and only needs the operating system.

## Building

Downloads and tool caches go to `artifacts/cache/`, results to `artifacts/native/<rid>/` (`runtime/` plus
`tools/`, which holds the `yukimapnik-smoke` test program and a test font and is not shipped). The first build
compiles Mapnik, Boost and ICU with vcpkg and takes roughly 30-90 minutes depending on the machine; later builds reuse the vcpkg binary
cache (`VCPKG_DEFAULT_BINARY_CACHE`, default `artifacts/cache/vcpkg-binary` on Windows and
`artifacts/cache/<rid>/vcpkg-binary` on Linux).

### Windows x64

Requires Visual Studio 2022 or later with the "Desktop development with C++" workload, git, Python 3 (SwiftShader's
build) and PowerShell 7.

```powershell
./native/build-windows.ps1
./native/test-runtime.ps1 -RuntimeDir artifacts/native/win-x64/runtime
```

`build-windows.ps1` bootstraps vcpkg in `artifacts/cache/vcpkg`, builds yukimapnik (triplet
`x64-windows-static-release`) and runs its smoke test, checks out MapLibre Native in `artifacts/cache/maplibre-native`,
applies the patches (again whenever they change) and builds yukimaplibre, builds SwiftShader from
`artifacts/cache/swiftshader`, downloads and verifies the PostgreSQL, PostGIS, osm2pgsql, Node.js and Java runtime
archives, merges the PostGIS bundle (`postgis`, `postgis_topology`, `shp2pgsql`, `pgsql2shp` and the
DLLs they need) into the PostgreSQL tree, removes pgAdmin 4, StackBuilder, documentation, headers and import
libraries, copies the Visual C++ runtime DLLs next to the executables that import them (checked with
`dumpbin /dependents`), and fails if any DLL dependency cannot be resolved.

### Linux x64 / arm64

Requires Docker; builds for the architecture of the Docker host (no cross compilation).

```sh
bash native/build-linux.sh             # build and test
bash native/build-linux.sh --skip-build # only test artifacts/native/<rid>
```

[`build-linux.sh`](build-linux.sh) builds the Debian 11 build image from [`linux/Dockerfile`](linux/Dockerfile),
runs [`linux/build.sh`](linux/build.sh) in it (PROJ 9.9 without network/TIFF support for PostGIS, LuaJIT,
PostgreSQL, PostGIS, osm2pgsql, yukimapnik with the `<arch>-linux-release` triplet, Node.js), bundles every
non-glibc shared library found with `ldd` and rewrites RPATHs with `patchelf`. MapLibre Native needs C++23, so
[`linux/build-maplibre.sh`](linux/build-maplibre.sh) then builds yukimaplibre and SwiftShader in a manylinux_2_28
container (AlmaLinux 8, GCC 14, [`linux/maplibre.Dockerfile`](linux/maplibre.Dockerfile)) with libstdc++ linked
statically, and adds them and the Java runtime to the runtime. It then runs
[`test-runtime.sh`](test-runtime.sh) in a clean `debian:11` container as an unprivileged user after moving the
runtime to a different path ([`linux/test-clean.sh`](linux/test-clean.sh)). GEOS, json-c, libxml2 (with ICU),
SQLite, expat, bzip2 and zlib come from Debian packages.

On a Windows host with Docker Desktop, use a Docker volume for the cache (bind mounts are slow):

```powershell
docker build -t yukiosm-native-build:debian11 native/linux
docker run --rm -v "${PWD}/native:/src:ro" -v yukiosm-native-cache-linux-x64:/cache `
    -v "${PWD}/artifacts/native/linux-x64:/out" yukiosm-native-build:debian11 bash /src/linux/build.sh
docker run --rm -v "${PWD}/native:/src:ro" -v "${PWD}/artifacts/native/linux-x64:/native:ro" `
    debian:11 bash /src/linux/test-clean.sh
```

### yukimapnik only

```sh
cd native/yukimapnik
export VCPKG_ROOT=/path/to/vcpkg   # checked out at VCPKG_COMMIT
cmake --preset linux-x64           # or win-x64 (from a Visual Studio developer prompt), linux-arm64
cmake --build --preset linux-x64
ctest --preset linux-x64
```

The manifest uses an overlay of the vcpkg `mapnik` port ([`yukimapnik/ports/mapnik`](yukimapnik/ports/mapnik))
that differs from the upstream port in three ways:

- `libpq` and `proj` are requested without their default features (OpenSSL, curl, TIFF).
- [`0001-reuse-harfbuzz-fonts.patch`](yukimapnik/ports/mapnik/0001-reuse-harfbuzz-fonts.patch) makes Mapnik reuse
  the HarfBuzz font of each font face while shaping. Unpatched Mapnik creates a new HarfBuzz font for every label
  and every face it tries; with OpenStreetMap Carto's fontset, a Chinese label tries about 57 faces before reaching
  Noto Sans CJK, which made label shaping take most of the rendering time. The rendered output is unchanged.
- [`0002-keep-postgis-connections-open.patch`](yukimapnik/ports/mapnik/0002-keep-postgis-connections-open.patch)
  returns the database connection used to initialize a PostGIS datasource to the connection pool instead of closing
  it (upstream closes it to support forking, which YukiOsmRenderer does not do). Otherwise loading a map opens one
  connection per layer while holding the pool lock, so concurrent map loads are serialized; on Windows, where
  starting a PostgreSQL backend is slow, loading a map took about 2.5 seconds.

Refresh the overlay from the vcpkg checkout when `VCPKG_COMMIT` changes. The overlay triplets in
[`yukimapnik/triplets`](yukimapnik/triplets) build release-only static libraries (static CRT on Windows, `-fPIC`
on Linux).

### Continuous integration

[`.github/workflows/native.yml`](../.github/workflows/native.yml) runs the same scripts on `windows-latest`,
`ubuntu-24.04` and `ubuntu-24.04-arm`, caches the vcpkg binary packages and uploads the artifacts
`native-win-x64` (`runtime/` and `tools/`) and `native-linux-x64` / `native-linux-arm64`
(`native-<rid>.tar.gz` with `runtime/` and `tools/`, because artifact archives do not keep Unix permissions).

## yukimapnik

[`yukimapnik`](yukimapnik) is a small C ABI over Mapnik used through P/Invoke. The contract is documented in
[`include/yukimapnik.h`](yukimapnik/include/yukimapnik.h). `yukimapnik-smoke` ([`test/smoke.cpp`](yukimapnik/test/smoke.cpp))
loads the shared library from a given path and checks every function: rendering (straight RGBA output),
encoding (`png256:z=5`, `png`), error reporting, concurrent use of separate maps and the registration of the
`postgis` datasource plugin; with `--pg <conninfo>` it also renders PostGIS layers including reprojection from
EPSG:4326 and text.
