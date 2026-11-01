using System.Buffers.Binary;

namespace YukiOsmRenderer.Tests.Tiff {
    /// <summary>Minimal reader of the first TIFF/BigTIFF directory, used to verify tags independently of the writer.</summary>
    sealed class TiffDirectoryReader {
        readonly Dictionary<ushort, (ushort Type, long Count, byte[] Value)> entries = [];

        public TiffDirectoryReader(byte[] file) {
            if (file[0] != 'I' || file[1] != 'I') {
                throw new InvalidDataException("Only little-endian TIFF files are supported.");
            }
            var version = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2));
            IsBigTiff = version == 43;
            if (!IsBigTiff && version != 42) {
                throw new InvalidDataException("Not a TIFF file.");
            }
            var offset = IsBigTiff ? (long)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(8)) : BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
            var count = IsBigTiff ? (long)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan((int)offset)) : BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan((int)offset));
            var position = offset + (IsBigTiff ? 8 : 2);
            var entrySize = IsBigTiff ? 20 : 12;
            var inlineSize = IsBigTiff ? 8 : 4;
            for (var i = 0; i < count; i++) {
                var entry = file.AsSpan((int)(position + i * entrySize), entrySize);
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(entry);
                var type = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
                var valueCount = IsBigTiff ? (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
                var size = (int)(valueCount * TypeSize(type));
                var valueField = entry[(IsBigTiff ? 12 : 8)..];
                byte[] value;
                if (size <= inlineSize) {
                    value = valueField[..size].ToArray();
                }
                else {
                    var valueOffset = IsBigTiff ? (long)BinaryPrimitives.ReadUInt64LittleEndian(valueField) : BinaryPrimitives.ReadUInt32LittleEndian(valueField);
                    value = file.AsSpan((int)valueOffset, size).ToArray();
                }
                Tags.Add(tag);
                entries[tag] = (type, valueCount, value);
            }
        }

        public bool IsBigTiff { get; }

        public List<ushort> Tags { get; } = [];

        public bool Has(ushort tag) {
            return entries.ContainsKey(tag);
        }

        public long[] Integers(ushort tag) {
            var (type, count, value) = entries[tag];
            var size = TypeSize(type);
            var result = new long[count];
            for (var i = 0; i < count; i++) {
                var span = value.AsSpan(i * size);
                result[i] = type switch {
                    3 => BinaryPrimitives.ReadUInt16LittleEndian(span),
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(span),
                    16 => (long)BinaryPrimitives.ReadUInt64LittleEndian(span),
                    _ => throw new InvalidDataException($"Tag {tag} is not an integer tag."),
                };
            }
            return result;
        }

        public double[] Doubles(ushort tag) {
            var (_, count, value) = entries[tag];
            var result = new double[count];
            for (var i = 0; i < count; i++) {
                result[i] = BinaryPrimitives.ReadDoubleLittleEndian(value.AsSpan(i * 8));
            }
            return result;
        }

        public string Ascii(ushort tag) {
            return System.Text.Encoding.ASCII.GetString(entries[tag].Value).TrimEnd('\0');
        }

        static int TypeSize(ushort type) {
            return type switch {
                1 or 2 or 6 or 7 => 1,
                3 or 8 => 2,
                4 or 9 or 11 => 4,
                5 or 10 or 12 or 16 or 17 or 18 => 8,
                _ => throw new InvalidDataException($"Unknown TIFF type {type}."),
            };
        }
    }
}
