using YukiOsmRenderer.Core.Import;
using YukiOsmRenderer.Core.Vector;

namespace YukiOsmRenderer.Tests.Vector {
    public class VectorSchemaTests {
        const string Schema = """
            schema_name: Shortbread
            attribution: OpenStreetMap
            sources:
              ocean:
                type: shapefile
                url: https://osmdata.openstreetmap.de/download/water-polygons-split-3857.zip
              osm:
                type: osm
                url: geofabrik:monaco
            layers:
              - id: water
                features:
                  - source: ocean
                    geometry: polygon
            examples:
              - name: test
            """;

        [Test]
        public void PointsTheSourcesAtLocalFiles() {
            var input = Path.Combine(Path.GetTempPath(), "dir with space", "merged.osm.pbf");
            var water = Path.Combine(Path.GetTempPath(), "water's edge.zip");

            var prepared = VectorSchema.Prepare(Schema, input, water);

            Assert.That(prepared, Does.Not.Contain("url:"));
            Assert.That(prepared, Does.Not.Contain("examples"));
            Assert.That(prepared, Does.Contain("schema_name: Shortbread"));
            Assert.That(prepared, Does.Contain("geometry: polygon"));
            var reparsed = new YamlDotNet.Serialization.Deserializer().Deserialize<Dictionary<string, object>>(prepared);
            var sources = (Dictionary<object, object>)reparsed["sources"];
            Assert.That(((Dictionary<object, object>)sources["osm"])["local_path"], Is.EqualTo(Path.GetFullPath(input)));
            Assert.That(((Dictionary<object, object>)sources["osm"])["type"], Is.EqualTo("osm"));
            Assert.That(((Dictionary<object, object>)sources["ocean"])["local_path"], Is.EqualTo(Path.GetFullPath(water)));
        }

        [Test]
        public void SchemasWithoutOceanAreAccepted() {
            var prepared = VectorSchema.Prepare("sources:\n  osm:\n    type: osm\nlayers: []\n", "in.osm.pbf", "water.zip");

            Assert.That(prepared, Does.Contain("local_path"));
            Assert.That(prepared, Does.Not.Contain("water.zip"));
        }

        [TestCase("sources:\n  other:\n    type: osm\n")]
        [TestCase("layers: []\n")]
        [TestCase("- a\n- b\n")]
        public void RejectsSchemasWithoutAnOsmSource(string yaml) {
            Assert.Throws<InvalidDataException>(() => VectorSchema.Prepare(yaml, "in.osm.pbf", "water.zip"));
        }
    }

    public class VectorBuildStateTests {
        static VectorBuildState State() {
            return new VectorBuildState {
                Fingerprint = "f",
                Schema = "s",
                MaxZoom = 14,
                PlanetilerArguments = ["a=1"],
                Planetiler = "0.10.2",
                WaterPolygons = "w",
            };
        }

        [Test]
        public void SameStateHasNoDifference() {
            Assert.That(State().Difference(State()), Is.Null);
        }

        [Test]
        public void ReportsWhatChanged() {
            Assert.Multiple(() => {
                Assert.That(State().Difference(State() with { Version = 0 }), Does.Contain("version"));
                Assert.That(State().Difference(State() with { Fingerprint = "g" }), Does.Contain("input files"));
                Assert.That(State().Difference(State() with { Schema = "t" }), Does.Contain("schema"));
                Assert.That(State().Difference(State() with { MaxZoom = 15 }), Does.Contain("--max-data-zoom"));
                Assert.That(State().Difference(State() with { PlanetilerArguments = ["a=2"] }), Does.Contain("--planetiler-arg"));
                Assert.That(State().Difference(State() with { PlanetilerArguments = [] }), Does.Contain("--planetiler-arg"));
                Assert.That(State().Difference(State() with { Planetiler = "0.11" }), Does.Contain("Planetiler"));
                Assert.That(State().Difference(State() with { WaterPolygons = "x" }), Does.Contain("water polygons"));
            });
        }

        [Test]
        public void CompletionTimeAndInputNamesDoNotMatter() {
            var built = State() with { CompletedAt = DateTimeOffset.UnixEpoch, Inputs = [new InputFileInfo("a.osm.pbf", 1, "h")] };

            Assert.That(built.Difference(State()), Is.Null);
        }
    }
}
