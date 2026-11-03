using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;

namespace YukiOsmRenderer.Core.Vector {
    public enum PmTilesCompression : byte {
        Unknown = 0,
        None = 1,
        Gzip = 2,
        Brotli = 3,
        Zstd = 4,
    }

    public enum PmTilesTileType : byte {
        Unknown = 0,
        Mvt = 1,
        Png = 2,
        Jpeg = 3,
        Webp = 4,
        Avif = 5,
    }

    /// <summary>Header of a PMTiles version 3 archive.</summary>
    public sealed record PmTilesHeader {
        public const int Size = 127;

        public ulong RootDirectoryOffset { get; init; }
        public ulong RootDirectoryLength { get; init; }
        public ulong MetadataOffset { get; init; }
        public ulong MetadataLength { get; init; }
        public ulong LeafDirectoriesOffset { get; init; }
        public ulong TileDataOffset { get; init; }
        public PmTilesCompression InternalCompression { get; init; }
        public PmTilesCompression TileCompression { get; init; }
        public PmTilesTileType TileType { get; init; }
        public int MinZoom { get; init; }
        public int MaxZoom { get; init; }
        public double MinLongitude { get; init; }
        public double MinLatitude { get; init; }
        public double MaxLongitude { get; init; }
        public double MaxLatitude { get; init; }
        public int CenterZoom { get; init; }
        public double CenterLongitude { get; init; }
        public double CenterLatitude { get; init; }

