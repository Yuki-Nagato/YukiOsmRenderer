using Microsoft.Extensions.Logging.Abstractions;
using YukiOsmRenderer.Core.Database;
using YukiOsmRenderer.Core.Runtime;

namespace YukiOsmRenderer.Tests.Runtime {
    public class RuntimeTests {
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

        [Test]
        public void LayoutResolvesPathsBelowTheHome() {
            var layout = RuntimeLayout.Resolve(directory);
            Assert.Multiple(() => {
                Assert.That(layout.Root, Is.EqualTo(Path.GetFullPath(directory)));
                Assert.That(layout.PostgresExecutable("initdb"), Does.StartWith(Path.Combine(layout.RuntimeDirectory, "pgsql", "bin", "initdb")));
                Assert.That(layout.CartoProjectFile, Is.EqualTo(Path.Combine(directory, "assets", "carto", "project.mml")));
                Assert.That(layout.MapnikPluginsDirectory, Is.Null);
                Assert.That(layout.ProjDataDirectory, Is.Null);
                Assert.That(layout.ReadManifest().MapnikApi, Is.EqualTo("3.0.22"));
            });
        }

        [Test]
        public void LayoutReportsMissingFiles() {
            var layout = RuntimeLayout.Resolve(directory);
            Assert.That(layout.Validate, Throws.TypeOf<FileNotFoundException>().With.Message.Contains("osm2pgsql"));
        }

        [Test]
        public void LayoutFindsProjDataInPostGisBundles() {
            var proj = Path.Combine(directory, "runtime", "pgsql", "share", "contrib", "postgis-3.6", "proj");
            Directory.CreateDirectory(proj);
            File.WriteAllText(Path.Combine(proj, "proj.db"), "");
            Assert.That(RuntimeLayout.Resolve(directory).ProjDataDirectory, Is.EqualTo(proj));
        }

        [Test]
        public void LayoutReadsTheManifest() {
            Directory.CreateDirectory(Path.Combine(directory, "assets"));
            File.WriteAllText(Path.Combine(directory, "assets", "manifest.json"), """{ "openStreetMapCarto": "v6.1.0", "carto": "1.2.0", "externalData": "2026-10-30" }""");
            var manifest = RuntimeLayout.Resolve(directory).ReadManifest();
            Assert.Multiple(() => {
                Assert.That(manifest.OpenStreetMapCarto, Is.EqualTo("v6.1.0"));
                Assert.That(manifest.Carto, Is.EqualTo("1.2.0"));
                Assert.That(manifest.ExternalData, Is.EqualTo("2026-10-30"));
                Assert.That(manifest.MapnikApi, Is.EqualTo("3.0.22"));
            });
        }

        [Test]
        public void DataDirectoryIsExclusive() {
            using var data = DataDirectory.Open(directory);
            Assert.Multiple(() => {
                Assert.That(Directory.Exists(data.TemporaryDirectory), Is.True);
                Assert.That(Directory.Exists(data.StylesDirectory), Is.True);
                Assert.That(() => DataDirectory.Open(directory), Throws.TypeOf<IOException>().With.Message.Contains("in use"));
            });
        }

        [Test]
        public void DataDirectoryClearsTemporaryFiles() {
            using (var data = DataDirectory.Open(directory)) {
                File.WriteAllText(Path.Combine(data.TemporaryDirectory, "left-over"), "");
            }
            using (var data = DataDirectory.Open(directory)) {
                Assert.That(Directory.GetFiles(data.TemporaryDirectory), Is.Empty);
            }
        }

        [Test]
        public async Task ProcessRunnerCapturesOutput() {
            var result = await ProcessRunner.RunAsync(new ProcessSpec("dotnet", ["--version"]) { CaptureOutput = true }, NullLogger.Instance);
            Assert.That(result.StandardOutput.Trim(), Does.Match(@"^\d+\.\d+\.\d+"));
        }

        [Test]
        public async Task ProcessRunnerHandsOutTheProcessAndItsLines() {
            var lines = new List<string>();
            var processId = 0;
            await ProcessRunner.RunAsync(new ProcessSpec("dotnet", ["--version"]) {
                InterceptLine = line => {
                    lock (lines) {
                        lines.Add(line);
                    }
                    return true;
                },
                Started = process => processId = process.Id,
            }, NullLogger.Instance);
            Assert.Multiple(() => {
                Assert.That(processId, Is.Not.Zero);
                Assert.That(lines, Has.Some.Match(@"^\d+\.\d+\.\d+"));
            });
        }

        [Test]
        public void ProcessRunnerReportsFailures() {
            Assert.That(async () => await ProcessRunner.RunAsync(new ProcessSpec("dotnet", ["no-such-command-yukiosm"]), NullLogger.Instance),
                Throws.TypeOf<ProcessFailedException>().With.Property(nameof(ProcessFailedException.ExitCode)).Not.Zero);
        }
    }
}
