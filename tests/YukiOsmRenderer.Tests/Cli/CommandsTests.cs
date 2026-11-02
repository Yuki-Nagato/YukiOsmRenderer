using YukiOsmRenderer.Cli;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Tests.Cli {
    public class CommandsTests {
        RenderSettings? render;
        ServeSettings? serve;
        TilesSettings? tiles;

        async Task<int> InvokeAsync(params string[] args) {
            render = null;
            serve = null;
            tiles = null;
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
                Assert.That(render.Style, Is.EqualTo(StyleOptions.Default));
                Assert.That(render.MaxPixels, Is.EqualTo(RenderSettings.DefaultMaxPixels));
            });
        }

        [Test]
        public async Task RenderParsesAllOptions() {
            var code = await InvokeAsync("render",
                "-i", "beijing.osm.pbf", "--input", "tianjin.osm.pbf",
                "--zoom", "12", "--bbox", "115.4,39.4,117.5,41.1", "-o", "out.tif", "--scale", "2",
                "--cjk", "jp", "--data-dir", "data", "--home", "home", "--threads", "3", "--force-import", "-v",
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
                Assert.That(render.Style.Key, Is.EqualTo("transparent;keep=buildings,water-areas;no-outline;hide=amenity-points;cjk=jp"));
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
        [TestCase("serve", "-i", "a.osm.pbf", "--port", "70000")]
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
        public async Task RejectsInvalidArguments(params string[] args) {
            Assert.Multiple(async () => {
                Assert.That(await InvokeAsync(args), Is.Not.Zero);
                Assert.That(render, Is.Null);
                Assert.That(serve, Is.Null);
                Assert.That(tiles, Is.Null);
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
                Assert.That(tiles.Style, Is.EqualTo(StyleOptions.Default));
            });
        }

        [Test]
        public async Task TilesParsesAllOptions() {
            var code = await InvokeAsync("tiles", "-i", "beijing.osm.pbf", "-z", "10-14", "--bbox", "116.2,39.8,116.6,40.0", "--output", "tiles",
                "--template", "{z}/{x}-{y}.png", "--scale", "2", "--cjk", "tc", "--transparent", "--keep-fill", "water-areas", "--hide-outline",
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
                Assert.That(tiles.Style.Key, Is.EqualTo("transparent;keep=water-areas;no-outline;hide=buildings;cjk=tc"));
            });
        }

        [Test]
        public async Task SingleZoomTemplateNeedsNoZoomPlaceholder() {
            Assert.That(await InvokeAsync("tiles", "-z", "7", "-o", "out", "--template", "{x}_{y}.png"), Is.Zero);
            Assert.That(tiles!.Template.HasZoom, Is.False);
        }

        [Test]
        public async Task ServeParsesOptions() {
            Assert.That(await InvokeAsync("serve", "-i", "a.osm.pbf", "--host", "0.0.0.0", "-p", "9000", "--cache-size", "64", "--cjk", "kr", "--max-style-variants", "3"), Is.Zero);
            Assert.Multiple(() => {
                Assert.That(serve!.Host, Is.EqualTo("0.0.0.0"));
                Assert.That(serve.Port, Is.EqualTo(9000));
                Assert.That(serve.CacheMegabytes, Is.EqualTo(64));
                Assert.That(serve.MaxStyleVariants, Is.EqualTo(3));
                Assert.That(serve.Common.Cjk, Is.EqualTo(CjkVariant.Korean));
            });
        }

        [Test]
        public async Task ServeUsesDefaults() {
            Assert.That(await InvokeAsync("serve", "-i", "a.osm.pbf"), Is.Zero);
            Assert.Multiple(() => {
                Assert.That(serve!.Host, Is.EqualTo("localhost"));
                Assert.That(serve.Port, Is.EqualTo(8080));
                Assert.That(serve.CacheMegabytes, Is.EqualTo(512));
            });
        }

        [Test]
        public void FormatsSizes() {
            Assert.Multiple(() => {
                Assert.That(RenderRunner.FormatSize(512), Is.EqualTo("512 B"));
                Assert.That(RenderRunner.FormatSize(1536), Is.EqualTo("1.5 KB"));
                Assert.That(RenderRunner.FormatSize(5L * 1024 * 1024 * 1024), Is.EqualTo("5 GB"));
            });
        }
    }
}
