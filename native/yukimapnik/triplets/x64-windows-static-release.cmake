# Release-only static libraries with the static CRT (/MT), so yukimapnik.dll does not need the
# Visual C++ redistributable.
set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE static)
set(VCPKG_LIBRARY_LINKAGE static)
set(VCPKG_BUILD_TYPE release)

include("${CMAKE_CURRENT_LIST_DIR}/common/ports.cmake")
