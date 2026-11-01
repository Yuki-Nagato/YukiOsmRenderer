using BitMiracle.LibTiff.Classic;
using YukiOsmRenderer.Core.Tiff;
using LibTiff = BitMiracle.LibTiff.Classic.Tiff;

namespace YukiOsmRenderer.Tests.Tiff {
    public class GeoTiffWriterTests {
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

        static byte Sample(long x, long y, int channel) {
            return channel switch {
                0 => (byte)(x * 7),
                1 => (byte)(y * 3),
                2 => (byte)(x + y),
                _ => (byte)(255 - (x ^ y)),
            };
        }

        static byte[] TilePixels(GeoTiffOptions options, long column, long row) {
            var size = options.TileSize;
            var pixels = new byte[options.TileByteCount];
            for (var y = 0; y < size; y++) {
                for (var x = 0; x < size; x++) {
                    var gx = column * size + x;
                    var gy = row * size + y;
                    if (gx >= options.Width || gy >= options.Height) {
                        continue;
                    }
                    for (var c = 0; c < options.Channels; c++) {
                        pixels[(y * size + x) * options.Channels + c] = Sample(gx, gy, c);
                    }
                }
            }
            return pixels;
        }

        static void WriteAllTiles(GeoTiffWriter writer, bool reverse = false) {
            var options = writer.Options;
            var tiles = new List<(long, long)>();
            for (long row = 0; row < options.TileRows; row++) {
                for (long column = 0; column < options.TileColumns; column++) {
                    tiles.Add((column, row));
                }
            }
            if (reverse) {
                tiles.Reverse();
            }
            foreach (var (column, row) in tiles) {
                writer.WriteTile(column, row, TilePixels(options, column, row));
            }
        }

        void AssertPixels(string path, GeoTiffOptions options) {
            using var tiff = LibTiff.Open(path, "r");
            Assert.That(tiff, Is.Not.Null);
            Assert.Multiple(() => {
                Assert.That(tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt(), Is.EqualTo(options.Width));
                Assert.That(tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt(), Is.EqualTo(options.Height));
                Assert.That(tiff.GetField(TiffTag.TILEWIDTH)[0].ToInt(), Is.EqualTo(options.TileSize));
                Assert.That(tiff.GetField(TiffTag.SAMPLESPERPIXEL)[0].ToInt(), Is.EqualTo(options.Channels));
                Assert.That(tiff.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt(), Is.EqualTo(8));
            });
            var buffer = new byte[tiff.TileSize()];
            for (var row = 0; row < options.TileRows; row++) {
                for (var column = 0; column < options.TileColumns; column++) {
                    var read = tiff.ReadTile(buffer, 0, column * options.TileSize, row * options.TileSize, 0, 0);
                    Assert.That(read, Is.EqualTo(options.TileByteCount));
                    var expected = TilePixels(options, column, row);
                    Assert.That(buffer.AsSpan(0, options.TileByteCount).SequenceEqual(expected), Is.True, $"Tile {column},{row} differs.");
                }
            }
        }

        [Test]
        public void WritesDeflateCompressedGeoTiff() {
            var path = Path.Combine(directory, "deflate.tif");
            var options = new GeoTiffOptions {
                Width = 600,
                Height = 300,
                Channels = 4,
                TileSize = 256,
                GeoReference = new GeoReference(12934000.5, 4865000.25, 38.21851414258813, 38.21851414258813),
            };
            using (var stream = File.Create(path))
            using (var writer = new GeoTiffWriter(stream, options)) {
                Assert.That(writer.IsBigTiff, Is.False);
                WriteAllTiles(writer, reverse: true);
                writer.Finish();
            }
            AssertPixels(path, options);

            var directoryReader = new TiffDirectoryReader(File.ReadAllBytes(path));
            Assert.Multiple(() => {
                Assert.That(directoryReader.IsBigTiff, Is.False);
                Assert.That(directoryReader.Tags, Is.Ordered, "Tags must be sorted.");
                Assert.That(directoryReader.Integers(259), Is.EqualTo(new long[] { 8 }), "Compression");
                Assert.That(directoryReader.Integers(317), Is.EqualTo(new long[] { 2 }), "Predictor");
                Assert.That(directoryReader.Integers(338), Is.EqualTo(new long[] { 2 }), "ExtraSamples");
                Assert.That(directoryReader.Doubles(33550), Is.EqualTo(new[] { 38.21851414258813, 38.21851414258813, 0 }));
                Assert.That(directoryReader.Doubles(33922), Is.EqualTo(new[] { 0, 0, 0, 12934000.5, 4865000.25, 0 }));
                var keys = directoryReader.Integers(34735);
                Assert.That(keys[..4], Is.EqualTo(new long[] { 1, 1, 0, 5 }));
                Assert.That(keys.Chunk(4).Skip(1).Select(k => k[0]), Is.Ordered, "GeoKeys must be sorted.");
                Assert.That(keys.Chunk(4).Single(k => k[0] == 3072)[3], Is.EqualTo(3857));
                Assert.That(directoryReader.Ascii(34737), Is.EqualTo("WGS 84 / Pseudo-Mercator|"));
                Assert.That(directoryReader.Ascii(305), Is.EqualTo("YukiOsmRenderer"));
            });
        }

