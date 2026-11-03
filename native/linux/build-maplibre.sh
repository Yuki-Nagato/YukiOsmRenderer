#!/usr/bin/env bash
# YukiOsmRenderer - builds yukimaplibre (patched MapLibre Native) and SwiftShader and adds them and the Java
# runtime to a Linux runtime built by build.sh.
#
# Runs inside the manylinux_2_28 build container (native/linux/maplibre.Dockerfile); see native/build-linux.sh.
#   SRC_DIR   (default /src)   native/ directory of the repository, may be read-only
#   CACHE_DIR (default /cache) persistent cache: downloads, source checkouts, vcpkg checkout and binary cache
#   OUT_DIR   (default /out)   contains runtime/ and tools/ from build.sh; receives runtime/maplibre, runtime/jre
#   HOST_UID, HOST_GID         optional owner of the files written to OUT_DIR and CACHE_DIR
set -euo pipefail

SRC_DIR=${SRC_DIR:-/src}
# The helpers and pinned versions of build.sh (sourcing it does not run its build).
# shellcheck source=build.sh
source "$SRC_DIR/linux/build.sh"
STAGE=$OUT_DIR/runtime
MLN_SRC=$CACHE_DIR/maplibre-native
SWIFTSHADER_SRC=$CACHE_DIR/swiftshader
case $RID in
    linux-x64) TEMURIN_KEY=LINUX_X64 ;;
    linux-arm64) TEMURIN_KEY=LINUX_ARM64 ;;
esac
temurin_url_var="TEMURIN_${TEMURIN_KEY}_URL"
temurin_sha_var="TEMURIN_${TEMURIN_KEY}_SHA256"
TEMURIN_URL=${!temurin_url_var}
TEMURIN_SHA256=${!temurin_sha_var}

# sync_checkout <dir> <repository> <commit>: shallow checkout of one commit; returns 0 if the checkout changed.
sync_checkout() {
    local dir=$1 repository=$2 commit=$3
    git config --global --add safe.directory '*'
    if [[ ! -d $dir/.git ]]; then
        git init --quiet "$dir"
        git -C "$dir" remote add origin "$repository"
    fi
    if [[ $(git -C "$dir" rev-parse --verify --quiet 'HEAD^{commit}' || true) == "$commit" ]]; then
        return 1
    fi
    echo "Fetching $repository at $commit"
    git -C "$dir" fetch --quiet --depth 1 origin "$commit"
    git -C "$dir" checkout --quiet --force FETCH_HEAD
}

