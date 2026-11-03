using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core.Engine;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Runtime;
using YukiOsmRenderer.Core.Styling;
using YukiOsmRenderer.Core.Tiff;
using YukiOsmRenderer.Core.Vector;
using YukiOsmRenderer.Server;
using LibTiff = BitMiracle.LibTiff.Classic.Tiff;

namespace YukiOsmRenderer.Tests.Integration {
    /// <summary>
    /// End-to-end tests of the vector engine against a complete installation (YUKIOSM_HOME) and the Beijing and
    /// Tianjin extracts in testdata/. Building the vector tiles takes under a minute; they are kept in the vector/
    /// directory of the test data directory.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    [NonParallelizable]
    public class VectorEndToEndTests {
        // Tiananmen Square and surroundings.
        static readonly GeoBoundingBox Center = new(116.37, 39.89, 116.42, 39.92);

        ILoggerFactory loggerFactory = null!;
        VectorEnvironment environment = null!;
        VectorEnvironmentOptions options = null!;
        string outputDirectory = null!;

        [OneTimeSetUp]
        public async Task StartEnvironment() {
            var home = Environment.GetEnvironmentVariable(RuntimeLayout.HomeEnvironmentVariable);
            var beijing = EndToEndTests.FindTestData("beijing-*.osm.pbf");
            var tianjin = EndToEndTests.FindTestData("tianjin-*.osm.pbf");
            if (string.IsNullOrEmpty(home) || beijing == null || tianjin == null) {
                Assert.Ignore($"Set {RuntimeLayout.HomeEnvironmentVariable} to a complete installation and download the test data (testdata/README.md).");
            }
            outputDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "integration-output", "vector");
            Directory.CreateDirectory(outputDirectory);
            loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
            options = new VectorEnvironmentOptions {
                InputFiles = [beijing!, tianjin!],
                Home = home,
                DataDirectory = Environment.GetEnvironmentVariable("YUKIOSM_TEST_DATA_DIR") ?? Path.Combine(Path.GetTempPath(), "yukiosm-integration"),
                Threads = Math.Min(Environment.ProcessorCount, 8),
            };
            environment = await VectorEnvironment.StartAsync(options, loggerFactory);
        }

        [OneTimeTearDown]
        public async Task StopEnvironment() {
            if (environment != null) {
                await environment.DisposeAsync();
            }
            loggerFactory?.Dispose();
        }

        async Task<byte[]> RenderAsync(string name, StyleOptions style, int zoom, GeoBoundingBox box) {
            var window = PixelWindow.FromBoundingBox(box, zoom, 256);
            var path = Path.Combine(outputDirectory, name);
            using var lease = environment.AcquireRenderer(style);
            await using (var file = File.Create(path))
            using (var writer = new GeoTiffWriter(file, AreaRenderer.CreateTiffOptions(window, style.Transparent, environment.TemporaryDirectory))) {
                await AreaRenderer.RenderAsync(lease.Renderer, window, TileFormat.Png32, writer, environment.Threads, loggerFactory.CreateLogger("test"));
            }
            TestContext.AddTestAttachment(path);
            using var tiff = LibTiff.Open(path, "r");
            return EndToEndTests.ReadAllPixels(tiff, out _);
        }

        [Test]
        public void ReusesTheBuiltTiles() {
            var state = Path.Combine(options.DataDirectory, "vector", "state.json");
            var built = File.GetLastWriteTimeUtc(state);

            Assert.That(environment.Tiles.Header.MaxZoom, Is.EqualTo(14));
            Assert.That(environment.Tiles.Header.MinLongitude, Is.LessThan(116.0));
            Assert.That(environment.Tiles.Header.MaxLongitude, Is.GreaterThan(117.5), "Tianjin is included.");
            Assert.DoesNotThrowAsync(async () => {
                await using var again = await VectorEnvironment.StartAsync(options, loggerFactory);
            });
            Assert.That(File.GetLastWriteTimeUtc(state), Is.EqualTo(built));
        }

        [Test]
        public async Task RendersOpaqueGeoTiff() {
            var pixels = await RenderAsync("beijing-z14.tif", StyleOptions.Default, 14, Center);
            var colors = new HashSet<int>();
            for (var i = 0; i < pixels.Length; i += 3) {
                colors.Add(pixels[i] << 16 | pixels[i + 1] << 8 | pixels[i + 2]);
            }
            Assert.That(colors, Has.Count.GreaterThan(500), "A city map has many colors (roads, labels, areas).");
        }

