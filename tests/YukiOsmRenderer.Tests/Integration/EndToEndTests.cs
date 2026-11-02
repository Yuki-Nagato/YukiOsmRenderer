using System.Buffers.Binary;
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
using YukiOsmRenderer.Server;
using LibTiff = BitMiracle.LibTiff.Classic.Tiff;
using TiffTag = BitMiracle.LibTiff.Classic.TiffTag;

namespace YukiOsmRenderer.Tests.Integration {
    /// <summary>
    /// End-to-end tests against a complete installation (YUKIOSM_HOME) and the Beijing and Tianjin extracts in
    /// testdata/. They import the data once, which takes several minutes, and are skipped when either is missing.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    [NonParallelizable]
    public class EndToEndTests {
        // Tiananmen Square and surroundings.
        static readonly GeoBoundingBox Center = new(116.37, 39.89, 116.42, 39.92);

        ILoggerFactory loggerFactory = null!;
        RenderEnvironment environment = null!;
        string dataDirectory = null!;
        string outputDirectory = null!;

        internal static string? FindTestData(string pattern) {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null) {
                var testData = Path.Combine(directory.FullName, "testdata");
                if (Directory.Exists(testData)) {
                    return Directory.GetFiles(testData, pattern).Order(StringComparer.Ordinal).LastOrDefault();
                }
                directory = directory.Parent;
            }
            return null;
        }

