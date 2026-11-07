using System.Globalization;

namespace YukiElevation {
    internal sealed record ServiceOptions {
        public const string Usage = """
            Usage: yukielevation [options]

            Serves elevations in metres for WGS 84 coordinates, entirely from local files.

            Options:
              --gebco <dir>         Directory with the GeoTIFFs of GEBCO (or other rasters in degrees that form one
                                    grid); used where no more detailed data exists.
              --glo30 <dir>         Directory tree with Copernicus DEM tiles (GLO-30 or GLO-90), which are preferred.
              --host <host>         Address to listen on (default: localhost; 0.0.0.0 for all).
              --port <port>         Port (default: 8100).
              --max-points <n>      Maximum number of points of one POST request (default: 1000).
              --cache-mb <n>        Memory for decoded raster blocks, in MiB (default: 512).
              --max-open-tiles <n>  Copernicus tiles kept open (default: 256).
              --version             Show the version.
              -h, --help            Show this help.

            Endpoints:
              GET  /elevation?lat=<lat>&lon=<lon>[&interpolation=bilinear|nearest]
              POST /elevation[?interpolation=...]  with a JSON array of {"lat": ..., "lon": ...}
              GET  /datasets
              GET  /health
            """;

        public string? Gebco { get; init; }

        public string? Glo30 { get; init; }

        public string Host { get; init; } = "localhost";

        public int Port { get; init; } = 8100;

        public int MaxPoints { get; init; } = 1000;

        public int CacheMegabytes { get; init; } = 512;

        public int MaxOpenTiles { get; init; } = 256;

        /// <summary>The options of a command line, or null if it asks for help.</summary>
        /// <exception cref="ArgumentException">The command line is invalid.</exception>
        public static ServiceOptions? Parse(IReadOnlyList<string> args) {
            var options = new ServiceOptions();
            for (var i = 0; i < args.Count; i++) {
                var name = args[i];
                if (name is "-h" or "--help" or "-?") {
                    return null;
                }
                string? value = null;
                var equals = name.IndexOf('=');
                if (name.StartsWith("--", StringComparison.Ordinal) && equals > 0) {
                    value = name[(equals + 1)..];
                    name = name[..equals];
                }
                string Value() {
                    if (value != null) {
                        return value;
                    }
                    if (i + 1 >= args.Count) {
                        throw new ArgumentException($"Option '{name}' needs a value.");
                    }
                    return args[++i];
                }
                options = name switch {
                    "--gebco" => options with { Gebco = Directory(Value(), name) },
                    "--glo30" => options with { Glo30 = Directory(Value(), name) },
                    "--host" => options with { Host = Value() },
                    "--port" => options with { Port = Integer(Value(), name, 1, 65535) },
                    "--max-points" => options with { MaxPoints = Integer(Value(), name, 1, 1_000_000) },
                    "--cache-mb" => options with { CacheMegabytes = Integer(Value(), name, 16, 1_000_000) },
                    "--max-open-tiles" => options with { MaxOpenTiles = Integer(Value(), name, 1, 100_000) },
                    _ => throw new ArgumentException($"Unknown option '{name}'."),
                };
            }
            if (options.Gebco == null && options.Glo30 == null) {
                throw new ArgumentException("Give at least one of --gebco and --glo30.");
            }
            return options;
        }

        static string Directory(string value, string name) {
            var path = Path.GetFullPath(value);
            if (!System.IO.Directory.Exists(path)) {
                throw new ArgumentException($"The directory '{path}' of {name} does not exist.");
            }
            return path;
        }

        static int Integer(string value, string name, int min, int max) {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < min || result > max) {
                throw new ArgumentException($"{name} must be a whole number from {min} to {max}.");
            }
            return result;
        }
    }
}
