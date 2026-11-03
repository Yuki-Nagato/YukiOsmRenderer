using Microsoft.Extensions.Logging.Abstractions;
using YukiOsmRenderer.Core.Vector;

namespace YukiOsmRenderer.Tests.Vector {
    public class OsmPbfTests {
        string directory = "";

        [SetUp]
        public void SetUp() {
            directory = Path.Combine(Path.GetTempPath(), "yukiosm-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown() {
            Directory.Delete(directory, recursive: true);
        }

        static OsmElement Node(long id, double latitude, double longitude, params string[] tags) {
            return new OsmElement {
                Type = OsmType.Node,
                Id = id,
                Tags = tags,
                Latitude = (long)Math.Round(latitude * 1e9),
                Longitude = (long)Math.Round(longitude * 1e9),
            };
        }

        static OsmElement Way(long id, long[] nodes, params string[] tags) {
            return new OsmElement { Type = OsmType.Way, Id = id, References = nodes, Tags = tags };
        }

        static OsmElement Relation(long id, (OsmType Type, long Id, string Role)[] members, params string[] tags) {
            return new OsmElement {
                Type = OsmType.Relation,
                Id = id,
                Tags = tags,
                References = members.Select(m => m.Id).ToArray(),
                MemberTypes = members.Select(m => m.Type).ToArray(),
                MemberRoles = members.Select(m => m.Role).ToArray(),
            };
        }

        string Write(string name, (long, long, long, long)? bounds, params OsmElement[] elements) {
            var path = Path.Combine(directory, name);
            using var writer = new OsmPbfWriter(path, bounds);
            foreach (var element in elements) {
                writer.Write(element);
            }
            return path;
        }

        static List<OsmElement> Read(string path) {
            using var reader = OsmPbfReader.Open(path);
            return reader.ReadElements().Select(e => new OsmElement {
                Type = e.Type,
                Id = e.Id,
                Tags = e.Tags,
                Latitude = e.Latitude,
                Longitude = e.Longitude,
                References = e.References,
                MemberTypes = e.MemberTypes,
                MemberRoles = e.MemberRoles,
            }).ToList();
        }

        [Test]
        public void WrittenFilesReadBackWithTheSameObjects() {
            var path = Write("a.osm.pbf", (116_000_000_000, 117_000_000_000, 40_000_000_000, 39_000_000_000),
                Node(1, 39.9042, 116.4074, "name", "北京", "place", "city"),
                Node(2, -33.8688, 151.2093),
                Node(5, 0, -0.0000001),
                Way(10, [1, 2, 5, 1], "highway", "primary"),
                Relation(20, [(OsmType.Way, 10, "outer"), (OsmType.Node, 1, "label")], "type", "multipolygon"));

            var elements = Read(path);

            Assert.That(elements.Select(e => (e.Type, e.Id)), Is.EqualTo(new[] {
                (OsmType.Node, 1L), (OsmType.Node, 2L), (OsmType.Node, 5L), (OsmType.Way, 10L), (OsmType.Relation, 20L),
            }));
            Assert.That(elements[0].Tags, Is.EqualTo(new[] { "name", "北京", "place", "city" }));
            Assert.That(elements[0].Latitude, Is.EqualTo(39_904_200_000));
            Assert.That(elements[1].Longitude, Is.EqualTo(151_209_300_000));
            Assert.That(elements[2].Longitude, Is.EqualTo(-100));
            Assert.That(elements[1].Tags, Is.Empty);
            Assert.That(elements[3].References, Is.EqualTo(new long[] { 1, 2, 5, 1 }));
            Assert.That(elements[3].Tags, Is.EqualTo(new[] { "highway", "primary" }));
            Assert.That(elements[4].References, Is.EqualTo(new long[] { 10, 1 }));
            Assert.That(elements[4].MemberTypes, Is.EqualTo(new[] { OsmType.Way, OsmType.Node }));
            Assert.That(elements[4].MemberRoles, Is.EqualTo(new[] { "outer", "label" }));
            using var reader = OsmPbfReader.Open(path);
            Assert.That(reader.Bounds, Is.EqualTo((116_000_000_000L, 117_000_000_000L, 40_000_000_000L, 39_000_000_000L)));
        }

        [Test]
        public void ManyNodesSpanSeveralBlocks() {
            var nodes = Enumerable.Range(1, 20_000).Select(i => Node(i * 3L, i * 1e-4, -i * 1e-4)).ToArray();
            var path = Write("many.osm.pbf", null, nodes);

            var elements = Read(path);

            Assert.That(elements, Has.Count.EqualTo(20_000));
            Assert.That(elements[^1].Id, Is.EqualTo(60_000));
            Assert.That(elements[^1].Latitude, Is.EqualTo(2_000_000_000));
        }

        [Test]
        public void MergeCombinesSortedFilesAndWritesSharedObjectsOnce() {
            var a = Write("a.osm.pbf", (0, 10_000_000_000, 10_000_000_000, 0),
                Node(1, 1, 1), Node(3, 3, 3, "name", "a"), Way(10, [1, 3]), Relation(30, [(OsmType.Way, 10, "")]));
            var b = Write("b.osm.pbf", (-5_000_000_000, 5_000_000_000, 20_000_000_000, 5_000_000_000),
                Node(2, 2, 2), Node(3, 3, 3, "name", "a"), Way(10, [1, 3]), Way(11, [2, 3]));
            var output = Path.Combine(directory, "merged.osm.pbf");

            OsmPbfMerger.Merge([a, b], output, NullLogger.Instance);

            var elements = Read(output);
            Assert.That(elements.Select(e => (e.Type, e.Id)), Is.EqualTo(new[] {
                (OsmType.Node, 1L), (OsmType.Node, 2L), (OsmType.Node, 3L), (OsmType.Way, 10L), (OsmType.Way, 11L), (OsmType.Relation, 30L),
            }));
            Assert.That(elements[2].Tags, Is.EqualTo(new[] { "name", "a" }));
            using var reader = OsmPbfReader.Open(output);
            Assert.That(reader.Bounds, Is.EqualTo((-5_000_000_000L, 10_000_000_000L, 20_000_000_000L, 0L)));
        }

        [Test]
        public void MergeRejectsUnsortedFiles() {
            var a = Write("a.osm.pbf", null, Node(1, 1, 1));
            var unsorted = Write("unsorted.osm.pbf", null, Node(5, 1, 1), Node(2, 2, 2));

            var error = Assert.Throws<InvalidDataException>(() => OsmPbfMerger.Merge([a, unsorted], Path.Combine(directory, "merged.osm.pbf"), NullLogger.Instance));

            Assert.That(error!.Message, Does.Contain("unsorted.osm.pbf").And.Contain("not sorted"));
        }

        [Test]
        public void RejectsFilesThatAreNotOsmPbf() {
            var path = Path.Combine(directory, "text.osm.pbf");
            File.WriteAllText(path, "not a pbf file at all");

            Assert.Throws<InvalidDataException>(() => OsmPbfReader.Open(path).Dispose());
        }
    }
}
