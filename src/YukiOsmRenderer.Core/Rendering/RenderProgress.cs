using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace YukiOsmRenderer.Core.Rendering {
    /// <summary>State of a render job. The estimates extrapolate from the work done so far.</summary>
    /// <param name="Metatiles">Rendered metatiles, the unit of work.</param>
    /// <param name="Tiles">Output tiles written: PNG files, or tiles of the GeoTIFF.</param>
    /// <param name="Bytes">Encoded size of the tiles written.</param>
    public readonly record struct RenderProgress(long Metatiles, long TotalMetatiles, long Tiles, long TotalTiles, long Bytes, TimeSpan Elapsed) {
        public double Fraction => TotalMetatiles == 0 ? 1 : (double)Metatiles / TotalMetatiles;

        public bool IsComplete => Metatiles == TotalMetatiles;

        /// <summary>Estimated size of the whole output; null before the first tile is written.</summary>
        public long? EstimatedTotalBytes => Tiles == 0 ? null : (long)Math.Round((double)Bytes / Tiles * TotalTiles);

        /// <summary>Estimated duration of the whole job; null before the first metatile is rendered.</summary>
        public TimeSpan? EstimatedTotalTime {
            get {
                if (Metatiles == 0) {
                    return null;
                }
                var ticks = Elapsed.Ticks / Fraction;
                return ticks < TimeSpan.MaxValue.Ticks ? TimeSpan.FromTicks((long)ticks) : TimeSpan.MaxValue;
            }
        }

        /// <summary>For example "37.5 % | tiles 1,234/3,290 | size 45.2 MB/~120.6 MB | time 1:23/~3:41, 2:18 left | 14.9 tiles/s".</summary>
        public override string ToString() {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"{Fraction * 100:F1} % | tiles {Tiles:N0}/{TotalTiles:N0} | size {Formatting.Size(Bytes)}");
            if (!IsComplete && EstimatedTotalBytes is { } totalBytes) {
                text.Append(CultureInfo.InvariantCulture, $"/~{Formatting.Size(totalBytes)}");
            }
            text.Append(CultureInfo.InvariantCulture, $" | time {Formatting.Duration(Elapsed)}");
            if (!IsComplete && EstimatedTotalTime is { } totalTime) {
                text.Append(CultureInfo.InvariantCulture, $"/~{Formatting.Duration(totalTime)}, {Formatting.Duration(totalTime - Elapsed)} left");
            }
            if (Elapsed > TimeSpan.Zero) {
                text.Append(CultureInfo.InvariantCulture, $" | {Tiles / Elapsed.TotalSeconds:F1} tiles/s");
            }
            return text.ToString();
        }
    }

    /// <summary>Counts the work of parallel render workers and logs the progress at regular intervals.</summary>
    sealed class ProgressTracker(long totalMetatiles, long totalTiles, ILogger logger) {
        static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

        readonly Stopwatch stopwatch = Stopwatch.StartNew();
        readonly Lock sync = new();
        long metatiles;
        long tiles;
        long bytes;
        long reportedMetatiles;
        TimeSpan lastReport;

        public RenderProgress Current => new(Interlocked.Read(ref metatiles), totalMetatiles, Interlocked.Read(ref tiles), totalTiles, Interlocked.Read(ref bytes), stopwatch.Elapsed);

        public void AddTile(long size) {
            Interlocked.Add(ref bytes, size);
            Interlocked.Increment(ref tiles);
        }

        /// <summary>Call after the tiles of the metatile have been added.</summary>
        public void CompleteMetatile() {
            Interlocked.Increment(ref metatiles);
            lock (sync) {
                var progress = Current;
                if (progress.Metatiles > reportedMetatiles && (progress.Elapsed - lastReport >= Interval || progress.IsComplete)) {
                    reportedMetatiles = progress.Metatiles;
                    lastReport = progress.Elapsed;
                    logger.LogInformation("Progress: {Progress}", progress);
                }
            }
        }
    }
}
