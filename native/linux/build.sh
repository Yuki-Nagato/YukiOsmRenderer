#!/usr/bin/env bash
# YukiOsmRenderer - builds the Linux native runtime (linux-x64 or linux-arm64, by host architecture).
#
# Runs inside the Debian 11 build container (native/linux/Dockerfile); see native/build-linux.sh.
#   SRC_DIR   (default /src)   native/ directory of the repository, may be read-only
#   CACHE_DIR (default /cache) persistent cache: downloads, vcpkg checkout, vcpkg binary cache
#   OUT_DIR   (default /out)   receives runtime/ and tools/ (artifacts/native/linux-<arch>)
#   HOST_UID, HOST_GID         optional owner of the files written to OUT_DIR and CACHE_DIR
set -euo pipefail

SRC_DIR=${SRC_DIR:-/src}
CACHE_DIR=${CACHE_DIR:-/cache}
OUT_DIR=${OUT_DIR:-/out}
WORK_DIR=${WORK_DIR:-/build}
JOBS=${JOBS:-$(nproc)}
# Install prefix used while building. The finished runtime is relocatable.
STAGE=/opt/yukiosm/runtime
DEPS=/opt/yukiosm/deps
# Libraries provided by glibc itself; everything else is bundled.
GLIBC_RE='^(ld-linux[-a-z0-9_]*|libc|libm|libdl|libpthread|librt|libutil|libresolv|libanl|libnsl|libmvec|libBrokenLocale|libthread_db|libnss_[a-z0-9]+)\.so'

log() { printf '\n==> %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

case "$(uname -m)" in
    x86_64) RID=linux-x64; TRIPLET=x64-linux-release; NODE_KEY=LINUX_X64 ;;
    aarch64) RID=linux-arm64; TRIPLET=arm64-linux-release; NODE_KEY=LINUX_ARM64 ;;
    *) die "unsupported architecture $(uname -m)" ;;
esac

# Pinned versions (CR is stripped in case the checkout uses CRLF line endings).
while IFS='=' read -r key value; do
    [[ $key =~ ^[A-Z0-9_]+$ ]] || continue
    declare "$key=$value"
done < <(tr -d '\r' < "$SRC_DIR/versions.env")
node_url_var="NODE_${NODE_KEY}_URL"
node_sha_var="NODE_${NODE_KEY}_SHA256"
NODE_URL=${!node_url_var}
NODE_SHA256=${!node_sha_var}

# download <url> <sha256> [file name]: prints the path of the verified file in the cache.
download() {
    local url=$1 sha=$2 name=${3:-$(basename "$1")}
    local path="$CACHE_DIR/downloads/$name"
    mkdir -p "$CACHE_DIR/downloads"
    if [[ -f $path ]] && echo "$sha  $path" | sha256sum -c --status -; then
        echo "$path"
        return
    fi
    echo "Downloading $url" >&2
    curl -fsSL --retry 5 --retry-delay 5 -o "$path.partial" "$url"
    echo "$sha  $path.partial" | sha256sum -c --status - || die "SHA256 mismatch for $url"
    mv "$path.partial" "$path"
    echo "$path"
}

# extract <archive> <directory>: extracts an archive with a single top-level directory.
extract() {
    rm -rf "$2"
    mkdir -p "$2"
    tar -xf "$1" -C "$2" --strip-components=1
}

is_elf() {
    [[ -f $1 && ! -L $1 ]] && [[ $(head -c 4 "$1" | od -An -c | tr -d ' \n') == '177ELF' ]]
}

elf_files() {
    local f
    while IFS= read -r -d '' f; do
        is_elf "$f" && printf '%s\n' "$f"
    done < <(find "$@" -type f -print0)
    return 0
}

add_license() { # <file> <component> [name]
    local dir="$STAGE/licenses/$2"
    mkdir -p "$dir"
    cp "$1" "$dir/${3:-$(basename "$1")}"
}