        [Test]
        public async Task RendersTransparentGeoTiffKeepingSelectedFills() {
            var style = StyleOptions.Default with { Transparent = true };
            var transparent = await RenderAsync("beijing-z14-transparent.tif", style, 14, Center);
            var water = await RenderAsync("beijing-z14-water.tif", style with { KeepFillLayers = new SortedSet<string>(["water-*"], StringComparer.Ordinal) }, 14, Center);

            static (long Transparent, long Opaque) Count(byte[] pixels) {
                long transparent = 0, opaque = 0;
                for (var i = 3; i < pixels.Length; i += 4) {
                    if (pixels[i] == 0) {
                        transparent++;
                    }
                    else if (pixels[i] == 255) {
                        opaque++;
                    }
                }
                return (transparent, opaque);
            }

            var plain = Count(transparent);
            var withWater = Count(water);
            Assert.Multiple(() => {
                Assert.That(plain.Transparent, Is.GreaterThan(transparent.Length / 4 / 4), "Most of the background is transparent.");
                Assert.That(plain.Opaque, Is.GreaterThan(1000), "Roads and labels are drawn.");
                Assert.That(withWater.Opaque, Is.GreaterThan(plain.Opaque), "The moats and lakes around the Forbidden City are filled.");
            });
        }

        [Test]
        public void ThemesAndCjkVariantsChangeTheRendering() {
            var metatile = Metatile.Containing(15, 26979, 12416);
            byte[] Render(StyleOptions style) {
                var size = metatile.PixelSize(256);
                var rgba = new byte[size * size * 4];
                using var lease = environment.AcquireRenderer(style);
                lease.Renderer.RenderMetatile(metatile, 256, rgba);
                return rgba;
            }

            var classic = Render(StyleOptions.Default);
            var dark = Render(StyleOptions.Default with { Theme = "colorful-dark" });
            var japanese = Render(StyleOptions.Default with { Cjk = CjkVariant.Japanese });
            var large = Render(StyleOptions.Default with { TextScale = 1.5 });

            static double Brightness(byte[] rgba) {
                long sum = 0;
                for (var i = 0; i < rgba.Length; i += 4) {
                    sum += rgba[i] + rgba[i + 1] + rgba[i + 2];
                }
                return sum / (rgba.Length / 4 * 3.0);
            }

            static long Differences(byte[] a, byte[] b) {
                long count = 0;
                for (var i = 0; i < a.Length; i += 4) {
                    if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) {
                        count++;
                    }
                }
                return count;
            }

            Assert.Multiple(() => {
                Assert.That(Brightness(classic), Is.GreaterThan(150));
                Assert.That(Brightness(dark), Is.LessThan(100));
                // Labels only: glyph shapes of the same characters differ between the variants.
                Assert.That(Differences(classic, japanese), Is.GreaterThan(100).And.LessThan(classic.Length / 4 / 10));
                Assert.That(Differences(classic, large), Is.GreaterThan(Differences(classic, japanese)));
            });
        }

        [Test]
        public async Task RendersConcurrently() {
            // Metatiles of several style variants at once, as a busy server does.
            var styles = new[] { StyleOptions.Default, StyleOptions.Default with { Theme = "gray" } };
            var renders = Enumerable.Range(0, 3 * environment.Threads).Select(i => Task.Run(() => {
                var metatile = Metatile.Containing(14, 13480 + 8 * (i % 4), 6200 + 8 * (i / 4));
                var rgba = new byte[metatile.PixelSize(256) * metatile.PixelSize(256) * 4];
                using var lease = environment.AcquireRenderer(styles[i % 2]);
                lease.Renderer.RenderMetatile(metatile, 256, rgba);
                return rgba.Any(b => b != 0);
            })).ToArray();

            var all = Task.WhenAll(renders);
            Assert.That(await Task.WhenAny(all, Task.Delay(TimeSpan.FromMinutes(3))), Is.SameAs(all), "Concurrent renders finish.");
            Assert.That(all.Result, Is.All.True);
        }

        [Test]
        public async Task ServesTilesAndVectorTiles() {
            await using var app = TileServer.Build(new TileServerOptions { WebDirectory = environment.Layout.WebDirectory }, environment,
                builder => builder.WebHost.UseTestServer());
            await app.StartAsync();
            using var client = app.GetTestClient();

            var png = await client.GetByteArrayAsync("/tiles/13/6743/3104.png?theme=natural");
            using var request = new HttpRequestMessage(HttpMethod.Get, "/vector/13/6743/3104.pbf");
            request.Headers.AcceptEncoding.ParseAdd("gzip");
            var vectorTile = await client.SendAsync(request);
            var style = await client.GetStringAsync("/vector/styles/classic.json?transparent=1");
            var tileJson = await client.GetStringAsync("/vector/tiles.json");
            var empty = await client.GetAsync("/vector/13/0/0.pbf");

            Assert.Multiple(async () => {
                Assert.That(png.AsSpan(1, 3).SequenceEqual("PNG"u8), Is.True);
                Assert.That(vectorTile.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(vectorTile.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "gzip" }));
                Assert.That((await vectorTile.Content.ReadAsByteArrayAsync()).Length, Is.GreaterThan(1000));
                Assert.That(style, Does.Contain("\"http://localhost/vector/tiles.json\""));
                Assert.That(style, Does.Contain("http://localhost/vector/glyphs/{fontstack}/{range}.pbf"));
                Assert.That(style, Does.Not.Contain("file:"));
                Assert.That(tileJson, Does.Contain("\"vector_layers\""));
                Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(await client.GetStringAsync("/themes"), Does.Contain("colorful-dark"));
            });
        }
    }
}
