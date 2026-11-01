using System.Buffers;
using System.Collections.Concurrent;
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

        public async Task<byte[]> GetTileAsync(TileAddress tile, StyleOptions style, CancellationToken cancellationToken) {
            var key = $"{style.Key}|{tile.TileSize}|{tile.Zoom}/{tile.X}/{tile.Y}";
            if (cache != null && cache.TryGetValue(key, out byte[]? cached) && cached != null) {
                return cached;
            }
            var metatile = Metatile.Containing(tile.Zoom, tile.X, tile.Y);
            var metatileKey = $"{style.Key}|{tile.TileSize}|{metatile.Zoom}/{metatile.X}/{metatile.Y}";
            var render = inflight.GetOrAdd(metatileKey, _ => new Lazy<Task<IReadOnlyDictionary<(long, long), byte[]>>>(
                () => RenderMetatileAsync(metatile, tile.TileSize, style, metatileKey)));
            // The render continues for the benefit of the cache even if this request is cancelled.
            var tiles = await render.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            return tiles[(tile.X, tile.Y)];
        }

        public void Dispose() {
            cache?.Dispose();
            limiter.Dispose();
        }

        async Task<IReadOnlyDictionary<(long, long), byte[]>> RenderMetatileAsync(Metatile metatile, int tileSize, StyleOptions style, string metatileKey) {
            try {
                await limiter.WaitAsync().ConfigureAwait(false);
                try {
                    return await Task.Run(() => Render(metatile, tileSize, style)).ConfigureAwait(false);
                }
                finally {
                    limiter.Release();
                }
            }
            finally {
                inflight.TryRemove(metatileKey, out _);
            }
        }

        Dictionary<(long, long), byte[]> Render(Metatile metatile, int tileSize, StyleOptions style) {
            var started = DateTime.UtcNow;
            var size = metatile.PixelSize(tileSize);
            var length = size * size * 4;
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try {
                using var lease = provider.AcquireRenderer(style);
                var pixels = buffer.AsSpan(0, length);
                lease.Renderer.RenderMetatile(metatile, tileSize, pixels);
                var tiles = new Dictionary<(long, long), byte[]>(metatile.Size * metatile.Size);
                for (var row = 0; row < metatile.Size; row++) {
                    for (var column = 0; column < metatile.Size; column++) {
                        var offset = (row * tileSize * size + column * tileSize) * 4;
                        var png = lease.Renderer.EncodeTile(pixels[offset..], tileSize, tileSize, size * 4);
                        var x = metatile.X + column;
                        var y = metatile.Y + row;
                        tiles[(x, y)] = png;
                        cache?.Set($"{style.Key}|{tileSize}|{metatile.Zoom}/{x}/{y}", png, new MemoryCacheEntryOptions { Size = png.Length });
                    }
                }
                Interlocked.Increment(ref renderedMetatiles);
                logger.LogDebug("Rendered metatile {Zoom}/{X}/{Y} ({Style}, {TileSize} px) in {Duration} ms",
                    metatile.Zoom, metatile.X, metatile.Y, style.Key, tileSize, (DateTime.UtcNow - started).TotalMilliseconds);
                return tiles;
            }
            finally {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
