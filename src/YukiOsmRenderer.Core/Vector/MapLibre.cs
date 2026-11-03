using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace YukiOsmRenderer.Core.Vector {
    /// <summary>P/Invoke declarations for the native yukimaplibre library (native/yukimaplibre/include/yukimaplibre.h).</summary>
    internal static unsafe partial class MapLibreNative {
        internal const string LibraryName = "yukimaplibre";

        internal static void UseLibrary(string path) {
            Runtime.NativeLibraries.Register(LibraryName, path);
        }

        [LibraryImport(LibraryName, EntryPoint = "yml_version")]
        internal static partial IntPtr Version();

        [LibraryImport(LibraryName, EntryPoint = "yml_set_log_callback")]
        internal static partial void SetLogCallback(delegate* unmanaged[Cdecl]<int, byte*, void> callback);

        [LibraryImport(LibraryName, EntryPoint = "yml_load_vulkan", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int LoadVulkan(string path, out IntPtr error);

        [LibraryImport(LibraryName, EntryPoint = "yml_set_worker_threads")]
        internal static partial void SetWorkerThreads(int count);

        [LibraryImport(LibraryName, EntryPoint = "yml_map_create", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int MapCreate(string styleJson, float pixelRatio, int tileMode, out MapHandle map, out IntPtr error);

        [LibraryImport(LibraryName, EntryPoint = "yml_map_render")]
        internal static partial int MapRender(MapHandle map, double longitude, double latitude, double zoom, uint width, uint height,
            byte* rgba, nuint rgbaSize, out IntPtr error);

        [LibraryImport(LibraryName, EntryPoint = "yml_map_destroy")]
        internal static partial void MapDestroy(IntPtr map);

        [LibraryImport(LibraryName, EntryPoint = "yml_free")]
        internal static partial void Free(IntPtr pointer);

        internal static void Check(int status, IntPtr error, string operation) {
            if (status == 0) {
                return;
            }
            var message = "unknown error";
            if (error != IntPtr.Zero) {
                message = Marshal.PtrToStringUTF8(error) ?? message;
                Free(error);
            }
            throw new MapLibreException($"MapLibre failed to {operation}: {message}");
        }

        internal sealed class MapHandle : SafeHandle {
            public MapHandle() : base(IntPtr.Zero, true) {
            }

            public override bool IsInvalid => handle == IntPtr.Zero;

            protected override bool ReleaseHandle() {
                MapDestroy(handle);
                return true;
            }
        }
    }

    public sealed class MapLibreException(string message) : Exception(message) {
    }

    /// <summary>Process-wide access to the bundled MapLibre Native library and its software Vulkan implementation.</summary>
    public static unsafe class MapLibre {
        static readonly Lock Sync = new();
        static bool initialized;
        static ILogger? logger;

        // Reported once per map although they are expected with a software renderer.
        static readonly string[] IgnoredMessages = ["Vulkan layers not found", "Feature not available: wideLines"];

        public static bool IsInitialized {
            get {
                lock (Sync) {
                    return initialized;
                }
            }
        }

        /// <param name="library">yukimaplibre library.</param>
        /// <param name="vulkanLibrary">Vulkan implementation, named like the system library (vulkan-1.dll, libvulkan.so.1).</param>
        /// <param name="workerThreads">Threads parsing and laying out tiles, shared by all maps.</param>
        public static void Initialize(string library, string vulkanLibrary, int workerThreads, ILogger log) {
            lock (Sync) {
                if (initialized) {
                    return;
                }
                logger = log;
                MapLibreNative.UseLibrary(library);
                MapLibreNative.SetLogCallback(&OnLog);
                var status = MapLibreNative.LoadVulkan(vulkanLibrary, out var error);
                MapLibreNative.Check(status, error, "load the Vulkan library");
                MapLibreNative.SetWorkerThreads(Math.Max(1, workerThreads));
                initialized = true;
            }
        }

        public static string Version => Marshal.PtrToStringUTF8(MapLibreNative.Version()) ?? "unknown";

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        static void OnLog(int severity, byte* message) {
            try {
                var text = Marshal.PtrToStringUTF8((IntPtr)message) ?? "";
                if (logger == null || IgnoredMessages.Any(text.Contains)) {
                    return;
                }
                // MapLibre warns about routine things (e.g. tiles beyond the maximum zoom of the data).
                var level = severity switch {
                    >= 3 => LogLevel.Warning,
                    _ => LogLevel.Debug,
                };
                logger.Log(level, "MapLibre: {Message}", text);
            }
            catch {
                // Exceptions must not cross into native code.
            }
        }
    }

    /// <summary>A MapLibre map with its own native render thread. Calls are serialized by the native library.</summary>
    public sealed unsafe class MapLibreMap : IDisposable {
        readonly MapLibreNative.MapHandle handle;

        MapLibreMap(MapLibreNative.MapHandle handle, float pixelRatio) {
            this.handle = handle;
            PixelRatio = pixelRatio;
        }

        public float PixelRatio { get; }

        public static MapLibreMap Create(string styleJson, float pixelRatio) {
            if (!MapLibre.IsInitialized) {
                throw new InvalidOperationException("MapLibre has not been initialized.");
            }
            var status = MapLibreNative.MapCreate(styleJson, pixelRatio, 0, out var handle, out var error);
            MapLibreNative.Check(status, error, "create a map");
            return new MapLibreMap(handle, pixelRatio);
        }

        /// <summary>Renders straight RGBA pixels of (width x height) logical pixels times the pixel ratio.</summary>
        public void Render(double longitude, double latitude, double zoom, int width, int height, Span<byte> rgba) {
            fixed (byte* pixels = rgba) {
                var status = MapLibreNative.MapRender(handle, longitude, latitude, zoom, (uint)width, (uint)height, pixels, (nuint)rgba.Length, out var error);
                MapLibreNative.Check(status, error, "render");
            }
        }

        public static int PixelSize(int logicalSize, float pixelRatio) {
            return (int)(logicalSize * pixelRatio);
        }

        public void Dispose() {
            handle.Dispose();
        }
    }
}