        /// <exception cref="InvalidDataException">The data is not a PMTiles version 3 header.</exception>
        public static PmTilesHeader Parse(ReadOnlySpan<byte> data) {
            if (data.Length < Size || !data[..7].SequenceEqual("PMTiles"u8)) {
                throw new InvalidDataException("Not a PMTiles archive.");
            }
            if (data[7] != 3) {
                throw new InvalidDataException($"Unsupported PMTiles version {data[7]}; version 3 is required.");
            }
            static double Degrees(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 10_000_000.0;
            return new PmTilesHeader {
                RootDirectoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
                RootDirectoryLength = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]),
                MetadataOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[24..]),
                MetadataLength = BinaryPrimitives.ReadUInt64LittleEndian(data[32..]),
                LeafDirectoriesOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[40..]),
                TileDataOffset = BinaryPrimitives.ReadUInt64LittleEndian(data[56..]),
                InternalCompression = (PmTilesCompression)data[97],
                TileCompression = (PmTilesCompression)data[98],
                TileType = (PmTilesTileType)data[99],
                MinZoom = data[100],
                MaxZoom = data[101],
                MinLongitude = Degrees(data[102..]),
                MinLatitude = Degrees(data[106..]),
                MaxLongitude = Degrees(data[110..]),
                MaxLatitude = Degrees(data[114..]),
                CenterZoom = data[118],
                CenterLongitude = Degrees(data[119..]),
                CenterLatitude = Degrees(data[123..]),
            };
        }
    }

    /// <summary>
    /// Reads tiles from a PMTiles version 3 archive with positional reads, so one instance serves concurrent
    /// requests. Directories are cached in memory.
    /// </summary>
    public sealed class PmTilesArchive : IDisposable {
        const int MaxDirectoryDepth = 3;
        const int MaxCachedLeafDirectories = 4096;

        readonly SafeFileHandle file;
        readonly Entry[] root;
        readonly ConcurrentDictionary<(ulong Offset, ulong Length), Entry[]> leaves = new();

        PmTilesArchive(string path, SafeFileHandle file, PmTilesHeader header, Entry[] root) {
            Path = path;
            this.file = file;
            Header = header;
            this.root = root;
        }

        public string Path { get; }

        public PmTilesHeader Header { get; }

        /// <exception cref="InvalidDataException">The file is not a supported PMTiles archive.</exception>
        public static PmTilesArchive Open(string path) {
            var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
            try {
                Span<byte> headerBytes = stackalloc byte[PmTilesHeader.Size];
                if (RandomAccess.Read(file, headerBytes, 0) != PmTilesHeader.Size) {
                    throw new InvalidDataException("Not a PMTiles archive.");
                }
                var header = PmTilesHeader.Parse(headerBytes);
                if (header.InternalCompression is not (PmTilesCompression.None or PmTilesCompression.Gzip or PmTilesCompression.Brotli)) {
                    throw new InvalidDataException($"Unsupported PMTiles directory compression {header.InternalCompression}.");
                }
                var root = ParseDirectory(Decompress(Read(file, header.RootDirectoryOffset, header.RootDirectoryLength), header.InternalCompression));
                return new PmTilesArchive(System.IO.Path.GetFullPath(path), file, header, root);
            }
            catch {
                file.Dispose();
                throw;
            }
        }

        /// <summary>The metadata JSON document of the archive.</summary>
        public string ReadMetadata() {
            var data = Decompress(Read(file, Header.MetadataOffset, Header.MetadataLength), Header.InternalCompression);
            return System.Text.Encoding.UTF8.GetString(data);
        }

        /// <summary>The stored bytes of a tile, compressed as <see cref="PmTilesHeader.TileCompression"/>, or null if the archive has no such tile.</summary>
        public byte[]? GetTile(int zoom, long x, long y) {
            if (zoom < 0 || zoom > 31 || x < 0 || y < 0 || x >= 1L << zoom || y >= 1L << zoom) {
                return null;
            }
            var tileId = TileId(zoom, (uint)x, (uint)y);
            var directory = root;
            for (var depth = 0; depth <= MaxDirectoryDepth; depth++) {
                if (!TryFind(directory, tileId, out var entry)) {
                    return null;
                }
                if (entry.RunLength > 0) {
                    return Read(file, Header.TileDataOffset + entry.Offset, entry.Length);
                }
                directory = LeafDirectory(Header.LeafDirectoriesOffset + entry.Offset, entry.Length);
            }
            return null;
        }

        /// <summary>Tile id: the number of tiles of lower zoom levels plus the position on the Hilbert curve.</summary>
        public static ulong TileId(int zoom, uint x, uint y) {
            var id = ((1UL << (zoom * 2)) - 1) / 3;
            for (var s = zoom == 0 ? 0u : 1u << (zoom - 1); s > 0; s >>= 1) {
                var rx = (x & s) != 0 ? 1u : 0u;
                var ry = (y & s) != 0 ? 1u : 0u;
                id += (ulong)s * s * ((3 * rx) ^ ry);
                if (ry == 0) {
                    if (rx == 1) {
                        x = s - 1 - (x & (s - 1));
                        y = s - 1 - (y & (s - 1));
                    }
                    (x, y) = (y, x);
                }
            }
            return id;
        }

        public void Dispose() {
            file.Dispose();
        }

        Entry[] LeafDirectory(ulong offset, ulong length) {
            if (leaves.TryGetValue((offset, length), out var cached)) {
                return cached;
            }
            var directory = ParseDirectory(Decompress(Read(file, offset, length), Header.InternalCompression));
            if (leaves.Count >= MaxCachedLeafDirectories) {
                leaves.Clear();
            }
            leaves[(offset, length)] = directory;
            return directory;
        }

        static bool TryFind(Entry[] entries, ulong tileId, out Entry entry) {
            int low = 0, high = entries.Length - 1;
            while (low <= high) {
                var middle = (low + high) >>> 1;
                var current = entries[middle].TileId;
                if (tileId > current) {
                    low = middle + 1;
                }
                else if (tileId < current) {
                    high = middle - 1;
                }
                else {
                    entry = entries[middle];
                    return true;
                }
            }
            // Run-length entries cover tile ids after their own; leaf directory pointers cover everything up to the next entry.
            if (high >= 0 && (entries[high].RunLength == 0 || tileId - entries[high].TileId < entries[high].RunLength)) {
                entry = entries[high];
                return true;
            }
            entry = default;
            return false;
        }

        static byte[] Read(SafeFileHandle file, ulong offset, ulong length) {
            if (length > int.MaxValue) {
                throw new InvalidDataException("A PMTiles section is too large.");
            }
            var buffer = new byte[(int)length];
            var read = 0;
            while (read < buffer.Length) {
                var count = RandomAccess.Read(file, buffer.AsSpan(read), (long)offset + read);
                if (count == 0) {
                    throw new InvalidDataException("The PMTiles archive is truncated.");
                }
                read += count;
            }
            return buffer;
        }

        /// <summary>Decompresses directory or tile data.</summary>
        public static byte[] Decompress(byte[] data, PmTilesCompression compression) {
            Stream? decompressor = compression switch {
                PmTilesCompression.None or PmTilesCompression.Unknown => null,
                PmTilesCompression.Gzip => new GZipStream(new MemoryStream(data), CompressionMode.Decompress),
                PmTilesCompression.Brotli => new BrotliStream(new MemoryStream(data), CompressionMode.Decompress),
                _ => throw new InvalidDataException($"Unsupported PMTiles compression {compression}."),
            };
            if (decompressor == null) {
                return data;
            }
            using (decompressor) {
                using var output = new MemoryStream(data.Length * 4);
                decompressor.CopyTo(output);
                return output.ToArray();
            }
        }

        static Entry[] ParseDirectory(byte[] data) {
            var position = 0;
            var count = ReadVarint(data, ref position);
            if (count > (ulong)data.Length) {
                throw new InvalidDataException("Malformed PMTiles directory.");
            }
            var entries = new Entry[(int)count];
            ulong tileId = 0;
            for (var i = 0; i < entries.Length; i++) {
                tileId += ReadVarint(data, ref position);
                entries[i].TileId = tileId;
            }
            for (var i = 0; i < entries.Length; i++) {
                entries[i].RunLength = checked((uint)ReadVarint(data, ref position));
            }
            for (var i = 0; i < entries.Length; i++) {
                entries[i].Length = checked((uint)ReadVarint(data, ref position));
            }
            for (var i = 0; i < entries.Length; i++) {
                var value = ReadVarint(data, ref position);
                entries[i].Offset = i > 0 && value == 0 ? entries[i - 1].Offset + entries[i - 1].Length : value - 1;
            }
            return entries;
        }

        static ulong ReadVarint(byte[] data, ref int position) {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7) {
                if (position >= data.Length) {
                    throw new InvalidDataException("Malformed PMTiles directory.");
                }
                var b = data[position++];
                value |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80) {
                    return value;
                }
            }
            throw new InvalidDataException("Malformed PMTiles directory.");
        }

        struct Entry {
            public ulong TileId;
            public ulong Offset;
            public uint Length;
            public uint RunLength;
        }
    }
}