# Adds the Debian copyright file of the package that owns a bundled library.
add_debian_license() {
    local path=$1 candidate pkg
    for candidate in "$path" "$(readlink -f "$path")" "/usr$path" "${path#/usr}"; do
        pkg=$(dpkg -S "$candidate" 2>/dev/null | head -n 1 | cut -d: -f1) || true
        if [[ -n $pkg ]]; then
            [[ -f /usr/share/doc/$pkg/copyright ]] && add_license "/usr/share/doc/$pkg/copyright" debian "$pkg.txt"
            return
        fi
    done
    echo "warning: no Debian package found for $path" >&2
}

build_proj() {
    log "Building PROJ $PROJ_VERSION (for PostGIS)"
    local src="$WORK_DIR/proj"
    local archive
    archive=$(download "$PROJ_SOURCE_URL" "$PROJ_SOURCE_SHA256")
    extract "$archive" "$src"
    cmake -S "$src" -B "$src/build" -G Ninja \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX="$DEPS/proj" \
        -DCMAKE_INSTALL_LIBDIR=lib \
        -DBUILD_SHARED_LIBS=ON \
        -DBUILD_APPS=OFF \
        -DBUILD_TESTING=OFF \
        -DENABLE_CURL=OFF \
        -DENABLE_TIFF=OFF \
        -DEMBED_PROJ_DATA_PATH=OFF
    cmake --build "$src/build" -j "$JOBS"
    cmake --install "$src/build"
    add_license "$src/COPYING" proj COPYING.txt
}

build_luajit() {
    log "Building LuaJIT $LUAJIT_VERSION (for osm2pgsql)"
    local src="$WORK_DIR/luajit"
    local archive
    archive=$(download "$LUAJIT_SOURCE_URL" "$LUAJIT_SOURCE_SHA256" "luajit-$LUAJIT_VERSION.tar.gz")
    extract "$archive" "$src"
    make -C "$src" -j "$JOBS" amalg PREFIX="$DEPS/luajit" BUILDMODE=static CFLAGS=-fPIC
    make -C "$src" install PREFIX="$DEPS/luajit"
    add_license "$src/COPYRIGHT" luajit COPYRIGHT.txt
}

build_postgresql() {
    log "Building PostgreSQL $POSTGRESQL_VERSION"
    local src="$WORK_DIR/postgresql"
    local archive
    archive=$(download "$POSTGRESQL_SOURCE_URL" "$POSTGRESQL_SOURCE_SHA256")
    extract "$archive" "$src"
    (
        cd "$src"
        ./configure --prefix="$STAGE/pgsql" --disable-rpath --without-icu --without-readline \
            --without-lz4 --without-zstd --with-zlib
    )
    make -C "$src" -j "$JOBS"
    make -C "$src" install
    make -C "$src/contrib" -j "$JOBS"
    make -C "$src/contrib" install
    add_license "$src/COPYRIGHT" postgresql COPYRIGHT.txt
}

build_postgis() {
    log "Building PostGIS $POSTGIS_VERSION"
    local src="$WORK_DIR/postgis"
    local archive
    archive=$(download "$POSTGIS_SOURCE_URL" "$POSTGIS_SOURCE_SHA256")
    extract "$archive" "$src"
    (
        cd "$src"
        ./configure --with-pgconfig="$STAGE/pgsql/bin/pg_config" --with-projdir="$DEPS/proj" \
            --without-raster --without-protobuf --without-address-standardizer --without-tiger \
            --without-gui --without-phony-revision --without-interrupt-tests
    )
    # Not parallel: PostGIS's utils directory regenerates the same SQL files (e.g. raster/rt_pg/rtpostgis.sql) from
    # several sub-makes at once, which races under -j and intermittently fails the build.
    make -C "$src"
    make -C "$src" install
    add_license "$src/LICENSE.TXT" postgis LICENSE.txt
    add_license "$src/COPYING" postgis COPYING.txt
}

