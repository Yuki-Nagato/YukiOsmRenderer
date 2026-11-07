namespace YukiElevation.Tests {
    public class ServiceOptionsTests {
        static readonly string Existing = Path.GetTempPath();

        [Test]
        public void ParsesAllOptions() {
            var options = ServiceOptions.Parse(["--gebco", Existing, $"--glo30={Existing}", "--host", "0.0.0.0", "--port", "9000",
                "--max-points", "50", "--cache-mb", "64", "--max-open-tiles", "8"]);

            Assert.That(options, Is.EqualTo(new ServiceOptions {
                Gebco = Path.GetFullPath(Existing),
                Glo30 = Path.GetFullPath(Existing),
                Host = "0.0.0.0",
                Port = 9000,
                MaxPoints = 50,
                CacheMegabytes = 64,
                MaxOpenTiles = 8,
            }));
        }

        [Test]
        public void HasDefaults() {
            var options = ServiceOptions.Parse(["--gebco", Existing])!;

            Assert.That((options.Glo30, options.Host, options.Port, options.MaxPoints), Is.EqualTo(((string?)null, "localhost", 8100, 1000)));
        }

        [TestCase("--help")]
        [TestCase("-h")]
        public void ReturnsNullForHelp(string help) {
            Assert.That(ServiceOptions.Parse(["--gebco", Existing, help]), Is.Null);
        }

        [TestCase(new string[0], "at least one")]
        [TestCase(new[] { "--gebco" }, "needs a value")]
        [TestCase(new[] { "--gebco", "/does/not/exist/yukielevation" }, "does not exist")]
        [TestCase(new[] { "--glo30", ".", "--port", "70000" }, "--port")]
        [TestCase(new[] { "--glo30", ".", "--max-points", "x" }, "--max-points")]
        [TestCase(new[] { "--glo30", ".", "--verbose" }, "Unknown option")]
        public void RejectsInvalidCommandLines(string[] args, string message) {
            var e = Assert.Throws<ArgumentException>(() => ServiceOptions.Parse(args));
            Assert.That(e!.Message, Does.Contain(message));
        }
    }
}
