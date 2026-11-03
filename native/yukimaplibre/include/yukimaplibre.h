/* C ABI around MapLibre Native, used by YukiOsmRenderer to render vector tiles headlessly. */
#ifndef YUKIMAPLIBRE_H
#define YUKIMAPLIBRE_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(YUKIMAPLIBRE_BUILD)
#    define YML_API __declspec(dllexport)
#  else
#    define YML_API __declspec(dllimport)
#  endif
#else
#  define YML_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Status codes; on failure, functions with an error parameter store a message that must be released with yml_free. */
#define YML_OK 0
#define YML_ERROR 1

typedef struct yml_map yml_map;

/* severity: 0 debug, 1 info, 2 warning, 3 error. Called from MapLibre's threads. */
typedef void (*yml_log_callback)(int severity, const char* message);

/* Version of MapLibre Native the library was built from. */
YML_API const char* yml_version(void);

/* Routes MapLibre log messages to the callback; null discards them. */
YML_API void yml_set_log_callback(yml_log_callback callback);

/*
 * Loads the Vulkan implementation (e.g. SwiftShader) from the given path before any map is created, so that
 * MapLibre's own lookup of vulkan-1.dll / libvulkan.so.1 finds it. Must be named like the system library.
 */
YML_API int yml_load_vulkan(const char* path, char** error);

/*
 * Sets the number of threads that parse and lay out tiles for all maps; maps created before keep using the
 * previous pool while any of them exists. 0 restores MapLibre's default of 4.
 */
YML_API void yml_set_worker_threads(int count);

/*
 * Creates a map with its own render thread. style_json is a MapLibre style; resources are loaded from file://
 * and pmtiles://file:// URLs. tile_mode selects MapLibre's tile map mode (no fading, labels that may cross tile
 * edges are placed consistently); otherwise the static mode is used. pixel_ratio is the device pixel ratio.
 */
YML_API int yml_map_create(const char* style_json, float pixel_ratio, int tile_mode, yml_map** map, char** error);

/*
 * Renders the map centered on the given point. width and height are logical pixels; rgba receives
 * (width * pixel_ratio) x (height * pixel_ratio) straight (not premultiplied) RGBA pixels and must hold
 * rgba_size bytes. Calls on one map are serialized.
 */
YML_API int yml_map_render(yml_map* map, double longitude, double latitude, double zoom, uint32_t width, uint32_t height,
                           uint8_t* rgba, size_t rgba_size, char** error);

YML_API void yml_map_destroy(yml_map* map);

YML_API void yml_free(void* pointer);

#ifdef __cplusplus
}
#endif

#endif
