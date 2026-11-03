using System.Reflection;
using System.Runtime.InteropServices;

namespace YukiOsmRenderer.Core.Runtime {
    /// <summary>
    /// Locations of the bundled native libraries. An assembly can only have one import resolver, so all libraries
    /// imported by this assembly are resolved here.
    /// </summary>
    internal static class NativeLibraries {
        static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal);
        static bool registered;

        /// <summary>Makes the runtime load the library imported as <paramref name="name"/> from the given file.</summary>
        public static void Register(string name, string path) {
            lock (Paths) {
                if (!registered) {
                    NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
                    registered = true;
                }
                Paths[name] = path;
            }
        }

        static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) {
            string? path;
            lock (Paths) {
                Paths.TryGetValue(name, out path);
            }
            return path != null ? NativeLibrary.Load(path) : IntPtr.Zero;
        }
    }
}
