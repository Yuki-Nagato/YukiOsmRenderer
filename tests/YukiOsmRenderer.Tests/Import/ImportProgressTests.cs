using System.Diagnostics;
using System.Runtime.InteropServices;
using YukiOsmRenderer.Core.Import;

namespace YukiOsmRenderer.Tests.Import {
    public class ImportProgressTests {
        const string Processing = "Processing: Node(9612345k 1234.5k/s) Way(123456k 45.67k/s) Relation(0 0.0/s)";

        [DllImport("libc", EntryPoint = "lseek")]
        static extern long Lseek(int descriptor, long offset, int whence);

        static ImportProgress Create(ListLogger logger, TimeSpan interval) {
            // Nothing listens on the discard port, so the database activity is unknown.
            return new ImportProgress("osm2pgsql", logger, "Host=127.0.0.1;Port=9;Username=x;Password=x;Database=x;Timeout=1", Stopwatch.StartNew(), interval);
        }

        [Test]
        public async Task InterceptsOnlyProgressLines() {
            await using var progress = Create(new ListLogger(), TimeSpan.FromHours(1));
            Assert.Multiple(() => {
                Assert.That(progress.Intercept(Processing), Is.True);
                Assert.That(progress.Intercept("Left to process: 1234..."), Is.True);
                Assert.That(progress.Intercept(new string(' ', 90)), Is.True);
                Assert.That(progress.Intercept("2026-11-03 10:00:00  Storing properties to table '\"public\".\"osm2pgsql_properties\"'."), Is.False);
                Assert.That(progress.Intercept("2026-11-03 12:00:00  Going over 4000 pending relations (using 4 threads)"), Is.False);
                Assert.That(progress.Intercept("2026-11-03 12:30:00  Reading input files done in 2h 30m 1s."), Is.False);
            });
        }

        [Test]
        public void DescribesInputAndToolStatus() {
            var text = ImportProgress.Describe(Processing, 0, "", 25L << 30, 100L << 30, TimeSpan.FromHours(3), []);
            Assert.Multiple(() => {
                Assert.That(text, Is.EqualTo("input 25.0 % of 100 GB, about 3:00:00 left | " + Processing));
                Assert.That(ImportProgress.Describe(Processing, 0, "", 25L << 30, 100L << 30, null, []), Is.EqualTo("input 25.0 % of 100 GB | " + Processing));
                Assert.That(ImportProgress.Describe(Processing, 0, "", 101L << 30, 100L << 30, TimeSpan.FromHours(3), []), Is.EqualTo("input 100.0 % of 100 GB | " + Processing));
            });
        }

        [TestCase("Processing: Node(5260k 1753.3k/s) Way(0k 0.00k/s) Relation(0 0.0/s)", 0)]
        [TestCase("Processing: Node(6597k 942.4k/s) Way(525k 75.00k/s) Relation(0 0.0/s)", 1)]
        [TestCase("Processing: Node(6597k 942.4k/s) Way(761k 76.10k/s) Relation(9950 3316.7/s)", 2)]
        public void RecognizesTheElementTypeBeingRead(string status, int expected) {
            Assert.That(ImportProgress.ElementType(status), Is.EqualTo(expected));
        }

        [Test]
        public void DescribesPendingObjects() {
            var text = ImportProgress.Describe("Left to process: 1000...", 4000, "relations", null, 0, null, []);
            Assert.That(text, Is.EqualTo("pending relations 75.0 % (1,000 of 4,000 left)"));
        }

        [Test]
        public void DescribesDatabaseTasks() {
            DatabaseTask[] tasks = [
                new(TimeSpan.FromMinutes(42), "CREATE INDEX ON planet_osm_polygon USING GIST (way)", "building index: scanning table", 0.35, null),
                new(TimeSpan.FromSeconds(75), "COPY public.water_polygons FROM stdin", null, null, 1234567),
                new(TimeSpan.FromSeconds(10), "ANALYZE", null, null, null),
                new(TimeSpan.FromSeconds(10), "ANALYZE planet_osm_line", null, null, null),
            ];
            var text = ImportProgress.Describe(null, 0, "", null, 0, null, tasks);
            Assert.Multiple(() => {
                Assert.That(text, Is.EqualTo(
                    "CREATE INDEX ON planet_osm_polygon USING GIST (way) (building index: scanning table 35.0 %, 42:00) | "
                    + "COPY public.water_polygons FROM stdin (1,234,567 rows, 1:15) | ANALYZE (0:10) | 1 more statements"));
                Assert.That(ImportProgress.Describe(null, 0, "", null, 0, null, []), Is.EqualTo("working"));
            });
        }

        [Test]
        public async Task LogsTheLatestStatusAtIntervals() {
            var logger = new ListLogger();
            await using (var progress = Create(logger, TimeSpan.FromMilliseconds(100))) {
                progress.Intercept(Processing);
                var timeout = Stopwatch.StartNew();
                while (logger.Messages.Count == 0 && timeout.Elapsed < TimeSpan.FromSeconds(10)) {
                    await Task.Delay(50);
                }
            }
            Assert.That(logger.Messages, Is.Not.Empty);
            Assert.That(logger.Messages[0], Does.Match(@"^Import \d+:\d\d, osm2pgsql \d+:\d\d: Processing: Node\("));
        }

        [Test]
        public void MeasuresHowMuchOfTheInputWasRead() {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) {
                Assert.Ignore("Only supported on Linux and Windows.");
            }
            var path = Path.Combine(Path.GetTempPath(), $"yukiosm-input-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(path, new byte[10000]);
            try {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
                file.ReadExactly(new byte[1000]);
                if (OperatingSystem.IsLinux()) {
                    // .NET reads with pread, which leaves the position of the descriptor alone, unlike osm2pgsql's read.
                    Assert.That(Lseek((int)file.SafeFileHandle.DangerousGetHandle(), 1000, 0), Is.EqualTo(1000));
                }
                var read = ImportProgress.InputBytesRead(Process.GetCurrentProcess(), [Path.GetFullPath(path)]);
                if (OperatingSystem.IsLinux()) {
                    Assert.That(read, Is.EqualTo(1000));
                }
                else {
                    Assert.That(read, Is.GreaterThanOrEqualTo(1000), "All bytes the process has read.");
                }
            }
            finally {
                File.Delete(path);
            }
        }
    }
}
