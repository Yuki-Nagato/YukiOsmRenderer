using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using YukiOsmRenderer.Core.Engine;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Styling;
using YukiOsmRenderer.Core.Vector;
using YukiOsmRenderer.Server;
using YukiOsmRenderer.Tests.Rendering;

namespace YukiOsmRenderer.Tests.Server {
    public class VectorEndpointsTests {
        static readonly byte[] Tile = [0x1a, 0x03, 0x0a, 0x01, 0x78];

        sealed class FakeVectorProvider(string assets) : IRendererProvider, IVectorTileProvider {
            readonly FakeRendererProvider inner = new("water-ocean", "building");

            public List<(StyleOptions Options, string BaseUrl)> WebStyles { get; } = [];

            public IReadOnlyCollection<string> LayerIds => inner.LayerIds;

            public IReadOnlyList<string> Themes { get; } = ["classic", "colorful"];

            public string AssetsDirectory => assets;

            public void Validate(StyleOptions options) {
                inner.Validate(options);
            }

            public IRendererLease AcquireRenderer(StyleOptions options) {
                return inner.AcquireRenderer(options);
            }

            public (byte[] Data, PmTilesCompression Compression)? GetVectorTile(int zoom, long x, long y) {
                return (zoom, x, y) switch {
                    (1, 0, 0) => (Gzip(Tile), PmTilesCompression.Gzip),
                    (1, 1, 0) => (Tile, PmTilesCompression.None),
                    _ => null,
                };
            }

            public string TileJson(string baseUrl) {
                return $$"""{"tiles":["{{baseUrl}}vector/{z}/{x}/{y}.pbf"]}""";
            }

            public string WebStyle(StyleOptions options, string baseUrl) {
                if (!Themes.Contains(options.Theme ?? "classic")) {
                    throw new ArgumentException($"Unknown theme '{options.Theme}'.");
                }
                options.Validate(LayerIds);
                WebStyles.Add((options, baseUrl));
                return """{"version":8}""";
            }
        }

