using System.Buffers.Binary;
using System.IO.Compression;

namespace YukiOsmRenderer.Core.Rendering {
    /// <summary>Decodes the indexed-color PNG images that Mapnik writes for png256 (bit depth 1, 2, 4 or 8, not interlaced).</summary>
    static class PalettePng {
        static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        /// <summary>Decodes an image of the given size into straight RGBA pixels.</summary>
        /// <exception cref="InvalidDataException">The data is not an indexed-color PNG image of that size.</exception>
        public static void Decode(ReadOnlySpan<byte> png, Span<byte> rgba, int width, int height, int stride) {
            if (!png.StartsWith(Signature)) {
                throw new InvalidDataException("Not a PNG image.");
            }
            var palette = new byte[256 * 4];
            var paletteSize = 0;
            var bitDepth = 0;
            using var compressed = new MemoryStream();
            var position = Signature.Length;
            while (true) {
                if (position + 12 > png.Length) {
                    throw new InvalidDataException("The PNG image is truncated.");
                }
                var length = BinaryPrimitives.ReadInt32BigEndian(png[position..]);
                if (length < 0 || length > png.Length - position - 12) {
                    throw new InvalidDataException("The PNG image is truncated.");
                }
                var type = png.Slice(position + 4, 4);
                var data = png.Slice(position + 8, length);
                position += 12 + length;
                if (type.SequenceEqual("IHDR"u8)) {
                    if (length != 13 || BinaryPrimitives.ReadInt32BigEndian(data) != width || BinaryPrimitives.ReadInt32BigEndian(data[4..]) != height) {
                        throw new InvalidDataException($"Expected a PNG image of {width} x {height} pixels.");
                    }
                    bitDepth = data[8];
                    if (data[9] != 3 || bitDepth is not (1 or 2 or 4 or 8) || data[12] != 0) {
                        throw new InvalidDataException("Only non-interlaced indexed-color PNG images are supported.");
                    }
                }
                else if (type.SequenceEqual("PLTE"u8)) {
                    paletteSize = Math.Min(length / 3, 256);
                    for (var i = 0; i < paletteSize; i++) {
                        data.Slice(i * 3, 3).CopyTo(palette.AsSpan(i * 4));
                        palette[i * 4 + 3] = 255;
                    }
                }
                else if (type.SequenceEqual("tRNS"u8)) {
                    for (var i = 0; i < Math.Min(length, paletteSize); i++) {
                        palette[i * 4 + 3] = data[i];
                    }
                }
                else if (type.SequenceEqual("IDAT"u8)) {
                    compressed.Write(data);
                }
                else if (type.SequenceEqual("IEND"u8)) {
                    break;
                }
            }
            if (bitDepth == 0 || paletteSize == 0) {
                throw new InvalidDataException("The PNG image has no header or palette.");
            }

            var rowBytes = (width * bitDepth + 7) / 8;
            var current = new byte[rowBytes];
            var previous = new byte[rowBytes];
            var mask = (1 << bitDepth) - 1;
            compressed.Position = 0;
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            for (var y = 0; y < height; y++) {
                var filter = zlib.ReadByte();
                zlib.ReadExactly(current);
                Unfilter(filter, current, previous);
                var row = rgba.Slice(y * stride, width * 4);
                for (var x = 0; x < width; x++) {
                    var bit = x * bitDepth;
                    var index = (current[bit / 8] >> (8 - bitDepth - bit % 8)) & mask;
                    if (index >= paletteSize) {
                        throw new InvalidDataException("A pixel refers to a color outside of the palette.");
                    }
                    palette.AsSpan(index * 4, 4).CopyTo(row[(x * 4)..]);
                }
                (current, previous) = (previous, current);
            }
        }

        // Pixels of indexed-color images are filtered byte by byte.
        static void Unfilter(int filter, Span<byte> row, ReadOnlySpan<byte> previous) {
            switch (filter) {
                case 0:
                    break;
                case 1:
                    for (var i = 1; i < row.Length; i++) {
                        row[i] += row[i - 1];
                    }
                    break;
                case 2:
                    for (var i = 0; i < row.Length; i++) {
                        row[i] += previous[i];
                    }
                    break;
                case 3:
                    for (var i = 0; i < row.Length; i++) {
                        row[i] += (byte)(((i > 0 ? row[i - 1] : 0) + previous[i]) / 2);
                    }
                    break;
                case 4:
                    for (var i = 0; i < row.Length; i++) {
                        row[i] += i > 0 ? Paeth(row[i - 1], previous[i], previous[i - 1]) : previous[i];
                    }
                    break;
                default:
                    throw new InvalidDataException($"Unknown PNG filter type {filter}.");
            }
        }

        static byte Paeth(byte left, byte up, byte upLeft) {
            var estimate = left + up - upLeft;
            var toLeft = Math.Abs(estimate - left);
            var toUp = Math.Abs(estimate - up);
            var toUpLeft = Math.Abs(estimate - upLeft);
            return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
        }
    }
}