prepare_maplibre() {
    log "Preparing MapLibre Native $MAPLIBRE_NATIVE_VERSION"
    local changed=0 stamp patch marker="$MLN_SRC/.git/yukiosm-patches.txt"
    sync_checkout "$MLN_SRC" "$MAPLIBRE_NATIVE_REPOSITORY" "$MAPLIBRE_NATIVE_COMMIT" && changed=1
    stamp=$MAPLIBRE_NATIVE_COMMIT
    for patch in "$SRC_DIR"/yukimaplibre/patches/*.patch; do
        stamp+=";$(basename "$patch") $(sha256sum "$patch" | cut -d' ' -f1)"
    done
    if [[ $changed -eq 1 || ! -f $marker || $(cat "$marker") != "$stamp" ]]; then
        echo "Applying the yukimaplibre patches"
        git -C "$MLN_SRC" checkout --quiet --force "$MAPLIBRE_NATIVE_COMMIT"
        for patch in "$SRC_DIR"/yukimaplibre/patches/*.patch; do
            git -C "$MLN_SRC" apply "$patch"
        done
        echo "$stamp" > "$marker"
    fi
    # Top-level submodules first, then those nested in them ("<submodule> <path>").
    local top=() first second
    while read -r first second; do
        [[ -z $first || $first == \#* || -n $second ]] && continue
        top+=("$first")
    done < <(tr -d '\r' < "$SRC_DIR/yukimaplibre/submodules.txt")
    git -C "$MLN_SRC" submodule update --init --depth 1 --jobs 8 -- "${top[@]}"
    while read -r first second; do
        [[ -z $first || $first == \#* || -z $second ]] && continue
        git -C "$MLN_SRC/$first" submodule update --init --depth 1 -- "$second"
    done < <(tr -d '\r' < "$SRC_DIR/yukimaplibre/submodules.txt")
}

build_yukimaplibre() {
    log "Building yukimaplibre (MapLibre Native $MAPLIBRE_NATIVE_VERSION; the first build takes a while)"
    grep -q "\"builtin-baseline\": \"$VCPKG_COMMIT\"" "$SRC_DIR/yukimaplibre/vcpkg.json" \
        || die "builtin-baseline in yukimaplibre/vcpkg.json differs from VCPKG_COMMIT in versions.env"
    setup_vcpkg
    export YUKIMAPLIBRE_MLN_SOURCE_DIR=$MLN_SRC
    export YUKIMAPLIBRE_MLN_VERSION=$MAPLIBRE_NATIVE_VERSION
    local build="$WORK_DIR/yukimaplibre"
    (
        cd "$SRC_DIR/yukimaplibre"
        cmake --preset "$RID" -B "$build" \
            -DVCPKG_INSTALLED_DIR="$WORK_DIR/vcpkg_installed" \
            -DVCPKG_INSTALL_OPTIONS="--clean-after-build"
    )
    cmake --build "$build"
    rm -rf "$STAGE/maplibre"
    cmake --install "$build" --component runtime --prefix "$STAGE/maplibre"
    cmake --install "$build" --component smoke --prefix "$OUT_DIR/tools"

    rm -rf "$STAGE/licenses/maplibre"
    add_license "$MLN_SRC/LICENSE.md" maplibre LICENSE.md
    add_license "$MLN_SRC/LICENSES.core.md" maplibre LICENSES.core.md
    local port
    for port in "$WORK_DIR/vcpkg_installed/$TRIPLET/share"/*/; do
        port=$(basename "$port")
        [[ $port == vcpkg-* || ! -f $WORK_DIR/vcpkg_installed/$TRIPLET/share/$port/copyright ]] && continue
        add_license "$WORK_DIR/vcpkg_installed/$TRIPLET/share/$port/copyright" maplibre "$port.txt"
    done
    add_license "$SRC_DIR/licenses/maplibre.txt" maplibre README.txt
}

build_swiftshader() {
    log "Building SwiftShader ${SWIFTSHADER_COMMIT:0:12}"
    sync_checkout "$SWIFTSHADER_SRC" "$SWIFTSHADER_REPOSITORY" "$SWIFTSHADER_COMMIT" || true
    local build="$WORK_DIR/swiftshader"
    # Headless: no window system integration. libstdc++ is linked statically like everything else.
    cmake -S "$SWIFTSHADER_SRC" -B "$build" -G Ninja \
        -DCMAKE_BUILD_TYPE=Release \
        -DSWIFTSHADER_BUILD_TESTS=OFF \
        -DSWIFTSHADER_BUILD_BENCHMARKS=OFF \
        -DSWIFTSHADER_BUILD_PVR=OFF \
        -DSWIFTSHADER_WARNINGS_AS_ERRORS=OFF \
        -DSWIFTSHADER_ENABLE_ASTC=OFF \
        -DSWIFTSHADER_BUILD_WSI_XCB=OFF \
        -DSWIFTSHADER_BUILD_WSI_WAYLAND=OFF \
        "-DCMAKE_SHARED_LINKER_FLAGS=-static-libstdc++ -static-libgcc -Wl,--exclude-libs,ALL"
    cmake --build "$build" --target vk_swiftshader
    # Named like the Vulkan loader, which MapLibre Native loads.
    cp "$build/Linux/libvk_swiftshader.so" "$STAGE/maplibre/libvulkan.so.1"
    patchelf --set-soname libvulkan.so.1 "$STAGE/maplibre/libvulkan.so.1"

    rm -rf "$STAGE/licenses/swiftshader"
    add_license "$SWIFTSHADER_SRC/LICENSE.txt" swiftshader LICENSE.txt
    add_license "$SWIFTSHADER_SRC/third_party/marl/LICENSE" swiftshader marl.txt
    add_license "$SWIFTSHADER_SRC/third_party/SPIRV-Tools/LICENSE" swiftshader SPIRV-Tools.txt
    add_license "$SWIFTSHADER_SRC/third_party/SPIRV-Headers/LICENSE" swiftshader SPIRV-Headers.txt
    add_license "$SRC_DIR/licenses/llvm.txt" swiftshader LLVM.txt
    add_license "$SRC_DIR/licenses/swiftshader.txt" swiftshader README.txt
}

