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

namespace YukiOsmRenderer.Tests.Integration {
    /// <summary>Checks that the CJK variant changes the glyph forms of labels. Skipped like <see cref="EndToEndTests"/>.</summary>
    [TestFixture]
    [Category("Integration")]
    [NonParallelizable]
    public class CjkVariantTests {
        // Around Dongzhimen: "直" has different Simplified Chinese, Traditional Chinese and Japanese forms.
        static readonly GeoBoundingBox Area = new(116.43310546875, 39.93922484079195, 116.4385986328125, 39.943436461974215);
        const string Tile = "/tiles/16/53964/24824.png";

        ILoggerFactory loggerFactory = null!;
        RenderEnvironment environment = null!;

        [OneTimeSetUp]
        public async Task StartEnvironment() {
            var home = Environment.GetEnvironmentVariable(RuntimeLayout.HomeEnvironmentVariable);
            var beijing = EndToEndTests.FindTestData("beijing-*.osm.pbf");
            var tianjin = EndToEndTests.FindTestData("tianjin-*.osm.pbf");
            if (string.IsNullOrEmpty(home) || beijing == null || tianjin == null) {
                Assert.Ignore($"Set {RuntimeLayout.HomeEnvironmentVariable} to a complete installation and download the test data (testdata/README.md).");
            }
            loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
            environment = await RenderEnvironment.StartAsync(new EnvironmentOptions {
                InputFiles = [beijing!, tianjin!],
                Home = home,
                DataDirectory = Environment.GetEnvironmentVariable("YUKIOSM_TEST_DATA_DIR") ?? Path.Combine(Path.GetTempPath(), "yukiosm-integration"),
                Threads = 2,
            }, loggerFactory);
        }

        [OneTimeTearDown]
        public async Task StopEnvironment() {
            if (environment != null) {
                await environment.DisposeAsync();
            }
            loggerFactory?.Dispose();
        }

        async Task<byte[]> RenderAsync(CjkVariant variant) {
            var window = PixelWindow.FromBoundingBox(Area, 16);
            var output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "integration-output");
            Directory.CreateDirectory(output);
            var path = Path.Combine(output, $"cjk-{variant.ShortName()}.tif");
            using (var lease = environment.AcquireRenderer(StyleOptions.Create(false, cjk: variant)))
            await using (var file = File.Create(path))
            using (var writer = new GeoTiffWriter(file, AreaRenderer.CreateTiffOptions(window, false, environment.TemporaryDirectory))) {
                await AreaRenderer.RenderAsync(lease.Renderer, window, TileFormat.Png32, writer, environment.Threads, loggerFactory.CreateLogger("test"));
            }
            using var tiff = LibTiff.Open(path, "r");
            return EndToEndTests.ReadAllPixels(tiff, out _);
        }

        [Test]
        public async Task VariantsChangeGlyphForms() {
            var simplified = await RenderAsync(CjkVariant.SimplifiedChinese);
            var japanese = await RenderAsync(CjkVariant.Japanese);
            var traditional = await RenderAsync(CjkVariant.TraditionalChinese);
            Assert.Multiple(() => {
                Assert.That(japanese, Is.Not.EqualTo(simplified));
                Assert.That(traditional, Is.Not.EqualTo(simplified));
                Assert.That(traditional, Is.Not.EqualTo(japanese));
            });
        }

        [Test]
        public async Task ServerSelectsTheVariantPerRequest() {
            await using var app = TileServer.Build(new TileServerOptions { DefaultCjk = CjkVariant.Japanese }, environment,
                builder => builder.WebHost.UseTestServer());
            await app.StartAsync();
            using var client = app.GetTestClient();
            var defaultTile = await client.GetByteArrayAsync(Tile);
            var japanese = await client.GetByteArrayAsync(Tile + "?cjk=jp");
            var simplified = await client.GetByteArrayAsync(Tile + "?cjk=sc");
            Assert.Multiple(() => {
                Assert.That(defaultTile, Is.EqualTo(japanese), "Requests without cjk use the server default.");
                Assert.That(simplified, Is.Not.EqualTo(japanese));
            });
        }
    }
}
