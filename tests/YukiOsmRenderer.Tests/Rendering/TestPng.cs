using System.Buffers.Binary;
using System.IO.Compression;

namespace YukiOsmRenderer.Tests.Rendering {
    /// <summary>Writes indexed-color PNG images for tests. Chunk CRCs are left zero.</summary>
    static class TestPng {
        /// <param name="indices">Palette index of every pixel, row-major.</param>
        /// <param name="palette">RGBA of every palette entry.</param>
        /// <param name="filter">Filter type of each row (0 to 4).</param>
        public static byte[] EncodeIndexed(int width, int height, byte[] indices, byte[] palette, int bitDepth, Func<int, int>? filter = null) {
            var rowBytes = (width * bitDepth + 7) / 8;
            var previous = new byte[rowBytes];
            using var raw = new MemoryStream();
            for (var y = 0; y < height; y++) {
                var row = new byte[rowBytes];
                for (var x = 0; x < width; x++) {
                    var bit = x * bitDepth;
                    row[bit / 8] |= (byte)(indices[y * width + x] << (8 - bitDepth - bit % 8));
                }
                var type = filter?.Invoke(y) ?? 0;
                raw.WriteByte((byte)type);
                for (var i = 0; i < rowBytes; i++) {
                    var left = i > 0 ? row[i - 1] : 0;
                    var upLeft = i > 0 ? previous[i - 1] : 0;
                    var predictor = type switch {
                        0 => 0,
                        1 => left,
                        2 => previous[i],
                        3 => (left + previous[i]) / 2,
                        _ => Paeth(left, previous[i], upLeft),
                    };
                    raw.WriteByte((byte)(row[i] - predictor));
                }
                previous = row;
            }
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) {
                raw.Position = 0;
                raw.CopyTo(zlib);
            }
            var data = compressed.ToArray();

            using var png = new MemoryStream();
            png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = (byte)bitDepth;
            header[9] = 3;
            WriteChunk(png, "IHDR", header);
            var count = palette.Length / 4;
            WriteChunk(png, "PLTE", Enumerable.Range(0, count).SelectMany(i => palette.Skip(i * 4).Take(3)).ToArray());
            WriteChunk(png, "tRNS", Enumerable.Range(0, count).Select(i => palette[i * 4 + 3]).ToArray());
            // Two IDAT chunks, as decoders must join them.
            WriteChunk(png, "IDAT", data[..(data.Length / 2)]);
            WriteChunk(png, "IDAT", data[(data.Length / 2)..]);
            WriteChunk(png, "IEND", []);
            return png.ToArray();
        }

        static int Paeth(int left, int up, int upLeft) {
            var estimate = left + up - upLeft;
            var toLeft = Math.Abs(estimate - left);
            var toUp = Math.Abs(estimate - up);
            var toUpLeft = Math.Abs(estimate - upLeft);
            return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
        }

        static void WriteChunk(Stream png, string type, byte[] data) {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            png.Write(length);
            png.Write(System.Text.Encoding.ASCII.GetBytes(type));
            png.Write(data);
            png.Write(new byte[4]);
        }
    }
}
