using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Tests.Rendering {
    /// <summary>
    /// Renderer that paints every pixel with a value derived from its global pixel coordinates, so tests can check
    /// that rendered pixels end up in the right place.
    /// </summary>
    sealed class FakeRenderer : IMapRenderer, IDisposable {
        int renders;

        public int Renders => Volatile.Read(ref renders);

        public bool Disposed { get; private set; }

        public StyleOptions? Options { get; init; }

        public static (byte R, byte G, byte B, byte A) Pixel(long globalX, long globalY) {
            return ((byte)(globalX % 251), (byte)(globalY % 241), (byte)((globalX + globalY) % 239), (byte)(128 + globalX % 2));
        }

        public void RenderMetatile(Metatile metatile, int tileSize, Span<byte> rgba) {
            Interlocked.Increment(ref renders);
            var size = metatile.PixelSize(tileSize);
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    var (r, g, b, a) = Pixel(metatile.X * tileSize + x, metatile.Y * tileSize + y);
                    var i = (y * size + x) * 4;
                    rgba[i] = r;
                    rgba[i + 1] = g;
                    rgba[i + 2] = b;
                    rgba[i + 3] = a;
                }
            }
        }

        /// <summary>Returns the first pixel, the size and the stride instead of a real PNG.</summary>
        public byte[] EncodeTile(ReadOnlySpan<byte> rgba, int width, int height, int stride) {
            var result = new byte[16];
            rgba[..4].CopyTo(result);
            BitConverter.TryWriteBytes(result.AsSpan(4), width);
            BitConverter.TryWriteBytes(result.AsSpan(8), height);
            BitConverter.TryWriteBytes(result.AsSpan(12), stride);
            return result;
        }

        public void Dispose() {
            Disposed = true;
        }
    }

    sealed class FakeRendererProvider(params string[] layers) : IRendererProvider {
        readonly Dictionary<string, FakeRenderer> renderers = [];

        public IReadOnlySet<string> LayerIds { get; } = layers.ToHashSet();

        public IReadOnlyDictionary<string, FakeRenderer> Renderers => renderers;

        public IRendererLease AcquireRenderer(StyleOptions options) {
            options.Validate(LayerIds);
            lock (renderers) {
                if (!renderers.TryGetValue(options.Key, out var renderer)) {
                    renderers[options.Key] = renderer = new FakeRenderer { Options = options };
                }
                return new Lease(renderer);
            }
        }

        sealed class Lease(IMapRenderer renderer) : IRendererLease {
            public IMapRenderer Renderer => renderer;

            public void Dispose() {
            }
        }
    }
}
