#!/usr/bin/env bash
# YukiOsmRenderer - builds the Linux native runtime for the architecture of the Docker host.
#
# Usage: native/build-linux.sh [--skip-build] [--skip-test]
#
# 1. Builds the build environment image from native/linux/Dockerfile (Debian 11, glibc 2.31).
# 2. Runs native/linux/build.sh in it; the result is written to artifacts/native/linux-<arch>/.
# 3. Runs native/test-runtime.sh in a clean debian:11 container as an unprivileged user, with the
#    runtime moved to a different path.
#
# Environment:
#   YUKIOSM_NATIVE_CACHE  host directory or Docker volume name mounted as cache
#                         (default: artifacts/cache/linux-<arch>)
set -euo pipefail

native_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo_dir=$(dirname "$native_dir")

case "$(uname -m)" in
    x86_64 | amd64) rid=linux-x64 ;;
    aarch64 | arm64) rid=linux-arm64 ;;
    *) echo "unsupported architecture $(uname -m)" >&2; exit 1 ;;
esac

skip_build=0
skip_test=0
for arg in "$@"; do
    case "$arg" in
        --skip-build) skip_build=1 ;;
        --skip-test) skip_test=1 ;;
        *) echo "usage: $0 [--skip-build] [--skip-test]" >&2; exit 2 ;;
    esac
done

out_dir="$repo_dir/artifacts/native/$rid"
cache=${YUKIOSM_NATIVE_CACHE:-$repo_dir/artifacts/cache/$rid}
image=yukiosm-native-build:debian11
mkdir -p "$out_dir"
[[ $cache == */* ]] && mkdir -p "$cache"

if [[ $skip_build -eq 0 ]]; then
    echo "==> Building $image"
    docker build -t "$image" "$native_dir/linux"
    echo "==> Building $rid runtime"
    docker run --rm \
        -v "$native_dir:/src:ro" \
        -v "$cache:/cache" \
        -v "$out_dir:/out" \
        -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
        "$image" bash /src/linux/build.sh
fi

if [[ $skip_test -eq 0 ]]; then
    echo "==> Testing $rid runtime in a clean debian:11 container"
    docker run --rm \
        -v "$native_dir:/src:ro" \
        -v "$out_dir:/native:ro" \
        debian:11 bash /src/linux/test-clean.sh
fi
