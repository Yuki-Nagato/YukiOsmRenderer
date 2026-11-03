using System.Collections.Concurrent;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Tests.Rendering {
    /// <summary>
    /// Renderer that paints every pixel with a value derived from its global pixel coordinates, so tests can check
    /// that rendered pixels end up in the right place.
    /// </summary>
    sealed class FakeRenderer : IMapRenderer, IDisposable {
        readonly ConcurrentDictionary<TileFormat, bool> formats = new();
        int renders;

        public int Renders => Volatile.Read(ref renders);

        public bool Disposed { get; private set; }

        public StyleOptions? Options { get; init; }

        /// <summary>Formats passed to <see cref="EncodeTile"/>.</summary>
        public ICollection<TileFormat> Formats => formats.Keys;

        /// <summary>Encode tiles as real indexed-color PNG images with the colors of <see cref="Quantize"/>.</summary>
        public bool EncodesPng { get; init; }

        public static (byte R, byte G, byte B, byte A) Pixel(long globalX, long globalY) {
            return ((byte)(globalX % 251), (byte)(globalY % 241), (byte)((globalX + globalY) % 239), (byte)(128 + globalX % 2));
        }

        /// <summary>Reduces a color to one of 256 opaque colors (3 bits red and green, 2 bits blue).</summary>
        public static (byte R, byte G, byte B, byte A) Quantize(byte r, byte g, byte b) {
            return ((byte)(r & 0xE0), (byte)(g & 0xE0), (byte)(b & 0xC0), 255);
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

        /// <summary>Returns the first pixel, the size and the stride instead of a real PNG, unless <see cref="EncodesPng"/> is set.</summary>
        public byte[] EncodeTile(ReadOnlySpan<byte> rgba, int width, int height, int stride, TileFormat format) {
            formats.TryAdd(format, true);
            if (EncodesPng) {
                var indices = new byte[width * height];
                for (var y = 0; y < height; y++) {
                    for (var x = 0; x < width; x++) {
                        var pixel = rgba.Slice(y * stride + x * 4, 4);
                        indices[y * width + x] = (byte)((pixel[0] >> 5 << 5) | (pixel[1] >> 5 << 2) | (pixel[2] >> 6));
                    }
                }
                var palette = new byte[256 * 4];
                for (var i = 0; i < 256; i++) {
                    (palette[i * 4], palette[i * 4 + 1], palette[i * 4 + 2], palette[i * 4 + 3]) = ((byte)(i >> 5 << 5), (byte)((i >> 2 & 7) << 5), (byte)((i & 3) << 6), (byte)255);
                }
                return TestPng.EncodeIndexed(width, height, indices, palette, 8, row => row % 5);
            }
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

        public IReadOnlyCollection<string> LayerIds { get; } = layers.ToHashSet();

        public IReadOnlyDictionary<string, FakeRenderer> Renderers => renderers;

        public void Validate(StyleOptions options) {
            options.Validate(LayerIds);
        }

        public IRendererLease AcquireRenderer(StyleOptions options) {
            Validate(options);
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
