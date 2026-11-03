using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace YukiOsmRenderer.Core.Vector {
    internal enum OsmType : byte {
        Node = 0,
        Way = 1,
        Relation = 2,
    }

    /// <summary>An OSM object without metadata (version, timestamp, user), which renderers do not need.</summary>
    internal sealed class OsmElement {
        public OsmType Type;
        public long Id;

        /// <summary>Alternating keys and values.</summary>
        public string[] Tags = [];

        /// <summary>Node coordinates in nanodegrees.</summary>
        public long Latitude, Longitude;

        /// <summary>Node ids of a way, or member ids of a relation.</summary>
        public long[] References = [];

        public OsmType[] MemberTypes = [];
        public string[] MemberRoles = [];
    }

    /// <summary>Reads the objects of an .osm.pbf file in file order.</summary>
    internal sealed class OsmPbfReader : IDisposable {
        const int MaxBlobHeaderSize = 64 * 1024;
        const int MaxBlobSize = 32 * 1024 * 1024;

        readonly Stream stream;

        OsmPbfReader(Stream stream, (long Left, long Right, long Top, long Bottom)? bounds) {
            this.stream = stream;
            Bounds = bounds;
        }

        /// <summary>Bounding box from the header in nanodegrees, if the file has one.</summary>
        public (long Left, long Right, long Top, long Bottom)? Bounds { get; }

        public long Position => stream.Position;

        public long Length => stream.Length;

        public static OsmPbfReader Open(string path) {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            try {
                var (type, data) = ReadBlob(stream) ?? throw new InvalidDataException($"'{path}' is empty.");
                if (type != "OSMHeader") {
                    throw new InvalidDataException($"'{path}' is not an OSM PBF file.");
                }
                return new OsmPbfReader(stream, ParseHeader(data, path));
            }
            catch {
                stream.Dispose();
                throw;
            }
        }

        public IEnumerable<OsmElement> ReadElements() {
            while (ReadBlob(stream) is var (type, data)) {
                if (type != "OSMData") {
                    continue;
                }
                foreach (var element in ParseBlock(data)) {
                    yield return element;
                }
            }
        }

        public void Dispose() {
            stream.Dispose();
        }

        static (long, long, long, long)? ParseHeader(byte[] data, string path) {
            (long, long, long, long)? bounds = null;
            var reader = new ProtoReader(data);
            while (!reader.End) {
                var (field, wireType) = reader.ReadKey();
                if (field == 1 && wireType == 2) {
                    var box = new ProtoReader(reader.ReadBytes());
                    long left = 0, right = 0, top = 0, bottom = 0;
                    while (!box.End) {
                        var (f, w) = box.ReadKey();
                        switch (f) {
                            case 1: left = box.ReadSignedVarint(); break;
                            case 2: right = box.ReadSignedVarint(); break;
                            case 3: top = box.ReadSignedVarint(); break;
                            case 4: bottom = box.ReadSignedVarint(); break;
                            default: box.Skip(w); break;
                        }
                    }
                    bounds = (left, right, top, bottom);
                }
                else if (field == 4 && wireType == 2) {
                    var feature = reader.ReadString();
                    if (feature is not ("OsmSchema-V0.6" or "DenseNodes" or "HistoricalInformation")) {
                        throw new InvalidDataException($"'{path}' requires the unsupported feature {feature}.");
                    }
                }
                else {
                    reader.Skip(wireType);
                }
            }
            return bounds;
        }

        /// <summary>Reads the next blob and returns its type and uncompressed data, or null at the end of the file.</summary>
        static (string Type, byte[] Data)? ReadBlob(Stream stream) {
            Span<byte> lengthBytes = stackalloc byte[4];
            var read = stream.ReadAtLeast(lengthBytes, 4, throwOnEndOfStream: false);
            if (read == 0) {
                return null;
            }
            if (read < 4) {
                throw new InvalidDataException("Truncated OSM PBF file.");
            }
            var headerLength = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (headerLength is <= 0 or > MaxBlobHeaderSize) {
                throw new InvalidDataException("Malformed OSM PBF file.");
            }
            var header = new byte[headerLength];
            stream.ReadExactly(header);
            string? type = null;
            var dataSize = 0;
            var headerReader = new ProtoReader(header);
            while (!headerReader.End) {
                var (field, wireType) = headerReader.ReadKey();
                switch (field) {
                    case 1: type = headerReader.ReadString(); break;
                    case 3: dataSize = checked((int)headerReader.ReadVarint()); break;
                    default: headerReader.Skip(wireType); break;
                }
            }
            if (type == null || dataSize is < 0 or > MaxBlobSize) {
                throw new InvalidDataException("Malformed OSM PBF file.");
            }
            var blob = new byte[dataSize];
            stream.ReadExactly(blob);

            byte[]? raw = null;
            var rawSize = 0;
            ReadOnlySpan<byte> zlib = default;
            var blobReader = new ProtoReader(blob);
            while (!blobReader.End) {
                var (field, wireType) = blobReader.ReadKey();
                switch (field) {
                    case 1: raw = blobReader.ReadBytes().ToArray(); break;
                    case 2: rawSize = checked((int)blobReader.ReadVarint()); break;
                    case 3: zlib = blobReader.ReadBytes(); break;
                    case 4 or 5 or 6 or 7: throw new InvalidDataException("OSM PBF blobs compressed with lzma, bzip2, lz4 or zstd are not supported.");
                    default: blobReader.Skip(wireType); break;
                }
            }
            if (raw != null) {
                return (type, raw);
            }
            if (rawSize is <= 0 or > MaxBlobSize) {
                throw new InvalidDataException("Malformed OSM PBF blob.");
            }
            var data = new byte[rawSize];
            using (var inflater = new ZLibStream(new MemoryStream(zlib.ToArray()), CompressionMode.Decompress)) {
                inflater.ReadExactly(data);
            }
            return (type, data);
        }

        static List<OsmElement> ParseBlock(byte[] data) {
            var strings = new List<string>();
            // Groups are decoded after the whole block, whose granularity and offsets may follow them.
            var groups = new List<byte[]>();
            long granularity = 100, latOffset = 0, lonOffset = 0;
            var reader = new ProtoReader(data);
            while (!reader.End) {
                var (field, wireType) = reader.ReadKey();
                switch (field) {
                    case 1: {
                            var table = new ProtoReader(reader.ReadBytes());
                            while (!table.End) {
                                var (f, w) = table.ReadKey();
                                if (f == 1) {
                                    strings.Add(table.ReadString());
                                }
                                else {
                                    table.Skip(w);
                                }
                            }
                            break;
                        }
                    case 2:
                        groups.Add(reader.ReadBytes().ToArray());
                        break;
                    case 17: granularity = (long)reader.ReadVarint(); break;
                    case 19: latOffset = (long)reader.ReadVarint(); break;
                    case 20: lonOffset = (long)reader.ReadVarint(); break;
                    default: reader.Skip(wireType); break;
                }
            }

            var elements = new List<OsmElement>();
            foreach (var groupData in groups) {
                var group = new ProtoReader(groupData);
                while (!group.End) {
                    var (field, wireType) = group.ReadKey();
                    switch (field) {
                        case 1: elements.Add(ParseNode(group.ReadBytes(), strings, granularity, latOffset, lonOffset)); break;
                        case 2: ParseDenseNodes(group.ReadBytes(), strings, granularity, latOffset, lonOffset, elements); break;
                        case 3: elements.Add(ParseWay(group.ReadBytes(), strings)); break;
                        case 4: elements.Add(ParseRelation(group.ReadBytes(), strings)); break;
                        default: group.Skip(wireType); break;
                    }
                }
            }
            return elements;
        }

        static string[] Tags(List<ulong> keys, List<ulong> values, List<string> strings) {
            var tags = new string[keys.Count * 2];
            for (var i = 0; i < keys.Count; i++) {
                tags[2 * i] = strings[(int)keys[i]];
                tags[2 * i + 1] = strings[(int)values[i]];
            }
            return tags;
        }

        static OsmElement ParseNode(ReadOnlySpan<byte> data, List<string> strings, long granularity, long latOffset, long lonOffset) {
            var node = new OsmElement { Type = OsmType.Node };
            List<ulong> keys = [], values = [];
            var reader = new ProtoReader(data);
            while (!reader.End) {
                var (field, wireType) = reader.ReadKey();
                switch (field) {
                    case 1: node.Id = reader.ReadSignedVarint(); break;
                    case 2: reader.ReadRepeated(wireType, keys); break;
                    case 3: reader.ReadRepeated(wireType, values); break;
                    case 8: node.Latitude = latOffset + granularity * reader.ReadSignedVarint(); break;
                    case 9: node.Longitude = lonOffset + granularity * reader.ReadSignedVarint(); break;
                    default: reader.Skip(wireType); break;
                }
            }
            node.Tags = Tags(keys, values, strings);
            return node;
        }

        static void ParseDenseNodes(ReadOnlySpan<byte> data, List<string> strings, long granularity, long latOffset, long lonOffset, List<OsmElement> elements) {
            List<ulong> ids = [], lats = [], lons = [], keysValues = [];
            var reader = new ProtoReader(data);
            while (!reader.End) {
                var (field, wireType) = reader.ReadKey();
                switch (field) {
                    case 1: reader.ReadRepeated(wireType, ids); break;
                    case 8: reader.ReadRepeated(wireType, lats); break;
                    case 9: reader.ReadRepeated(wireType, lons); break;
                    case 10: reader.ReadRepeated(wireType, keysValues); break;
                    default: reader.Skip(wireType); break;
                }
            }
            if (lats.Count != ids.Count || lons.Count != ids.Count) {
                throw new InvalidDataException("Malformed dense nodes.");
            }
            long id = 0, lat = 0, lon = 0;
            var tagIndex = 0;
            var tags = new List<string>();
            for (var i = 0; i < ids.Count; i++) {
                id += ProtoReader.Decode(ids[i]);
                lat += ProtoReader.Decode(lats[i]);
                lon += ProtoReader.Decode(lons[i]);
                tags.Clear();
                // Pairs of key and value indexes, each node's terminated by 0; absent when no node has tags.
                while (tagIndex < keysValues.Count && keysValues[tagIndex] != 0) {
                    tags.Add(strings[(int)keysValues[tagIndex]]);
                    tags.Add(strings[(int)keysValues[tagIndex + 1]]);
                    tagIndex += 2;
                }
                tagIndex++;
                elements.Add(new OsmElement {
                    Type = OsmType.Node,
                    Id = id,
                    Latitude = latOffset + granularity * lat,
                    Longitude = lonOffset + granularity * lon,
                    Tags = tags.Count == 0 ? [] : [.. tags],
                });
            }
        }

        static OsmElement ParseWay(ReadOnlySpan<byte> data, List<string> strings) {
            var way = new OsmElement { Type = OsmType.Way };
            List<ulong> keys = [], values = [], refs = [];
            var reader = new ProtoReader(data);
            while (!reader.End) {
                var (field, wireType) = reader.ReadKey();
                switch (field) {
                    case 1: way.Id = (long)reader.ReadVarint(); break;
                    case 2: reader.ReadRepeated(wireType, keys); break;
                    case 3: reader.ReadRepeated(wireType, values); break;
                    case 8: reader.ReadRepeated(wireType, refs); break;
                    default: reader.Skip(wireType); break;
                }
            }
            way.Tags = Tags(keys, values, strings);
            way.References = new long[refs.Count];
            long id = 0;
            for (var i = 0; i < refs.Count; i++) {
                id += ProtoReader.Decode(refs[i]);
                way.References[i] = id;
            }
            return way;
        }

        static OsmElement ParseRelation(ReadOnlySpan<byte> data, List<string> strings) {
            var relation = new OsmElement { Type = OsmType.Relation };
            List<ulong> keys = [], values = [], roles = [], ids = [], types = [];
            var reader = new ProtoReader(data);
            while (!reader.End) {
                var (field, wireType) = reader.ReadKey();
                switch (field) {
                    case 1: relation.Id = (long)reader.ReadVarint(); break;
                    case 2: reader.ReadRepeated(wireType, keys); break;
                    case 3: reader.ReadRepeated(wireType, values); break;
                    case 8: reader.ReadRepeated(wireType, roles); break;
                    case 9: reader.ReadRepeated(wireType, ids); break;
                    case 10: reader.ReadRepeated(wireType, types); break;
                    default: reader.Skip(wireType); break;
                }
            }
            if (roles.Count != ids.Count || types.Count != ids.Count) {
                throw new InvalidDataException("Malformed relation.");
            }
            relation.Tags = Tags(keys, values, strings);
            relation.References = new long[ids.Count];
            relation.MemberTypes = new OsmType[ids.Count];
            relation.MemberRoles = new string[ids.Count];
            long id = 0;
            for (var i = 0; i < ids.Count; i++) {
                id += ProtoReader.Decode(ids[i]);
                relation.References[i] = id;
                relation.MemberTypes[i] = (OsmType)types[i];
                relation.MemberRoles[i] = strings[(int)roles[i]];
            }
            return relation;
        }
    }

    /// <summary>Writes objects, sorted by type and id, to an .osm.pbf file with dense nodes and zlib compression.</summary>
    internal sealed class OsmPbfWriter : IDisposable {
        const int MaxElementsPerBlock = 8000;
        const int TargetBlockSize = 8 * 1024 * 1024;

        readonly Stream stream;
        readonly List<OsmElement> pending = [];
        readonly ProtoWriter block = new();
        readonly ProtoWriter scratch = new();
        readonly ProtoWriter frame = new();
        int pendingSize;

        public OsmPbfWriter(string path, (long Left, long Right, long Top, long Bottom)? bounds) {
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            var header = new ProtoWriter();
            if (bounds is var (left, right, top, bottom)) {
                var box = new ProtoWriter();
                box.WriteSignedVarintField(1, left);
                box.WriteSignedVarintField(2, right);
                box.WriteSignedVarintField(3, top);
                box.WriteSignedVarintField(4, bottom);
                header.WriteBytesField(1, box.Written);
            }
            header.WriteStringField(4, "OsmSchema-V0.6");
            header.WriteStringField(4, "DenseNodes");
            header.WriteStringField(5, "Sort.Type_then_ID");
            header.WriteStringField(16, "yukiosm");
            WriteBlob("OSMHeader", header.Written);
        }

        public void Write(OsmElement element) {
            if (pending.Count > 0 && (pending[0].Type != element.Type || pending.Count >= MaxElementsPerBlock || pendingSize >= TargetBlockSize)) {
                Flush();
            }
            pending.Add(element);
            pendingSize += 16 + element.Tags.Sum(t => t.Length + 2) + element.References.Length * 4;
        }

        public void Dispose() {
            Flush();
            stream.Dispose();
        }

        void Flush() {
            if (pending.Count == 0) {
                return;
            }
            var strings = new Dictionary<string, int>(StringComparer.Ordinal) { [""] = 0 };
            int Index(string text) {
                if (!strings.TryGetValue(text, out var index)) {
                    strings[text] = index = strings.Count;
                }
                return index;
            }

            var group = new ProtoWriter();
            switch (pending[0].Type) {
                case OsmType.Node: {
                        var ids = new ulong[pending.Count];
                        var lats = new ulong[pending.Count];
                        var lons = new ulong[pending.Count];
                        var keysValues = new List<ulong>();
                        long lastId = 0, lastLat = 0, lastLon = 0;
                        var anyTags = pending.Any(n => n.Tags.Length > 0);
                        for (var i = 0; i < pending.Count; i++) {
                            var node = pending[i];
                            // Coordinates in units of the default granularity (100 nanodegrees).
                            var lat = (long)Math.Round(node.Latitude / 100.0);
                            var lon = (long)Math.Round(node.Longitude / 100.0);
                            ids[i] = ProtoWriter.Encode(node.Id - lastId);
                            lats[i] = ProtoWriter.Encode(lat - lastLat);
                            lons[i] = ProtoWriter.Encode(lon - lastLon);
                            (lastId, lastLat, lastLon) = (node.Id, lat, lon);
                            if (anyTags) {
                                for (var t = 0; t < node.Tags.Length; t++) {
                                    keysValues.Add((ulong)Index(node.Tags[t]));
                                }
                                keysValues.Add(0);
                            }
                        }
                        scratch.Clear();
                        scratch.WritePacked(1, ids);
                        scratch.WritePacked(8, lats);
                        scratch.WritePacked(9, lons);
                        scratch.WritePacked(10, keysValues.ToArray());
                        group.WriteBytesField(2, scratch.Written);
                        break;
                    }
                case OsmType.Way:
                    foreach (var way in pending) {
                        scratch.Clear();
                        scratch.WriteVarintField(1, (ulong)way.Id);
                        WriteTags(way, Index);
                        var refs = new ulong[way.References.Length];
                        long last = 0;
                        for (var i = 0; i < refs.Length; i++) {
                            refs[i] = ProtoWriter.Encode(way.References[i] - last);
                            last = way.References[i];
                        }
                        scratch.WritePacked(8, refs);
                        group.WriteBytesField(3, scratch.Written);
                    }
                    break;
                case OsmType.Relation:
                    foreach (var relation in pending) {
                        scratch.Clear();
                        scratch.WriteVarintField(1, (ulong)relation.Id);
                        WriteTags(relation, Index);
                        var count = relation.References.Length;
                        var roles = new ulong[count];
                        var ids = new ulong[count];
                        var types = new ulong[count];
                        long last = 0;
                        for (var i = 0; i < count; i++) {
                            roles[i] = (ulong)Index(relation.MemberRoles[i]);
                            ids[i] = ProtoWriter.Encode(relation.References[i] - last);
                            last = relation.References[i];
                            types[i] = (ulong)relation.MemberTypes[i];
                        }
                        scratch.WritePacked(8, roles);
                        scratch.WritePacked(9, ids);
                        scratch.WritePacked(10, types);
                        group.WriteBytesField(4, scratch.Written);
                    }
                    break;
            }

            var table = new ProtoWriter();
            foreach (var text in strings.OrderBy(s => s.Value).Select(s => s.Key)) {
                table.WriteStringField(1, text);
            }
            block.Clear();
            block.WriteBytesField(1, table.Written);
            block.WriteBytesField(2, group.Written);
            WriteBlob("OSMData", block.Written);
            pending.Clear();
            pendingSize = 0;
        }

        void WriteTags(OsmElement element, Func<string, int> index) {
            var count = element.Tags.Length / 2;
            var keys = new ulong[count];
            var values = new ulong[count];
            for (var i = 0; i < count; i++) {
                keys[i] = (ulong)index(element.Tags[2 * i]);
                values[i] = (ulong)index(element.Tags[2 * i + 1]);
            }
            scratch.WritePacked(2, keys);
            scratch.WritePacked(3, values);
        }

        void WriteBlob(string type, ReadOnlySpan<byte> data) {
            var compressed = new MemoryStream();
            using (var deflater = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) {
                deflater.Write(data);
            }
            var blob = new ProtoWriter();
            blob.WriteVarintField(2, (ulong)data.Length);
            blob.WriteBytesField(3, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
            var header = new ProtoWriter();
            header.WriteStringField(1, type);
            header.WriteVarintField(3, (ulong)blob.Length);
            frame.Clear();
            frame.WriteBigEndianInt32(header.Length);
            stream.Write(frame.Written);
            stream.Write(header.Written);
            stream.Write(blob.Written);
        }
    }

    /// <summary>
    /// Merges .osm.pbf files sorted by type and id (as extracts are) into one sorted file, which Planetiler needs as it
    /// reads a single OSM file. Objects contained in several files, such as those along the borders of neighbouring
    /// extracts, are written once.
    /// </summary>
    public static class OsmPbfMerger {
        static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(30);

        public static void Merge(IReadOnlyList<string> inputs, string output, ILogger logger, CancellationToken cancellationToken = default) {
            var readers = new List<OsmPbfReader>();
            try {
                foreach (var input in inputs) {
                    readers.Add(OsmPbfReader.Open(input));
                }
                var bounds = readers.All(r => r.Bounds != null)
                    ? readers.Select(r => r.Bounds!.Value).Aggregate((a, b) => (Math.Min(a.Left, b.Left), Math.Max(a.Right, b.Right), Math.Max(a.Top, b.Top), Math.Min(a.Bottom, b.Bottom)))
                    : ((long, long, long, long)?)null;
                var total = readers.Sum(r => r.Length);
                var enumerators = readers.Select(r => r.ReadElements().GetEnumerator()).ToList();
                var queue = new PriorityQueue<int, (OsmType Type, long Id, int Input)>();
                var last = new (OsmType Type, long Id)?[readers.Count];
                for (var i = 0; i < enumerators.Count; i++) {
                    Advance(i);
                }

                long written = 0, duplicates = 0;
                (OsmType Type, long Id)? previous = null;
                var progress = Stopwatch.StartNew();
                using (var writer = new OsmPbfWriter(output, bounds)) {
                    while (queue.TryDequeue(out var input, out var key)) {
                        var element = enumerators[input].Current;
                        if (previous == (key.Type, key.Id)) {
                            duplicates++;
                        }
                        else {
                            writer.Write(element);
                            previous = (key.Type, key.Id);
                            written++;
                        }
                        Advance(input);
                        if (progress.Elapsed >= ProgressInterval) {
                            cancellationToken.ThrowIfCancellationRequested();
                            logger.LogInformation("Merging: {Percent:0.0} % read, {Written:N0} objects written", 100.0 * readers.Sum(r => r.Position) / total, written);
                            progress.Restart();
                        }
                    }
                }
                logger.LogInformation("Merged {Count} files: {Written:N0} objects, {Duplicates:N0} contained in several files", readers.Count, written, duplicates);

                void Advance(int index) {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!enumerators[index].MoveNext()) {
                        return;
                    }
                    var element = enumerators[index].Current;
                    if (last[index] is var (type, id) && (element.Type < type || (element.Type == type && element.Id <= id))) {
                        throw new InvalidDataException($"'{inputs[index]}' is not sorted by type and id; sort it first, e.g. with osmium sort.");
                    }
                    last[index] = (element.Type, element.Id);
                    queue.Enqueue(index, (element.Type, element.Id, index));
                }
            }
            finally {
                foreach (var reader in readers) {
                    reader.Dispose();
                }
            }
        }
    }
}
