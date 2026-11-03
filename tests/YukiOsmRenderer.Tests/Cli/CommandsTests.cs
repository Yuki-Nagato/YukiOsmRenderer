using YukiOsmRenderer.Cli;
using YukiOsmRenderer.Core.Engine;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Tests.Cli {
    public class CommandsTests {
        RenderSettings? render;
        ServeSettings? serve;
        TilesSettings? tiles;
        ImportSettings? import;

        async Task<int> InvokeAsync(params string[] args) {
            render = null;
            serve = null;
            tiles = null;
            import = null;
            var root = Commands.Create(
                (settings, _) => {
                    render = settings;
                    return Task.FromResult(0);
                },
                (settings, _) => {
                    serve = settings;
                    return Task.FromResult(0);
                },
                (settings, _) => {
                    tiles = settings;
                    return Task.FromResult(0);
                },
                (settings, _) => {
                    import = settings;
                    return Task.FromResult(0);
                });
            var configuration = new System.CommandLine.InvocationConfiguration { Output = TextWriter.Null, Error = TextWriter.Null };
            return await root.Parse(args).InvokeAsync(configuration);
        }

        [Test]
        public async Task RenderUsesDefaults() {
            Assert.That(await InvokeAsync("render", "-i", "a.osm.pbf", "-z", "5"), Is.Zero);
            Assert.That(render, Is.Not.Null);
            Assert.Multiple(() => {
                Assert.That(render!.Common.Inputs, Is.EqualTo(new[] { "a.osm.pbf" }));
                Assert.That(render.Common.Cjk, Is.EqualTo(CjkVariant.SimplifiedChinese));
                Assert.That(render.Common.Threads, Is.EqualTo(Environment.ProcessorCount));
                Assert.That(render.Zoom, Is.EqualTo(5));
                Assert.That(render.BoundingBox, Is.EqualTo(GeoBoundingBox.World));
                Assert.That(render.Output, Is.EqualTo(RenderSettings.StandardOutput));
                Assert.That(render.TileSize, Is.EqualTo(256));
                Assert.That(render.Format, Is.EqualTo(TileFormat.Png256));
                Assert.That(render.Style, Is.EqualTo(StyleOptions.Default));
                Assert.That(render.MaxPixels, Is.EqualTo(RenderSettings.DefaultMaxPixels));
            });
        }

        [Test]
        public async Task RenderParsesAllOptions() {
            var code = await InvokeAsync("render",
                "-i", "beijing.osm.pbf", "--input", "tianjin.osm.pbf",
                "--zoom", "12", "--bbox", "115.4,39.4,117.5,41.1", "-o", "out.tif", "--scale", "2", "--format", "png32",
                "--cjk", "jp", "--text-scale", "1.25", "--data-dir", "data", "--home", "home", "--threads", "3", "--force-import", "-v",
                "--transparent", "--keep-fill", "water-areas,buildings", "--hide-outline", "--hide-layer", "amenity-points",
                "--max-pixels", "1000");
            Assert.That(code, Is.Zero);
            Assert.Multiple(() => {
                Assert.That(render!.Common.Inputs, Is.EqualTo(new[] { "beijing.osm.pbf", "tianjin.osm.pbf" }));
                Assert.That(render.Common.Cjk, Is.EqualTo(CjkVariant.Japanese));
                Assert.That(render.Common.DataDirectory, Is.EqualTo("data"));
                Assert.That(render.Common.Home, Is.EqualTo("home"));
                Assert.That(render.Common.Threads, Is.EqualTo(3));
                Assert.That(render.Common.ForceImport, Is.True);
                Assert.That(render.Common.Verbose, Is.True);
                Assert.That(render.Zoom, Is.EqualTo(12));
                Assert.That(render.BoundingBox, Is.EqualTo(new GeoBoundingBox(115.4, 39.4, 117.5, 41.1)));
                Assert.That(render.Output, Is.EqualTo("out.tif"));
                Assert.That(render.TileSize, Is.EqualTo(512));
                Assert.That(render.Format, Is.EqualTo(TileFormat.Png32));
                Assert.That(render.Style.Key, Is.EqualTo("transparent;keep=buildings,water-areas;no-outline;hide=amenity-points;cjk=jp;text=1.25"));
                Assert.That(render.MaxPixels, Is.EqualTo(1000));
            });
        }

        [Test]
        public async Task MultipleInputsCanFollowOneOption() {
            Assert.That(await InvokeAsync("render", "-i", "a.osm.pbf", "b.osm.pbf", "-z", "1"), Is.Zero);
            Assert.That(render!.Common.Inputs, Is.EqualTo(new[] { "a.osm.pbf", "b.osm.pbf" }));
        }

        [Test]
        public async Task InputCanBeOmitted() {
            Assert.That(await InvokeAsync("render", "-z", "5"), Is.Zero);
            Assert.That(await InvokeAsync("serve"), Is.Zero);
            Assert.That(serve!.Common.Inputs, Is.Empty);
        }

        [TestCase("render", "-z", "5", "--force-import")]
        [TestCase("render", "-i", "-z", "5")]
        [TestCase("render", "-i", "a.osm.pbf")]
        [TestCase("render", "-i", "a.osm.pbf", "-z", "23")]
        [TestCase("render", "-i", "a.osm.pbf", "-z", "5", "--bbox", "1,2,3")]
        [TestCase("render", "-i", "a.osm.pbf", "-z", "5", "--bbox", "170,0,-170,10")]
        [TestCase("render", "-i", "a.osm.pbf", "-z", "5", "--cjk", "cn")]
        [TestCase("render", "-i", "a.osm.pbf", "-z", "5", "--scale", "3")]
        [TestCase("render", "-i", "a.osm.pbf", "-z", "5", "--threads", "0")]
        [TestCase("render", "-i", "a.osm.pbf", "-z", "5", "--format", "jpeg")]
        [TestCase("render", "-z", "5", "--text-scale", "4")]
        [TestCase("render", "-z", "5", "--text-scale", "1,5")]
        [TestCase("tiles", "-z", "5", "-o", "out", "--text-scale", "0.1")]
        [TestCase("serve", "--text-scale", "big")]
        [TestCase("serve", "-i", "a.osm.pbf", "--port", "70000")]
        [TestCase("serve", "--format", "png8")]
        [TestCase("tiles", "-z", "5")]
        [TestCase("tiles", "-o", "out")]
        [TestCase("tiles", "-z", "14-10", "-o", "out")]
        [TestCase("tiles", "-z", "10-23", "-o", "out")]
        [TestCase("tiles", "-z", "1-2-3", "-o", "out")]
        [TestCase("tiles", "-z", "x", "-o", "out")]
        [TestCase("tiles", "-z", "5", "-o", "out", "--template", "{z}/{x}.png")]
        [TestCase("tiles", "-z", "5", "-o", "out", "--template", "{z}/{x}/{y}/{s}.png")]
        [TestCase("tiles", "-z", "5-6", "-o", "out", "--template", "{x}/{y}.png")]
        [TestCase("tiles", "-z", "5-6", "-o", "out", "--template", "{z}/{x}/{y.png")]
        [TestCase("tiles", "-z", "5", "-o", "out", "--scale", "3")]
        [TestCase("serve", "--theme", "classic")]
        [TestCase("serve", "--engine", "mapnik")]
        [TestCase("serve", "--engine", "carto", "--pmtiles", "a.pmtiles")]
        [TestCase("serve", "--pmtiles", "a.pmtiles", "-i", "a.osm.pbf")]
        [TestCase("serve", "--engine", "vector", "--schema", "s.yml")]
        [TestCase("serve", "--engine", "vector", "-i", "a.osm.pbf", "--max-data-zoom", "16")]
        [TestCase("serve", "--engine", "vector", "-i", "a.osm.pbf", "--planetiler-arg", "novalue")]
        [TestCase("serve", "--engine", "vector", "--public-url", "ftp://example.com/")]
        [TestCase("render", "-z", "5", "-i", "a.osm.pbf", "--max-data-zoom", "14")]
        [TestCase("import")]
        [TestCase("import", "-i", "a.osm.pbf", "-o", "a.pmtiles")]
        [TestCase("import", "-i", "a.osm.pbf", "--schema", "s.yml")]
        public async Task RejectsInvalidArguments(params string[] args) {
            Assert.Multiple(async () => {
                Assert.That(await InvokeAsync(args), Is.Not.Zero);
                Assert.That(render, Is.Null);
                Assert.That(serve, Is.Null);
                Assert.That(tiles, Is.Null);
                Assert.That(import, Is.Null);
            });
        }

        [Test]
        public async Task CartoIsTheDefaultEngine() {
            Assert.That(await InvokeAsync("serve"), Is.Zero);
            Assert.Multiple(() => {
                Assert.That(serve!.Common.Engine, Is.EqualTo(EngineKind.Carto));
                Assert.That(serve.Common.PmTiles, Is.Null);
                Assert.That(serve.Common.Theme, Is.EqualTo("classic"));
                Assert.That(serve.PublicUrl, Is.Null);
            });
        }

        [Test]
        public async Task ServeParsesVectorOptions() {
            Assert.That(await InvokeAsync("serve", "--engine", "vector", "-i", "a.osm.pbf", "b.osm.pbf", "--theme", "colorful", "--max-data-zoom", "15",
                "--schema", "my.yml", "--planetiler-arg", "min_feature_size=0.5", "--planetiler-arg", "simplify_tolerance=0.05", "--import-memory", "24",
                "--public-url", "https://example.com/map/", "--threads", "6", "--force-import"), Is.Zero);
            var common = serve!.Common;
            Assert.Multiple(() => {
                Assert.That(common.Engine, Is.EqualTo(EngineKind.Vector));
                Assert.That(common.Inputs, Is.EqualTo(new[] { "a.osm.pbf", "b.osm.pbf" }));
                Assert.That(common.Theme, Is.EqualTo("colorful"));
                Assert.That(common.VectorBuild.MaxZoom, Is.EqualTo(15));
                Assert.That(common.VectorBuild.Schema, Is.EqualTo("my.yml"));
                Assert.That(common.VectorBuild.PlanetilerArguments, Is.EqualTo(new[] { "min_feature_size=0.5", "simplify_tolerance=0.05" }));
                Assert.That(common.VectorBuild.MemoryMegabytes, Is.EqualTo(24 * 1024));
                Assert.That(common.VectorBuild.Force, Is.True);
                Assert.That(serve.PublicUrl, Is.EqualTo("https://example.com/map/"));
            });
        }

        [Test]
        public async Task PmTilesImplyTheVectorEngine() {
            Assert.That(await InvokeAsync("tiles", "--pmtiles", "world.pmtiles", "-z", "3", "-o", "out", "--transparent", "--keep-fill", "water-*"), Is.Zero);
            Assert.Multiple(() => {
                Assert.That(tiles!.Common.Engine, Is.EqualTo(EngineKind.Vector));
                Assert.That(tiles.Common.PmTiles, Is.EqualTo("world.pmtiles"));
                Assert.That(tiles.Style.KeepFillLayers, Is.EqualTo(new[] { "water-*" }));
            });
        }

        [Test]
        public async Task ImportParsesOptions() {
            Assert.That(await InvokeAsync("import", "-i", "planet.osm.pbf", "--engine", "vector", "-o", "planet.pmtiles", "--max-data-zoom", "13"), Is.Zero);
            Assert.Multiple(() => {
                Assert.That(import!.Common.Engine, Is.EqualTo(EngineKind.Vector));
                Assert.That(import.Common.Inputs, Is.EqualTo(new[] { "planet.osm.pbf" }));
                Assert.That(import.Output, Is.EqualTo("planet.pmtiles"));
                Assert.That(import.Common.VectorBuild.MaxZoom, Is.EqualTo(13));
            });
            Assert.That(await InvokeAsync("import", "-i", "a.osm.pbf", "--data-dir", "data"), Is.Zero);
            Assert.Multiple(() => {
                Assert.That(import!.Common.Engine, Is.EqualTo(EngineKind.Carto));
                Assert.That(import.Output, Is.Null);
            });
        }

        [Test]
        public async Task TilesUsesDefaults() {
            Assert.That(await InvokeAsync("tiles", "-z", "12", "-o", "out"), Is.Zero);
            Assert.Multiple(() => {
                Assert.That(tiles!.Common.Inputs, Is.Empty);
                Assert.That(tiles.MinZoom, Is.EqualTo(12));
                Assert.That(tiles.MaxZoom, Is.EqualTo(12));
                Assert.That(tiles.BoundingBox, Is.EqualTo(GeoBoundingBox.World));
                Assert.That(tiles.OutputDirectory, Is.EqualTo("out"));
                Assert.That(tiles.Template.Text, Is.EqualTo("z{z}/y{y}/x{x}.png"));
                Assert.That(tiles.TileSize, Is.EqualTo(256));
                Assert.That(tiles.Format, Is.EqualTo(TileFormat.Png256));
                Assert.That(tiles.Style, Is.EqualTo(StyleOptions.Default));
            });
        }

        [Test]
        public async Task TilesParsesAllOptions() {
            var code = await InvokeAsync("tiles", "-i", "beijing.osm.pbf", "-z", "10-14", "--bbox", "116.2,39.8,116.6,40.0", "--output", "tiles",
                "--template", "{z}/{x}-{y}.png", "--scale", "2", "--format", "PNG32", "--cjk", "tc", "--text-scale", "0.8", "--transparent", "--keep-fill", "water-areas", "--hide-outline",
                "--hide-layer", "buildings");
            Assert.That(code, Is.Zero);
            Assert.Multiple(() => {
                Assert.That(tiles!.Common.Inputs, Is.EqualTo(new[] { "beijing.osm.pbf" }));
                Assert.That(tiles.MinZoom, Is.EqualTo(10));
                Assert.That(tiles.MaxZoom, Is.EqualTo(14));
                Assert.That(tiles.BoundingBox, Is.EqualTo(new GeoBoundingBox(116.2, 39.8, 116.6, 40.0)));
                Assert.That(tiles.OutputDirectory, Is.EqualTo("tiles"));
                Assert.That(tiles.Template.Format(10, 843, 387), Is.EqualTo("10/843-387.png"));
                Assert.That(tiles.TileSize, Is.EqualTo(512));
                Assert.That(tiles.Format, Is.EqualTo(TileFormat.Png32));
                Assert.That(tiles.Style.Key, Is.EqualTo("transparent;keep=water-areas;no-outline;hide=buildings;cjk=tc;text=0.8"));
            });
        }

        [Test]
        public async Task SingleZoomTemplateNeedsNoZoomPlaceholder() {
            Assert.That(await InvokeAsync("tiles", "-z", "7", "-o", "out", "--template", "{x}_{y}.png"), Is.Zero);
            Assert.That(tiles!.Template.HasZoom, Is.False);
        }

        [Test]
        public async Task ServeParsesOptions() {
            Assert.That(await InvokeAsync("serve", "-i", "a.osm.pbf", "--host", "0.0.0.0", "-p", "9000", "--cache-size", "64", "--cjk", "kr", "--max-style-variants", "3",
                "--format", "png32", "--text-scale", "1.5"), Is.Zero);
            Assert.Multiple(() => {
                Assert.That(serve!.Host, Is.EqualTo("0.0.0.0"));
                Assert.That(serve.Port, Is.EqualTo(9000));
                Assert.That(serve.CacheMegabytes, Is.EqualTo(64));
                Assert.That(serve.MaxStyleVariants, Is.EqualTo(3));
                Assert.That(serve.Common.Cjk, Is.EqualTo(CjkVariant.Korean));
                Assert.That(serve.Common.TextScale, Is.EqualTo(1.5));
                Assert.That(serve.DefaultFormat, Is.EqualTo(TileFormat.Png32));
            });
        }

        [Test]
        public async Task ServeUsesDefaults() {
            Assert.That(await InvokeAsync("serve", "-i", "a.osm.pbf"), Is.Zero);
            Assert.Multiple(() => {
                Assert.That(serve!.Host, Is.EqualTo("localhost"));
                Assert.That(serve.Port, Is.EqualTo(8080));
                Assert.That(serve.CacheMegabytes, Is.EqualTo(512));
                Assert.That(serve.Common.TextScale, Is.EqualTo(1));
                Assert.That(serve.DefaultFormat, Is.EqualTo(TileFormat.Png256));
            });
        }
    }
}
