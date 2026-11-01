using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core.Engine;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Runtime;
using YukiOsmRenderer.Core.Styling;
using YukiOsmRenderer.Core.Tiff;
using LibTiff = BitMiracle.LibTiff.Classic.Tiff;

namespace YukiOsmRenderer.Tests.Integration {
    /// <summary>Checks that the CJK variant changes the glyph forms of labels. Skipped like <see cref="EndToEndTests"/>.</summary>
    [TestFixture]
    [Category("Integration")]
    [NonParallelizable]
    public class CjkVariantTests {
        // Around Dongzhimen: "直" has different Simplified Chinese and Japanese forms.
        static readonly GeoBoundingBox Area = new(116.43310546875, 39.93922484079195, 116.4385986328125, 39.943436461974215);

        static async Task<byte[]> RenderAsync(CjkVariant variant, string home, string[] inputs, string dataDirectory, string output) {
            using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
            await using var environment = await RenderEnvironment.StartAsync(new EnvironmentOptions {
                InputFiles = inputs,
                Home = home,
                DataDirectory = dataDirectory,
                Cjk = variant,
                Threads = 2,
            }, loggerFactory);
            var window = PixelWindow.FromBoundingBox(Area, 16);
            var path = Path.Combine(output, $"cjk-{variant.ShortName()}.tif");
            using (var lease = environment.AcquireRenderer(StyleOptions.Default))
            await using (var file = File.Create(path))
            using (var writer = new GeoTiffWriter(file, AreaRenderer.CreateTiffOptions(window, false, environment.TemporaryDirectory))) {
                await AreaRenderer.RenderAsync(lease.Renderer, window, writer, environment.Threads, loggerFactory.CreateLogger("test"));
            }
            using var tiff = LibTiff.Open(path, "r");
            return EndToEndTests.ReadAllPixels(tiff, out _);
        }

        [Test]
        public async Task VariantsChangeGlyphForms() {
            var home = Environment.GetEnvironmentVariable(RuntimeLayout.HomeEnvironmentVariable);
            var beijing = EndToEndTests.FindTestData("beijing-*.osm.pbf");
            var tianjin = EndToEndTests.FindTestData("tianjin-*.osm.pbf");
            if (string.IsNullOrEmpty(home) || beijing == null || tianjin == null) {
                Assert.Ignore($"Set {RuntimeLayout.HomeEnvironmentVariable} to a complete installation and download the test data (testdata/README.md).");
            }
            var dataDirectory = Environment.GetEnvironmentVariable("YUKIOSM_TEST_DATA_DIR") ?? Path.Combine(Path.GetTempPath(), "yukiosm-integration");
            var output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "integration-output");
            Directory.CreateDirectory(output);
            string[] inputs = [beijing!, tianjin!];

            var simplified = await RenderAsync(CjkVariant.SimplifiedChinese, home!, inputs, dataDirectory, output);
            var japanese = await RenderAsync(CjkVariant.Japanese, home!, inputs, dataDirectory, output);
            var traditional = await RenderAsync(CjkVariant.TraditionalChinese, home!, inputs, dataDirectory, output);
            Assert.Multiple(() => {
                Assert.That(japanese, Is.Not.EqualTo(simplified));
                Assert.That(traditional, Is.Not.EqualTo(simplified));
                Assert.That(traditional, Is.Not.EqualTo(japanese));
            });
        }
    }
}