build_osm2pgsql() {
    log "Building osm2pgsql $OSM2PGSQL_VERSION"
    local src="$WORK_DIR/osm2pgsql"
    local archive
    archive=$(download "$OSM2PGSQL_SOURCE_URL" "$OSM2PGSQL_SOURCE_SHA256" "osm2pgsql-$OSM2PGSQL_VERSION.tar.gz")
    extract "$archive" "$src"
    cmake -S "$src" -B "$src/build" -G Ninja \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX="$STAGE/osm2pgsql" \
        -DBUILD_TESTS=OFF \
        -DWITH_PROJ=OFF \
        -DWITH_LUAJIT=ON \
        -DLUAJIT_INCLUDE_DIR="$DEPS/luajit/include/luajit-2.1" \
        -DLUAJIT_LIBRARY="$DEPS/luajit/lib/libluajit-5.1.a" \
        "-DLUAJIT_LIBRARIES=$DEPS/luajit/lib/libluajit-5.1.a;m;dl" \
        -DCMAKE_POLICY_DEFAULT_CMP0074=NEW \
        -DPostgreSQL_ROOT="$STAGE/pgsql"
    cmake --build "$src/build" -j "$JOBS"
    cmake --install "$src/build"
    rm -rf "$STAGE/osm2pgsql/share/man" "$STAGE/osm2pgsql/bin/osm2pgsql-replication"
    add_license "$src/COPYING" osm2pgsql COPYING.txt
}