        [OneTimeSetUp]
        public async Task StartEnvironment() {
            var home = Environment.GetEnvironmentVariable(RuntimeLayout.HomeEnvironmentVariable);
            var beijing = FindTestData("beijing-*.osm.pbf");
            var tianjin = FindTestData("tianjin-*.osm.pbf");
            if (string.IsNullOrEmpty(home) || beijing == null || tianjin == null) {
                Assert.Ignore($"Set {RuntimeLayout.HomeEnvironmentVariable} to a complete installation and download the test data (testdata/README.md).");
            }
            dataDirectory = Environment.GetEnvironmentVariable("YUKIOSM_TEST_DATA_DIR")
                ?? Path.Combine(Path.GetTempPath(), "yukiosm-integration");
            outputDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "integration-output");
            Directory.CreateDirectory(outputDirectory);
            loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
            environment = await RenderEnvironment.StartAsync(new EnvironmentOptions {
                InputFiles = [beijing!, tianjin!],
                Home = home,
                DataDirectory = dataDirectory,
                Threads = Math.Min(Environment.ProcessorCount, 8),
            }, loggerFactory);
        }

        [OneTimeTearDown]
        public async Task StopEnvironment() {
            if (environment != null) {
                await environment.DisposeAsync();
            }
            loggerFactory?.Dispose();
        }

        static (int Width, int Height) PngSize(byte[] png) {
            Assert.That(png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), Is.True, "PNG signature");
            return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        }

        async Task<string> RenderAsync(string name, StyleOptions style, int zoom, GeoBoundingBox box, int tileSize = 256) {
            var window = PixelWindow.FromBoundingBox(box, zoom, tileSize);
            var path = Path.Combine(outputDirectory, name);
            using var lease = environment.AcquireRenderer(style);
            await using (var file = File.Create(path))
            using (var writer = new GeoTiffWriter(file, AreaRenderer.CreateTiffOptions(window, style.Transparent, environment.TemporaryDirectory))) {
                await AreaRenderer.RenderAsync(lease.Renderer, window, writer, environment.Threads, loggerFactory.CreateLogger("test"));
            }
            TestContext.AddTestAttachment(path);
            return path;
        }

        internal static byte[] ReadAllPixels(LibTiff tiff, out int channels) {
            var width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
            var height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
            channels = tiff.GetField(TiffTag.SAMPLESPERPIXEL)[0].ToInt();
            var tileSize = tiff.GetField(TiffTag.TILEWIDTH)[0].ToInt();
            var pixels = new byte[width * height * channels];
            var tile = new byte[tiff.TileSize()];
            for (var y = 0; y < height; y += tileSize) {
                for (var x = 0; x < width; x += tileSize) {
                    tiff.ReadTile(tile, 0, x, y, 0, 0);
                    for (var row = 0; row < tileSize && y + row < height; row++) {
                        var count = Math.Min(tileSize, width - x) * channels;
                        Array.Copy(tile, row * tileSize * channels, pixels, ((y + row) * width + x) * channels, count);
                    }
                }
            }
            return pixels;
        }

        [Test]
        public async Task RendersOpaqueGeoTiff() {
            var path = await RenderAsync("beijing-z14.tif", StyleOptions.Default, 14, Center);
            using var tiff = LibTiff.Open(path, "r");
            var pixels = ReadAllPixels(tiff, out var channels);
            Assert.That(channels, Is.EqualTo(3));
            var colors = new HashSet<int>();
            for (var i = 0; i < pixels.Length; i += 3) {
                colors.Add(pixels[i] << 16 | pixels[i + 1] << 8 | pixels[i + 2]);
            }
            Assert.That(colors, Has.Count.GreaterThan(500), "A city map has many colors (roads, labels, areas).");
        }

        [Test]
        public async Task RendersTransparentGeoTiff() {
            var path = await RenderAsync("beijing-z14-transparent.tif", StyleOptions.Create(true), 14, Center);
            using var tiff = LibTiff.Open(path, "r");
            var pixels = ReadAllPixels(tiff, out var channels);
            Assert.That(channels, Is.EqualTo(4));
            long transparent = 0;
            long opaque = 0;
            for (var i = 3; i < pixels.Length; i += 4) {
                if (pixels[i] == 0) {
                    transparent++;
                }
                else if (pixels[i] == 255) {
                    opaque++;
                }
            }
            Assert.Multiple(() => {
                Assert.That(transparent, Is.GreaterThan(pixels.Length / 4 / 4), "Most of the background is transparent.");
                Assert.That(opaque, Is.GreaterThan(1000), "Roads and labels are drawn.");
            });
        }

        [Test]
        public async Task RendersCombinedExtractsAtLowZoom() {
            // Beijing and Tianjin together, including the coast of the Bohai Sea.
            var path = await RenderAsync("beijing-tianjin-z9.tif", StyleOptions.Default, 9, new GeoBoundingBox(115.4, 38.5, 118.1, 41.1));
            using var tiff = LibTiff.Open(path, "r");
            Assert.That(tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt(), Is.GreaterThan(400));
        }

        [Test]
        public async Task ServesTiles() {
            await using var app = TileServer.Build(new TileServerOptions { WebDirectory = environment.Layout.WebDirectory }, environment,
                builder => builder.WebHost.UseTestServer());
            await app.StartAsync();
            using var client = app.GetTestClient();
            var standard = await client.GetByteArrayAsync("/tiles/13/6743/3104.png");
            var retina = await client.GetByteArrayAsync("/tiles/13/6743/3104@2x.png");
            var transparent = await client.GetByteArrayAsync("/tiles/13/6743/3104.png?transparent=1&keepFill=water-areas");
            Assert.Multiple(async () => {
                Assert.That(PngSize(standard), Is.EqualTo((256, 256)));
                Assert.That(PngSize(retina), Is.EqualTo((512, 512)));
                Assert.That(PngSize(transparent), Is.EqualTo((256, 256)));
                Assert.That(transparent, Is.Not.EqualTo(standard));
                Assert.That(await client.GetStringAsync("/"), Does.Contain("leaflet"));
            });
        }

        [Test]
        public async Task WritesTileFilesEqualToServedTiles() {
            var tileDirectory = Path.Combine(outputDirectory, "tiles");
            var template = TilePathTemplate.Parse("{z}/{x}-{y}.png");
            long written;
            using (var lease = environment.AcquireRenderer(StyleOptions.Default)) {
                written = (await TileSetRenderer.RenderAsync(lease.Renderer, Center, 13, 14, 256, tileDirectory, template, environment.Threads, loggerFactory.CreateLogger("test"))).Tiles;
            }
            Assert.That(Directory.GetFiles(tileDirectory, "*.png", SearchOption.AllDirectories), Has.Length.EqualTo(written));
            Assert.That(written, Is.EqualTo(TileSetRenderer.CountTiles(Center, 13, 14)));

            var x = (long)(WebMercator.XToPixel(WebMercator.LongitudeToX(116.4), 14) / 256);
            var y = (long)(WebMercator.YToPixel(WebMercator.LatitudeToY(39.9), 14) / 256);
            var file = await File.ReadAllBytesAsync(Path.Combine(tileDirectory, template.Format(14, x, y)));
            await using var app = TileServer.Build(new TileServerOptions { WebDirectory = environment.Layout.WebDirectory }, environment,
                builder => builder.WebHost.UseTestServer());
            await app.StartAsync();
            using var client = app.GetTestClient();
            var served = await client.GetByteArrayAsync($"/tiles/14/{x}/{y}.png");
            Assert.Multiple(() => {
                Assert.That(PngSize(file), Is.EqualTo((256, 256)));
                Assert.That(file, Is.EqualTo(served));
            });
        }
    }
}
