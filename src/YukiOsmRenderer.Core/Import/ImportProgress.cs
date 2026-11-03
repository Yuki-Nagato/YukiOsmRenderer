using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace YukiOsmRenderer.Core.Import {
    /// <summary>A statement the database is running.</summary>
    /// <param name="Fraction">Progress of an index build, if known.</param>
    /// <param name="Rows">Rows copied so far by a COPY.</param>
    readonly record struct DatabaseTask(TimeSpan Running, string Statement, string? Phase, double? Fraction, long? Rows);

    /// <summary>
    /// Logs the progress of a long import step at regular intervals: the latest status line of the running tool, how much
    /// of its input it has read and what the database is busy with.
    /// </summary>
    sealed partial class ImportProgress : IAsyncDisposable {
        const int MaxTasks = 3;

        // Tasks shorter than this are routine queries of osm2pgsql, not steps worth reporting.
        static readonly TimeSpan MinTaskDuration = TimeSpan.FromSeconds(5);

        readonly string step;
        readonly ILogger logger;
        readonly string connectionString;
        readonly Stopwatch import;
        readonly IReadOnlyList<string> inputFiles;
        readonly long inputSize;
        readonly TimeSpan interval;
        readonly Stopwatch stopwatch = Stopwatch.StartNew();
        readonly CancellationTokenSource stop = new();
        readonly Lock sync = new();
        readonly Task loop;
        string? status;
        TimeSpan statusTime;
        long pendingTotal;
        string pendingType = "";
        Process? process;
        // Only used by the reporting loop.
        (int ElementType, TimeSpan Elapsed, long Read) phase = (-1, TimeSpan.Zero, 0);

        /// <param name="import">Runs since the start of the whole import.</param>
        /// <param name="inputFiles">Files read by the process attached with <see cref="Attach"/>.</param>
        public ImportProgress(string step, ILogger logger, string connectionString, Stopwatch import, TimeSpan interval, IReadOnlyList<string>? inputFiles = null) {
            this.step = step;
            this.logger = logger;
            this.connectionString = connectionString;
            this.import = import;
            this.interval = interval;
            this.inputFiles = (inputFiles ?? []).Select(ResolvePath).ToList();
            inputSize = this.inputFiles.Sum(file => new FileInfo(file).Length);
            loop = RunAsync();
        }

        /// <summary>Takes the output lines of osm2pgsql; returns true for those that only report progress.</summary>
        public bool Intercept(string line) {
            if (string.IsNullOrWhiteSpace(line)) {
                return true;
            }
            if (PendingPattern().Match(line) is { Success: true } pending) {
                lock (sync) {
                    pendingTotal = long.Parse(pending.Groups[1].Value, CultureInfo.InvariantCulture);
                    pendingType = pending.Groups[2].Value;
                }
                return false;
            }
            if (!line.StartsWith("Processing: ", StringComparison.Ordinal) && !line.StartsWith("Left to process: ", StringComparison.Ordinal)) {
                return false;
            }
            lock (sync) {
                status = line.Trim();
                statusTime = stopwatch.Elapsed;
            }
            return true;
        }

        public void Attach(Process process) {
            lock (sync) {
                this.process = process;
            }
        }

        public async ValueTask DisposeAsync() {
            await stop.CancelAsync().ConfigureAwait(false);
            await loop.ConfigureAwait(false);
            stop.Dispose();
        }

        /// <summary>One line describing the progress; the parts that are unknown are left out.</summary>
        internal static string Describe(string? status, long pendingTotal, string pendingType, long? inputRead, long inputSize, TimeSpan? inputRemaining,
            IReadOnlyList<DatabaseTask> tasks) {
            var parts = new List<string>();
            if (inputRead is { } read && inputSize > 0) {
                var fraction = Math.Clamp((double)read / inputSize, 0, 1);
                var input = string.Create(CultureInfo.InvariantCulture, $"input {fraction * 100:F1} % of {Formatting.Size(inputSize)}");
                if (fraction < 1 && inputRemaining is { } remaining) {
                    input += $", about {Formatting.Duration(remaining)} left";
                }
                parts.Add(input);
            }
            if (status != null) {
                if (LeftPattern().Match(status) is { Success: true } left && pendingTotal > 0) {
                    var remaining = long.Parse(left.Groups[1].Value, CultureInfo.InvariantCulture);
                    parts.Add(string.Create(CultureInfo.InvariantCulture,
                        $"pending {pendingType} {(1 - (double)remaining / pendingTotal) * 100:F1} % ({remaining:N0} of {pendingTotal:N0} left)"));
                }
                else {
                    parts.Add(status);
                }
            }
            foreach (var task in tasks.Take(MaxTasks)) {
                var details = new List<string>();
                if (task.Phase is { Length: > 0 } phase) {
                    details.Add(task.Fraction is { } fraction ? string.Create(CultureInfo.InvariantCulture, $"{phase} {fraction * 100:F1} %") : phase);
                }
                if (task.Rows is { } rows) {
                    details.Add(string.Create(CultureInfo.InvariantCulture, $"{rows:N0} rows"));
                }
                details.Add(Formatting.Duration(task.Running));
                parts.Add($"{task.Statement} ({string.Join(", ", details)})");
            }
            if (tasks.Count > MaxTasks) {
                parts.Add($"{tasks.Count - MaxTasks} more statements");
            }
            return parts.Count > 0 ? string.Join(" | ", parts) : "working";
        }

        /// <summary>Bytes of the input files the process has read so far, or null where this is unknown.</summary>
        /// <remarks>Exact on Linux (file positions); on Windows all bytes the process has read, which are mostly the input.</remarks>
        internal static long? InputBytesRead(Process process, IReadOnlyList<string> inputFiles) {
            try {
                if (OperatingSystem.IsLinux()) {
                    long total = 0;
                    var found = false;
                    foreach (var descriptor in Directory.EnumerateFiles($"/proc/{process.Id}/fd")) {
                        if (new FileInfo(descriptor).LinkTarget is not { } target || !inputFiles.Contains(target)) {
                            continue;
                        }
                        foreach (var line in File.ReadLines($"/proc/{process.Id}/fdinfo/{Path.GetFileName(descriptor)}")) {
                            if (line.StartsWith("pos:", StringComparison.Ordinal)) {
                                total += long.Parse(line.AsSpan(4).Trim(), CultureInfo.InvariantCulture);
                                found = true;
                                break;
                            }
                        }
                    }
                    return found ? total : null;
                }
                if (OperatingSystem.IsWindows()) {
                    return GetProcessIoCounters(process.SafeHandle, out var counters) ? (long)counters.ReadTransferCount : null;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException) {
                // The process has exited or its file descriptors cannot be read.
            }
            return null;
        }

        static string ResolvePath(string path) {
            var full = Path.GetFullPath(path);
            return File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName ?? full;
        }

        async Task RunAsync() {
            using var timer = new PeriodicTimer(interval);
            try {
                while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false)) {
                    try {
                        await ReportAsync(stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OperationCanceledException) {
                        logger.LogDebug(e, "Cannot report the import progress");
                    }
                }
            }
            catch (OperationCanceledException) {
                // The step has finished.
            }
        }

        async Task ReportAsync(CancellationToken cancellationToken) {
            string? currentStatus;
            long total;
            string type;
            Process? current;
            var elapsed = stopwatch.Elapsed;
            lock (sync) {
                // A status line that is no longer updated belongs to a phase that has ended.
                currentStatus = elapsed - statusTime <= 2 * interval ? status : null;
                total = pendingTotal;
                type = pendingType;
                current = process;
            }
            var reading = currentStatus?.StartsWith("Processing: ", StringComparison.Ordinal) == true;
            var inputRead = reading && current != null && inputFiles.Count > 0 ? InputBytesRead(current, inputFiles) : null;
            var inputRemaining = inputRead is { } read ? EstimateRemaining(ElementType(currentStatus!), elapsed, read) : null;
            IReadOnlyList<DatabaseTask> tasks = [];
            try {
                tasks = await QueryDatabaseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException) {
                logger.LogDebug("Cannot query the database activity: {Message}", e.Message);
            }
            // While the tool reports its own progress, the COPY streams feeding the tables are no news.
            var shown = tasks.Where(task => task.Running >= MinTaskDuration && !(currentStatus != null && task.Statement.StartsWith("COPY ", StringComparison.OrdinalIgnoreCase))).ToList();
            logger.LogInformation("Import {Total}, {Step} {Elapsed}: {Details}", Formatting.Duration(import.Elapsed), step, Formatting.Duration(elapsed),
                Describe(currentStatus, total, type, inputRead, inputSize, inputRemaining, shown));
        }

        /// <summary>
        /// Extrapolates the reading rate since osm2pgsql started on the current element type: nodes are processed much
        /// faster per byte than ways and relations, so no estimate is given while reading nodes.
        /// </summary>
        TimeSpan? EstimateRemaining(int elementType, TimeSpan elapsed, long read) {
            if (elementType != phase.ElementType) {
                phase = (elementType, elapsed, read);
                return null;
            }
            if (elementType == 0 || elapsed <= phase.Elapsed || read <= phase.Read || read >= inputSize) {
                return null;
            }
            var bytesPerSecond = (read - phase.Read) / (elapsed - phase.Elapsed).TotalSeconds;
            return TimeSpan.FromSeconds(Math.Min((inputSize - read) / bytesPerSecond, TimeSpan.FromDays(365).TotalSeconds));
        }

        /// <summary>0 while osm2pgsql reads nodes, 1 for ways and 2 for relations.</summary>
        internal static int ElementType(string status) {
            var counts = CountsPattern().Match(status);
            if (!counts.Success) {
                return 0;
            }
            return counts.Groups[2].Value != "0" ? 2 : counts.Groups[1].Value != "0" ? 1 : 0;
        }

        async Task<IReadOnlyList<DatabaseTask>> QueryDatabaseAsync(CancellationToken cancellationToken) {
            const string sql = """
                SELECT extract(epoch FROM clock_timestamp() - a.query_start)::float8,
                       left(regexp_replace(a.query, '\s+', ' ', 'g'), 200),
                       i.phase, i.blocks_done, i.blocks_total, i.tuples_done, i.tuples_total, c.tuples_processed
                FROM pg_stat_activity a
                LEFT JOIN pg_stat_progress_create_index i ON i.pid = a.pid
                LEFT JOIN pg_stat_progress_copy c ON c.pid = a.pid
                WHERE a.datname = current_database() AND a.state = 'active' AND a.backend_type = 'client backend'
                    AND a.pid <> pg_backend_pid() AND a.query_start IS NOT NULL
                ORDER BY a.query_start
                """;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 30 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var tasks = new List<DatabaseTask>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
                double? fraction = null;
                if (!reader.IsDBNull(2)) {
                    var (blocksDone, blocksTotal, tuplesDone, tuplesTotal) = (reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6));
                    fraction = blocksTotal > 0 ? (double)blocksDone / blocksTotal : tuplesTotal > 0 ? (double)tuplesDone / tuplesTotal : null;
                }
                tasks.Add(new DatabaseTask(
                    TimeSpan.FromSeconds(Math.Max(0, reader.GetDouble(0))),
                    Shorten(reader.GetString(1).Trim()),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    fraction,
                    reader.IsDBNull(7) ? null : reader.GetInt64(7)));
            }
            return tasks;
        }

        static string Shorten(string statement) {
            const int maxLength = 80;
            return statement.Length <= maxLength ? statement : statement[..(maxLength - 3)] + "...";
        }

        // osm2pgsql log lines start with a timestamp.
        [GeneratedRegex(@"Going over (\d+) pending (\w+)")]
        private static partial Regex PendingPattern();

        [GeneratedRegex(@"^Left to process: (\d+)")]
        private static partial Regex LeftPattern();

        [GeneratedRegex(@"Way\((\d+)k .*Relation\((\d+) ")]
        private static partial Regex CountsPattern();

        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetProcessIoCounters(SafeHandle process, out IoCounters counters);
    }
}
