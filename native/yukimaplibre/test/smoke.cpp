// Smoke test of the yukimaplibre C ABI: yukimaplibre-smoke <yukimaplibre library> <Vulkan library>
// Renders a style with only a background and checks the pixels, which needs a working Vulkan implementation.
#include "yukimaplibre.h"

#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <thread>
#include <vector>

#if defined(_WIN32)
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#else
#  include <dlfcn.h>
#endif

namespace {

void* openLibrary(const char* path) {
#if defined(_WIN32)
    return reinterpret_cast<void*>(LoadLibraryA(path));
#else
    return dlopen(path, RTLD_NOW | RTLD_LOCAL);
#endif
}

void* symbol(void* library, const char* name) {
#if defined(_WIN32)
    return reinterpret_cast<void*>(GetProcAddress(static_cast<HMODULE>(library), name));
#else
    return dlsym(library, name);
#endif
}

template <typename T>
T load(void* library, const char* name) {
    auto* address = symbol(library, name);
    if (address == nullptr) {
        std::fprintf(stderr, "missing symbol %s\n", name);
        std::exit(1);
    }
    return reinterpret_cast<T>(address);
}

void log(int severity, const char* message) {
    std::fprintf(stderr, "[%d] %s\n", severity, message);
}

} // namespace

int main(int argc, char** argv) {
    if (argc != 3) {
        std::fprintf(stderr, "usage: yukimaplibre-smoke <yukimaplibre library> <Vulkan library>\n");
        return 2;
    }
    void* library = openLibrary(argv[1]);
    if (library == nullptr) {
        std::fprintf(stderr, "cannot load %s\n", argv[1]);
        return 1;
    }
    auto version = load<decltype(&yml_version)>(library, "yml_version");
    auto setLog = load<decltype(&yml_set_log_callback)>(library, "yml_set_log_callback");
    auto loadVulkan = load<decltype(&yml_load_vulkan)>(library, "yml_load_vulkan");
    auto create = load<decltype(&yml_map_create)>(library, "yml_map_create");
    auto render = load<decltype(&yml_map_render)>(library, "yml_map_render");
    auto destroy = load<decltype(&yml_map_destroy)>(library, "yml_map_destroy");
    auto release = load<decltype(&yml_free)>(library, "yml_free");

    std::printf("MapLibre Native %s\n", version());
    setLog(log);
    char* error = nullptr;
    if (loadVulkan(argv[2], &error) != YML_OK) {
        std::fprintf(stderr, "%s\n", error);
        release(error);
        return 1;
    }

    const char* style = R"json({"version":8,"sources":{},"layers":[
        {"id":"background","type":"background","paint":{"background-color":"rgba(255,0,0,0.5)"}}]})json";
    int failures = 0;
    for (float ratio : {1.0f, 2.0f}) {
        yml_map* map = nullptr;
        if (create(style, ratio, 1, &map, &error) != YML_OK) {
            std::fprintf(stderr, "%s\n", error);
            release(error);
            return 1;
        }
        const uint32_t size = 64;
        const auto pixels = static_cast<size_t>(size * ratio) * static_cast<size_t>(size * ratio);
        std::vector<uint8_t> rgba(pixels * 4);
        for (int i = 0; i < 2; i++) {
            if (render(map, 116.4, 39.9, 10, size, size, rgba.data(), rgba.size(), &error) != YML_OK) {
                std::fprintf(stderr, "%s\n", error);
                release(error);
                failures++;
                break;
            }
            // Straight alpha: half transparent red.
            const auto* last = &rgba[rgba.size() - 4];
            if (rgba[0] != 255 || rgba[1] != 0 || rgba[3] < 126 || rgba[3] > 129 || last[0] != rgba[0] || last[3] != rgba[3]) {
                std::fprintf(stderr, "unexpected pixel %d,%d,%d,%d at ratio %.0f\n", rgba[0], rgba[1], rgba[2], rgba[3], ratio);
                failures++;
            }
        }
        destroy(map);
    }

    // Maps render concurrently on their own threads.
    std::atomic<int> concurrentFailures{0};
    std::vector<std::thread> threads;
    for (int t = 0; t < 4; t++) {
        threads.emplace_back([&, t] {
            yml_map* map = nullptr;
            char* threadError = nullptr;
            if (create(style, 1.0f, 0, &map, &threadError) != YML_OK) {
                std::fprintf(stderr, "%s\n", threadError);
                release(threadError);
                concurrentFailures++;
                return;
            }
            std::vector<uint8_t> rgba(512 * 512 * 4);
            for (int i = 0; i < 4; i++) {
                if (render(map, 116.4 + t, 39.9, 10 + i, 512, 512, rgba.data(), rgba.size(), &threadError) != YML_OK) {
                    std::fprintf(stderr, "%s\n", threadError);
                    release(threadError);
                    concurrentFailures++;
                    break;
                }
                if (rgba[0] != 255 || rgba[3] < 126 || rgba[3] > 129) {
                    concurrentFailures++;
                }
            }
            destroy(map);
        });
    }
    for (auto& thread : threads) {
        thread.join();
    }
    if (concurrentFailures != 0) {
        std::fprintf(stderr, "%d concurrent renders failed\n", concurrentFailures.load());
        failures += concurrentFailures;
    }
    std::printf(failures == 0 ? "ok\n" : "failed\n");
    return failures == 0 ? 0 : 1;
}
