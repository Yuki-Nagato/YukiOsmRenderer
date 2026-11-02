using System.Collections.Concurrent;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Core.Rendering {
    /// <summary>
    /// Renders a Mapnik XML style with a pool of map instances. The number of concurrent renders across all
    /// renderers sharing <paramref name="renderSlots"/> is limited by that semaphore.
    /// </summary>
    public sealed class MapnikStyleRenderer(string xml, string basePath, SemaphoreSlim renderSlots) : IMapRenderer, IDisposable {
        readonly int? styleBufferSize = MapnikStyleTransformer.BufferSize(xml);
        readonly ConcurrentBag<MapnikMap> idleMaps = [];
        volatile bool disposed;

        public void RenderMetatile(Metatile metatile, int tileSize, Span<byte> rgba) {
            ObjectDisposedException.ThrowIf(disposed, this);
            var size = metatile.PixelSize(tileSize);
            renderSlots.Wait();
            try {
                var map = idleMaps.TryTake(out var idle) ? idle : MapnikMap.Load(xml, basePath);
                try {
                    // Like renderd: the style's buffer size wins, otherwise half a tile scaled by the scale factor.
                    var bufferSize = styleBufferSize.HasValue && styleBufferSize.Value > 0 ? -1 : TileSizes.DefaultBufferSize(tileSize);
                    map.Render(metatile.Extent, size, size, TileSizes.ScaleFactor(tileSize), bufferSize, rgba);
                }
                finally {
                    if (disposed) {
                        map.Dispose();
                    }
                    else {
                        idleMaps.Add(map);
                    }
                }
            }
            finally {
                renderSlots.Release();
            }
        }

        public byte[] EncodeTile(ReadOnlySpan<byte> rgba, int width, int height, int stride, TileFormat format) {
            return Mapnik.Encode(rgba, width, height, stride, format.MapnikFormat());
        }

        public void Dispose() {
            disposed = true;
            while (idleMaps.TryTake(out var map)) {
                map.Dispose();
            }
        }
    }
}