        [Test]
        public void WritesUncompressedRgb() {
            var path = Path.Combine(directory, "rgb.tif");
            var options = new GeoTiffOptions {
                Width = 100,
                Height = 70,
                Channels = 3,
                TileSize = 64,
                Compression = TiffCompression.None,
            };
            using (var stream = File.Create(path))
            using (var writer = new GeoTiffWriter(stream, options)) {
                WriteAllTiles(writer);
                writer.Finish();
            }
            AssertPixels(path, options);
            var directoryReader = new TiffDirectoryReader(File.ReadAllBytes(path));
            Assert.Multiple(() => {
                Assert.That(directoryReader.Integers(259), Is.EqualTo(new long[] { 1 }));
                Assert.That(directoryReader.Has(317), Is.False);
                Assert.That(directoryReader.Has(338), Is.False);
                Assert.That(directoryReader.Has(33550), Is.False);
                Assert.That(directoryReader.Integers(325), Is.All.EqualTo(64 * 64 * 3));
            });
        }

        [Test]
        public void WritesBigTiff() {
            var path = Path.Combine(directory, "big.tif");
            var options = new GeoTiffOptions {
                Width = 300,
                Height = 200,
                TileSize = 128,
                BigTiff = BigTiffMode.Always,
                GeoReference = new GeoReference(0, 0, 1, 1),
            };
            using (var stream = File.Create(path))
            using (var writer = new GeoTiffWriter(stream, options)) {
                Assert.That(writer.IsBigTiff, Is.True);
                WriteAllTiles(writer);
                writer.Finish();
            }
            AssertPixels(path, options);
            Assert.That(new TiffDirectoryReader(File.ReadAllBytes(path)).IsBigTiff, Is.True);
        }

        [Test]
        public void ChoosesBigTiffForImagesThatMayExceedFourGigabytes() {
            var options = new GeoTiffOptions { Width = 40000, Height = 40000 };
            using var stream = new MemoryStream();
            using var writer = new GeoTiffWriter(stream, options);
            Assert.That(writer.IsBigTiff, Is.True);
        }

        [Test]
        public void NonSeekableOutputProducesTheSameFile() {
            var options = new GeoTiffOptions {
                Width = 300,
                Height = 280,
                TileSize = 128,
                TemporaryDirectory = directory,
                GeoReference = new GeoReference(1, 2, 3, 4),
            };
            var seekable = new MemoryStream();
            using (var writer = new GeoTiffWriter(seekable, options)) {
                WriteAllTiles(writer);
                writer.Finish();
            }
            var forwardOnly = new ForwardOnlyStream();
            using (var writer = new GeoTiffWriter(forwardOnly, options)) {
                WriteAllTiles(writer);
                writer.Finish();
            }
            Assert.Multiple(() => {
                Assert.That(forwardOnly.Data.ToArray(), Is.EqualTo(seekable.ToArray()));
                Assert.That(Directory.GetFiles(directory), Is.Empty, "The temporary file must be deleted.");
            });
        }

        [Test]
        public void RejectsMissingAndDuplicateTiles() {
            var options = new GeoTiffOptions { Width = 20, Height = 20, TileSize = 16 };
            using var writer = new GeoTiffWriter(new MemoryStream(), options);
            writer.WriteTile(0, 0, TilePixels(options, 0, 0));
            Assert.Multiple(() => {
                Assert.That(() => writer.WriteTile(0, 0, TilePixels(options, 0, 0)), Throws.InvalidOperationException);
                Assert.That(() => writer.WriteTile(2, 0, TilePixels(options, 0, 0)), Throws.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(() => writer.WriteTile(1, 0, new byte[10]), Throws.ArgumentException);
                Assert.That(writer.Finish, Throws.InvalidOperationException);
            });
        }

        [Test]
        public void HorizontalPredictorDifferencesNeighbouringPixels() {
            byte[] tile = [10, 20, 30, 15, 25, 40, 15, 5, 40, 1, 2, 3, 2, 4, 6, 3, 6, 9];
            GeoTiffWriter.ApplyHorizontalPredictor(tile, 3, 2);
            Assert.That(tile, Is.EqualTo(new byte[] { 10, 20, 20, 251, 251, 25, 15, 5, 25, 252, 218, 2, 2, 4, 4, 255, 0, 6 }));
        }

        sealed class ForwardOnlyStream : Stream {
            public MemoryStream Data { get; } = new();

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => throw new NotSupportedException();

            public override long Position {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() {
            }

            public override int Read(byte[] buffer, int offset, int count) {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin) {
                throw new NotSupportedException();
            }

            public override void SetLength(long value) {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count) {
                Data.Write(buffer, offset, count);
            }
        }
    }
}
