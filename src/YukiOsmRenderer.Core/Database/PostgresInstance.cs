using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;
using YukiOsmRenderer.Core.Runtime;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Core.Database {
    /// <summary>
    /// PostgreSQL cluster bundled with YukiOsmRenderer. It lives in the data directory, listens on a random
    /// localhost port with password authentication and is stopped when the instance is disposed.
    /// </summary>
    public sealed class PostgresInstance : IAsyncDisposable {
        public const string UserName = "yukiosm";
        public const string DatabaseName = "gis";
        const string ConfigurationFile = "yukiosm.conf";

        readonly RuntimeLayout layout;
        readonly DataDirectory data;
        readonly ILogger logger;
        bool running;

        PostgresInstance(RuntimeLayout layout, DataDirectory data, ILogger logger, int port, string password) {
            this.layout = layout;
            this.data = data;
            this.logger = logger;
            Port = port;
            Password = password;
        }

        public int Port { get; }

        public string Password { get; }

        public DatabaseConnection Connection => new("127.0.0.1", Port, DatabaseName, UserName, Password);

        /// <summary>Environment variables that point libpq based tools (psql, pg_restore, osm2pgsql) at this instance.</summary>
        public IReadOnlyDictionary<string, string?> ClientEnvironment => new Dictionary<string, string?> {
            ["PGHOST"] = "127.0.0.1",
            ["PGPORT"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PGUSER"] = UserName,
            ["PGPASSWORD"] = Password,
            ["PGDATABASE"] = DatabaseName,
            ["PGCLIENTENCODING"] = "UTF8",
        };

        public string ConnectionString(string database = DatabaseName) {
            return new NpgsqlConnectionStringBuilder {
                Host = "127.0.0.1",
                Port = Port,
                Username = UserName,
                Password = Password,
                Database = database,
                Pooling = false,
                CommandTimeout = 0,
            }.ConnectionString;
        }

        /// <summary>Initializes the cluster if needed and starts the server.</summary>
        public static async Task<PostgresInstance> StartAsync(RuntimeLayout layout, DataDirectory data, PostgresSettings settings, ILogger logger, CancellationToken cancellationToken = default) {
            if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess) {
                throw new InvalidOperationException("PostgreSQL cannot run as root; run yukiosm as a regular user.");
            }
            var password = await EnsureClusterAsync(layout, data, logger, cancellationToken).ConfigureAwait(false);
            var port = FindFreePort();
            var instance = new PostgresInstance(layout, data, logger, port, password);
            await File.WriteAllTextAsync(Path.Combine(data.PostgresDirectory, ConfigurationFile), settings.ToConfigurationFile(port), cancellationToken).ConfigureAwait(false);

            // A server left behind by a crashed run would hold the cluster; nothing else can use it while we hold the lock.
            if (File.Exists(Path.Combine(data.PostgresDirectory, "postmaster.pid"))) {
                logger.LogInformation("Stopping a PostgreSQL server left over from a previous run");
                await instance.RunPgCtlAsync(["stop", "-m", "fast", "-w", "-t", "300"], ignoreFailure: true, CancellationToken.None).ConfigureAwait(false);
            }

            logger.LogInformation("Starting PostgreSQL on port {Port}", port);
            await instance.RunPgCtlAsync(["start", "-w", "-t", "600", "-l", Path.Combine(data.LogDirectory, "postgresql.log")], ignoreFailure: false, cancellationToken).ConfigureAwait(false);
            instance.running = true;
            try {
                await using var connection = new NpgsqlConnection(instance.ConnectionString("postgres"));
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }
            catch {
                await instance.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            return instance;
        }

        public async Task StopAsync() {
            if (!running) {
                return;
            }
            running = false;
            logger.LogInformation("Stopping PostgreSQL");
            await RunPgCtlAsync(["stop", "-m", "fast", "-w", "-t", "300"], ignoreFailure: true, CancellationToken.None).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync() {
            await StopAsync().ConfigureAwait(false);
        }

        /// <summary>Environment for the server processes (PROJ data used by PostGIS).</summary>
        internal static Dictionary<string, string?> ServerEnvironment(RuntimeLayout layout) {
            var environment = new Dictionary<string, string?> {
                ["LC_ALL"] = "C",
                ["LANG"] = "C",
            };
            if (layout.ProjDataDirectory is { } proj) {
                environment["PROJ_DATA"] = proj;
                environment["PROJ_LIB"] = proj;
            }
            return environment;
        }

        async Task RunPgCtlAsync(IReadOnlyList<string> arguments, bool ignoreFailure, CancellationToken cancellationToken) {
            var fullArguments = new List<string> { "-D", data.PostgresDirectory, "-s" };
            fullArguments.AddRange(arguments);
            try {
                await ProcessRunner.RunAsync(new ProcessSpec(layout.PostgresExecutable("pg_ctl"), fullArguments) {
                    Environment = ServerEnvironment(layout),
                    OutputLogLevel = LogLevel.Information,
                    WaitForOutputClose = false,
                }, logger, cancellationToken).ConfigureAwait(false);
            }
            catch (ProcessFailedException e) when (ignoreFailure) {
                logger.LogDebug("pg_ctl {Command} failed: {Message}", arguments[0], e.Message);
            }
        }

        static async Task<string> EnsureClusterAsync(RuntimeLayout layout, DataDirectory data, ILogger logger, CancellationToken cancellationToken) {
            var versionFile = Path.Combine(data.PostgresDirectory, "PG_VERSION");
            if (File.Exists(versionFile) && File.Exists(data.PasswordFile)) {
                await CheckClusterVersionAsync(layout, data, (await File.ReadAllTextAsync(versionFile, cancellationToken).ConfigureAwait(false)).Trim(), logger, cancellationToken).ConfigureAwait(false);
                return (await File.ReadAllTextAsync(data.PasswordFile, cancellationToken).ConfigureAwait(false)).Trim();
            }

            logger.LogInformation("Initializing PostgreSQL cluster in {Directory}", data.PostgresDirectory);
            if (Directory.Exists(data.PostgresDirectory)) {
                Directory.Delete(data.PostgresDirectory, true);
            }
            var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            await File.WriteAllTextAsync(data.PasswordFile, password, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(data.PasswordFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            await ProcessRunner.RunAsync(new ProcessSpec(layout.PostgresExecutable("initdb"), [
                "-D", data.PostgresDirectory,
                "-U", UserName,
                "--pwfile", data.PasswordFile,
                "-A", "scram-sha-256",
                "-E", "UTF8",
                "--locale", "C",
            ]) {
                Environment = ServerEnvironment(layout),
            }, logger, cancellationToken).ConfigureAwait(false);
            await File.AppendAllTextAsync(
                Path.Combine(data.PostgresDirectory, "postgresql.conf"),
                $"\ninclude_if_exists = '{ConfigurationFile}'\n",
                cancellationToken).ConfigureAwait(false);
            return password;
        }

        /// <summary>A server only opens clusters of its own major version.</summary>
        static async Task CheckClusterVersionAsync(RuntimeLayout layout, DataDirectory data, string clusterVersion, ILogger logger, CancellationToken cancellationToken) {
            var result = await ProcessRunner.RunAsync(new ProcessSpec(layout.PostgresExecutable("pg_ctl"), ["--version"]) {
                Environment = ServerEnvironment(layout),
                CaptureOutput = true,
            }, logger, cancellationToken).ConfigureAwait(false);
            // For example "pg_ctl (PostgreSQL) 17.11".
            var serverVersion = Regex.Match(result.StandardOutput, @"\)\s*(\d+)").Groups[1].Value;
            if (serverVersion.Length > 0 && serverVersion != clusterVersion) {
                throw new InvalidOperationException(
                    $"The database in {data.Root} was created by PostgreSQL {clusterVersion}, but this version of yukiosm comes with PostgreSQL {serverVersion}, " +
                    "which cannot open it. Import the input files into a new data directory (another --data-dir), or keep using the earlier version of " +
                    "yukiosm for this one. For large databases, converting it with pg_upgrade from runtime/pgsql/bin is faster, giving the runtime/pgsql/bin " +
                    "directory of the earlier version as the old binaries.");
            }
        }

        static int FindFreePort() {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
