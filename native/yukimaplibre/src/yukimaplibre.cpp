#include "yukimaplibre.h"

#include <mln/actor/scheduler.hpp>
#include <mln/gfx/headless_frontend.hpp>
#include <mln/map/camera.hpp>
#include <mln/map/map.hpp>
#include <mln/map/map_observer.hpp>
#include <mln/map/map_options.hpp>
#include <mln/renderer/renderer.hpp>
#include <mln/storage/resource_options.hpp>
#include <mln/style/style.hpp>
#include <mln/util/enum.hpp>
#include <mln/util/event.hpp>
#include <mln/util/image.hpp>
#include <mln/util/logging.hpp>
#include <mln/util/premultiply.hpp>
#include <mln/util/run_loop.hpp>

#include <atomic>
#include <condition_variable>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <exception>
#include <functional>
#include <future>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>

#if defined(_WIN32)
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#else
#  include <dlfcn.h>
#endif

#ifndef YML_MAPLIBRE_VERSION
#  define YML_MAPLIBRE_VERSION "unknown"
#endif

namespace mln::vulkan {
// From patches/0005-vulkan-library-path.patch.
void setVulkanLibrary(std::string name);
} // namespace mln::vulkan

namespace {

std::atomic<yml_log_callback> logCallback{nullptr};

class LogObserver final : public mln::Log::Observer {
public:
    bool onRecord(mln::EventSeverity severity, mln::Event event, int64_t code, const std::string& message) override {
        if (auto callback = logCallback.load()) {
            std::string text = std::string(mln::Enum<mln::Event>::toString(event)) + ": " + message;
            if (code != -1) {
                text += " (" + std::to_string(code) + ")";
            }
            int level = 1;
            switch (severity) {
                case mln::EventSeverity::Debug: level = 0; break;
                case mln::EventSeverity::Info: level = 1; break;
                case mln::EventSeverity::Warning: level = 2; break;
                default: level = 3; break;
            }
            callback(level, text.c_str());
        }
        return true;
    }
};

std::once_flag logInitialized;

void initializeLogging() {
    std::call_once(logInitialized, [] {
        mln::Log::useLogThread(false);
        mln::Log::setObserver(std::make_unique<LogObserver>());
    });
}

char* duplicate(const std::string& text) {
    auto* copy = static_cast<char*>(std::malloc(text.size() + 1));
    if (copy != nullptr) {
        std::memcpy(copy, text.c_str(), text.size() + 1);
    }
    return copy;
}

int fail(char** error, const std::string& message) {
    if (error != nullptr) {
        *error = duplicate(message);
    }
    return YML_ERROR;
}

std::string describe(const std::exception_ptr& exception) {
    try {
        std::rethrow_exception(exception);
    } catch (const std::exception& e) {
        return e.what();
    } catch (...) {
        return "unknown error";
    }
}

} // namespace

/* Owns a MapLibre map and its frontend on a dedicated thread with its own run loop. */
struct yml_map {
    std::mutex queueMutex;
    std::condition_variable queueChanged;
    std::deque<std::function<void()>> queue;
    bool stopping = false;

    std::mutex renderMutex;
    std::thread thread;

    mln::HeadlessFrontend* frontend = nullptr;
    mln::Map* map = nullptr;

    void post(std::function<void()> task) {
        {
            std::lock_guard<std::mutex> lock(queueMutex);
            queue.push_back(std::move(task));
        }
        queueChanged.notify_one();
    }

    void run(const std::string& style, float pixelRatio, bool tileMode, std::promise<void>& ready) {
        try {
            // The default run loop is libuv's global loop, which cannot run on several threads at once.
            mln::util::RunLoop loop(mln::util::RunLoop::Type::New);
            mln::HeadlessFrontend headless({256, 256}, pixelRatio);
            // The cache would keep about 11 times the tiles of the view, hundreds of laid out tiles for a metatile;
            // rendering metatiles was faster without it and maps stay much smaller.
            headless.getRenderer()->setTileCacheEnabled(false);
            mln::Map instance(headless,
                              mln::MapObserver::nullObserver(),
                              mln::MapOptions()
                                  .withMapMode(tileMode ? mln::MapMode::Tile : mln::MapMode::Static)
                                  // Metatiles with their buffer may extend beyond the poles; the camera must not move.
                                  .withConstrainMode(mln::ConstrainMode::None)
                                  .withSize(headless.getSize())
                                  .withPixelRatio(pixelRatio),
                              // Only local resources are used; nothing may be written to disk.
                              mln::ResourceOptions().withCachePath(":memory:").withMaximumCacheSize(0));
            instance.getStyle().loadJSON(style);
            frontend = &headless;
            map = &instance;
            ready.set_value();

            for (;;) {
                std::function<void()> task;
                {
                    std::unique_lock<std::mutex> lock(queueMutex);
                    queueChanged.wait(lock, [this] { return stopping || !queue.empty(); });
                    if (queue.empty()) {
                        break;
                    }
                    task = std::move(queue.front());
                    queue.pop_front();
                }
                task();
            }
            frontend = nullptr;
            map = nullptr;
        } catch (...) {
            try {
                ready.set_exception(std::current_exception());
            } catch (const std::future_error&) {
                // The map was already created; nothing is waiting for it.
            }
        }
    }
};

