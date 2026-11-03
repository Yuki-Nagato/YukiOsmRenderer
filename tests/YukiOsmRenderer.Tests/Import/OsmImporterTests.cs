using YukiOsmRenderer.Core.Import;

namespace YukiOsmRenderer.Tests.Import {
    public class OsmImporterTests {
        const long Gigabyte = 1024L * 1024 * 1024;

        [Test]
        public void SmallImportsUseTheNodeCache() {
            var arguments = OsmImporter.BuildOsm2pgsqlArguments("carto/flex.lua", ["a.osm.pbf", "b.osm.pbf"], 52L * 1024 * 1024,
                new ImportOptions { Processes = 6 }, "data/flat-nodes.bin", 48 * Gigabyte);
            Assert.Multiple(() => {
                Assert.That(arguments, Does.Contain("--create"));
                Assert.That(arguments, Does.Contain("--slim"));
                Assert.That(arguments, Does.Contain("--drop"));
                Assert.That(arguments, Does.Contain("--output=flex"));
                Assert.That(arguments, Does.Contain("--style=carto/flex.lua"));
                Assert.That(arguments, Does.Contain("--database=gis"));
                Assert.That(arguments, Does.Contain("--number-processes=6"));
                Assert.That(arguments, Does.Contain("--cache=100"));
                Assert.That(arguments, Does.Contain("--log-progress=true"));
                Assert.That(arguments.Any(a => a.StartsWith("--flat-nodes", StringComparison.Ordinal)), Is.False);
                Assert.That(arguments[^2], Is.EqualTo(Path.GetFullPath("a.osm.pbf")));
                Assert.That(arguments[^1], Is.EqualTo(Path.GetFullPath("b.osm.pbf")));
            });
        }

        [Test]
        public void CacheIsLimitedByMemory() {
            var arguments = OsmImporter.BuildOsm2pgsqlArguments("flex.lua", ["a.osm.pbf"], 6 * Gigabyte, new ImportOptions(), "nodes.bin", 8 * Gigabyte);
            Assert.That(arguments, Does.Contain("--cache=4096"));
        }

        [Test]
        public void LargeImportsUseFlatNodesWithoutCache() {
            var arguments = OsmImporter.BuildOsm2pgsqlArguments("flex.lua", ["planet.osm.pbf"], 80 * Gigabyte, new ImportOptions(), "nodes.bin", 128 * Gigabyte);
            Assert.Multiple(() => {
                Assert.That(arguments, Does.Contain("--flat-nodes=nodes.bin"));
                Assert.That(arguments, Does.Contain("--cache=0"));
            });
        }

        [Test]
        public void FlatNodesCanBeForced() {
            var arguments = OsmImporter.BuildOsm2pgsqlArguments("flex.lua", ["a.osm.pbf"], 1024, new ImportOptions { FlatNodes = true }, "nodes.bin", 8 * Gigabyte);
            Assert.That(arguments, Does.Contain("--flat-nodes=nodes.bin"));
        }

        [Test]
        public void StateMatchesOnlyCompletedIdenticalImports() {
            var state = new ImportState { Fingerprint = "f", CartoVersion = "v6.1.0", ExternalData = "2026-10", Completed = true };
            Assert.Multiple(() => {
                Assert.That(state.Matches("f", "v6.1.0", "2026-10"), Is.True);
                Assert.That(state.Matches("g", "v6.1.0", "2026-10"), Is.False);
                Assert.That(state.Matches("f", "v6.2.0", "2026-10"), Is.False);
                Assert.That(state.Matches("f", "v6.1.0", "2026-11"), Is.False);
                Assert.That((state with { Completed = false }).Matches("f", "v6.1.0", "2026-10"), Is.False);
                Assert.That(state.IsUsable("v6.1.0", "2026-10"), Is.True);
                Assert.That(state.IsUsable("v6.2.0", "2026-10"), Is.False);
                Assert.That(state.IsUsable("v6.1.0", "2026-11"), Is.False);
                Assert.That((state with { Completed = false }).IsUsable("v6.1.0", "2026-10"), Is.False);
                Assert.That((state with { Version = 0 }).IsUsable("v6.1.0", "2026-10"), Is.False);
            });
        }
    }
}