install_jre() {
    log "Installing the Temurin Java runtime $TEMURIN_VERSION"
    local archive link
    archive=$(download "$TEMURIN_URL" "$TEMURIN_SHA256")
    extract "$archive" "$STAGE/jre"
    # Like the rest of the runtime, no symbolic links.
    while IFS= read -r link; do
        cp --remove-destination "$(readlink -f "$link")" "$link"
    done < <(find "$STAGE/jre" -type l)
    rm -rf "$STAGE/licenses/temurin"
    add_license "$STAGE/jre/NOTICE" temurin NOTICE.txt
}

finalize_maplibre() {
    log "Checking yukimaplibre and SwiftShader"
    strip --strip-unneeded "$STAGE/maplibre/libyukimaplibre.so" "$STAGE/maplibre/libvulkan.so.1"
    local f line
    for f in "$STAGE/maplibre/libyukimaplibre.so" "$STAGE/maplibre/libvulkan.so.1"; do
        while IFS= read -r line; do
            [[ $line == *"not found"* ]] && die "$f: unresolved dependency: $line"
            [[ $line =~ ^[[:space:]]*([^[:space:]]+)[[:space:]]=\> ]] || continue
            [[ ${BASH_REMATCH[1]} =~ $GLIBC_RE ]] || die "$f needs ${BASH_REMATCH[1]}, which is not part of glibc"
        done < <(ldd "$f")
    done
    python3 - "$STAGE/versions.json" <<EOF
import json, sys
path = sys.argv[1]
with open(path) as f:
    versions = json.load(f)
# The versions come before the sources, so that the first occurrence of a key is the version (see test-runtime.sh).
sources = versions.pop("sources")
versions["maplibreNative"] = "$MAPLIBRE_NATIVE_VERSION"
versions["swiftshader"] = "$SWIFTSHADER_COMMIT"
versions["temurin"] = "$TEMURIN_VERSION"
sources["maplibreNative"] = "$MAPLIBRE_NATIVE_REPOSITORY@$MAPLIBRE_NATIVE_COMMIT with native/yukimaplibre/patches"
sources["swiftshader"] = "$SWIFTSHADER_REPOSITORY@$SWIFTSHADER_COMMIT"
sources["temurin"] = "$TEMURIN_URL"
versions["sources"] = sources
with open(path, "w") as f:
    json.dump(versions, f, indent=2)
    f.write("\n")
EOF
    log "Running the yukimaplibre smoke test"
    "$OUT_DIR/tools/yukimaplibre-smoke" "$STAGE/maplibre/libyukimaplibre.so" "$STAGE/maplibre/libvulkan.so.1"
    "$STAGE/jre/bin/java" -version
}

main_maplibre() {
    [[ -f $STAGE/versions.json ]] || die "$STAGE/versions.json not found; run build.sh first"
    mkdir -p "$WORK_DIR" "$CACHE_DIR"
    prepare_maplibre
    build_yukimaplibre
    build_swiftshader
    install_jre
    finalize_maplibre
    if [[ -n ${HOST_UID:-} ]]; then
        chown -R "$HOST_UID:${HOST_GID:-$HOST_UID}" "$OUT_DIR" "$CACHE_DIR"
    fi
    log "MapLibre Native, SwiftShader and the Java runtime added to $STAGE"
}

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
    main_maplibre "$@"
fi
