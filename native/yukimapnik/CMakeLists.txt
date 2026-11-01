cmake_minimum_required(VERSION 3.25)

# Use BoostConfig.cmake (installed by vcpkg) instead of the removed FindBoost module.
if(POLICY CMP0167)
    cmake_policy(SET CMP0167 NEW)
endif()

project(yukimapnik VERSION 1.0.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

option(YUKIMAPNIK_BUILD_SMOKE "Build the yukimapnik-smoke test executable" ON)

# mapnikConfig.cmake requires PkgConfig; on Windows pkgconf comes from vcpkg (host dependency).
if(NOT PKG_CONFIG_EXECUTABLE)
    find_program(PKG_CONFIG_EXECUTABLE NAMES pkgconf pkg-config)
endif()
find_package(mapnik CONFIG REQUIRED)
# The postgis input plugin is compiled into the static Mapnik library, but the exported target
# does not carry its libpq dependency.
find_package(PostgreSQL REQUIRED)

function(yukimapnik_link_mapnik target)
    target_link_libraries(${target} PRIVATE mapnik::mapnik PostgreSQL::PostgreSQL)
    if(WIN32)
        # Needed by the static libpq, not added by vcpkg's FindPostgreSQL wrapper.
        target_link_libraries(${target} PRIVATE ws2_32)
    endif()
endfunction()

add_library(yukimapnik SHARED src/yukimapnik.cpp)
target_include_directories(yukimapnik PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
yukimapnik_link_mapnik(yukimapnik)
set_target_properties(yukimapnik PROPERTIES
    C_VISIBILITY_PRESET hidden
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
)

# Linker settings for self-contained binaries that do not clash with libraries loaded by the
# .NET host: libstdc++/libgcc are linked statically and only the ym_* functions are exported.
function(yukimapnik_static_runtime target)
    if(MSVC)
        target_compile_options(${target} PRIVATE /utf-8 /bigobj /permissive- /W3)
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

yukimapnik_static_runtime(yukimapnik)
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    set(version_script "${CMAKE_CURRENT_SOURCE_DIR}/src/yukimapnik.map")
    target_link_options(yukimapnik PRIVATE "-Wl,--version-script=${version_script}" -Wl,-z,defs)
    set_property(TARGET yukimapnik APPEND PROPERTY LINK_DEPENDS "${version_script}")
endif()

install(TARGETS yukimapnik
    RUNTIME DESTINATION . COMPONENT runtime
    LIBRARY DESTINATION . COMPONENT runtime
    ARCHIVE DESTINATION lib COMPONENT development EXCLUDE_FROM_ALL
)

if(YUKIMAPNIK_BUILD_SMOKE)
    # Calls the C ABI of the shared library given on the command line. Mapnik is linked statically
    # as well, to check the datasource plugin registration through the Mapnik C++ API.
    add_executable(yukimapnik-smoke test/smoke.cpp)
    target_include_directories(yukimapnik-smoke PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
    yukimapnik_link_mapnik(yukimapnik-smoke)
    target_link_libraries(yukimapnik-smoke PRIVATE ${CMAKE_DL_LIBS})
    find_package(Threads REQUIRED)
    target_link_libraries(yukimapnik-smoke PRIVATE Threads::Threads)
    yukimapnik_static_runtime(yukimapnik-smoke)
    add_dependencies(yukimapnik-smoke yukimapnik)

    install(TARGETS yukimapnik-smoke RUNTIME DESTINATION . COMPONENT smoke EXCLUDE_FROM_ALL)

    # Mapnik installs the DejaVu fonts, which are used by the smoke test.
    find_path(YUKIMAPNIK_TEST_FONTS_DIR NAMES DejaVuSans.ttf PATH_SUFFIXES share/mapnik/fonts lib/mapnik/fonts REQUIRED)
    install(FILES "${YUKIMAPNIK_TEST_FONTS_DIR}/DejaVuSans.ttf" DESTINATION fonts COMPONENT smoke EXCLUDE_FROM_ALL)

    enable_testing()
    add_test(NAME yukimapnik-smoke COMMAND yukimapnik-smoke "$<TARGET_FILE:yukimapnik>" "${YUKIMAPNIK_TEST_FONTS_DIR}")
endif()
