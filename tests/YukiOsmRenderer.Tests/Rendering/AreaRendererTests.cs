using Microsoft.Extensions.Logging.Abstractions;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Tiff;
using LibTiff = BitMiracle.LibTiff.Classic.Tiff;

namespace YukiOsmRenderer.Tests.Rendering {
    public class AreaRendererTests {
        string directory = null!;

        [SetUp]
        public void SetUp() {
            directory = Path.Combine(Path.GetTempPath(), "yukiosm-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown() {
            Directory.Delete(directory, true);
        }

        [TestCase(false, 256)]
        [TestCase(true, 256)]
        [TestCase(false, 512)]
        public async Task RendersWindowIntoGeoTiff(bool transparent, int tileSize) {
            // A window that starts and ends in the middle of metatiles.
            var window = new PixelWindow(12, tileSize, 3370L * tileSize + 77, 1550L * tileSize + 201, tileSize * 9 + 13, tileSize * 4 + 99);
            var renderer = new FakeRenderer();
            var options = AreaRenderer.CreateTiffOptions(window, transparent, directory);
            var path = Path.Combine(directory, "out.tif");
            await using (var file = File.Create(path))
            using (var writer = new GeoTiffWriter(file, options)) {
                await AreaRenderer.RenderAsync(renderer, window, writer, 4, NullLogger.Instance);
            }

            Assert.That(renderer.Renders, Is.EqualTo(window.CoveringMetatileCount()));
            using var tiff = LibTiff.Open(path, "r");
            Assert.That(tiff, Is.Not.Null);
            var channels = transparent ? 4 : 3;
            Assert.Multiple(() => {
                Assert.That(tiff.GetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGEWIDTH)[0].ToLong(), Is.EqualTo(window.Width));
                Assert.That(tiff.GetField(BitMiracle.LibTiff.Classic.TiffTag.IMAGELENGTH)[0].ToLong(), Is.EqualTo(window.Height));
                Assert.That(tiff.GetField(BitMiracle.LibTiff.Classic.TiffTag.SAMPLESPERPIXEL)[0].ToInt(), Is.EqualTo(channels));
            });
            var buffer = new byte[tiff.TileSize()];
            for (var row = 0; row < options.TileRows; row++) {
                for (var column = 0; column < options.TileColumns; column++) {
                    tiff.ReadTile(buffer, 0, column * 256, row * 256, 0, 0);
                    // Spot-check a few pixels per tile.
                    foreach (var (x, y) in new[] { (0, 0), (255, 0), (17, 200), (255, 255) }) {
                        var imageX = column * 256L + x;
                        var imageY = row * 256L + y;
                        if (imageX >= window.Width || imageY >= window.Height) {
                            continue;
                        }
                        var expected = FakeRenderer.Pixel(window.X + imageX, window.Y + imageY);
                        var i = (y * 256 + x) * channels;
                        Assert.That(buffer[i], Is.EqualTo(expected.R), $"R at {imageX},{imageY}");
                        Assert.That(buffer[i + 1], Is.EqualTo(expected.G), $"G at {imageX},{imageY}");
                        Assert.That(buffer[i + 2], Is.EqualTo(expected.B), $"B at {imageX},{imageY}");
                        if (transparent) {
                            Assert.That(buffer[i + 3], Is.EqualTo(expected.A), $"A at {imageX},{imageY}");
                        }
                    }
                }
            }
        }

        [Test]
        public void TiffOptionsCarryTheGeoReference() {
            var window = new PixelWindow(1, 256, 256, 0, 256, 256);
            var options = AreaRenderer.CreateTiffOptions(window, false, null);
            Assert.Multiple(() => {
                Assert.That(options.Channels, Is.EqualTo(3));
                Assert.That(options.GeoReference!.OriginX, Is.EqualTo(0).Within(1e-6));
                Assert.That(options.GeoReference.OriginY, Is.EqualTo(WebMercator.OriginShift).Within(1e-6));
                Assert.That(options.GeoReference.PixelSizeX, Is.EqualTo(WebMercator.OriginShift / 256).Within(1e-9));
            });
        }

        [Test]
        public void DefaultBufferSizeFollowsRenderd() {
            Assert.Multiple(() => {
                Assert.That(TileSizes.DefaultBufferSize(256), Is.EqualTo(128));
                Assert.That(TileSizes.DefaultBufferSize(512), Is.EqualTo(512));
                Assert.That(TileSizes.ScaleFactor(512), Is.EqualTo(2.0));
            });
        }
    }
}
