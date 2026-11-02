using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using YukiOsmRenderer.Core.Database;
using YukiOsmRenderer.Core.Runtime;

namespace YukiOsmRenderer.Core.Import {
    public sealed record ImportOptions {
        /// <summary>Number of parallel processes/threads used by osm2pgsql and pg_restore.</summary>
        public int Processes { get; init; } = Environment.ProcessorCount;

        /// <summary>Use the osm2pgsql flat node store; null decides by input size.</summary>
        public bool? FlatNodes { get; init; }

        /// <summary>Inputs larger than this use the flat node store when <see cref="FlatNodes"/> is null.</summary>
        public long FlatNodesThresholdBytes { get; init; } = 8L * 1024 * 1024 * 1024;

        /// <summary>Rebuild the database even if it already matches the input files.</summary>
        public bool Force { get; init; }
    }

    /// <summary>
    /// Builds the rendering database the same way OpenStreetMap Carto's import does: osm2pgsql with the Carto flex
    /// style, indexes.sql, functions.sql, common-values.sql and the external data (water polygons etc.).
    /// </summary>
    public sealed class OsmImporter(RuntimeLayout layout, DataDirectory data, PostgresInstance postgres, ILogger logger) {
        /// <summary>Imports the input files unless the database already contains them; without input files, uses the existing database.</summary>
        /// <returns>True if an import was performed.</returns>
        public async Task<bool> EnsureImportedAsync(IReadOnlyList<string> inputFiles, ImportOptions options, CancellationToken cancellationToken = default) {
            if (inputFiles.Count == 0) {
                UseExistingImport(options);
                return false;
            }
            foreach (var file in inputFiles) {
                if (!File.Exists(file)) {
                    throw new FileNotFoundException($"Input file '{file}' does not exist.", file);
                }
            }
            logger.LogInformation("Checking input files");
            var (fingerprint, files) = InputFingerprint.Compute(inputFiles);
            var manifest = layout.ReadManifest();
            var state = ReadState();
            if (!options.Force && state != null && state.Matches(fingerprint, manifest.OpenStreetMapCarto, manifest.ExternalData)) {
                logger.LogInformation("Database is up to date (imported {Time:u})", state.CompletedAt);
                return false;
            }

            var started = DateTimeOffset.UtcNow;
            await WriteStateAsync(new ImportState {
                Fingerprint = fingerprint,
                CartoVersion = manifest.OpenStreetMapCarto,
                ExternalData = manifest.ExternalData,
                Inputs = files,
                Completed = false,
            }, cancellationToken).ConfigureAwait(false);

            var totalSize = files.Sum(f => f.Size);
            logger.LogInformation("Importing {Count} file(s), {Size:N0} MB in total", files.Count, totalSize / (1024 * 1024));
            await CreateDatabaseAsync(cancellationToken).ConfigureAwait(false);
            await RunOsm2pgsqlAsync(inputFiles, totalSize, options, cancellationToken).ConfigureAwait(false);
            foreach (var script in new[] { "indexes.sql", "functions.sql", "common-values.sql" }) {
                await RunSqlFileAsync(Path.Combine(layout.CartoDirectory, script), cancellationToken).ConfigureAwait(false);
            }
            await RestoreExternalDataAsync(options, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Analyzing the database");
            await ExecuteAsync("ANALYZE", PostgresInstance.DatabaseName, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync("CHECKPOINT", PostgresInstance.DatabaseName, cancellationToken).ConfigureAwait(false);

            await WriteStateAsync(new ImportState {
                Fingerprint = fingerprint,
                CartoVersion = manifest.OpenStreetMapCarto,
                ExternalData = manifest.ExternalData,
                Inputs = files,
                Completed = true,
                CompletedAt = DateTimeOffset.UtcNow,
            }, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Import finished in {Duration}", DateTimeOffset.UtcNow - started);
            return true;
        }

        void UseExistingImport(ImportOptions options) {
            if (options.Force) {
                throw new ArgumentException("Importing again requires input files.", nameof(options));
            }
            var state = ReadState();
            if (state is not { Completed: true }) {
                throw new InvalidOperationException($"No data has been imported into {data.Root} yet; specify the input files.");
            }
            var manifest = layout.ReadManifest();
            if (!state.IsUsable(manifest.OpenStreetMapCarto, manifest.ExternalData)) {
                throw new InvalidOperationException($"The data in {data.Root} was imported by a different version of yukiosm; specify the input files to import them again.");
            }
            logger.LogInformation("Using the imported {Files} (imported {Time:u})", string.Join(", ", state.Inputs.Select(f => f.Name)), state.CompletedAt);
        }

        internal static IReadOnlyList<string> BuildOsm2pgsqlArguments(
            string styleFile, IReadOnlyList<string> inputFiles, long totalInputSize, ImportOptions options, string flatNodesFile, long totalMemoryBytes) {
            var useFlatNodes = options.FlatNodes ?? totalInputSize > options.FlatNodesThresholdBytes;
            // Following the osm2pgsql manual: no node cache with flat nodes, otherwise about the input size, capped
            // here at half of the memory to leave room for PostgreSQL.
            var cacheMegabytes = useFlatNodes ? 0 : Math.Max(100, Math.Min(totalInputSize, totalMemoryBytes / 2) / (1024 * 1024));
            var arguments = new List<string> {
                "--create",
                "--slim",
                "--drop",
                "--output=flex",
                $"--style={styleFile}",
                $"--database={PostgresInstance.DatabaseName}",
                $"--number-processes={Math.Max(1, options.Processes)}",
                $"--cache={cacheMegabytes}",
                "--log-progress=false",
            };
            if (useFlatNodes) {
                arguments.Add($"--flat-nodes={flatNodesFile}");
            }
            arguments.AddRange(inputFiles.Select(Path.GetFullPath));
            return arguments;
        }

        async Task CreateDatabaseAsync(CancellationToken cancellationToken) {
            logger.LogInformation("Creating database {Database}", PostgresInstance.DatabaseName);
            await ExecuteAsync($"DROP DATABASE IF EXISTS {PostgresInstance.DatabaseName} WITH (FORCE)", "postgres", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync($"CREATE DATABASE {PostgresInstance.DatabaseName} ENCODING 'UTF8' TEMPLATE template0", "postgres", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync("CREATE EXTENSION postgis; CREATE EXTENSION hstore;", PostgresInstance.DatabaseName, cancellationToken).ConfigureAwait(false);
        }

        async Task RunOsm2pgsqlAsync(IReadOnlyList<string> inputFiles, long totalSize, ImportOptions options, CancellationToken cancellationToken) {
            if (File.Exists(data.FlatNodesFile)) {
                File.Delete(data.FlatNodesFile);
            }
            var arguments = BuildOsm2pgsqlArguments(
                Path.Combine(layout.CartoDirectory, "openstreetmap-carto-flex.lua"),
                inputFiles, totalSize, options, data.FlatNodesFile, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
            logger.LogInformation("Running osm2pgsql");
            await ProcessRunner.RunAsync(new ProcessSpec(layout.Osm2pgsqlExecutable, arguments) {
                WorkingDirectory = layout.CartoDirectory,
                Environment = postgres.ClientEnvironment,
                OutputLogLevel = LogLevel.Information,
            }, logger, cancellationToken).ConfigureAwait(false);
        }

        async Task RunSqlFileAsync(string path, CancellationToken cancellationToken) {
            logger.LogInformation("Running {Script}", Path.GetFileName(path));
            await ProcessRunner.RunAsync(new ProcessSpec(layout.PostgresExecutable("psql"), ["-X", "-q", "-v", "ON_ERROR_STOP=1", "-f", path]) {
                WorkingDirectory = layout.CartoDirectory,
                Environment = postgres.ClientEnvironment,
                OutputLogLevel = LogLevel.Debug,
            }, logger, cancellationToken).ConfigureAwait(false);
        }

        async Task RestoreExternalDataAsync(ImportOptions options, CancellationToken cancellationToken) {
            logger.LogInformation("Loading external data (water polygons, ice sheets)");
            await ProcessRunner.RunAsync(new ProcessSpec(layout.PostgresExecutable("pg_restore"), [
                "--no-owner",
                "--no-privileges",
                "--exit-on-error",
                $"--jobs={Math.Clamp(options.Processes, 1, 8)}",
                $"--dbname={PostgresInstance.DatabaseName}",
                layout.ExternalDataDump,
            ]) {
                Environment = postgres.ClientEnvironment,
                OutputLogLevel = LogLevel.Information,
            }, logger, cancellationToken).ConfigureAwait(false);
        }

        async Task ExecuteAsync(string sql, string database, CancellationToken cancellationToken) {
            await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        ImportState? ReadState() {
            if (!File.Exists(data.ImportStateFile)) {
                return null;
            }
            try {
                using var stream = File.OpenRead(data.ImportStateFile);
                return JsonSerializer.Deserialize(stream, CoreJsonContext.Default.ImportState);
            }
            catch (JsonException e) {
                logger.LogWarning("Ignoring unreadable import state: {Message}", e.Message);
                return null;
            }
        }

        async Task WriteStateAsync(ImportState state, CancellationToken cancellationToken) {
            var temporary = data.ImportStateFile + ".tmp";
            await using (var stream = File.Create(temporary)) {
                await JsonSerializer.SerializeAsync(stream, state, CoreJsonContext.Default.ImportState, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, data.ImportStateFile, overwrite: true);
        }
    }
}
