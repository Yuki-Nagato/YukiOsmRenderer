using Microsoft.Extensions.Logging.Abstractions;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Rendering;

namespace YukiOsmRenderer.Tests.Rendering {
    public class TileSetRendererTests {
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

        static GeoBoundingBox TileAlignedBox(int zoom, long x, long y, long columns, long rows) {
            var extent = WebMercator.TileRangeExtent(zoom, x, y, columns, rows);
            return new GeoBoundingBox(
                WebMercator.XToLongitude(extent.MinX), WebMercator.YToLatitude(extent.MinY),
                WebMercator.XToLongitude(extent.MaxX), WebMercator.YToLatitude(extent.MaxY));
        }

        [TestCase(256)]
        [TestCase(512)]
        public async Task WritesEveryTileOfTheAreaFromItsMetatile(int tileSize) {
            // Tiles 846-848 x 391-392 at zoom 10 and the 6 x 4 tiles below them at zoom 11; each level spans 2 x 2 metatiles.
            var box = TileAlignedBox(10, 846, 391, 3, 2);
            var renderer = new FakeRenderer();
            var template = TilePathTemplate.Parse("{z}/{x}-{y}.png");
            var result = await TileSetRenderer.RenderAsync(renderer, box, 10, 11, tileSize, TileFormat.Png32, directory, template, 4, NullLogger.Instance);

            var expected = new List<(int Zoom, long X, long Y)>();
            for (var x = 846; x <= 848; x++) {
                for (var y = 391; y <= 392; y++) {
                    expected.Add((10, x, y));
                }
            }
            for (var x = 1692; x <= 1697; x++) {
                for (var y = 782; y <= 785; y++) {
                    expected.Add((11, x, y));
                }
            }
            var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal);
            Assert.Multiple(() => {
                Assert.That(result.Tiles, Is.EqualTo(expected.Count));
                Assert.That(result.TotalTiles, Is.EqualTo(expected.Count));
                Assert.That(result.Bytes, Is.EqualTo(expected.Count * 16), "The fake renderer encodes a tile into 16 bytes.");
                Assert.That(result.IsComplete, Is.True);
                Assert.That(renderer.Formats, Is.EqualTo(new[] { TileFormat.Png32 }));
                Assert.That(TileSetRenderer.CountTiles(box, 10, 11), Is.EqualTo(expected.Count));
                Assert.That(files, Is.EqualTo(expected.Select(tile => template.Format(tile.Zoom, tile.X, tile.Y)).Order(StringComparer.Ordinal)));
                Assert.That(renderer.Renders, Is.EqualTo(8));
            });
            foreach (var (zoom, x, y) in expected) {
                var content = File.ReadAllBytes(Path.Combine(directory, template.Format(zoom, x, y)));
                var (r, g, b, a) = FakeRenderer.Pixel(x * tileSize, y * tileSize);
                Assert.Multiple(() => {
                    Assert.That(content[..4], Is.EqualTo(new[] { r, g, b, a }), $"First pixel of {zoom}/{x}/{y}");
                    Assert.That(BitConverter.ToInt32(content, 4), Is.EqualTo(tileSize));
                    Assert.That(BitConverter.ToInt32(content, 12), Is.EqualTo(WebMercator.MetatileSize * tileSize * 4), "Stride");
                });
            }
        }

        [Test]
        public async Task WrittenTilesMatchTheCount() {
            var box = new GeoBoundingBox(116.3, 39.85, 116.45, 39.95);
            var written = (await TileSetRenderer.RenderAsync(new FakeRenderer(), box, 0, 13, 256, TileFormat.Png256, directory, TilePathTemplate.Default, 4,
                NullLogger.Instance)).Tiles;
            Assert.Multiple(() => {
                Assert.That(written, Is.EqualTo(TileSetRenderer.CountTiles(box, 0, 13)));
                Assert.That(Directory.GetFiles(directory, "*.png", SearchOption.AllDirectories), Has.Length.EqualTo(written));
                Assert.That(File.Exists(Path.Combine(directory, "z0", "y0", "x0.png")), Is.True);
            });
        }

        [Test]
        public void FormatsTemplates() {
            Assert.Multiple(() => {
                Assert.That(TilePathTemplate.Default.Format(14, 13487, 6208), Is.EqualTo("z14/y6208/x13487.png"));
                Assert.That(TilePathTemplate.Default.HasZoom, Is.True);
                Assert.That(TilePathTemplate.Parse("{z}/{x}-{y}.png").Format(3, 6, 2), Is.EqualTo("3/6-2.png"));
                Assert.That(TilePathTemplate.Parse("tiles/{y}/{x}@2x.png").Format(3, 6, 2), Is.EqualTo("tiles/2/6@2x.png"));
                Assert.That(TilePathTemplate.Parse("{x}_{y}_{x}.png").Format(3, 6, 2), Is.EqualTo("6_2_6.png"));
                Assert.That(TilePathTemplate.Parse("{x}_{y}.png").HasZoom, Is.False);
            });
        }

        [TestCase("")]
        [TestCase("/tiles/{z}/{x}/{y}.png")]
        [TestCase("../{z}/{x}/{y}.png")]
        [TestCase("{z}/{x}/../{y}.png")]
        [TestCase("{z}/{x}.png")]
        [TestCase("{z}/{y}.png")]
        [TestCase("{z}/{x}/{y}/")]
        [TestCase("{z}/{x}/{y.png")]
        [TestCase("{z}/{x}}/{y}.png")]
        [TestCase("{z}/{x}/{-y}.png")]
        [TestCase("{z}/{X}/{y}.png")]
        public void RejectsInvalidTemplates(string text) {
            Assert.That(() => TilePathTemplate.Parse(text), Throws.TypeOf<FormatException>());
        }
    }
}
