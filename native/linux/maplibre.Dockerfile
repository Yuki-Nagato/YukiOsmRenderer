# Build environment for yukimaplibre (MapLibre Native) and SwiftShader on Linux (linux-x64 and linux-arm64).
# MapLibre Native needs C++23, which the GCC 10 of the Debian 11 image lacks; manylinux_2_28 (AlmaLinux 8,
# glibc 2.28) has GCC 14. Both libraries link libstdc++ statically and only need a glibc older than the 2.31
# baseline of the other components. The image is chosen by architecture in native/build-linux.sh.
ARG BASE_IMAGE=quay.io/pypa/manylinux_2_28_x86_64@sha256:7e41b1c7fd64dfc69703a1124b43c404382973ee06ec5d764b85c34d2221fded
FROM ${BASE_IMAGE}

ENV LANG=C.UTF-8 \
    VCPKG_FORCE_SYSTEM_BINARIES=1

# The image has GCC 14, CMake, git, patchelf, perl and python3; vcpkg also needs Ninja and zip, libjpeg-turbo NASM.
RUN dnf install -y nasm ninja-build zip \
    && dnf clean all \
    && ninja --version
