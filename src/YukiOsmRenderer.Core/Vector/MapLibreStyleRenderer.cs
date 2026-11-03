using System.Buffers;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Rendering;

namespace YukiOsmRenderer.Core.Vector {
    /// <summary>
    /// Renders one vector style variant with maps from a pool shared by all variants. Metatiles are rendered with a
    /// buffer around them, like Mapnik, so that labels near their edges are placed consistently with the neighbouring
    /// metatiles.
    /// </summary>
    /// <param name="renderSlots">Limits concurrent renders of all variants; also bounds the maps in use.</param>
    /// <param name="bufferScale">Factor for the buffer, so that enlarged labels are not cut at metatile edges.</param>
    public sealed class MapLibreStyleRenderer(string styleJson, MapPool<MapLibreMap> maps, SemaphoreSlim renderSlots, double bufferScale = 1) : IMapRenderer, IDisposable {
        // Logical pixels, as renderd uses for 256 pixel tiles.
        const int BufferSize = 128;

        volatile bool disposed;

        public void RenderMetatile(Metatile metatile, int tileSize, Span<byte> rgba) {
            ObjectDisposedException.ThrowIf(disposed, this);
            var size = metatile.PixelSize(tileSize);
            var extent = metatile.Extent;
            var longitude = WebMercator.XToLongitude((extent.MinX + extent.MaxX) / 2);
            var latitude = WebMercator.YToLatitude((extent.MinY + extent.MaxY) / 2);
            // MapLibre's zoom levels refer to 512 pixel tiles. Zoom 0 cannot go lower, so it is drawn at half the pixel ratio.
            double zoom;
            int logicalSize, buffer;
            float ratio;
            if (metatile.Zoom == 0) {
                zoom = 0;
                logicalSize = 512;
                buffer = 0;
                ratio = tileSize / 512f;
            }
            else {
                zoom = metatile.Zoom - 1;
                logicalSize = metatile.Size * TileSizes.Standard;
                buffer = (int)Math.Ceiling(BufferSize * bufferScale);
                ratio = tileSize / (float)TileSizes.Standard;
            }
            var fullSize = MapLibreMap.PixelSize(logicalSize + 2 * buffer, ratio);
            var offset = MapLibreMap.PixelSize(buffer, ratio);

            var image = ArrayPool<byte>.Shared.Rent(fullSize * fullSize * 4);
            renderSlots.Wait();
            try {
                var map = maps.Rent(this, ratio, () => MapLibreMap.Create(styleJson, ratio));
                var keep = false;
                try {
                    map.Render(longitude, latitude, zoom, logicalSize + 2 * buffer, logicalSize + 2 * buffer, image.AsSpan(0, fullSize * fullSize * 4));
                    keep = !disposed;
                }
                finally {
                    maps.Return(this, ratio, map, keep);
                }
            }
            finally {
                renderSlots.Release();
            }
            try {
                for (var row = 0; row < size; row++) {
                    image.AsSpan(((row + offset) * fullSize + offset) * 4, size * 4).CopyTo(rgba.Slice(row * size * 4, size * 4));
                }
            }
            finally {
                ArrayPool<byte>.Shared.Return(image);
            }
        }

        public byte[] EncodeTile(ReadOnlySpan<byte> rgba, int width, int height, int stride, TileFormat format) {
            return Mapnik.Encode(rgba, width, height, stride, format.MapnikFormat());
        }

        public void Dispose() {
            disposed = true;
            maps.Release(this);
        }
    }
}
