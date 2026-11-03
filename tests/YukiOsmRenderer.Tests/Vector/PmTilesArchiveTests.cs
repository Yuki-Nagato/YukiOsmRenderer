using System.Buffers.Binary;
using System.IO.Compression;
using YukiOsmRenderer.Core.Vector;

namespace YukiOsmRenderer.Tests.Vector {
    public class PmTilesArchiveTests {
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

        [TestCase(0, 0u, 0u, 0ul)]
        [TestCase(1, 0u, 0u, 1ul)]
        [TestCase(1, 0u, 1u, 2ul)]
        [TestCase(1, 1u, 1u, 3ul)]
        [TestCase(1, 1u, 0u, 4ul)]
        [TestCase(2, 0u, 0u, 5ul)]
        [TestCase(3, 7u, 0u, 84ul)]
        [TestCase(20, 0u, 0u, 366503875925ul)]
        public void ComputesTileIds(int zoom, uint x, uint y, ulong expected) {
            Assert.That(PmTilesArchive.TileId(zoom, x, y), Is.EqualTo(expected));
        }

        [Test]
        public void TileIdsAreUniqueAndDense() {
            var ids = new HashSet<ulong>();
            for (var z = 0; z <= 4; z++) {
                for (uint x = 0; x < 1u << z; x++) {
                    for (uint y = 0; y < 1u << z; y++) {
                        ids.Add(PmTilesArchive.TileId(z, x, y));
                    }
                }
            }
            Assert.That(ids, Is.EquivalentTo(Enumerable.Range(0, ids.Count).Select(i => (ulong)i)));
        }

