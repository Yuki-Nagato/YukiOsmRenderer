# Port specific settings shared by all yukimapnik triplets.

if(PORT STREQUAL "proj")
    # Embed proj.db into the static PROJ library and never look for data files on disk, so the
    # library is self-contained and unaffected by PROJ_DATA/PROJ_LIB set for other components.
    list(APPEND VCPKG_CMAKE_CONFIGURE_OPTIONS
        -DEMBED_RESOURCE_FILES=ON
        -DUSE_ONLY_EMBEDDED_RESOURCE_FILES=ON
    )
endif()
