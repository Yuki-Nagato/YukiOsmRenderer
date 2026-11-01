using YukiOsmRenderer.Core.Geo;

namespace YukiOsmRenderer.Core.Rendering {
    /// <summary>Renders one variant of the map style. Implementations are thread-safe.</summary>
    public interface IMapRenderer {
        /// <summary>
        /// Renders a metatile into <paramref name="rgba"/> as straight RGBA pixels. The image is
        /// <c>metatile.PixelSize(tileSize)</c> pixels wide and high.
        /// </summary>
        /// <param name="tileSize">256 for standard tiles or 512 for high resolution (@2x) tiles.</param>
        void RenderMetatile(Metatile metatile, int tileSize, Span<byte> rgba);

        /// <summary>Encodes a region of a rendered image as a tile (PNG).</summary>
        byte[] EncodeTile(ReadOnlySpan<byte> rgba, int width, int height, int stride);
    }

    public static class TileSizes {
        public const int Standard = 256;

        public const int HighResolution = 512;

        /// <summary>Mapnik scale factor for a tile size (1 for 256 px, 2 for 512 px).</summary>
        public static double ScaleFactor(int tileSize) {
            return tileSize / (double)Standard;
        }

        /// <summary>Buffer size renderd uses when the style does not define one.</summary>
        public static int DefaultBufferSize(int tileSize) {
            return (int)((tileSize >> 1) * ScaleFactor(tileSize));
        }
    }
}