        [TestCase(PmTilesCompression.None)]
        [TestCase(PmTilesCompression.Gzip)]
        public void ReadsTilesThroughRootAndLeafDirectories(PmTilesCompression compression) {
            var tiles = new Dictionary<(int Z, uint X, uint Y), byte[]> {
                [(0, 0, 0)] = [1, 2, 3],
                [(1, 1, 0)] = [4],
                [(3, 5, 2)] = [5, 6],
                [(3, 5, 3)] = [5, 6],
            };
            var path = Path.Combine(directory, "test.pmtiles");
            File.WriteAllBytes(path, Build(tiles, compression, leafSplit: 2, metadata: "{\"name\":\"test\"}"));

            using var archive = PmTilesArchive.Open(path);
            Assert.Multiple(() => {
                Assert.That(archive.Header.MinZoom, Is.EqualTo(0));
                Assert.That(archive.Header.MaxZoom, Is.EqualTo(3));
                Assert.That(archive.Header.TileType, Is.EqualTo(PmTilesTileType.Mvt));
                Assert.That(archive.Header.MinLongitude, Is.EqualTo(116.2).Within(1e-7));
                Assert.That(archive.Header.MaxLatitude, Is.EqualTo(40.0).Within(1e-7));
                Assert.That(archive.ReadMetadata(), Is.EqualTo("{\"name\":\"test\"}"));
                Assert.That(archive.GetTile(0, 0, 0), Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(archive.GetTile(1, 1, 0), Is.EqualTo(new byte[] { 4 }));
                Assert.That(archive.GetTile(3, 5, 2), Is.EqualTo(new byte[] { 5, 6 }));
                Assert.That(archive.GetTile(3, 5, 3), Is.EqualTo(new byte[] { 5, 6 }));
                Assert.That(archive.GetTile(1, 0, 0), Is.Null);
                Assert.That(archive.GetTile(3, 4, 2), Is.Null);
                Assert.That(archive.GetTile(4, 0, 0), Is.Null);
                Assert.That(archive.GetTile(3, 8, 0), Is.Null);
                Assert.That(archive.GetTile(-1, 0, 0), Is.Null);
            });
        }

        [Test]
        public void RejectsOtherFiles() {
            var path = Path.Combine(directory, "not.pmtiles");
            File.WriteAllBytes(path, new byte[200]);
            Assert.That(() => PmTilesArchive.Open(path), Throws.TypeOf<InvalidDataException>());
        }

        /// <summary>Writes an archive; identical consecutive tiles share one entry with a run length, as Planetiler does.</summary>
        internal static byte[] Build(IReadOnlyDictionary<(int Z, uint X, uint Y), byte[]> tiles, PmTilesCompression compression, int leafSplit = 0, string metadata = "{}") {
            var data = new MemoryStream();
            var entries = new List<(ulong Id, ulong Offset, uint Length, uint Run)>();
            foreach (var (key, bytes) in tiles.OrderBy(t => PmTilesArchive.TileId(t.Key.Z, t.Key.X, t.Key.Y))) {
                var id = PmTilesArchive.TileId(key.Z, key.X, key.Y);
                if (entries.Count > 0 && entries[^1].Id + entries[^1].Run == id && Read(data, entries[^1]).SequenceEqual(bytes)) {
                    entries[^1] = entries[^1] with { Run = entries[^1].Run + 1 };
                    continue;
                }
                entries.Add((id, (ulong)data.Length, (uint)bytes.Length, 1));
                data.Write(bytes);
            }

            byte[] root;
            var leaves = new MemoryStream();
            if (leafSplit > 0) {
                var pointers = new List<(ulong Id, ulong Offset, uint Length, uint Run)>();
                foreach (var chunk in entries.Chunk(leafSplit)) {
                    var leaf = Compress(Serialize(chunk), compression);
                    pointers.Add((chunk[0].Id, (ulong)leaves.Length, (uint)leaf.Length, 0));
                    leaves.Write(leaf);
                }
                root = Compress(Serialize(pointers), compression);
            }
            else {
                root = Compress(Serialize(entries), compression);
            }
            var meta = Compress(System.Text.Encoding.UTF8.GetBytes(metadata), compression);

            var header = new byte[PmTilesHeader.Size];
            "PMTiles"u8.CopyTo(header);
            header[7] = 3;
            ulong rootOffset = PmTilesHeader.Size, metaOffset = rootOffset + (ulong)root.Length, leafOffset = metaOffset + (ulong)meta.Length,
                dataOffset = leafOffset + (ulong)leaves.Length;
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), rootOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), (ulong)root.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), metaOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), (ulong)meta.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(40), leafOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(48), (ulong)leaves.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(56), dataOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(64), (ulong)data.Length);
            header[97] = (byte)compression;
            header[98] = (byte)PmTilesCompression.Gzip;
            header[99] = (byte)PmTilesTileType.Mvt;
            header[100] = (byte)tiles.Keys.Min(k => k.Z);
            header[101] = (byte)tiles.Keys.Max(k => k.Z);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(102), 1_162_000_000);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(106), 398_000_000);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(110), 1_166_000_000);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(114), 400_000_000);
            return [.. header, .. root, .. meta, .. leaves.ToArray(), .. data.ToArray()];
        }

        static byte[] Read(MemoryStream data, (ulong Id, ulong Offset, uint Length, uint Run) entry) {
            return data.GetBuffer().AsSpan((int)entry.Offset, (int)entry.Length).ToArray();
        }

        static byte[] Serialize(IReadOnlyList<(ulong Id, ulong Offset, uint Length, uint Run)> entries) {
            var output = new List<byte>();
            void Varint(ulong value) {
                while (value >= 0x80) {
                    output.Add((byte)(value | 0x80));
                    value >>= 7;
                }
                output.Add((byte)value);
            }
            Varint((ulong)entries.Count);
            ulong last = 0;
            foreach (var entry in entries) {
                Varint(entry.Id - last);
                last = entry.Id;
            }
            foreach (var entry in entries) {
                Varint(entry.Run);
            }
            foreach (var entry in entries) {
                Varint(entry.Length);
            }
            for (var i = 0; i < entries.Count; i++) {
                var contiguous = i > 0 && entries[i].Offset == entries[i - 1].Offset + entries[i - 1].Length;
                Varint(contiguous ? 0 : entries[i].Offset + 1);
            }
            return [.. output];
        }

        static byte[] Compress(byte[] data, PmTilesCompression compression) {
            if (compression != PmTilesCompression.Gzip) {
                return data;
            }
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) {
                gzip.Write(data);
            }
            return output.ToArray();
        }
    }
}
