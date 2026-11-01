using System.Runtime.InteropServices;
using System.Text.Json;

namespace YukiOsmRenderer.Core.Runtime {
    /// <summary>
    /// Locates the bundled native components (runtime/) and assets (assets/) of a YukiOsmRenderer installation.
    /// </summary>
    public sealed class RuntimeLayout {
        public const string HomeEnvironmentVariable = "YUKIOSM_HOME";

        RuntimeLayout(string root) {
            Root = root;
            RuntimeDirectory = Path.Combine(root, "runtime");
            AssetsDirectory = Path.Combine(root, "assets");
        }

        public string Root { get; }

        public string RuntimeDirectory { get; }

        public string AssetsDirectory { get; }

        public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        static string ExecutableSuffix => IsWindows ? ".exe" : "";

        public string PostgresBinDirectory => Path.Combine(RuntimeDirectory, "pgsql", "bin");

        public string Osm2pgsqlExecutable => IsWindows
            ? Path.Combine(RuntimeDirectory, "osm2pgsql", "osm2pgsql.exe")
            : Path.Combine(RuntimeDirectory, "osm2pgsql", "bin", "osm2pgsql");

        public string NodeExecutable => IsWindows
            ? Path.Combine(RuntimeDirectory, "node", "node.exe")
            : Path.Combine(RuntimeDirectory, "node", "bin", "node");

        public string MapnikLibrary => IsWindows
            ? Path.Combine(RuntimeDirectory, "mapnik", "yukimapnik.dll")
            : Path.Combine(RuntimeDirectory, "mapnik", "libyukimapnik.so");

        /// <summary>Directory with Mapnik input plugins, or null when they are linked into the wrapper library.</summary>
        public string? MapnikPluginsDirectory {
            get {
                var directory = Path.Combine(RuntimeDirectory, "mapnik", "input");
                return Directory.Exists(directory) ? directory : null;
            }
        }

        /// <summary>PROJ data directory used by PostGIS, if bundled.</summary>
        public string? ProjDataDirectory {
            get {
                var shared = Path.Combine(RuntimeDirectory, "pgsql", "share");
                var direct = Path.Combine(shared, "proj");
                if (File.Exists(Path.Combine(direct, "proj.db"))) {
                    return direct;
                }
                var contrib = Path.Combine(shared, "contrib");
                if (Directory.Exists(contrib)) {
                    return Directory.EnumerateFiles(contrib, "proj.db", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault();
                }
                return null;
            }
        }

        public string FontsDirectory => Path.Combine(AssetsDirectory, "fonts");

        /// <summary>OpenStreetMap Carto checkout (project.mml, style/, symbols/, patterns/, SQL and Lua files).</summary>
        public string CartoDirectory => Path.Combine(AssetsDirectory, "carto");

        public string CartoProjectFile => Path.Combine(CartoDirectory, "project.mml");

        public string CartoCompilerScript => Path.Combine(AssetsDirectory, "carto-compiler", "node_modules", "carto", "bin", "carto");

        /// <summary>pg_dump archive with the water polygons and other data that OpenStreetMap Carto loads from shapefiles.</summary>
        public string ExternalDataDump => Path.Combine(AssetsDirectory, "external-data", "external-data.dump");

        public string WebDirectory => Path.Combine(AssetsDirectory, "web");

        public string ManifestFile => Path.Combine(AssetsDirectory, "manifest.json");

        /// <summary>
        /// Resolves the installation directory from, in order: the explicit argument, the YUKIOSM_HOME environment
        /// variable and the directory of the executable.
        /// </summary>
        public static RuntimeLayout Resolve(string? home = null) {
            home ??= Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(home)) {
                home = AppContext.BaseDirectory;
            }
            return new RuntimeLayout(Path.GetFullPath(home));
        }

        public string PostgresExecutable(string name) {
            return Path.Combine(PostgresBinDirectory, name + ExecutableSuffix);
        }

        /// <summary>Reads assets/manifest.json, which records the versions of the bundled assets.</summary>
        public AssetManifest ReadManifest() {
            if (!File.Exists(ManifestFile)) {
                return new AssetManifest();
            }
            using var stream = File.OpenRead(ManifestFile);
            return JsonSerializer.Deserialize(stream, CoreJsonContext.Default.AssetManifest) ?? new AssetManifest();
        }

        /// <summary>Throws if a required file of the installation is missing.</summary>
        public void Validate() {
            string[] required = [
                PostgresExecutable("initdb"),
                PostgresExecutable("pg_ctl"),
                PostgresExecutable("postgres"),
                PostgresExecutable("psql"),
                PostgresExecutable("pg_restore"),
                Osm2pgsqlExecutable,
                NodeExecutable,
                MapnikLibrary,
                CartoProjectFile,
                CartoCompilerScript,
                ExternalDataDump,
                FontsDirectory,
            ];
            var missing = required.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
            if (missing.Count > 0) {
                throw new FileNotFoundException(
                    $"The YukiOsmRenderer installation at '{Root}' is incomplete. Missing:{Environment.NewLine}  "
                    + string.Join(Environment.NewLine + "  ", missing)
                    + $"{Environment.NewLine}Set {HomeEnvironmentVariable} or --home to a complete installation.");
            }
        }
    }

    // Mutable properties: the JSON source generator would reset missing init-only properties to null.
    public sealed record AssetManifest {
        /// <summary>Version (tag) of OpenStreetMap Carto, e.g. "v6.1.0".</summary>
        public string OpenStreetMapCarto { get; set; } = "unknown";

        /// <summary>Version of the carto compiler.</summary>
        public string Carto { get; set; } = "unknown";

        /// <summary>Mapnik API version passed to carto; tile.openstreetmap.org uses 3.0.22.</summary>
        public string MapnikApi { get; set; } = "3.0.22";

        /// <summary>Identifier of the external data (water polygons etc.) snapshot.</summary>
        public string ExternalData { get; set; } = "unknown";
    }
}