        static byte[] Gzip(byte[] data) {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest)) {
                gzip.Write(data);
            }
            return output.ToArray();
        }

        FakeVectorProvider provider = null!;
        WebApplication app = null!;
        HttpClient client = null!;
        string directory = null!;

        async Task Start(string? publicUrl = null) {
            app = TileServer.Build(new TileServerOptions { WebDirectory = directory, PublicUrl = publicUrl }, provider, builder => builder.WebHost.UseTestServer());
            await app.StartAsync();
            client = app.GetTestClient();
        }

        [SetUp]
        public void SetUp() {
            directory = Path.Combine(Path.GetTempPath(), "yukiosm-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory, "glyphs", "noto_sans_regular"));
            Directory.CreateDirectory(Path.Combine(directory, "glyphs", "noto_sans_bold"));
            Directory.CreateDirectory(Path.Combine(directory, "sprites"));
            File.WriteAllBytes(Path.Combine(directory, "glyphs", "noto_sans_regular", "0-255.pbf"), [1]);
            File.WriteAllBytes(Path.Combine(directory, "glyphs", "noto_sans_bold", "256-511.pbf"), [2]);
            File.WriteAllText(Path.Combine(directory, "sprites", "base@2x.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "secret.txt"), "secret");
            provider = new FakeVectorProvider(directory);
        }

        [TearDown]
        public async Task TearDown() {
            client?.Dispose();
            if (app != null) {
                await app.DisposeAsync();
            }
            Directory.Delete(directory, true);
        }

        [Test]
        public async Task ServesThemesAndTileJsonWithTheRequestUrl() {
            await Start();

            Assert.That(await client.GetFromJsonAsync<string[]>("/themes"), Is.EqualTo(new[] { "classic", "colorful" }));
            Assert.That(await client.GetStringAsync("/vector/tiles.json"), Does.Contain("\"http://localhost/vector/{z}/{x}/{y}.pbf\""));
        }

        [Test]
        public async Task PublicUrlReplacesTheRequestUrl() {
            await Start("https://maps.example.com/osm");

            Assert.That(await client.GetStringAsync("/vector/tiles.json"), Does.Contain("\"https://maps.example.com/osm/vector/{z}/{x}/{y}.pbf\""));
            await client.GetStringAsync("/vector/styles/colorful.json");
            Assert.That(provider.WebStyles.Single().BaseUrl, Is.EqualTo("https://maps.example.com/osm/"));
        }

        [Test]
        public async Task ServesGzippedTilesAsStoredWhenAccepted() {
            await Start();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/vector/1/0/0.pbf");
            request.Headers.AcceptEncoding.ParseAdd("gzip, br");

            var response = await client.SendAsync(request);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "gzip" }));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/vnd.mapbox-vector-tile"));
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Gzip(Tile)));
        }

        [TestCase("/vector/1/0/0.pbf")]
        [TestCase("/vector/1/1/0.pbf")]
        public async Task ServesUncompressedTilesOtherwise(string url) {
            await Start();

            var response = await client.GetAsync(url);

            Assert.That(response.Content.Headers.ContentEncoding, Is.Empty);
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Tile));
        }

        [TestCase("/vector/1/1/1.pbf", HttpStatusCode.NoContent)]
        [TestCase("/vector/1/2/0.pbf", HttpStatusCode.NotFound)]
        [TestCase("/vector/1/0/0.png", HttpStatusCode.NotFound)]
        [TestCase("/vector/1/0/-1.pbf", HttpStatusCode.NotFound)]
        [TestCase("/vector/styles/classic.txt", HttpStatusCode.NotFound)]
        [TestCase("/vector/styles/unknown.json", HttpStatusCode.BadRequest)]
        [TestCase("/vector/styles/classic.json?transparent=1&keepFill=lakes", HttpStatusCode.BadRequest)]
        [TestCase("/vector/glyphs/noto_sans_regular/0-255.txt", HttpStatusCode.NotFound)]
        [TestCase("/vector/glyphs/..%2F..%2Fsecret/0-255.pbf", HttpStatusCode.OK)]
        [TestCase("/vector/sprites/..%2Fsecret.txt", HttpStatusCode.NotFound)]
        [TestCase("/vector/sprites/missing.png", HttpStatusCode.NotFound)]
        public async Task RejectsInvalidRequests(string url, HttpStatusCode status) {
            await Start();

            var response = await client.GetAsync(url);

            Assert.That(response.StatusCode, Is.EqualTo(status));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("secret"));
        }

        [Test]
        public async Task StylesTakeTheThemeFromTheFileAndOptionsFromTheQuery() {
            await Start();

            var response = await client.GetAsync("/vector/styles/colorful.json?transparent=1&keepFill=water-ocean&textScale=1.5");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            var (options, baseUrl) = provider.WebStyles.Single();
            Assert.That(options.Theme, Is.EqualTo("colorful"));
            Assert.That(options.Transparent, Is.True);
            Assert.That(options.KeepFillLayers, Is.EqualTo(new[] { "water-ocean" }));
            Assert.That(options.TextScale, Is.EqualTo(1.5));
            Assert.That(baseUrl, Is.EqualTo("http://localhost/"));
        }

        [Test]
        public async Task ServesGlyphsFromTheFirstFontThatHasThem() {
            await Start();

            Assert.That(await client.GetByteArrayAsync("/vector/glyphs/noto_sans_regular/0-255.pbf"), Is.EqualTo(new byte[] { 1 }));
            Assert.That(await client.GetByteArrayAsync("/vector/glyphs/noto_sans_regular,noto_sans_bold/256-511.pbf"), Is.EqualTo(new byte[] { 2 }));
            // CJK ranges are not bundled: an empty glyph set.
            var cjk = await client.GetAsync("/vector/glyphs/noto_sans_regular/19968-20223.pbf");
            Assert.That(cjk.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await cjk.Content.ReadAsByteArrayAsync(), Is.Empty);
            Assert.That(await client.GetStringAsync("/vector/sprites/base@2x.json"), Is.EqualTo("{}"));
        }
    }
}
