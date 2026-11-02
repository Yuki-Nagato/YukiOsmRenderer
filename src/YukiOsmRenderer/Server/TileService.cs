using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Server {
    internal sealed record TileServiceOptions {
        /// <summary>Maximum number of metatiles rendered at the same time.</summary>
        public int Concurrency { get; init; } = Environment.ProcessorCount;

        /// <summary>Memory for cached tiles in bytes; 0 disables the cache.</summary>
        public long CacheBytes { get; init; } = 512L * 1024 * 1024;
    }

    /// <summary>
    /// Produces PNG tiles. Like renderd, a request renders the whole metatile containing the tile; all of its tiles are
    /// cached and concurrent requests for the same metatile share one render.
    /// </summary>
    internal sealed class TileService : IDisposable {
        readonly IRendererProvider provider;
        readonly ILogger logger;
        readonly MemoryCache? cache;
        readonly SemaphoreSlim limiter;
        readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyDictionary<(long X, long Y), byte[]>>>> inflight = new();
        long renderedMetatiles;

        public TileService(IRendererProvider provider, TileServiceOptions options, ILogger<TileService> logger) {
            this.provider = provider;
            this.logger = logger;
            limiter = new SemaphoreSlim(Math.Max(1, options.Concurrency));
            if (options.CacheBytes > 0) {
                cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = options.CacheBytes, CompactionPercentage = 0.25 });
            }
        }

        /// <summary>Number of metatiles rendered so far.</summary>
        public long RenderedMetatiles => Interlocked.Read(ref renderedMetatiles);

        public async Task<byte[]> GetTileAsync(TileAddress tile, StyleOptions style, TileFormat format, CancellationToken cancellationToken) {
            var variant = $"{style.Key}|{format.Name()}|{tile.TileSize}";
            var key = $"{variant}|{tile.Zoom}/{tile.X}/{tile.Y}";
            if (cache != null && cache.TryGetValue(key, out byte[]? cached) && cached != null) {
                return cached;
            }
            var metatile = Metatile.Containing(tile.Zoom, tile.X, tile.Y);
            var metatileKey = $"{variant}|{metatile.Zoom}/{metatile.X}/{metatile.Y}";
            var render = inflight.GetOrAdd(metatileKey, _ => new Lazy<Task<IReadOnlyDictionary<(long, long), byte[]>>>(
                () => RenderMetatileAsync(metatile, tile.TileSize, style, format, variant, metatileKey)));
            // The render continues for the benefit of the cache even if this request is cancelled.
            var tiles = await render.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            return tiles[(tile.X, tile.Y)];
        }

        public void Dispose() {
            cache?.Dispose();
            limiter.Dispose();
        }

        async Task<IReadOnlyDictionary<(long, long), byte[]>> RenderMetatileAsync(Metatile metatile, int tileSize, StyleOptions style, TileFormat format, string variant,
            string metatileKey) {
            try {
                await limiter.WaitAsync().ConfigureAwait(false);
                try {
                    return await Task.Run(() => Render(metatile, tileSize, style, format, variant)).ConfigureAwait(false);
                }
                finally {
                    limiter.Release();
                }
            }
            finally {
                inflight.TryRemove(metatileKey, out _);
            }
        }

        Dictionary<(long, long), byte[]> Render(Metatile metatile, int tileSize, StyleOptions style, TileFormat format, string variant) {
            var stopwatch = Stopwatch.StartNew();
            var size = metatile.PixelSize(tileSize);
            var length = size * size * 4;
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try {
                using var lease = provider.AcquireRenderer(style);
                lease.Renderer.RenderMetatile(metatile, tileSize, buffer.AsSpan(0, length));
                var rendered = stopwatch.Elapsed;

                // Rendering a metatile is single-threaded in Mapnik, but its tiles can be encoded in parallel.
                var count = metatile.Size * metatile.Size;
                var encoded = new byte[count][];
                Parallel.For(0, count, index => {
                    var offset = ((index / metatile.Size) * tileSize * size + (index % metatile.Size) * tileSize) * 4;
                    encoded[index] = lease.Renderer.EncodeTile(buffer.AsSpan(offset, length - offset), tileSize, tileSize, size * 4, format);
                });

                var tiles = new Dictionary<(long, long), byte[]>(count);
                for (var index = 0; index < count; index++) {
                    var x = metatile.X + index % metatile.Size;
                    var y = metatile.Y + index / metatile.Size;
                    tiles[(x, y)] = encoded[index];
                    cache?.Set($"{variant}|{metatile.Zoom}/{x}/{y}", encoded[index], new MemoryCacheEntryOptions { Size = encoded[index].Length });
                }
                Interlocked.Increment(ref renderedMetatiles);
                logger.LogDebug("Rendered metatile {Zoom}/{X}/{Y} ({Variant}): render {Render} ms, encode {Encode} ms",
                    metatile.Zoom, metatile.X, metatile.Y, variant, (long)rendered.TotalMilliseconds, (long)(stopwatch.Elapsed - rendered).TotalMilliseconds);
                return tiles;
            }
            finally {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
