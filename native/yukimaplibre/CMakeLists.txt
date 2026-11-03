cmake_minimum_required(VERSION 3.25)

project(yukimaplibre VERSION 1.0.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(YUKIMAPLIBRE_MLN_SOURCE_DIR "" CACHE PATH "Patched MapLibre Native source tree (see native/README.md)")
set(YUKIMAPLIBRE_MLN_VERSION "unknown" CACHE STRING "MapLibre Native version reported by yml_version")
option(YUKIMAPLIBRE_BUILD_SMOKE "Build the yukimaplibre-smoke test executable" ON)

if(NOT EXISTS "${YUKIMAPLIBRE_MLN_SOURCE_DIR}/CMakeLists.txt")
    message(FATAL_ERROR "YUKIMAPLIBRE_MLN_SOURCE_DIR must point to the MapLibre Native sources")
endif()

# Headless Vulkan only (a software implementation such as SwiftShader is loaded at run time); no windowing system,
# no network use, no system ICU. Dependencies come from vcpkg instead of MapLibre's own vcpkg checkout.
set(MLN_WITH_VULKAN ON CACHE BOOL "" FORCE)
set(MLN_WITH_OPENGL OFF CACHE BOOL "" FORCE)
set(MLN_WITH_EGL OFF CACHE BOOL "" FORCE)
set(MLN_WITH_GLFW OFF CACHE BOOL "" FORCE)
set(MLN_WITH_NODE OFF CACHE BOOL "" FORCE)
set(MLN_WITH_X11 OFF CACHE BOOL "" FORCE)
set(MLN_WITH_WAYLAND OFF CACHE BOOL "" FORCE)
set(MLN_WITH_WERROR OFF CACHE BOOL "" FORCE)
set(MLN_WITH_PMTILES ON CACHE BOOL "" FORCE)
set(MLN_TEXT_SHAPING_HARFBUZZ ON CACHE BOOL "" FORCE)
set(MLN_USE_BUILTIN_ICU ON CACHE BOOL "" FORCE)
set(MLN_WINDOWS_SKIP_VENDOR_PACKAGES ON CACHE BOOL "" FORCE)

if(CMAKE_SYSTEM_NAME STREQUAL "Linux" AND DEFINED VCPKG_INSTALLED_DIR)
    # MapLibre's Linux build links libuv and libwebp by the library names pkg-config reports, without their directory.
    link_directories("${VCPKG_INSTALLED_DIR}/${VCPKG_TARGET_TRIPLET}/lib")
endif()

add_subdirectory("${YUKIMAPLIBRE_MLN_SOURCE_DIR}" maplibre-native EXCLUDE_FROM_ALL SYSTEM)

add_library(yukimaplibre SHARED src/yukimaplibre.cpp)
target_include_directories(yukimaplibre PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_compile_definitions(yukimaplibre PRIVATE YUKIMAPLIBRE_BUILD "YML_MAPLIBRE_VERSION=\"${YUKIMAPLIBRE_MLN_VERSION}\"")
if(MSVC)
    # MapLibre's public headers use M_PI and expect windows.h without min/max macros.
    target_compile_definitions(yukimaplibre PRIVATE _USE_MATH_DEFINES NOMINMAX)
endif()
target_link_libraries(yukimaplibre PRIVATE mbgl-core)
set_target_properties(yukimaplibre PROPERTIES
    C_VISIBILITY_PRESET hidden
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
)

# Same linker settings as yukimapnik: self-contained, only the yml_* functions exported.
function(yukimaplibre_static_runtime target)
    if(MSVC)
        target_compile_options(${target} PRIVATE /utf-8 /bigobj /permissive-)
    elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
        target_link_options(${target} PRIVATE
            -static-libstdc++
            -static-libgcc
            -Wl,--exclude-libs,ALL
            -Wl,-z,relro,-z,now
            -Wl,--gc-sections
        )
        target_compile_options(${target} PRIVATE -ffunction-sections -fdata-sections)
    endif()
endfunction()

yukimaplibre_static_runtime(yukimaplibre)
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    set(version_script "${CMAKE_CURRENT_SOURCE_DIR}/src/yukimaplibre.map")
    target_link_options(yukimaplibre PRIVATE "-Wl,--version-script=${version_script}" -Wl,-z,defs)
    set_property(TARGET yukimaplibre APPEND PROPERTY LINK_DEPENDS "${version_script}")
endif()

install(TARGETS yukimaplibre
    RUNTIME DESTINATION . COMPONENT runtime
    LIBRARY DESTINATION . COMPONENT runtime
    ARCHIVE DESTINATION lib COMPONENT development EXCLUDE_FROM_ALL
)

if(YUKIMAPLIBRE_BUILD_SMOKE)
    # Calls the C ABI of the shared library given on the command line with the Vulkan library given as well.
    add_executable(yukimaplibre-smoke test/smoke.cpp)
    target_include_directories(yukimaplibre-smoke PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
    find_package(Threads REQUIRED)
    target_link_libraries(yukimaplibre-smoke PRIVATE Threads::Threads ${CMAKE_DL_LIBS})
    yukimaplibre_static_runtime(yukimaplibre-smoke)
    add_dependencies(yukimaplibre-smoke yukimaplibre)
    install(TARGETS yukimaplibre-smoke RUNTIME DESTINATION . COMPONENT smoke EXCLUDE_FROM_ALL)
endif()
