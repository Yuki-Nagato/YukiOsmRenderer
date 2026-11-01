# Build environment for the Linux native runtime (linux-x64 and linux-arm64).
# Debian 11 provides the glibc 2.31 baseline; the image only contains the toolchain, the sources
# are mounted at build time (see native/build-linux.sh).
FROM debian:11

ARG CMAKE_VERSION=4.4.3
ARG CMAKE_SHA256_X86_64=d6c83076c575bc00b823522ac974bda66d0af05d6ddc30e739c12385cf32c6cc
ARG CMAKE_SHA256_AARCH64=2efc974dbd63b4444c0e8494b92f2e80c2d7e635b4b80eac2916985ddd8f72a6

ENV DEBIAN_FRONTEND=noninteractive \
    LANG=C.UTF-8 \
    VCPKG_FORCE_SYSTEM_BINARIES=1

# Debian 11 has reached its end of life; its packages are served by archive.debian.org.
RUN printf '%s\n' \
        'deb http://archive.debian.org/debian bullseye main' \
        'deb http://archive.debian.org/debian bullseye-updates main' \
        'deb http://archive.debian.org/debian-security bullseye-security main' \
        > /etc/apt/sources.list \
    && printf 'Acquire::Check-Valid-Until "false";\nAcquire::Retries "5";\n' > /etc/apt/apt.conf.d/99archive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        autoconf \
        autoconf-archive \
        automake \
        bison \
        bzip2 \
        ca-certificates \
        curl \
        file \
        flex \
        g++ \
        gcc \
        git \
        libboost-dev \
        libbz2-dev \
        libexpat1-dev \
        libgeos-dev \
        libjson-c-dev \
        libsqlite3-dev \
        libtool \
        libxml2-dev \
        make \
        ninja-build \
        nlohmann-json3-dev \
        patch \
        patchelf \
        perl \
        pkg-config \
        python3 \
        sqlite3 \
        tar \
        unzip \
        xz-utils \
        zip \
        zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

# Debian 11 ships CMake 3.18; vcpkg, PROJ and the presets need a newer one.
RUN set -eu; \
    arch="$(uname -m)"; \
    case "$arch" in \
        x86_64) sha="$CMAKE_SHA256_X86_64" ;; \
        aarch64) sha="$CMAKE_SHA256_AARCH64" ;; \
        *) echo "unsupported architecture $arch" >&2; exit 1 ;; \
    esac; \
    curl -fsSL -o /opt/cmake.tar.gz "https://github.com/Kitware/CMake/releases/download/v${CMAKE_VERSION}/cmake-${CMAKE_VERSION}-linux-${arch}.tar.gz"; \
    echo "$sha  /opt/cmake.tar.gz" | sha256sum -c -; \
    mkdir /opt/cmake; \
    tar -xzf /opt/cmake.tar.gz -C /opt/cmake --strip-components=1; \
    rm /opt/cmake.tar.gz; \
    ln -s /opt/cmake/bin/cmake /opt/cmake/bin/ctest /opt/cmake/bin/cpack /usr/local/bin/; \
    cmake --version
