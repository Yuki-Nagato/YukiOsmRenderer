using Microsoft.Extensions.Logging.Abstractions;
using YukiOsmRenderer.Core.Runtime;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Tests.Styling {
    public class CartoCompilerTests {
        string directory = null!;

        [SetUp]
        public void SetUp() {
            directory = Path.Combine(Path.GetTempPath(), "yukiosm-tests", Guid.NewGuid().ToString("N"));
            var style = Path.Combine(directory, "assets", "carto", "style");
            Directory.CreateDirectory(style);
            File.WriteAllText(Path.Combine(directory, "assets", "carto", "project.mml"), MmlProjectTests.SampleProject);
            File.WriteAllText(Path.Combine(style, "fonts.mss"), "@book-fonts: \"Noto Sans Regular\", \"Noto Sans CJK JP Regular\";");
            File.WriteAllText(Path.Combine(style, "style.mss"), "Map { background-color: #f2efe9; }");
        }

        [TearDown]
        public void TearDown() {
            Directory.Delete(directory, true);
        }

        [Test]
        public void ReadsSourcesWithTheCjkVariantApplied() {
            var compiler = new CartoCompiler(RuntimeLayout.Resolve(directory), Path.Combine(directory, "cache"), NullLogger.Instance);
            var sources = compiler.ReadSources(CjkVariant.TraditionalChinese).ToDictionary(s => s.RelativePath.Replace('\\', '/'), s => s.Content);
            Assert.Multiple(() => {
                Assert.That(sources.Keys, Is.EqualTo(new[] { "project.mml", "style/fonts.mss", "style/style.mss" }));
                Assert.That(sources["style/fonts.mss"], Does.Contain("Noto Sans CJK TC Regular"));
                Assert.That(sources["style/style.mss"], Is.EqualTo("Map { background-color: #f2efe9; }"));
            });
        }

        [Test]
        public async Task UsesTheCacheWithoutRunningCarto() {
            var layout = RuntimeLayout.Resolve(directory);
            var cache = Path.Combine(directory, "cache");
            var compiler = new CartoCompiler(layout, cache, NullLogger.Instance);
            // No node/carto exists in this layout, so a compile attempt fails.
            Assert.That(async () => await compiler.CompileAsync(CjkVariant.Korean), Throws.Exception);
            Assert.That(Directory.GetDirectories(cache), Is.Empty, "The work directory must be removed.");

            // Pretend a previous run compiled the style.
            var key = CartoCompiler.ComputeKey(compiler.ReadSources(CjkVariant.Korean), layout.ReadManifest());
            await File.WriteAllTextAsync(Path.Combine(cache, $"carto-kr-{key}.xml"), "<Map/>");
            Assert.That(await compiler.CompileAsync(CjkVariant.Korean), Is.EqualTo("<Map/>"));
        }
    }
}
