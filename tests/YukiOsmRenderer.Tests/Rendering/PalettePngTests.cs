using YukiOsmRenderer.Core.Rendering;

namespace YukiOsmRenderer.Tests.Rendering {
    public class PalettePngTests {
        [TestCase(1, 13, 5)]
        [TestCase(2, 13, 7)]
        [TestCase(4, 31, 9)]
        [TestCase(8, 256, 11)]
        public void DecodesAllBitDepthsAndFilters(int bitDepth, int width, int height) {
            var random = new Random(bitDepth);
            var colors = Math.Min(1 << bitDepth, 200);
            var palette = new byte[colors * 4];
            random.NextBytes(palette);
            var indices = Enumerable.Range(0, width * height).Select(_ => (byte)random.Next(colors)).ToArray();
            var png = TestPng.EncodeIndexed(width, height, indices, palette, bitDepth, row => row % 5);

            // A stride larger than a row, as for tiles within a metatile.
            var stride = width * 4 + 8;
            var rgba = new byte[stride * height];
            PalettePng.Decode(png, rgba, width, height, stride);
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    var index = indices[y * width + x];
                    Assert.That(rgba.AsSpan(y * stride + x * 4, 4).ToArray(), Is.EqualTo(palette.AsSpan(index * 4, 4).ToArray()), $"Pixel {x},{y}");
                }
                Assert.That(rgba.AsSpan(y * stride + width * 4, 8).ToArray(), Is.All.Zero, $"Padding of row {y}");
            }
        }

        [Test]
        public void RejectsOtherImages() {
            var palette = new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 };
            var png = TestPng.EncodeIndexed(4, 4, new byte[16], palette, 2);
            var rgba = new byte[4 * 4 * 4];
            var fullColor = png.ToArray();
            fullColor[25] = 6;
            var outsidePalette = TestPng.EncodeIndexed(4, 4, Enumerable.Repeat((byte)3, 16).ToArray(), palette, 2);
            Assert.Multiple(() => {
                Assert.That(() => PalettePng.Decode(png, rgba, 4, 4, 16), Throws.Nothing);
                Assert.That(() => PalettePng.Decode(png.AsSpan(1).ToArray(), rgba, 4, 4, 16), Throws.TypeOf<InvalidDataException>());
                Assert.That(() => PalettePng.Decode(png, rgba, 2, 8, 8), Throws.TypeOf<InvalidDataException>());
                Assert.That(() => PalettePng.Decode(fullColor, rgba, 4, 4, 16), Throws.TypeOf<InvalidDataException>());
                Assert.That(() => PalettePng.Decode(png[..40], rgba, 4, 4, 16), Throws.TypeOf<InvalidDataException>());
                Assert.That(() => PalettePng.Decode(outsidePalette, rgba, 4, 4, 16), Throws.TypeOf<InvalidDataException>());
            });
        }

        [Test]
        public void ParsesTileFormats() {
            Assert.Multiple(() => {
                Assert.That(TileFormats.TryParse("PNG32", out var png32), Is.True);
                Assert.That(png32, Is.EqualTo(TileFormat.Png32));
                Assert.That(TileFormats.Parse("png256"), Is.EqualTo(TileFormat.Png256));
                Assert.That(TileFormats.TryParse("jpeg", out _), Is.False);
                Assert.That(TileFormat.Png256.MapnikFormat(), Is.EqualTo("png256:z=5"));
                Assert.That(TileFormat.Png32.Name(), Is.EqualTo("png32"));
            });
        }
    }
}
