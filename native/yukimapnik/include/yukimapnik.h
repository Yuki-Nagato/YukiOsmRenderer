// YukiOsmRenderer - native wrapper around Mapnik.
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Contract between the native library and YukiOsmRenderer.Core (MapnikNative.cs).
// Any change here must be mirrored in the C# P/Invoke declarations.

#ifndef YUKIMAPNIK_H
#define YUKIMAPNIK_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define YM_API __declspec(dllexport)
#else
#define YM_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct ym_map ym_map;

// Conventions:
// - Functions returning int return 0 on success and non-zero on failure.
// - On failure, *error (if error is not NULL) receives a heap-allocated, NUL-terminated UTF-8
//   message that must be released with ym_free. On success *error is set to NULL.
// - Strings passed in are NUL-terminated UTF-8.
// - ym_initialize must be called once before any other function except ym_mapnik_version.
// - Different ym_map instances may be used concurrently from different threads; a single
//   ym_map instance must not be used concurrently.

// Returns the Mapnik version string, e.g. "4.0.7". The string is static and must not be freed.
YM_API const char* ym_mapnik_version(void);

// Registers Mapnik input plugins and fonts.
// plugins_dir: directory containing *.input plugins, or NULL when plugins are linked statically.
// fonts_dir: directory scanned recursively for font files.
YM_API int ym_initialize(const char* plugins_dir, const char* fonts_dir, char** error);

// Loads a Mapnik XML style from a string. base_path resolves relative file references
// (symbols, patterns) in the style. On success *map receives a new map instance.
YM_API int ym_map_load(const char* xml, const char* base_path, ym_map** map, char** error);

// Releases a map instance. NULL is ignored.
YM_API void ym_map_free(ym_map* map);

// Renders the map extent [minx, miny, maxx, maxy] (in the map's SRS, EPSG:3857 for
// OpenStreetMap Carto) into an image of width x height pixels.
// scale_factor: Mapnik scale factor (1.0 for 256 px tiles, 2.0 for 512 px "@2x" tiles).
// buffer_size: Mapnik buffer size in pixels; a negative value keeps the value from the style.
// rgba: caller-allocated buffer of width * height * 4 bytes that receives straight
//       (non-premultiplied) RGBA8 pixels, row-major, without row padding.
YM_API int ym_map_render(ym_map* map, double minx, double miny, double maxx, double maxy,
    int32_t width, int32_t height, double scale_factor, int32_t buffer_size,
    uint8_t* rgba, char** error);

// Encodes straight RGBA8 pixels with Mapnik's image writers.
// stride: distance in bytes between the starts of two consecutive rows (>= width * 4).
// format: Mapnik format string, e.g. "png256:z=5" (the format used by tile.openstreetmap.org) or "png".
// On success *data receives a heap-allocated buffer of *size bytes that must be released with ym_free.
YM_API int ym_encode(const uint8_t* rgba, int32_t width, int32_t height, int32_t stride,
    const char* format, uint8_t** data, size_t* size, char** error);

// Releases memory returned by this library (error messages and encoded images). NULL is ignored.
YM_API void ym_free(void* ptr);

#ifdef __cplusplus
}
#endif

#endif
