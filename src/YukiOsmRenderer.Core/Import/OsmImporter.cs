using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;
using YukiOsmRenderer.Core.Database;
using YukiOsmRenderer.Core.Runtime;

namespace YukiOsmRenderer.Core.Import {
    public sealed record ImportOptions {
        /// <summary>Number of parallel processes/threads used by osm2pgsql.</summary>
        public int Processes { get; init; } = Environment.ProcessorCount;

        /// <summary>Use the osm2pgsql flat node store; null decides by input size.</summary>
        public bool? FlatNodes { get; init; }

        /// <summary>Inputs larger than this use the flat node store when <see cref="FlatNodes"/> is null.</summary>
        public long FlatNodesThresholdBytes { get; init; } = 8L * 1024 * 1024 * 1024;

        /// <summary>Rebuild the database even if it already matches the input files.</summary>
        public bool Force { get; init; }

        /// <summary>
        /// Without input files, bring a database imported by an earlier version up to date where that works in place,
        /// instead of using it unchanged.
        /// </summary>
        public bool UpdateExisting { get; init; }

        /// <summary>How often the progress of long import steps is logged.</summary>
        public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Builds the rendering database the same way OpenStreetMap Carto's import does: osm2pgsql with the Carto flex
    /// style, indexes.sql, functions.sql, common-values.sql and the external data (water polygons etc.).
    /// </summary>
    public sealed class OsmImporter(RuntimeLayout layout, DataDirectory data, PostgresInstance postgres, ILogger logger) {
        readonly Stopwatch import = new();
        /// <summary>
        /// Imports the input files unless the database already contains them, updating it in place where the scripts or
        /// the external data of this installation differ but the tables do not. Without input files, uses the existing
        /// database, unchanged unless <see cref="ImportOptions.UpdateExisting"/> is set.
        /// </summary>
        /// <returns>
        /// Null if the database matches this installation; otherwise what to run to bring it up to date, for when it
        /// turns out not to work as it is.
        /// </returns>
        public async Task<string?> EnsureImportedAsync(IReadOnlyList<string> inputFiles, ImportOptions options, CancellationToken cancellationToken = default) {
            var installation = ImportInstallation.Read(layout);
            if (inputFiles.Count == 0) {
                return await UseExistingImportAsync(installation, options, cancellationToken).ConfigureAwait(false);
            }
            foreach (var file in inputFiles) {
                if (!File.Exists(file)) {
                    throw new FileNotFoundException($"Input file '{file}' does not exist.", file);
                }
            }
            logger.LogInformation("Checking input files");
            var (fingerprint, files) = InputFingerprint.Compute(inputFiles);
            var state = ReadState();
            if (!options.Force && state is { Completed: true } && state.Fingerprint == fingerprint) {
                var difference = state.CompareWith(installation);
                if (difference.IsEmpty) {
                    logger.LogInformation("Database is up to date (imported {Time:u})", state.CompletedAt);
                    return null;
                }
                if (!difference.ImportScript) {
                    await UpdateAsync(state, difference, installation, options, cancellationToken).ConfigureAwait(false);
                    return null;
                }
                logger.LogInformation("Importing again: the tables were created by the import script of OpenStreetMap Carto {Imported}, which differs from that of {Installed}",
                    state.CartoVersion, installation.CartoVersion);
            }

            var started = DateTimeOffset.UtcNow;
            import.Restart();
            var newState = new ImportState {
                Fingerprint = fingerprint,
                CartoVersion = installation.CartoVersion,
                ImportScript = installation.ImportScript,
                SqlScripts = installation.SqlScripts,
                ExternalData = installation.ExternalData,
                Inputs = files,
                Completed = false,
            };
            await WriteStateAsync(newState, cancellationToken).ConfigureAwait(false);

            var totalSize = files.Sum(f => f.Size);
            logger.LogInformation("Importing {Count} file(s), {Size:N0} MB in total", files.Count, totalSize / (1024 * 1024));
            await CreateDatabaseAsync(cancellationToken).ConfigureAwait(false);
            await RunOsm2pgsqlAsync(inputFiles, totalSize, options, cancellationToken).ConfigureAwait(false);
            foreach (var script in ImportInstallation.SqlScriptNames) {
                await RunSqlFileAsync(Path.Combine(layout.CartoDirectory, script), options, cancellationToken).ConfigureAwait(false);
            }
            await LoadExternalDataAsync(options, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Analyzing the database");
            await using (StartProgress("ANALYZE", options)) {
                await ExecuteAsync("ANALYZE", PostgresInstance.DatabaseName, cancellationToken).ConfigureAwait(false);
            }
            await ExecuteAsync("CHECKPOINT", PostgresInstance.DatabaseName, cancellationToken).ConfigureAwait(false);

            await WriteStateAsync(newState with { Completed = true, CompletedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Import finished in {Duration}", DateTimeOffset.UtcNow - started);
            return null;
        }

        async Task<string?> UseExistingImportAsync(ImportInstallation installation, ImportOptions options, CancellationToken cancellationToken) {
            if (options.Force) {
                throw new ArgumentException("Importing again requires input files.", nameof(options));
            }
            var state = ReadState();
            if (state is not { Completed: true }) {
                throw new InvalidOperationException($"No data has been imported into {data.Root} yet; specify the input files.");
            }
            var difference = state.CompareWith(installation);
            if (options.UpdateExisting && difference.CanUpdateInPlace) {
                try {
                    state = await UpdateAsync(state, difference, installation, options, cancellationToken).ConfigureAwait(false);
                }
                catch (ProcessFailedException e) when (difference.ImportScript) {
                    throw new InvalidOperationException(
                        $"Updating the database in place failed: {e.Message}{Environment.NewLine}{ImportAgainAdvice(state)}", e);
                }
                difference = state.CompareWith(installation);
            }
            logger.LogInformation("Using the imported {Files} (imported {Time:u})", string.Join(", ", state.Inputs.Select(f => f.Name)), state.CompletedAt);
            if (difference.IsEmpty) {
                return null;
            }
            var advice = difference.ImportScript ? ImportAgainAdvice(state) : UpdateAdvice();
            logger.LogWarning("The database was imported by an earlier version of yukiosm ({Difference}); using it as it is. {Advice}",
                Describe(state, difference, installation), advice);
            return advice;
        }

        string UpdateAdvice() {
            return $"To update it in place without importing the input files again, run: yukiosm import --data-dir {QuoteArgument(data.Root)}";
        }

        string ImportAgainAdvice(ImportState state) {
            var inputs = string.Join(" ", state.Inputs.Select(f => "-i " + QuoteArgument(f.Name)));
            return $"Its tables only change by importing the input files again: yukiosm import --data-dir {QuoteArgument(data.Root)} {inputs} (with the paths of these files)";
        }

        static string QuoteArgument(string value) {
            return value.Any(c => char.IsWhiteSpace(c) || c == '"') ? $"\"{value}\"" : value;
        }

        static string Describe(ImportState state, ImportDifference difference, ImportInstallation installation) {
            var parts = new List<string>();
            if (difference.ImportScript) {
                parts.Add(state.CartoVersion == installation.CartoVersion
                    ? "tables from another import script"
                    : $"tables from the import script of OpenStreetMap Carto {state.CartoVersion}, this version has {installation.CartoVersion}");
            }
            if (difference.SqlScripts.Count > 0) {
                parts.Add("other " + string.Join(", ", difference.SqlScripts));
            }
            if (difference.ExternalData) {
                parts.Add($"external data of {state.ExternalData}, this version has {installation.ExternalData}");
            }
            return string.Join("; ", parts);
        }

        /// <summary>Runs the SQL scripts that differ again and loads other external data, keeping the tables.</summary>
        async Task<ImportState> UpdateAsync(
            ImportState state, ImportDifference difference, ImportInstallation installation, ImportOptions options, CancellationToken cancellationToken) {
            var started = DateTimeOffset.UtcNow;
            import.Restart();
            logger.LogInformation("Updating the database in place ({Difference})", Describe(state, difference, installation));
            foreach (var script in difference.SqlScripts) {
                var path = Path.Combine(layout.CartoDirectory, script);
                if (script == "indexes.sql") {
                    path = Path.Combine(data.TemporaryDirectory, script);
                    await File.WriteAllTextAsync(path, ReplacingIndexes(await File.ReadAllTextAsync(Path.Combine(layout.CartoDirectory, script), cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
                }
                await RunSqlFileAsync(path, options, cancellationToken).ConfigureAwait(false);
            }
            if (difference.ExternalData) {
                await LoadExternalDataAsync(options, cancellationToken).ConfigureAwait(false);
            }
            await ExecuteAsync("CHECKPOINT", PostgresInstance.DatabaseName, cancellationToken).ConfigureAwait(false);

            var updated = state with {
                CartoVersion = difference.ImportScript ? state.CartoVersion : installation.CartoVersion,
                ImportScript = difference.ImportScript ? state.ImportScript : installation.ImportScript,
                SqlScripts = installation.SqlScripts,
                ExternalData = installation.ExternalData,
            };
            await WriteStateAsync(updated, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Database updated in {Duration}", DateTimeOffset.UtcNow - started);
            return updated;
        }

        /// <summary>Makes indexes.sql replace the indexes it creates, which already exist in an imported database.</summary>
        internal static string ReplacingIndexes(string sql) {
            return Regex.Replace(sql, @"\bCREATE INDEX (\w+) ON\b", "DROP INDEX IF EXISTS $1;" + "\n" + "CREATE INDEX $1 ON");
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
                // Progress lines are taken apart by ImportProgress rather than logged.
                "--log-progress=true",
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
            await using var progress = StartProgress("osm2pgsql", options, inputFiles);
            await ProcessRunner.RunAsync(new ProcessSpec(layout.Osm2pgsqlExecutable, arguments) {
                WorkingDirectory = layout.CartoDirectory,
                Environment = postgres.ClientEnvironment,
                OutputLogLevel = LogLevel.Information,
                InterceptLine = progress.Intercept,
                Started = progress.Attach,
            }, logger, cancellationToken).ConfigureAwait(false);
        }

        ImportProgress StartProgress(string step, ImportOptions options, IReadOnlyList<string>? inputFiles = null) {
            return new ImportProgress(step, logger, postgres.ConnectionString(PostgresInstance.DatabaseName), import, options.ProgressInterval, inputFiles);
        }

        async Task RunSqlFileAsync(string path, ImportOptions options, CancellationToken cancellationToken) {
            logger.LogInformation("Running {Script}", Path.GetFileName(path));
            await using var progress = StartProgress(Path.GetFileName(path), options);
            await ProcessRunner.RunAsync(new ProcessSpec(layout.PostgresExecutable("psql"), ["-X", "-q", "-v", "ON_ERROR_STOP=1", "-f", path]) {
                WorkingDirectory = layout.CartoDirectory,
                Environment = postgres.ClientEnvironment,
                OutputLogLevel = LogLevel.Debug,
            }, logger, cancellationToken).ConfigureAwait(false);
        }

        async Task LoadExternalDataAsync(ImportOptions options, CancellationToken cancellationToken) {
            logger.LogInformation("Loading external data (water polygons, ice sheets) with get-external-data.py from the bundled archives, without downloading");
            // The script takes the archives from its cache directory instead of downloading them (--cache --no-update) and
            // unpacks them there, so the bundled archives are made available in a writable directory.
            var cache = Path.Combine(data.TemporaryDirectory, "external-data");
            if (Directory.Exists(cache)) {
                Directory.Delete(cache, true);
            }
            Directory.CreateDirectory(cache);
            try {
                foreach (var file in Directory.EnumerateFiles(layout.ExternalDataDirectory)) {
                    LinkOrCopy(file, Path.Combine(cache, Path.GetFileName(file)));
                }
                await using var progress = StartProgress("external data", options);
                await ProcessRunner.RunAsync(new ProcessSpec(layout.PythonExecutable, [
                    layout.ExternalDataScript,
                    "--config", layout.ExternalDataConfig,
                    "--data", cache,
                    "--cache",
                    "--no-update",
                    "--database", PostgresInstance.DatabaseName,
                ]) {
                    WorkingDirectory = layout.CartoDirectory,
                    Environment = new Dictionary<string, string?>(postgres.ClientEnvironment) {
                        // The script runs ogr2ogr from PATH; it must not load driver plugins of another GDAL installation.
                        ["PATH"] = layout.GdalBinDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                        ["GDAL_DRIVER_PATH"] = "disable",
                        ["PYTHONHOME"] = null,
                        ["PYTHONPATH"] = null,
                        ["PYTHONNOUSERSITE"] = "1",
                        ["PYTHONUTF8"] = "1",
                    },
                    OutputLogLevel = LogLevel.Information,
                }, logger, cancellationToken).ConfigureAwait(false);
            }
            finally {
                Directory.Delete(cache, true);
            }
        }

        static void LinkOrCopy(string source, string destination) {
            try {
                File.CreateSymbolicLink(destination, source);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                // Windows only allows symbolic links with developer mode or administrator rights.
                File.Copy(source, destination);
            }
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
