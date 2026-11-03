using System.Buffers.Binary;
using System.Text;

namespace YukiOsmRenderer.Core.Vector {
    /// <summary>Minimal protocol buffers reader over a byte span, enough for the OSM PBF format.</summary>
    internal ref struct ProtoReader(ReadOnlySpan<byte> data) {
        readonly ReadOnlySpan<byte> data = data;
        int position;

        public readonly bool End => position >= data.Length;

        /// <summary>Reads the next field key: field number and wire type.</summary>
        public (int Field, int WireType) ReadKey() {
            var key = ReadVarint();
            return ((int)(key >> 3), (int)(key & 7));
        }

        public ulong ReadVarint() {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7) {
                if (position >= data.Length) {
                    throw new InvalidDataException("Truncated protocol buffer.");
                }
                var b = data[position++];
                value |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80) {
                    return value;
                }
            }
            throw new InvalidDataException("Malformed varint.");
        }

        public long ReadSignedVarint() {
            return Decode(ReadVarint());
        }

        public ReadOnlySpan<byte> ReadBytes() {
            var length = checked((int)ReadVarint());
            if (length < 0 || position + length > data.Length) {
                throw new InvalidDataException("Truncated protocol buffer.");
            }
            var bytes = data.Slice(position, length);
            position += length;
            return bytes;
        }

        public string ReadString() {
            return Encoding.UTF8.GetString(ReadBytes());
        }

        public void Skip(int wireType) {
            switch (wireType) {
                case 0:
                    ReadVarint();
                    break;
                case 1:
                    position += 8;
                    break;
                case 2:
                    ReadBytes();
                    break;
                case 5:
                    position += 4;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported wire type {wireType}.");
            }
            if (position > data.Length) {
                throw new InvalidDataException("Truncated protocol buffer.");
            }
        }

        /// <summary>Reads a repeated varint field that may be packed (wire type 2) or not (one value per key).</summary>
        public void ReadRepeated(int wireType, List<ulong> values) {
            if (wireType == 2) {
                var packed = new ProtoReader(ReadBytes());
                while (!packed.End) {
                    values.Add(packed.ReadVarint());
                }
            }
            else {
                values.Add(ReadVarint());
            }
        }

        public static long Decode(ulong zigzag) {
            return (long)(zigzag >> 1) ^ -(long)(zigzag & 1);
        }
    }

    /// <summary>Minimal protocol buffers writer.</summary>
    internal sealed class ProtoWriter {
        byte[] buffer = new byte[256];

        public int Length { get; private set; }

        public ReadOnlySpan<byte> Written => buffer.AsSpan(0, Length);

        public void Clear() {
            Length = 0;
        }

        public void WriteVarint(ulong value) {
            Ensure(10);
            while (value >= 0x80) {
                buffer[Length++] = (byte)(value | 0x80);
                value >>= 7;
            }
            buffer[Length++] = (byte)value;
        }

        public void WriteKey(int field, int wireType) {
            WriteVarint((ulong)((field << 3) | wireType));
        }

        public void WriteVarintField(int field, ulong value) {
            WriteKey(field, 0);
            WriteVarint(value);
        }

        public void WriteSignedVarintField(int field, long value) {
            WriteKey(field, 0);
            WriteVarint(Encode(value));
        }

        public void WriteBytesField(int field, ReadOnlySpan<byte> bytes) {
            WriteKey(field, 2);
            WriteVarint((ulong)bytes.Length);
            Ensure(bytes.Length);
            bytes.CopyTo(buffer.AsSpan(Length));
            Length += bytes.Length;
        }

        public void WriteStringField(int field, string value) {
            WriteBytesField(field, Encoding.UTF8.GetBytes(value));
        }

        /// <summary>Writes a packed repeated varint field; nothing for an empty list.</summary>
        public void WritePacked(int field, ReadOnlySpan<ulong> values) {
            if (values.IsEmpty) {
                return;
            }
            var size = 0;
            foreach (var value in values) {
                size += VarintSize(value);
            }
            WriteKey(field, 2);
            WriteVarint((ulong)size);
            foreach (var value in values) {
                WriteVarint(value);
            }
        }

        public void WriteBigEndianInt32(int value) {
            Ensure(4);
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(Length), value);
            Length += 4;
        }

        public static ulong Encode(long value) {
            return (ulong)((value << 1) ^ (value >> 63));
        }

        static int VarintSize(ulong value) {
            var size = 1;
            while (value >= 0x80) {
                value >>= 7;
                size++;
            }
            return size;
        }

        void Ensure(int extra) {
            if (Length + extra > buffer.Length) {
                Array.Resize(ref buffer, Math.Max(buffer.Length * 2, Length + extra));
            }
        }
    }
}