build_yukimapnik() {
    log "Building yukimapnik (Mapnik $MAPNIK_VERSION via vcpkg; the first build takes a while)"
    grep -q "\"builtin-baseline\": \"$VCPKG_COMMIT\"" "$SRC_DIR/yukimapnik/vcpkg.json" \
        || die "builtin-baseline in yukimapnik/vcpkg.json differs from VCPKG_COMMIT in versions.env"
    local vcpkg_root="$CACHE_DIR/vcpkg"
    git config --global --add safe.directory '*'
    if [[ ! -d $vcpkg_root/.git ]]; then
        git clone --quiet "$VCPKG_REPOSITORY" "$vcpkg_root"
    fi
    if [[ $(git -C "$vcpkg_root" rev-parse HEAD) != "$VCPKG_COMMIT" ]]; then
        git -C "$vcpkg_root" cat-file -e "$VCPKG_COMMIT^{commit}" 2>/dev/null || git -C "$vcpkg_root" fetch --quiet origin
        git -C "$vcpkg_root" checkout --quiet --force "$VCPKG_COMMIT"
    fi
    if [[ ! -x $vcpkg_root/vcpkg || $(cat "$vcpkg_root/yukiosm-bootstrapped.txt" 2>/dev/null) != "$VCPKG_COMMIT" ]]; then
        "$vcpkg_root/bootstrap-vcpkg.sh" -disableMetrics
        echo "$VCPKG_COMMIT" > "$vcpkg_root/yukiosm-bootstrapped.txt"
    fi
    export VCPKG_ROOT=$vcpkg_root
    export VCPKG_DISABLE_METRICS=1
    export VCPKG_FORCE_SYSTEM_BINARIES=1
    export VCPKG_DOWNLOADS=$CACHE_DIR/vcpkg-downloads
    export VCPKG_DEFAULT_BINARY_CACHE=${VCPKG_DEFAULT_BINARY_CACHE:-$CACHE_DIR/vcpkg-binary}
    mkdir -p "$VCPKG_DOWNLOADS" "$VCPKG_DEFAULT_BINARY_CACHE"

    local build="$WORK_DIR/yukimapnik"
    (
        cd "$SRC_DIR/yukimapnik"
        cmake --preset "$RID" -B "$build" \
            -DVCPKG_INSTALLED_DIR="$WORK_DIR/vcpkg_installed" \
            -DVCPKG_INSTALL_OPTIONS="--clean-after-build"
    )
    cmake --build "$build"
    ctest --test-dir "$build" --output-on-failure
    cmake --install "$build" --component runtime --prefix "$STAGE/mapnik"
    cmake --install "$build" --component smoke --prefix "$WORK_DIR/tools"

    local port
    for port in "$WORK_DIR/vcpkg_installed/$TRIPLET/share"/*/; do
        port=$(basename "$port")
        [[ $port == vcpkg-* || ! -f $WORK_DIR/vcpkg_installed/$TRIPLET/share/$port/copyright ]] && continue
        add_license "$WORK_DIR/vcpkg_installed/$TRIPLET/share/$port/copyright" mapnik "$port.txt"
    done
    add_license "$SRC_DIR/licenses/mapnik.txt" mapnik README.txt
}

install_node() {
    log "Installing Node.js $NODE_VERSION"
    local src="$WORK_DIR/node"
    local archive
    archive=$(download "$NODE_URL" "$NODE_SHA256")
    extract "$archive" "$src"
    # Only the executable is needed; npm/npx/corepack are not used at runtime.
    mkdir -p "$STAGE/node/bin"
    cp "$src/bin/node" "$STAGE/node/bin/node"
    add_license "$src/LICENSE" node LICENSE.txt
}

# Replaces the symlinks of shared libraries by a single file named after the SONAME.
normalize_shared_libs() {
    local dir=$1 f soname
    find "$dir" -maxdepth 1 -type l -delete
    for f in "$dir"/*.so*; do
        is_elf "$f" || continue
        soname=$(patchelf --print-soname "$f" 2>/dev/null || true)
        if [[ -n $soname && $(basename "$f") != "$soname" ]]; then
            mv "$f" "$dir/$soname"
        fi
    done
}

set_rpath() { # <rpath> <file>...
    local rpath=$1 f
    shift
    for f in "$@"; do
        patchelf --force-rpath --set-rpath "$rpath" "$f"
    done
}

# bundle_dependencies <libdir> <extra library path> <file>...: copies every non-glibc library that
# the files need (directly or indirectly) into libdir, unless it is already there.
bundle_dependencies() {
    local libdir=$1 extra=$2 file line name path
    shift 2
    mkdir -p "$libdir"
    for file in "$@"; do
        while IFS= read -r line; do
            if [[ $line == *"not found"* ]]; then
                die "$file: unresolved dependency: $line"
            fi
            [[ $line =~ ^[[:space:]]*([^[:space:]]+)[[:space:]]=\>[[:space:]](/[^[:space:]]+)[[:space:]]\(0x ]] || continue
            name=${BASH_REMATCH[1]}
            path=${BASH_REMATCH[2]}
            [[ $name =~ $GLIBC_RE ]] && continue
            [[ $path == "$libdir"/* || -e $libdir/$name ]] && continue
            echo "    bundling $name ($path) -> ${libdir#"$STAGE"/}"
            cp -L "$path" "$libdir/$name"
            chmod 0644 "$libdir/$name"
            patchelf --force-rpath --set-rpath '$ORIGIN' "$libdir/$name"
            [[ $path == "$STAGE"/* || $path == "$DEPS"/* ]] || add_debian_license "$path"
        done < <(LD_LIBRARY_PATH="$extra" ldd "$file")
    done
}

# Fails if an ELF file of the runtime resolves a non-glibc library outside of the runtime.
verify_runtime() {
    local f line path
    while IFS= read -r f; do
        while IFS= read -r line; do
            [[ $line == *"not found"* ]] && die "$f: unresolved dependency: $line"
            [[ $line =~ =\>[[:space:]](/[^[:space:]]+)[[:space:]]\(0x ]] || continue
            path=${BASH_REMATCH[1]}
            [[ $path == "$STAGE"/* ]] && continue
            [[ $(basename "$path") =~ $GLIBC_RE ]] || die "$f resolves $path outside of the runtime"
        done < <(env -u LD_LIBRARY_PATH ldd "$f")
    done < <(elf_files "$STAGE")
    if [[ -n $(find "$STAGE" -type l) ]]; then
        die "the runtime must not contain symbolic links: $(find "$STAGE" -type l | head -n 5)"
    fi
}

finalize_runtime() {
    log "Bundling shared libraries and setting RPATHs"
    rm -rf "$STAGE/pgsql/include" "$STAGE/pgsql/lib/pgxs" "$STAGE/pgsql/lib/pkgconfig" \
        "$STAGE/pgsql/share/doc" "$STAGE/pgsql/share/man"
    find "$STAGE/pgsql/lib" -name '*.a' -delete

    local f
    while IFS= read -r f; do
        strip --strip-unneeded "$f"
    done < <(elf_files "$STAGE/pgsql" "$STAGE/osm2pgsql" "$STAGE/mapnik")

    normalize_shared_libs "$STAGE/pgsql/lib"
    # Other symbolic links (PostGIS upgrade path stubs) are replaced by copies, so the runtime can
    # be stored on file systems and in archive formats without symlink support.
    local link
    while IFS= read -r link; do
        cp --remove-destination "$(readlink -f "$link")" "$link"
    done < <(find "$STAGE" -type l)
    # PROJ looks for proj.db in <libproj dir>/../share/proj, i.e. pgsql/share/proj.
    mkdir -p "$STAGE/pgsql/share/proj"
    cp -r "$DEPS/proj/share/proj/." "$STAGE/pgsql/share/proj/"

    mapfile -t pg_bin < <(elf_files "$STAGE/pgsql/bin")
    mapfile -t pg_lib < <(elf_files "$STAGE/pgsql/lib")
    set_rpath '$ORIGIN/../lib' "${pg_bin[@]}"
    set_rpath '$ORIGIN' "${pg_lib[@]}"
    bundle_dependencies "$STAGE/pgsql/lib" "$DEPS/proj/lib" "${pg_bin[@]}" "${pg_lib[@]}"

    mapfile -t osm_bin < <(elf_files "$STAGE/osm2pgsql/bin")
    set_rpath '$ORIGIN/../lib' "${osm_bin[@]}"
    bundle_dependencies "$STAGE/osm2pgsql/lib" "$STAGE/pgsql/lib" "${osm_bin[@]}"

    set_rpath '$ORIGIN/../lib' "$STAGE/node/bin/node"
    bundle_dependencies "$STAGE/node/lib" "" "$STAGE/node/bin/node"

    # libyukimapnik.so links everything statically except glibc; this only guards against regressions.
    set_rpath '$ORIGIN' "$STAGE/mapnik/libyukimapnik.so"
    bundle_dependencies "$STAGE/mapnik" "" "$STAGE/mapnik/libyukimapnik.so"

    verify_runtime
}

write_versions() {
    cat > "$STAGE/versions.json" <<EOF
{
  "rid": "$RID",
  "postgresql": "$POSTGRESQL_VERSION",
  "postgis": "$POSTGIS_VERSION",
  "osm2pgsql": "$OSM2PGSQL_VERSION",
  "mapnik": "$MAPNIK_VERSION",
  "node": "$NODE_VERSION",
  "mapnikPlugins": "static",
  "sources": {
    "postgresql": "$POSTGRESQL_SOURCE_URL",
    "postgis": "$POSTGIS_SOURCE_URL",
    "proj": "$PROJ_SOURCE_URL",
    "osm2pgsql": "$OSM2PGSQL_SOURCE_URL",
    "luajit": "$LUAJIT_SOURCE_URL",
    "mapnik": "vcpkg $VCPKG_REPOSITORY@$VCPKG_COMMIT",
    "node": "$NODE_URL"
  }
}
EOF
}

smoke_test_runtime() {
    log "Checking the assembled runtime"
    "$STAGE/pgsql/bin/postgres" --version
    "$STAGE/pgsql/bin/psql" --version
    "$STAGE/osm2pgsql/bin/osm2pgsql" --version 2>&1 | head -n 1
    "$STAGE/node/bin/node" --version
    "$WORK_DIR/tools/yukimapnik-smoke" "$STAGE/mapnik/libyukimapnik.so" "$WORK_DIR/tools/fonts"
}

main() {
    rm -rf "$STAGE" "$DEPS"
    mkdir -p "$STAGE" "$DEPS" "$WORK_DIR" "$CACHE_DIR"
    build_proj
    build_luajit
    build_postgresql
    build_postgis
    build_osm2pgsql
    build_yukimapnik
    install_node
    finalize_runtime
    write_versions
    smoke_test_runtime

    log "Copying runtime to $OUT_DIR"
    mkdir -p "$OUT_DIR"
    rm -rf "$OUT_DIR/runtime" "$OUT_DIR/tools"
    cp -a "$STAGE" "$OUT_DIR/runtime"
    cp -a "$WORK_DIR/tools" "$OUT_DIR/tools"
    if [[ -n ${HOST_UID:-} ]]; then
        chown -R "$HOST_UID:${HOST_GID:-$HOST_UID}" "$OUT_DIR" "$CACHE_DIR"
    fi
    log "Runtime ready: $(du -sh "$OUT_DIR/runtime" | cut -f1) in $OUT_DIR/runtime"
}

# Allows sourcing the script to run single steps while debugging.
if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
    main "$@"
fi
