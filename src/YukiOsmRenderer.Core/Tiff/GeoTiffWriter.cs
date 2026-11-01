using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace YukiOsmRenderer.Core.Tiff {
    /// <summary>
    /// Writes a tiled, optionally Deflate-compressed (Geo)TIFF or BigTIFF. Tiles can be written in any order
    /// and from multiple threads; the image directory is written by <see cref="Finish"/>.
    /// When the output stream cannot seek (for example stdout), the file is assembled in a temporary file first.
    /// </summary>
    public sealed class GeoTiffWriter : IDisposable {
        const ushort TypeAscii = 2;
        const ushort TypeShort = 3;
        const ushort TypeLong = 4;
        const ushort TypeDouble = 12;
        const ushort TypeLong8 = 16;

        readonly Stream output;
        readonly Stream target;
        readonly FileStream? temporaryFile;
        readonly GeoTiffOptions options;
        readonly long[] tileOffsets;
        readonly long[] tileByteCounts;
        readonly Lock sync = new();
        long position;
        long writtenTiles;
        bool finished;

        public GeoTiffWriter(Stream output, GeoTiffOptions options) {
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(options);
            options.Validate();
            if (options.TileCount > Array.MaxLength) {
                throw new ArgumentException("The image has too many tiles.");
            }
            this.output = output;
            this.options = options;
            IsBigTiff = options.BigTiff switch {
                BigTiffMode.Always => true,
                BigTiffMode.Never => false,
                _ => options.EstimateUncompressedSize() > uint.MaxValue,
            };
            tileOffsets = new long[options.TileCount];
            tileByteCounts = new long[options.TileCount];
            if (output.CanSeek && output.Position == 0) {
                target = output;
            }
            else {
                var directory = options.TemporaryDirectory ?? Path.GetTempPath();
                Directory.CreateDirectory(directory);
                temporaryFile = new FileStream(
                    Path.Combine(directory, $"yukiosm-{Guid.NewGuid():N}.tif.tmp"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.DeleteOnClose);
                target = temporaryFile;
            }
            WriteHeader();
        }

        public bool IsBigTiff { get; }

        public GeoTiffOptions Options => options;

        /// <summary>Applies the predictor and compression to an uncompressed tile without writing it.</summary>
        /// <param name="pixels">Tile pixels, <see cref="GeoTiffOptions.TileByteCount"/> bytes, interleaved, row-major.</param>
        public byte[] EncodeTile(ReadOnlySpan<byte> pixels) {
            if (pixels.Length != options.TileByteCount) {
                throw new ArgumentException($"A tile must contain exactly {options.TileByteCount} bytes.", nameof(pixels));
            }
            if (options.Compression == TiffCompression.None) {
                return pixels.ToArray();
            }
            var buffer = pixels.ToArray();
            ApplyHorizontalPredictor(buffer, options.TileSize, options.Channels);
            using var compressed = new MemoryStream(buffer.Length / 4);
            using (var zlib = new ZLibStream(compressed, options.CompressionLevel, leaveOpen: true)) {
                zlib.Write(buffer);
            }
            return compressed.ToArray();
        }

        /// <summary>Encodes and writes the tile at the given tile column and row. Thread-safe.</summary>
        public void WriteTile(long column, long row, ReadOnlySpan<byte> pixels) {
            WriteEncodedTile(column, row, EncodeTile(pixels));
        }

        /// <summary>Writes a tile produced by <see cref="EncodeTile"/>. Thread-safe.</summary>
        public void WriteEncodedTile(long column, long row, ReadOnlySpan<byte> data) {
            if (column < 0 || column >= options.TileColumns || row < 0 || row >= options.TileRows) {
                throw new ArgumentOutOfRangeException(nameof(column), $"Tile {column},{row} is outside the image.");
            }
            var index = row * options.TileColumns + column;
            lock (sync) {
                if (finished) {
                    throw new InvalidOperationException("The writer has already finished.");
                }
                if (tileByteCounts[index] != 0) {
                    throw new InvalidOperationException($"Tile {column},{row} has already been written.");
                }
                if (!IsBigTiff && position + data.Length > uint.MaxValue) {
                    throw new IOException("The image exceeds the 4 GiB limit of classic TIFF; use BigTIFF.");
                }
                tileOffsets[index] = position;
                tileByteCounts[index] = data.Length;
                target.Write(data);
                position += data.Length;
                Align();
                writtenTiles++;
            }
        }

        /// <summary>Writes the image directory and, if a temporary file was used, copies the result to the output.</summary>
        public void Finish() {
            lock (sync) {
                if (finished) {
                    return;
                }
                if (writtenTiles != options.TileCount) {
                    throw new InvalidOperationException($"Only {writtenTiles} of {options.TileCount} tiles have been written.");
                }
                finished = true;
                var directoryOffset = WriteDirectory();
                target.Flush();
                target.Seek(IsBigTiff ? 8 : 4, SeekOrigin.Begin);
                Span<byte> buffer = stackalloc byte[8];
                if (IsBigTiff) {
                    BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)directoryOffset);
                    target.Write(buffer);
                }
                else {
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer, checked((uint)directoryOffset));
                    target.Write(buffer[..4]);
                }
                target.Flush();
                if (temporaryFile != null) {
                    temporaryFile.Seek(0, SeekOrigin.Begin);
                    temporaryFile.CopyTo(output, 1 << 20);
                }
                output.Flush();
            }
        }

        public void Dispose() {
            temporaryFile?.Dispose();
        }

        internal static void ApplyHorizontalPredictor(Span<byte> tile, int tileSize, int channels) {
            var rowLength = tileSize * channels;
            for (var row = 0; row < tileSize; row++) {
                var line = tile.Slice(row * rowLength, rowLength);
                for (var i = rowLength - 1; i >= channels; i--) {
                    line[i] -= line[i - channels];
                }
            }
        }

        void WriteHeader() {
            Span<byte> header = stackalloc byte[16];
            header.Clear();
            header[0] = (byte)'I';
            header[1] = (byte)'I';
            if (IsBigTiff) {
                BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 43);
                BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 8);
                target.Write(header);
                position = 16;
            }
            else {
                BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 42);
                target.Write(header[..8]);
                position = 8;
            }
        }

        void Align() {
            if (position % 2 != 0) {
                target.WriteByte(0);
                position++;
            }
        }

        long WriteDirectory() {
            var entries = BuildEntries();
            var inlineSize = IsBigTiff ? 8 : 4;
            var entrySize = IsBigTiff ? 20 : 12;
            var directorySize = IsBigTiff ? 8 + entries.Count * entrySize + 8 : 2 + entries.Count * entrySize + 4;
            var directoryOffset = position;

            // Values that do not fit into an entry are stored right after the directory.
            var valueOffset = directoryOffset + directorySize;
            var externalOffsets = new long[entries.Count];
            for (var i = 0; i < entries.Count; i++) {
                if (entries[i].Value.Length > inlineSize) {
                    externalOffsets[i] = valueOffset;
                    valueOffset += entries[i].Value.Length + entries[i].Value.Length % 2;
                }
            }
            if (!IsBigTiff && valueOffset > uint.MaxValue) {
                throw new IOException("The image exceeds the 4 GiB limit of classic TIFF; use BigTIFF.");
            }

            using var directory = new MemoryStream();
            Span<byte> scratch = stackalloc byte[8];
            if (IsBigTiff) {
                BinaryPrimitives.WriteUInt64LittleEndian(scratch, (ulong)entries.Count);
                directory.Write(scratch);
            }
            else {
                BinaryPrimitives.WriteUInt16LittleEndian(scratch, checked((ushort)entries.Count));
                directory.Write(scratch[..2]);
            }
            for (var i = 0; i < entries.Count; i++) {
                var entry = entries[i];
                BinaryPrimitives.WriteUInt16LittleEndian(scratch, entry.Tag);
                directory.Write(scratch[..2]);
                BinaryPrimitives.WriteUInt16LittleEndian(scratch, entry.Type);
                directory.Write(scratch[..2]);
                if (IsBigTiff) {
                    BinaryPrimitives.WriteUInt64LittleEndian(scratch, (ulong)entry.Count);
                    directory.Write(scratch);
                }
                else {
                    BinaryPrimitives.WriteUInt32LittleEndian(scratch, checked((uint)entry.Count));
                    directory.Write(scratch[..4]);
                }
                scratch.Clear();
                if (entry.Value.Length > inlineSize) {
                    if (IsBigTiff) {
                        BinaryPrimitives.WriteUInt64LittleEndian(scratch, (ulong)externalOffsets[i]);
                    }
                    else {
                        BinaryPrimitives.WriteUInt32LittleEndian(scratch, (uint)externalOffsets[i]);
                    }
                }
                else {
                    entry.Value.CopyTo(scratch);
                }
                directory.Write(scratch[..inlineSize]);
            }
            // Offset of the next directory: none.
            scratch.Clear();
            directory.Write(scratch[..inlineSize]);
            foreach (var entry in entries) {
                if (entry.Value.Length > inlineSize) {
                    directory.Write(entry.Value);
                    if (entry.Value.Length % 2 != 0) {
                        directory.WriteByte(0);
                    }
                }
            }
            directory.Position = 0;
            directory.CopyTo(target);
            position += directory.Length;
            return directoryOffset;
        }

        List<TiffEntry> BuildEntries() {
            var channels = options.Channels;
            var entries = new List<TiffEntry> {
                TiffEntry.Longs(256, (uint)options.Width),
                TiffEntry.Longs(257, (uint)options.Height),
                TiffEntry.Shorts(258, Enumerable.Repeat((ushort)8, channels).ToArray()),
                TiffEntry.Shorts(259, options.Compression == TiffCompression.Deflate ? (ushort)8 : (ushort)1),
                TiffEntry.Shorts(262, 2),
                TiffEntry.Shorts(277, (ushort)channels),
                TiffEntry.Shorts(284, 1),
                TiffEntry.Ascii(305, options.Software),
            };
            if (options.Compression == TiffCompression.Deflate) {
                entries.Add(TiffEntry.Shorts(317, 2));
            }
            entries.Add(TiffEntry.Longs(322, (uint)options.TileSize));
            entries.Add(TiffEntry.Longs(323, (uint)options.TileSize));
            entries.Add(IsBigTiff ? TiffEntry.Long8s(324, tileOffsets) : TiffEntry.Longs(324, tileOffsets.Select(o => (uint)o).ToArray()));
            entries.Add(IsBigTiff ? TiffEntry.Long8s(325, tileByteCounts) : TiffEntry.Longs(325, tileByteCounts.Select(c => (uint)c).ToArray()));
            if (channels == 4) {
                // Unassociated alpha.
                entries.Add(TiffEntry.Shorts(338, 2));
            }
            entries.Add(TiffEntry.Shorts(339, Enumerable.Repeat((ushort)1, channels).ToArray()));
            if (options.GeoReference is { } geo) {
                entries.Add(TiffEntry.Doubles(33550, geo.PixelSizeX, geo.PixelSizeY, 0));
                entries.Add(TiffEntry.Doubles(33922, 0, 0, 0, geo.OriginX, geo.OriginY, 0));
                const string citation = "WGS 84 / Pseudo-Mercator|";
                entries.Add(TiffEntry.Shorts(34735,
                    1, 1, 0, 5,
                    1024, 0, 1, 1, // GTModelTypeGeoKey = ModelTypeProjected
                    1025, 0, 1, 1, // GTRasterTypeGeoKey = RasterPixelIsArea
                    1026, 34737, (ushort)citation.Length, 0, // GTCitationGeoKey
                    3072, 0, 1, 3857, // ProjectedCSTypeGeoKey = EPSG:3857
                    3076, 0, 1, 9001)); // ProjLinearUnitsGeoKey = metre
                entries.Add(TiffEntry.Ascii(34737, citation));
            }
            entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
            return entries;
        }

        readonly record struct TiffEntry(ushort Tag, ushort Type, long Count, byte[] Value) {
            public static TiffEntry Shorts(ushort tag, params ushort[] values) {
                var bytes = new byte[values.Length * 2];
                for (var i = 0; i < values.Length; i++) {
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
                }
                return new TiffEntry(tag, TypeShort, values.Length, bytes);
            }

            public static TiffEntry Longs(ushort tag, params uint[] values) {
                var bytes = new byte[values.Length * 4];
                for (var i = 0; i < values.Length; i++) {
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
                }
                return new TiffEntry(tag, TypeLong, values.Length, bytes);
            }

            public static TiffEntry Long8s(ushort tag, long[] values) {
                var bytes = new byte[values.Length * 8];
                for (var i = 0; i < values.Length; i++) {
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), (ulong)values[i]);
                }
                return new TiffEntry(tag, TypeLong8, values.Length, bytes);
            }

            public static TiffEntry Doubles(ushort tag, params double[] values) {
                var bytes = new byte[values.Length * 8];
                for (var i = 0; i < values.Length; i++) {
                    BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), values[i]);
                }
                return new TiffEntry(tag, TypeDouble, values.Length, bytes);
            }

            public static TiffEntry Ascii(ushort tag, string value) {
                var bytes = Encoding.ASCII.GetBytes(value + "\0");
                return new TiffEntry(tag, TypeAscii, bytes.Length, bytes);
            }
        }
    }
}
