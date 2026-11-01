// YukiOsmRenderer - smoke test for the yukimapnik C ABI (not shipped).
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Usage: yukimapnik-smoke <yukimapnik library> <fonts dir> [--pg "<conninfo>"] [--out <dir>]
//
// Loads the shared library from the given path and exercises every function of the C ABI.
// --pg  connection parameters ("host=... port=... dbname=... user=... password=...") of a
//       PostGIS database; enables rendering of PostGIS layers including reprojection and text.
// --out directory that receives the encoded test images.

#include "yukimapnik.h"

#include <mapnik/datasource_cache.hpp>
#include <mapnik/mapnik.hpp>
#include <mapnik/version.hpp>

#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#else
#include <dlfcn.h>
#endif

namespace {

char const* const merc_srs = "+proj=merc +a=6378137 +b=6378137 +lat_ts=0.0 +lon_0=0.0 +x_0=0.0 +y_0=0.0 +k=1.0 +units=m +nadgrids=@null +wktext +no_defs +over";
char const* const longlat_srs = "+proj=longlat +ellps=WGS84 +datum=WGS84 +no_defs";
double const merc_max = 20037508.342789244;

struct api
{
    decltype(&ym_mapnik_version) mapnik_version = nullptr;
    decltype(&ym_initialize) initialize = nullptr;
    decltype(&ym_map_load) map_load = nullptr;
    decltype(&ym_map_free) map_free = nullptr;
    decltype(&ym_map_render) map_render = nullptr;
    decltype(&ym_encode) encode = nullptr;
    decltype(&ym_free) free = nullptr;
};

int failures = 0;

void expect(bool condition, std::string const& what)
{
    std::printf("%s %s\n", condition ? "[ OK ]" : "[FAIL]", what.c_str());
    std::fflush(stdout);
    if (!condition)
    {
        ++failures;
    }
}

template<typename T>
void resolve(void* library, char const* name, T& function)
{
#if defined(_WIN32)
    function = reinterpret_cast<T>(reinterpret_cast<void*>(GetProcAddress(static_cast<HMODULE>(library), name)));
#else
    function = reinterpret_cast<T>(dlsym(library, name));
#endif
    if (function == nullptr)
    {
        throw std::runtime_error(std::string("missing export ") + name);
    }
}

api load_api(std::string const& path)
{
#if defined(_WIN32)
    int const length = MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, nullptr, 0);
    std::wstring wide(static_cast<std::size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, path.c_str(), -1, wide.data(), length);
    void* library = LoadLibraryExW(wide.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (library == nullptr)
    {
        throw std::runtime_error("LoadLibrary failed for " + path + " (error " + std::to_string(GetLastError()) + ")");
    }
#else
    void* library = dlopen(path.c_str(), RTLD_NOW | RTLD_LOCAL);
    if (library == nullptr)
    {
        throw std::runtime_error(std::string("dlopen failed: ") + dlerror());
    }
#endif
    api a;
    resolve(library, "ym_mapnik_version", a.mapnik_version);
    resolve(library, "ym_initialize", a.initialize);
    resolve(library, "ym_map_load", a.map_load);
    resolve(library, "ym_map_free", a.map_free);
    resolve(library, "ym_map_render", a.map_render);
    resolve(library, "ym_encode", a.encode);
    resolve(library, "ym_free", a.free);
    return a;
}

std::string take_error(api const& a, char* error)
{
    std::string message = error != nullptr ? error : "(no error message)";
    a.free(error);
    return message;
}

std::string xml_escape(std::string const& text)
{
    std::string result;
    for (char c : text)
    {
        switch (c)
        {
        case '&': result += "&amp;"; break;
        case '<': result += "&lt;"; break;
        case '>': result += "&gt;"; break;
        case '"': result += "&quot;"; break;
        default: result += c; break;
        }
    }
    return result;
}

// Parses "key=value key=value" connection parameters (values must not contain spaces).
std::vector<std::pair<std::string, std::string>> parse_conninfo(std::string const& conninfo)
{
    std::vector<std::pair<std::string, std::string>> result;
    std::istringstream stream(conninfo);
    std::string item;
    while (stream >> item)
    {
        auto const pos = item.find('=');
        if (pos == std::string::npos)
        {
            throw std::runtime_error("invalid connection parameter: " + item);
        }
        result.emplace_back(item.substr(0, pos), item.substr(pos + 1));
    }
    return result;
}

std::string postgis_datasource(std::vector<std::pair<std::string, std::string>> const& conninfo, std::string const& table,
    std::string const& geometry_field, int srid, std::string const& extent)
{
    std::string xml = "<Datasource><Parameter name=\"type\">postgis</Parameter>";
    for (auto const& kv : conninfo)
    {
        xml += "<Parameter name=\"" + xml_escape(kv.first) + "\">" + xml_escape(kv.second) + "</Parameter>";
    }
    xml += "<Parameter name=\"table\">" + xml_escape(table) + "</Parameter>";
    xml += "<Parameter name=\"geometry_field\">" + geometry_field + "</Parameter>";
    xml += "<Parameter name=\"srid\">" + std::to_string(srid) + "</Parameter>";
    xml += "<Parameter name=\"extent\">" + extent + "</Parameter>";
    xml += "<Parameter name=\"connect_timeout\">5</Parameter>";
    xml += "</Datasource>";
    return xml;
}

struct rgba_pixel
{
    int r, g, b, a;
};

rgba_pixel pixel_at(std::vector<uint8_t> const& image, int width, int x, int y)
{
    std::size_t const offset = (static_cast<std::size_t>(y) * static_cast<std::size_t>(width) + static_cast<std::size_t>(x)) * 4;
    return {image[offset], image[offset + 1], image[offset + 2], image[offset + 3]};
}

bool is_near(rgba_pixel p, rgba_pixel expected, int tolerance)
{
    return std::abs(p.r - expected.r) <= tolerance && std::abs(p.g - expected.g) <= tolerance
        && std::abs(p.b - expected.b) <= tolerance && std::abs(p.a - expected.a) <= tolerance;
}

std::string describe(rgba_pixel p)
{
    return "(" + std::to_string(p.r) + "," + std::to_string(p.g) + "," + std::to_string(p.b) + "," + std::to_string(p.a) + ")";
}

bool all_pixels_near(std::vector<uint8_t> const& image, int width, int height, rgba_pixel expected, int tolerance, rgba_pixel& first_mismatch)
{
    for (int y = 0; y < height; ++y)
    {
        for (int x = 0; x < width; ++x)
        {
            rgba_pixel const p = pixel_at(image, width, x, y);
            if (!is_near(p, expected, tolerance))
            {
                first_mismatch = p;
                return false;
            }
        }
    }
    return true;
}

bool render(api const& a, ym_map* map, int size, double scale_factor, std::vector<uint8_t>& image, std::string& error_message)
{
    image.assign(static_cast<std::size_t>(size) * static_cast<std::size_t>(size) * 4, 0);
    char* error = nullptr;
    if (a.map_render(map, -merc_max, -merc_max, merc_max, merc_max, size, size, scale_factor, -1, image.data(), &error) != 0)
    {
        error_message = take_error(a, error);
        return false;
    }
    return error == nullptr;
}

ym_map* load(api const& a, std::string const& xml, std::string& error_message)
{
    ym_map* map = nullptr;
    char* error = nullptr;
    if (a.map_load(xml.c_str(), ".", &map, &error) != 0)
    {
        error_message = take_error(a, error);
        return nullptr;
    }
    return map;
}

void write_file(std::string const& out_dir, std::string const& name, uint8_t const* data, std::size_t size)
{
    if (out_dir.empty())
    {
        return;
    }
    std::ofstream file(out_dir + "/" + name, std::ios::binary);
    file.write(reinterpret_cast<char const*>(data), static_cast<std::streamsize>(size));
}

bool is_png(uint8_t const* data, std::size_t size)
{
    static uint8_t const signature[] = {0x89, 'P', 'N', 'G', 0x0d, 0x0a, 0x1a, 0x0a};
    return size > sizeof(signature) && std::memcmp(data, signature, sizeof(signature)) == 0;
}

void test_background(api const& a, std::string const& out_dir)
{
    struct test_case
    {
        char const* color;
        rgba_pixel expected;
        int tolerance;
    };
    // The second case would be (26,51,102,128) if the output was still premultiplied.
    test_case const cases[] = {
        {"#3366cc", {0x33, 0x66, 0xcc, 255}, 0},
        {"rgba(51,102,204,0.5)", {51, 102, 204, 128}, 2},
    };

    for (auto const& test : cases)
    {
        std::string const xml = std::string("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Map background-color=\"") + test.color
            + "\" srs=\"" + merc_srs + "\"/>\n";
        std::string message;
        ym_map* map = load(a, xml, message);
        expect(map != nullptr, std::string("load map with background ") + test.color + (map == nullptr ? ": " + message : ""));
        if (map == nullptr)
        {
            continue;
        }

        std::vector<uint8_t> image;
        bool const rendered = render(a, map, 256, 1.0, image, message);
        expect(rendered, "render 256x256" + (rendered ? std::string() : ": " + message));
        if (rendered)
        {
            rgba_pixel mismatch{};
            bool const ok = all_pixels_near(image, 256, 256, test.expected, test.tolerance, mismatch);
            expect(ok, "all pixels are straight RGBA " + describe(test.expected) + (ok ? std::string() : ", found " + describe(mismatch)));

            for (char const* format : {"png256:z=5", "png"})
            {
                uint8_t* data = nullptr;
                std::size_t size = 0;
                char* error = nullptr;
                int const rc = a.encode(image.data(), 256, 256, 256 * 4, format, &data, &size, &error);
                expect(rc == 0 && is_png(data, size), std::string("encode ") + format + (rc == 0 ? " (" + std::to_string(size) + " bytes)" : ": " + take_error(a, error)));
                if (rc == 0)
                {
                    write_file(out_dir, std::string("background-") + (test.tolerance == 0 ? "opaque" : "alpha") + (format[3] == '2' ? "-png256.png" : ".png"), data, size);
                }
                a.free(data);
            }
        }
        a.map_free(map);
    }

    // Rows with padding must be encoded correctly as well.
    std::vector<uint8_t> padded(static_cast<std::size_t>(20) * 100, 0xff);
    uint8_t* data = nullptr;
    std::size_t size = 0;
    char* error = nullptr;
    int const rc = a.encode(padded.data(), 16, 20, 100, "png256:z=5", &data, &size, &error);
    expect(rc == 0 && is_png(data, size), "encode with stride > width * 4" + (rc == 0 ? std::string() : ": " + take_error(a, error)));
    a.free(data);
}

void test_errors(api const& a)
{
    std::string message;
    ym_map* map = load(a, "<Map", message);
    expect(map == nullptr && !message.empty(), "invalid XML is reported as error: " + message);
    a.map_free(map);

    map = load(a, std::string("<Map srs=\"") + merc_srs + "\"/>", message);
    if (map != nullptr)
    {
        std::vector<uint8_t> image(8 * 8 * 4);
        char* error = nullptr;
        int const rc = a.map_render(map, -1, -1, 1, 1, 8, 8, 1.0, -1, image.data(), &error);
        expect(rc != 0 && error != nullptr, "too small image size is reported as error: " + (rc != 0 ? take_error(a, error) : std::string()));
        a.map_free(map);
    }

    uint8_t* data = nullptr;
    std::size_t size = 0;
    char* error = nullptr;
    uint8_t pixel[4] = {0, 0, 0, 0};
    int const rc = a.encode(pixel, 1, 1, 4, "no-such-format", &data, &size, &error);
    expect(rc != 0 && data == nullptr, "unknown encoder format is reported as error: " + (rc != 0 ? take_error(a, error) : std::string()));
    a.map_free(nullptr);
    a.free(nullptr);
}

void test_threads(api const& a)
{
    std::atomic<int> ok{0};
    std::vector<std::thread> threads;
    for (int i = 0; i < 4; ++i)
    {
        threads.emplace_back([&a, &ok] {
            std::string message;
            ym_map* map = load(a, std::string("<Map background-color=\"#102030\" srs=\"") + merc_srs + "\"/>", message);
            if (map == nullptr)
            {
                return;
            }
            std::vector<uint8_t> image;
            bool good = true;
            for (int n = 0; n < 10 && good; ++n)
            {
                rgba_pixel mismatch{};
                good = render(a, map, 256, 1.0, image, message) && all_pixels_near(image, 256, 256, {0x10, 0x20, 0x30, 255}, 0, mismatch);
            }
            a.map_free(map);
            if (good)
            {
                ++ok;
            }
        });
    }
    for (auto& thread : threads)
    {
        thread.join();
    }
    expect(ok == 4, "concurrent rendering with separate map instances");
}

void test_postgis_registration(api const& a)
{
    // Static C++ API check: the Mapnik build linked into this executable is configured the same way
    // as the one linked into yukimapnik.
    mapnik::setup();
    auto const names = mapnik::datasource_cache::instance().plugin_names();
    std::string list;
    for (auto const& name : names)
    {
        list += (list.empty() ? "" : ", ") + name;
    }
    expect(mapnik::datasource_cache::instance().plugin_registered("postgis"), "Mapnik C++ API reports registered datasource plugin 'postgis' (registered: " + list + ")");

    // Through the library: an unreachable server must fail inside the PostGIS plugin, not because
    // the datasource type is unknown.
    std::string const xml = std::string("<Map srs=\"") + merc_srs + "\"><Layer name=\"l\">"
        + postgis_datasource({{"host", "127.0.0.1"}, {"port", "1"}, {"dbname", "none"}, {"user", "none"}}, "t", "way", 3857, "0,0,1,1")
        + "</Layer></Map>";
    std::string message;
    ym_map* map = load(a, xml, message);
    a.map_free(map);
    expect(message.find("Could not create datasource") == std::string::npos, "yukimapnik creates 'postgis' datasources (" + (map != nullptr ? std::string("loaded") : message) + ")");
}

void test_postgis_render(api const& a, std::string const& conninfo_text, std::string const& out_dir)
{
    auto const conninfo = parse_conninfo(conninfo_text);
    std::string const world_merc = "-20037508.34,-20037508.34,20037508.34,20037508.34";
    std::string const xml = std::string("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n")
        + "<Map background-color=\"white\" srs=\"" + merc_srs + "\">"
        + "<Style name=\"polygons\"><Rule><PolygonSymbolizer fill=\"#00ff00\"/></Rule></Style>"
        + "<Style name=\"labels\"><Rule><TextSymbolizer face-name=\"DejaVu Sans Book\" size=\"40\" fill=\"black\" allow-overlap=\"true\">[name]</TextSymbolizer></Rule></Style>"
        + "<Layer name=\"polygons\" srs=\"" + merc_srs + "\"><StyleName>polygons</StyleName>"
        + postgis_datasource(conninfo, "(SELECT ST_MakeEnvelope(-20037508.34, -20037508.34, 0, 20037508.34, 3857) AS way) AS t", "way", 3857, world_merc)
        + "</Layer>"
        // The label layer is stored in EPSG:4326 and must be reprojected by PROJ.
        + "<Layer name=\"labels\" srs=\"" + longlat_srs + "\"><StyleName>labels</StyleName>"
        + postgis_datasource(conninfo, "(SELECT ST_SetSRID(ST_MakePoint(90, 0), 4326) AS geom, 'Yuki' AS name) AS t", "geom", 4326, "-180,-85,180,85")
        + "</Layer></Map>\n";

    std::string message;
    ym_map* map = load(a, xml, message);
    expect(map != nullptr, "load map with PostGIS layers" + (map == nullptr ? ": " + message : std::string()));
    if (map == nullptr)
    {
        return;
    }
    std::vector<uint8_t> image;
    bool const rendered = render(a, map, 256, 1.0, image, message);
    expect(rendered, "render PostGIS layers" + (rendered ? std::string() : ": " + message));
    if (rendered)
    {
        rgba_pixel const polygon = pixel_at(image, 256, 64, 128);
        expect(is_near(polygon, {0, 255, 0, 255}, 0), "polygon pixel is green " + describe(polygon));
        rgba_pixel const background = pixel_at(image, 256, 200, 30);
        expect(is_near(background, {255, 255, 255, 255}, 0), "background pixel is white " + describe(background));
        int dark = 0;
        for (int y = 100; y < 156; ++y)
        {
            for (int x = 140; x < 245; ++x)
            {
                if (pixel_at(image, 256, x, y).r < 96)
                {
                    ++dark;
                }
            }
        }
        expect(dark > 50, "reprojected label is rendered around (192,128) (" + std::to_string(dark) + " dark pixels)");

        uint8_t* data = nullptr;
        std::size_t size = 0;
        char* error = nullptr;
        if (a.encode(image.data(), 256, 256, 256 * 4, "png256:z=5", &data, &size, &error) == 0)
        {
            write_file(out_dir, "postgis.png", data, size);
            a.free(data);
        }
        else
        {
            a.free(error);
        }
    }
    a.map_free(map);
}

} // namespace

