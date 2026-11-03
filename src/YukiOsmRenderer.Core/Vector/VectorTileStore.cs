using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core.Import;
using YukiOsmRenderer.Core.Runtime;

namespace YukiOsmRenderer.Core.Vector {
    public sealed record VectorBuildOptions {
        /// <summary>Highest zoom level stored in the tiles; clients and the renderer scale them up beyond.</summary>
        public int MaxZoom { get; init; } = 14;

        /// <summary>Planetiler schema; null for the bundled Shortbread schema.</summary>
        public string? Schema { get; init; }

        /// <summary>Further Planetiler arguments as key=value, e.g. min_feature_size=0.5.</summary>
        public IReadOnlyList<string> PlanetilerArguments { get; init; } = [];

        /// <summary>Java heap in MB; 0 for half of the physical memory.</summary>
        public int MemoryMegabytes { get; init; }

        public int Threads { get; init; } = Environment.ProcessorCount;

        public bool Force { get; init; }

        public const int MinMaxZoom = 10;

        public const int MaxMaxZoom = 15;
    }

    /// <summary>What the vector tiles of a data directory were built from; persisted next to them.</summary>
    public sealed record VectorBuildState {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        public string Fingerprint { get; set; } = "";

        public List<InputFileInfo> Inputs { get; set; } = [];

        /// <summary>SHA-256 of the schema.</summary>
        public string Schema { get; set; } = "";

        public List<string> PlanetilerArguments { get; set; } = [];

        public int MaxZoom { get; set; }

        public string Planetiler { get; set; } = "";

        public string WaterPolygons { get; set; } = "";

        public DateTimeOffset? CompletedAt { get; set; }

        /// <summary>Why tiles built with this state differ from the requested ones, or null if they do not.</summary>
        public string? Difference(VectorBuildState requested) {
            if (Version != requested.Version) {
                return "they were built by another version of yukiosm";
            }
            if (Fingerprint != requested.Fingerprint) {
                return "the input files changed";
            }
            if (Schema != requested.Schema) {
                return "the schema changed";
            }
            if (MaxZoom != requested.MaxZoom) {
                return "--max-data-zoom changed";
            }
            if (!PlanetilerArguments.SequenceEqual(requested.PlanetilerArguments)) {
                return "--planetiler-arg changed";
            }
            if (Planetiler != requested.Planetiler) {
                return "Planetiler was updated";
            }
            if (WaterPolygons != requested.WaterPolygons) {
                return "the water polygons were updated";
            }
            return null;
        }
    }

    /// <summary>
    /// The vector tiles in the "vector" directory of a data directory. Tiles are built with Planetiler into a
    /// temporary file that replaces the previous tiles only when the build succeeded. Using existing tiles writes
    /// nothing, so a read-only data directory can be served.
    /// </summary>
    public sealed partial class VectorTileStore(RuntimeLayout layout, string dataDirectory, ILogger logger) {
        public string Directory { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "vector");

        public string TilesFile => Path.Combine(Directory, "tiles.pmtiles");

        public string StateFile => Path.Combine(Directory, "state.json");

        string TemporaryDirectory => Path.Combine(Directory, "tmp");

        /// <summary>Builds the tiles unless they were built from the same input and options; without input files, uses the existing tiles.</summary>
        /// <returns>Path of the tiles.</returns>
        public async Task<string> EnsureBuiltAsync(IReadOnlyList<string> inputFiles, VectorBuildOptions options, CancellationToken cancellationToken = default) {
            var existing = ReadState();
            if (inputFiles.Count == 0) {
                if (options.Force) {
                    throw new ArgumentException("Building the vector tiles again requires input files.", nameof(options));
                }
                if (existing == null || !File.Exists(TilesFile)) {
                    throw new InvalidOperationException($"No vector tiles have been built in {Directory} yet; specify the input files.");
                }
                logger.LogInformation("Using the vector tiles built {Time:u} from {Files}", existing.CompletedAt, string.Join(", ", existing.Inputs.Select(i => i.Name)));
                return TilesFile;
            }
            foreach (var file in inputFiles) {
                if (!File.Exists(file)) {
                    throw new FileNotFoundException($"Input file '{file}' does not exist.", file);
                }
            }
            logger.LogInformation("Checking input files");
            var requested = CreateState(inputFiles, options);
            var difference = existing == null || !File.Exists(TilesFile) ? "none have been built yet" : existing.Difference(requested);
            if (difference == null && !options.Force) {
                logger.LogInformation("Vector tiles are up to date (built {Time:u})", existing!.CompletedAt);
                return TilesFile;
            }
            logger.LogInformation("Building vector tiles: {Reason}", options.Force ? "--force-import" : difference);
            await BuildAsync(inputFiles, options, requested, cancellationToken).ConfigureAwait(false);
            return TilesFile;
        }

