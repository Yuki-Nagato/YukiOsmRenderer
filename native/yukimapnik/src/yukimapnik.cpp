// YukiOsmRenderer - native wrapper around Mapnik.
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "yukimapnik.h"

#include <mapnik/agg_renderer.hpp>
#include <mapnik/geometry/box2d.hpp>
#include <mapnik/datasource_cache.hpp>
#include <mapnik/font_engine_freetype.hpp>
#include <mapnik/image.hpp>
#include <mapnik/image_util.hpp>
#include <mapnik/load_map.hpp>
#include <mapnik/map.hpp>
#include <mapnik/mapnik.hpp>
#include <mapnik/version.hpp>

#include <cmath>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <memory>
#include <mutex>
#include <new>
#include <stdexcept>
#include <string>

struct ym_map
{
    mapnik::Map map;
    // Buffer size defined by the style, restored when ym_map_render gets a negative buffer size.
    int style_buffer_size = 0;
};

namespace {

std::mutex init_mutex;

// Size limits of mapnik::Map (Map::MIN_MAPSIZE and Map::MAX_MAPSIZE are private); Map::resize
// silently ignores other sizes.
constexpr int32_t min_map_size = 16;
constexpr int32_t max_map_size = 16384;

char* copy_string(char const* text)
{
    std::size_t const length = std::strlen(text);
    char* copy = static_cast<char*>(std::malloc(length + 1));
    if (copy != nullptr)
    {
        std::memcpy(copy, text, length + 1);
    }
    return copy;
}

void set_error(char** error, char const* message)
{
    if (error != nullptr)
    {
        *error = copy_string(message != nullptr && message[0] != '\0' ? message : "unknown error");
    }
}

// Runs action and converts every C++ exception into a return code and an error message, so that
// no exception crosses the C ABI boundary.
template<typename Action>
int guarded(char** error, Action&& action) noexcept
{
    if (error != nullptr)
    {
        *error = nullptr;
    }
    try
    {
        action();
        return 0;
    }
    catch (std::bad_alloc const&)
    {
        set_error(error, "out of memory");
    }
    catch (std::exception const& ex)
    {
        set_error(error, ex.what());
    }
    catch (...)
    {
        set_error(error, "unknown C++ exception");
    }
    return 1;
}

void require(bool condition, char const* message)
{
    if (!condition)
    {
        throw std::invalid_argument(message);
    }
}

} // namespace

extern "C" {

YM_API const char* ym_mapnik_version(void)
{
    return MAPNIK_VERSION_STRING;
}

YM_API int ym_initialize(const char* plugins_dir, const char* fonts_dir, char** error)
{
    return guarded(error, [&] {
        std::lock_guard<std::mutex> lock(init_mutex);

        // Registers the statically linked input plugins and the image readers.
        mapnik::setup();

        if (plugins_dir != nullptr)
        {
            auto& cache = mapnik::datasource_cache::instance();
            cache.register_datasources(plugins_dir, false);
            if (cache.plugin_names().empty())
            {
                throw std::runtime_error(std::string("no Mapnik input plugins found in '") + plugins_dir + "'");
            }
        }

        if (fonts_dir != nullptr && !mapnik::freetype_engine::register_fonts(fonts_dir, true))
        {
            throw std::runtime_error(std::string("no fonts found in '") + fonts_dir + "'");
        }
    });
}

YM_API int ym_map_load(const char* xml, const char* base_path, ym_map** map, char** error)
{
    return guarded(error, [&] {
        require(xml != nullptr, "xml must not be NULL");
        require(map != nullptr, "map must not be NULL");
        *map = nullptr;

        auto instance = std::make_unique<ym_map>();
        mapnik::load_map_string(instance->map, xml, false, base_path != nullptr ? base_path : "");
        instance->style_buffer_size = instance->map.buffer_size();
        *map = instance.release();
    });
}

YM_API void ym_map_free(ym_map* map)
{
    delete map;
}

YM_API int ym_map_render(ym_map* map, double minx, double miny, double maxx, double maxy,
    int32_t width, int32_t height, double scale_factor, int32_t buffer_size,
    uint8_t* rgba, char** error)
{
    return guarded(error, [&] {
        require(map != nullptr, "map must not be NULL");
        require(rgba != nullptr, "rgba must not be NULL");
        require(width >= min_map_size && width <= max_map_size && height >= min_map_size && height <= max_map_size,
            "width and height must be between 16 and 16384 pixels");
        require(std::isfinite(minx) && std::isfinite(miny) && std::isfinite(maxx) && std::isfinite(maxy)
                && minx < maxx && miny < maxy,
            "invalid extent");
        require(std::isfinite(scale_factor) && scale_factor > 0.0, "scale_factor must be positive");

        mapnik::Map& m = map->map;
        m.resize(static_cast<unsigned>(width), static_cast<unsigned>(height));
        m.set_buffer_size(buffer_size >= 0 ? buffer_size : map->style_buffer_size);
        m.zoom_to_box(mapnik::box2d<double>(minx, miny, maxx, maxy));

        mapnik::image_rgba8 image(width, height);
        mapnik::agg_renderer<mapnik::image_rgba8> renderer(m, image, scale_factor);
        renderer.apply();
        // The renderer already demultiplies at the end of map processing; this is a no-op then.
        mapnik::demultiply_alpha(image);

        std::memcpy(rgba, image.bytes(), image.size());
    });
}

YM_API int ym_encode(const uint8_t* rgba, int32_t width, int32_t height, int32_t stride,
    const char* format, uint8_t** data, size_t* size, char** error)
{
    return guarded(error, [&] {
        require(data != nullptr && size != nullptr, "data and size must not be NULL");
        *data = nullptr;
        *size = 0;
        require(rgba != nullptr, "rgba must not be NULL");
        require(format != nullptr, "format must not be NULL");
        require(width > 0 && height > 0, "width and height must be positive");
        require(stride / 4 >= width, "stride must be at least width * 4");

        std::size_t const row_bytes = static_cast<std::size_t>(width) * 4;
        mapnik::image_rgba8 image(width, height, false);
        for (int32_t y = 0; y < height; ++y)
        {
            std::memcpy(image.get_row(static_cast<std::size_t>(y)), rgba + static_cast<std::size_t>(y) * static_cast<std::size_t>(stride), row_bytes);
        }
        image.set_premultiplied(false);

        std::string const encoded = mapnik::save_to_string(image, format);
        auto buffer = static_cast<uint8_t*>(std::malloc(encoded.empty() ? 1 : encoded.size()));
        if (buffer == nullptr)
        {
            throw std::bad_alloc();
        }
        std::memcpy(buffer, encoded.data(), encoded.size());
        *data = buffer;
        *size = encoded.size();
    });
}

YM_API void ym_free(void* ptr)
{
    std::free(ptr);
}

} // extern "C"