int main(int argc, char** argv)
{
    std::string library;
    std::string fonts_dir;
    std::string conninfo;
    std::string out_dir;
    std::vector<std::string> positional;
    for (int i = 1; i < argc; ++i)
    {
        std::string const arg = argv[i];
        if (arg == "--pg" && i + 1 < argc)
        {
            conninfo = argv[++i];
        }
        else if (arg == "--out" && i + 1 < argc)
        {
            out_dir = argv[++i];
        }
        else
        {
            positional.push_back(arg);
        }
    }
    if (positional.size() != 2)
    {
        std::fprintf(stderr, "usage: yukimapnik-smoke <yukimapnik library> <fonts dir> [--pg \"<conninfo>\"] [--out <dir>]\n");
        return 2;
    }
    library = positional[0];
    fonts_dir = positional[1];

    try
    {
        api const a = load_api(library);
        std::string const version = a.mapnik_version();
        expect(version == MAPNIK_VERSION_STRING, "ym_mapnik_version() = " + version);

        char* error = nullptr;
        int const rc = a.initialize(nullptr, fonts_dir.c_str(), &error);
        expect(rc == 0 && error == nullptr, "ym_initialize(NULL, \"" + fonts_dir + "\")" + (rc == 0 ? std::string() : ": " + take_error(a, error)));

        test_postgis_registration(a);
        test_background(a, out_dir);
        test_errors(a);
        test_threads(a);
        if (!conninfo.empty())
        {
            test_postgis_render(a, conninfo, out_dir);
        }
    }
    catch (std::exception const& ex)
    {
        expect(false, ex.what());
    }

    std::printf(failures == 0 ? "yukimapnik smoke test passed\n" : "yukimapnik smoke test FAILED (%d failures)\n", failures);
    return failures == 0 ? 0 : 1;
}
