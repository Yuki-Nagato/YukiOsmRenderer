using System.Runtime.InteropServices;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Runtime;

namespace YukiOsmRenderer.Core.Rendering {
    /// <summary>Process-wide access to the bundled Mapnik library.</summary>
    public static class Mapnik {
        static readonly Lock Sync = new();
        static bool initialized;

        public static bool IsInitialized {
            get {
                lock (Sync) {
                    return initialized;
                }
            }
        }

        /// <summary>Loads the native library and registers input plugins and fonts. Subsequent calls are ignored.</summary>
        public static void Initialize(RuntimeLayout layout) {
            lock (Sync) {
                if (initialized) {
                    return;
                }
                MapnikNative.UseLibrary(layout.MapnikLibrary);
                var status = MapnikNative.Initialize(layout.MapnikPluginsDirectory, layout.FontsDirectory, out var error);
                MapnikNative.Check(status, error, "initialize");
                initialized = true;
            }
        }

        public static string Version => Marshal.PtrToStringUTF8(MapnikNative.MapnikVersion()) ?? "unknown";

        /// <summary>Encodes straight RGBA pixels with a Mapnik image format such as "png256:z=5".</summary>
        public static unsafe byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, int stride, string format) {
            if (stride < width * 4 || rgba.Length < (long)stride * (height - 1) + width * 4) {
                throw new ArgumentException("The pixel buffer is too small for the given size and stride.", nameof(rgba));
            }
            fixed (byte* pixels = rgba) {
                var status = MapnikNative.Encode(pixels, width, height, stride, format, out var data, out var size, out var error);
                MapnikNative.Check(status, error, "encode an image");
                try {
                    return new ReadOnlySpan<byte>((void*)data, checked((int)size)).ToArray();
                }
                finally {
                    MapnikNative.Free(data);
                }
            }
        }
    }

    /// <summary>A loaded Mapnik map. Not thread-safe; use one instance per thread.</summary>
    public sealed class MapnikMap : IDisposable {
        readonly MapnikNative.MapHandle handle;

        MapnikMap(MapnikNative.MapHandle handle) {
            this.handle = handle;
        }

        /// <param name="xml">Mapnik XML style.</param>
        /// <param name="basePath">Directory against which relative file references in the style are resolved.</param>
        public static MapnikMap Load(string xml, string basePath) {
            if (!Mapnik.IsInitialized) {
                throw new InvalidOperationException("Mapnik has not been initialized.");
            }
            var status = MapnikNative.MapLoad(xml, basePath, out var handle, out var error);
            MapnikNative.Check(status, error, "load the style");
            return new MapnikMap(handle);
        }

        /// <summary>Renders the extent into <paramref name="rgba"/> as straight RGBA pixels.</summary>
        /// <param name="bufferSize">Buffer around the image in pixels; negative keeps the style's setting.</param>
        public unsafe void Render(ProjectedExtent extent, int width, int height, double scaleFactor, int bufferSize, Span<byte> rgba) {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
            if (rgba.Length < (long)width * height * 4) {
                throw new ArgumentException("The pixel buffer is too small.", nameof(rgba));
            }
            fixed (byte* pixels = rgba) {
                var status = MapnikNative.MapRender(handle, extent.MinX, extent.MinY, extent.MaxX, extent.MaxY, width, height, scaleFactor, bufferSize, pixels, out var error);
                MapnikNative.Check(status, error, "render");
            }
        }

        public void Dispose() {
            handle.Dispose();
        }
    }
}