extern "C" {

const char* yml_version(void) {
    return YML_MAPLIBRE_VERSION;
}

void yml_set_log_callback(yml_log_callback callback) {
    logCallback.store(callback);
    initializeLogging();
}

int yml_load_vulkan(const char* path, char** error) {
    if (path == nullptr) {
        return fail(error, "No Vulkan library given.");
    }
#if defined(_WIN32)
    int length = MultiByteToWideChar(CP_UTF8, 0, path, -1, nullptr, 0);
    std::wstring wide(static_cast<size_t>(length > 0 ? length : 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, path, -1, wide.data(), length);
    if (LoadLibraryW(wide.c_str()) == nullptr) {
        return fail(error, "Cannot load " + std::string(path) + " (error " + std::to_string(GetLastError()) + ").");
    }
#else
    if (dlopen(path, RTLD_NOW | RTLD_GLOBAL) == nullptr) {
        const char* reason = dlerror();
        return fail(error, "Cannot load " + std::string(path) + ": " + (reason != nullptr ? reason : "unknown error"));
    }
    // MapLibre would otherwise open libvulkan.so first, which may be the system's Vulkan loader.
    mln::vulkan::setVulkanLibrary(path);
#endif
    return YML_OK;
}

void yml_set_worker_threads(int count) {
    mln::Scheduler::SetBackgroundThreadCount(count > 0 ? static_cast<std::size_t>(count) : 0);
}

int yml_map_create(const char* style_json, float pixel_ratio, int tile_mode, yml_map** map, char** error) {
    if (style_json == nullptr || map == nullptr || !(pixel_ratio > 0)) {
        return fail(error, "Invalid arguments.");
    }
    initializeLogging();
    auto* instance = new (std::nothrow) yml_map();
    if (instance == nullptr) {
        return fail(error, "Out of memory.");
    }
    std::promise<void> ready;
    auto created = ready.get_future();
    try {
        instance->thread = std::thread(
            [instance, style = std::string(style_json), pixel_ratio, tile_mode, ready = std::move(ready)]() mutable {
                instance->run(style, pixel_ratio, tile_mode != 0, ready);
            });
        created.get();
    } catch (...) {
        auto message = describe(std::current_exception());
        if (instance->thread.joinable()) {
            instance->thread.join();
        }
        delete instance;
        return fail(error, "Cannot create the map: " + message);
    }
    *map = instance;
    return YML_OK;
}

int yml_map_render(yml_map* map, double longitude, double latitude, double zoom, uint32_t width, uint32_t height,
                   uint8_t* rgba, size_t rgba_size, char** error) {
    if (map == nullptr || rgba == nullptr || width == 0 || height == 0) {
        return fail(error, "Invalid arguments.");
    }
    std::lock_guard<std::mutex> serialize(map->renderMutex);
    std::promise<void> done;
    auto finished = done.get_future();
    map->post([&] {
        try {
            const mln::Size size{width, height};
            map->frontend->setSize(size);
            map->map->setSize(size);
            map->map->jumpTo(mln::CameraOptions()
                                 .withCenter(mln::LatLng{latitude, longitude})
                                 .withZoom(zoom)
                                 .withBearing(0.0)
                                 .withPitch(0.0));
            auto result = map->frontend->render(*map->map);
            auto image = mln::util::unpremultiply(std::move(result.image));
            if (image.bytes() != rgba_size) {
                throw std::runtime_error("The image has " + std::to_string(image.bytes()) + " bytes, the buffer " +
                                         std::to_string(rgba_size) + ".");
            }
            std::memcpy(rgba, image.data.get(), image.bytes());
            done.set_value();
        } catch (...) {
            done.set_exception(std::current_exception());
        }
    });
    try {
        finished.get();
    } catch (...) {
        return fail(error, describe(std::current_exception()));
    }
    return YML_OK;
}

void yml_map_destroy(yml_map* map) {
    if (map == nullptr) {
        return;
    }
    {
        std::lock_guard<std::mutex> lock(map->queueMutex);
        map->stopping = true;
    }
    map->queueChanged.notify_one();
    if (map->thread.joinable()) {
        map->thread.join();
    }
    delete map;
}

void yml_free(void* pointer) {
    std::free(pointer);
}

} // extern "C"