        /// <summary>Builds the tiles into the given file, independently of the data directory's own tiles.</summary>
        public async Task BuildToFileAsync(IReadOnlyList<string> inputFiles, VectorBuildOptions options, string output, CancellationToken cancellationToken = default) {
            foreach (var file in inputFiles) {
                if (!File.Exists(file)) {
                    throw new FileNotFoundException($"Input file '{file}' does not exist.", file);
                }
            }
            await BuildAsync(inputFiles, options, null, cancellationToken, Path.GetFullPath(output)).ConfigureAwait(false);
        }

        VectorBuildState CreateState(IReadOnlyList<string> inputFiles, VectorBuildOptions options) {
            var (fingerprint, files) = InputFingerprint.Compute(inputFiles);
            var manifest = layout.ReadManifest();
            return new VectorBuildState {
                Fingerprint = fingerprint,
                Inputs = [.. files],
                Schema = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(options.Schema ?? layout.VectorSchema))),
                PlanetilerArguments = [.. options.PlanetilerArguments],
                MaxZoom = options.MaxZoom,
                Planetiler = manifest.Planetiler,
                WaterPolygons = manifest.WaterPolygons,
            };
        }

        async Task BuildAsync(IReadOnlyList<string> inputFiles, VectorBuildOptions options, VectorBuildState? state, CancellationToken cancellationToken, string? output = null) {
            System.IO.Directory.CreateDirectory(Directory);
            FileStream buildLock;
            try {
                buildLock = new FileStream(Path.Combine(Directory, "build.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException e) {
                throw new IOException($"Vector tiles are already being built in {Directory} by another yukiosm process.", e);
            }
            using (buildLock) {
                if (System.IO.Directory.Exists(TemporaryDirectory)) {
                    System.IO.Directory.Delete(TemporaryDirectory, true);
                }
                System.IO.Directory.CreateDirectory(TemporaryDirectory);
                try {
                    // Planetiler reads a single OSM file.
                    var input = inputFiles[0];
                    if (inputFiles.Count > 1) {
                        input = Path.Combine(TemporaryDirectory, "merged.osm.pbf");
                        logger.LogInformation("Merging {Count} input files", inputFiles.Count);
                        await Task.Run(() => OsmPbfMerger.Merge(inputFiles, input, logger, cancellationToken), cancellationToken).ConfigureAwait(false);
                    }
                    var schemaFile = Path.Combine(TemporaryDirectory, "schema.yml");
                    var schema = VectorSchema.Prepare(await File.ReadAllTextAsync(options.Schema ?? layout.VectorSchema, cancellationToken).ConfigureAwait(false),
                        input, layout.WaterPolygons);
                    await File.WriteAllTextAsync(schemaFile, schema, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                    var tiles = Path.Combine(TemporaryDirectory, "tiles.pmtiles");

                    var totalSize = inputFiles.Sum(f => new FileInfo(f).Length);
                    logger.LogInformation("Building vector tiles from {Count} file(s), {Size} in total, up to zoom {Zoom}",
                        inputFiles.Count, Formatting.Size(totalSize), options.MaxZoom);
                    var started = Stopwatch.StartNew();
                    await ProcessRunner.RunAsync(new ProcessSpec(layout.JavaExecutable, PlanetilerArguments(options, schemaFile, tiles)) {
                        DisplayName = "planetiler",
                        WorkingDirectory = TemporaryDirectory,
                        InterceptLine = LogPlanetilerLine,
                    }, logger, cancellationToken).ConfigureAwait(false);
                    if (!File.Exists(tiles)) {
                        throw new InvalidOperationException("Planetiler did not write the vector tiles.");
                    }

                    if (output != null) {
                        File.Move(tiles, output, overwrite: true);
                    }
                    else {
                        File.Move(tiles, TilesFile, overwrite: true);
                        state!.CompletedAt = DateTimeOffset.UtcNow;
                        await using var stream = File.Create(StateFile);
                        await JsonSerializer.SerializeAsync(stream, state, CoreJsonContext.Default.VectorBuildState, cancellationToken).ConfigureAwait(false);
                    }
                    logger.LogInformation("Vector tiles built in {Duration}: {Size}", Formatting.Duration(started.Elapsed),
                        Formatting.Size(new FileInfo(output ?? TilesFile).Length));
                }
                finally {
                    try {
                        System.IO.Directory.Delete(TemporaryDirectory, true);
                    }
                    catch (IOException e) {
                        logger.LogDebug(e, "Could not delete {Directory}", TemporaryDirectory);
                    }
                }
            }
        }

        List<string> PlanetilerArguments(VectorBuildOptions options, string schemaFile, string tiles) {
            var memory = options.MemoryMegabytes > 0
                ? options.MemoryMegabytes
                : (int)Math.Max(1024, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (2 * 1024 * 1024));
            List<string> arguments = [
                $"-Xmx{memory}m",
                // Planetiler's libraries use native access and sun.misc.Unsafe, which Java 25 warns about.
                "--enable-native-access=ALL-UNNAMED",
                "--sun-misc-unsafe-memory-access=allow",
                "-jar", layout.PlanetilerJar,
                "generate-custom",
                $"--schema={schemaFile}",
                $"--output={tiles}",
                $"--tmpdir={Path.Combine(TemporaryDirectory, "planetiler")}",
                $"--threads={Math.Max(2, options.Threads)}",
                $"--maxzoom={options.MaxZoom}",
                "--force",
            ];
            arguments.AddRange(options.PlanetilerArguments.Select(a => "--" + a));
            return arguments;
        }

        /// <summary>Planetiler logs "0:01:23 INF [stage] - message"; its progress lines are worth showing.</summary>
        bool LogPlanetilerLine(string line) {
            var match = PlanetilerLogLine().Match(line);
            if (!match.Success) {
                return false;
            }
            var level = match.Groups["level"].Value switch {
                "INF" => LogLevel.Information,
                "WAR" => LogLevel.Warning,
                "ERR" => LogLevel.Error,
                _ => LogLevel.Debug,
            };
            var message = match.Groups["message"].Value;
            // Arguments and per-thread statistics (indented) are only of interest when debugging.
            if (level == LogLevel.Information && (message.Contains("- argument:", StringComparison.Ordinal) || message.Contains("-   ", StringComparison.Ordinal))) {
                level = LogLevel.Debug;
            }
            if (message.Trim().Length > 0) {
                logger.Log(level, "[planetiler] {Message}", message);
            }
            return true;
        }

        VectorBuildState? ReadState() {
            if (!File.Exists(StateFile)) {
                return null;
            }
            try {
                using var stream = File.OpenRead(StateFile);
                return JsonSerializer.Deserialize(stream, CoreJsonContext.Default.VectorBuildState);
            }
            catch (JsonException e) {
                logger.LogWarning("Ignoring the unreadable {File}: {Message}", StateFile, e.Message);
                return null;
            }
        }

        // Not anchored at the start: Planetiler may prefix lines with terminal escape sequences.
        [GeneratedRegex(@"\d+:\d\d:\d\d (?<level>DEB|INF|WAR|ERR|TRA) (?<message>.*)$")]
        private static partial Regex PlanetilerLogLine();
    }
}
