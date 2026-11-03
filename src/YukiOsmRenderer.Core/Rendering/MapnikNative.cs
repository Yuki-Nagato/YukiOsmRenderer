using System.Reflection;
using System.Runtime.InteropServices;

namespace YukiOsmRenderer.Core.Rendering {
    /// <summary>P/Invoke declarations for the native yukimapnik library (native/yukimapnik/include/yukimapnik.h).</summary>
    internal static unsafe partial class MapnikNative {
        internal const string LibraryName = "yukimapnik";

        /// <summary>Makes the runtime load yukimapnik from the given file.</summary>
        internal static void UseLibrary(string path) {
            Runtime.NativeLibraries.Register(LibraryName, path);
        }

        [LibraryImport(LibraryName, EntryPoint = "ym_mapnik_version")]
        internal static partial IntPtr MapnikVersion();

        [LibraryImport(LibraryName, EntryPoint = "ym_initialize", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Initialize(string? pluginsDirectory, string fontsDirectory, out IntPtr error);

        [LibraryImport(LibraryName, EntryPoint = "ym_map_load", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int MapLoad(string xml, string basePath, out MapHandle map, out IntPtr error);

        [LibraryImport(LibraryName, EntryPoint = "ym_map_free")]
        internal static partial void MapFree(IntPtr map);

        [LibraryImport(LibraryName, EntryPoint = "ym_map_render")]
        internal static partial int MapRender(MapHandle map, double minX, double minY, double maxX, double maxY,
            int width, int height, double scaleFactor, int bufferSize, byte* rgba, out IntPtr error);

        [LibraryImport(LibraryName, EntryPoint = "ym_encode", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Encode(byte* rgba, int width, int height, int stride, string format, out IntPtr data, out nuint size, out IntPtr error);

        [LibraryImport(LibraryName, EntryPoint = "ym_free")]
        internal static partial void Free(IntPtr pointer);

        /// <summary>Throws a <see cref="MapnikException"/> carrying the native error message if the call failed.</summary>
        internal static void Check(int status, IntPtr error, string operation) {
            if (status == 0) {
                return;
            }
            string message;
            if (error != IntPtr.Zero) {
                message = Marshal.PtrToStringUTF8(error) ?? "unknown error";
                Free(error);
            }
            else {
                message = "unknown error";
            }
            throw new MapnikException($"Mapnik failed to {operation}: {message}");
        }

        internal sealed class MapHandle : SafeHandle {
            public MapHandle() : base(IntPtr.Zero, true) {
            }

            public override bool IsInvalid => handle == IntPtr.Zero;

            protected override bool ReleaseHandle() {
                MapFree(handle);
                return true;
            }
        }
    }

    public sealed class MapnikException(string message) : Exception(message) {
    }
}
