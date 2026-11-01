using YukiOsmRenderer.Core.Tiff;

namespace YukiOsmRenderer.Tests.Tiff {
    public class TiledImageAssemblerTests {
        static byte[] Block(long originX, long originY, int width, int height) {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++) {
                for (var x = 0; x < width; x++) {
                    var i = (y * width + x) * 4;
                    pixels[i] = (byte)(originX + x);
                    pixels[i + 1] = (byte)(originY + y);
                    pixels[i + 2] = (byte)((originX + x) * (originY + y));
                    pixels[i + 3] = (byte)(originX + x + originY + y);
                }
            }
            return pixels;
        }

        [TestCase(4)]
        [TestCase(3)]
        public void AssemblesUnalignedBlocksIntoTiles(int channels) {
            const int width = 300;
            const int height = 200;
            const int tileSize = 64;
            const int blockSize = 128;
            const int offsetX = -37;
            const int offsetY = -11;
            var tiles = new Dictionary<(long, long), byte[]>();
            var assembler = new TiledImageAssembler(width, height, tileSize, channels, (column, row, pixels) => {
                lock (tiles) {
                    Assert.That(tiles.TryAdd((column, row), pixels), Is.True, "A tile must be completed only once.");
                }
            });
            var blocks = new List<(long X, long Y)>();
            for (long y = offsetY; y < height; y += blockSize) {
                for (long x = offsetX; x < width; x += blockSize) {
                    blocks.Add((x, y));
                }
            }
            Parallel.ForEach(blocks.OrderBy(b => (b.X * 31 + b.Y * 17) % 7), block => {
                // Pixel values are a function of the image coordinates, so blocks can be checked after assembly.
                assembler.Add(block.X, block.Y, blockSize, blockSize, Block(block.X, block.Y, blockSize, blockSize));
            });

            Assert.Multiple(() => {
                Assert.That(tiles, Has.Count.EqualTo(assembler.TotalTiles));
                Assert.That(assembler.CompletedTiles, Is.EqualTo(assembler.TotalTiles));
                Assert.That(assembler.PendingTiles, Is.Zero);
            });
            foreach (var ((column, row), pixels) in tiles) {
                Assert.That(pixels, Has.Length.EqualTo(tileSize * tileSize * channels));
                for (var y = 0; y < tileSize; y++) {
                    for (var x = 0; x < tileSize; x++) {
                        var gx = column * tileSize + x;
                        var gy = row * tileSize + y;
                        var i = (y * tileSize + x) * channels;
                        if (gx >= width || gy >= height) {
                            Assert.That(pixels[i], Is.Zero, "Padding must stay empty.");
                            continue;
                        }
                        Assert.That(pixels[i], Is.EqualTo((byte)gx));
                        Assert.That(pixels[i + 1], Is.EqualTo((byte)gy));
                        Assert.That(pixels[i + 2], Is.EqualTo((byte)(gx * gy)));
                        if (channels == 4) {
                            Assert.That(pixels[i + 3], Is.EqualTo((byte)(gx + gy)));
                        }
                    }
                }
            }
        }

        [Test]
        public void IgnoresBlocksOutsideTheImage() {
            var completed = 0;
            var assembler = new TiledImageAssembler(10, 10, 16, 4, (_, _, _) => completed++);
            assembler.Add(20, 20, 4, 4, new byte[64]);
            assembler.Add(-10, 0, 5, 5, new byte[100]);
            Assert.Multiple(() => {
                Assert.That(completed, Is.Zero);
                Assert.That(assembler.PendingTiles, Is.Zero);
            });
        }

        [Test]
        public void RejectsOverlappingBlocks() {
            var assembler = new TiledImageAssembler(16, 16, 16, 4, (_, _, _) => { });
            assembler.Add(0, 0, 16, 10, new byte[16 * 10 * 4]);
            Assert.That(() => assembler.Add(0, 5, 16, 11, new byte[16 * 11 * 4]), Throws.InvalidOperationException);
        }

        [Test]
        public void RejectsTooSmallBuffers() {
            var assembler = new TiledImageAssembler(16, 16, 16, 4, (_, _, _) => { });
            Assert.That(() => assembler.Add(0, 0, 16, 16, new byte[10]), Throws.ArgumentException);
        }
    }
}
