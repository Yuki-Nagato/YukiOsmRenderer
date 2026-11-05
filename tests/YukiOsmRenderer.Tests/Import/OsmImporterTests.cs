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

        static readonly ImportInstallation Installation = new("v6.1.0", "lua1",
            new Dictionary<string, string> { ["indexes.sql"] = "i1", ["functions.sql"] = "f1", ["common-values.sql"] = "c1" }, "2026-10");

        [Test]
        public void DifferenceTellsWhatToUpdate() {
            var state = new ImportState {
                CartoVersion = "v6.1.0",
                ImportScript = "lua1",
                SqlScripts = Installation.SqlScripts,
                ExternalData = "2026-10",
                Completed = true,
            };
            var update = (state with {
                SqlScripts = new Dictionary<string, string>(Installation.SqlScripts) { ["functions.sql"] = "f0" },
                ExternalData = "2026-09",
            }).CompareWith(Installation);
            Assert.Multiple(() => {
                Assert.That(state.CompareWith(Installation).IsEmpty, Is.True);
                Assert.That(update.ImportScript, Is.False);
                Assert.That(update.SqlScripts, Is.EqualTo(new[] { "functions.sql" }));
                Assert.That(update.ExternalData, Is.True);
                Assert.That(update.CanUpdateInPlace, Is.True);
                Assert.That((state with { ImportScript = "lua0" }).CompareWith(Installation).ImportScript, Is.True);
                Assert.That((state with { Version = 0 }).CompareWith(Installation).ImportScript, Is.True);
                // Another OpenStreetMap Carto version with the same scripts changes nothing.
                Assert.That((state with { CartoVersion = "v6.0.0" }).CompareWith(Installation).IsEmpty, Is.True);
            });
        }

        [Test]
        public void StatesWithoutHashesAreComparedByCartoVersion() {
            var state = new ImportState { CartoVersion = "v6.1.0", ExternalData = "2026-10", Completed = true };
            var older = (state with { CartoVersion = "v6.0.0" }).CompareWith(Installation);
            Assert.Multiple(() => {
                Assert.That(state.CompareWith(Installation).IsEmpty, Is.True);
                Assert.That(older.ImportScript, Is.True);
                Assert.That(older.SqlScripts, Is.EqualTo(ImportInstallation.SqlScriptNames));
                Assert.That(older.ExternalData, Is.False);
            });
        }

        [Test]
        public void IndexesAreReplaced() {
            var sql = OsmImporter.ReplacingIndexes("-- indexes\nCREATE INDEX planet_osm_line_ferry ON planet_osm_line USING GIST (way);\n");
            Assert.That(sql, Is.EqualTo("-- indexes\nDROP INDEX IF EXISTS planet_osm_line_ferry;\nCREATE INDEX planet_osm_line_ferry ON planet_osm_line USING GIST (way);\n"));
        }
    }
}
