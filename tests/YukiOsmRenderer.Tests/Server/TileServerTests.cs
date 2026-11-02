using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Primitives;
using Microsoft.AspNetCore.Http;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Styling;
using YukiOsmRenderer.Server;
using YukiOsmRenderer.Tests.Rendering;

namespace YukiOsmRenderer.Tests.Server {
    public class TileServerTests {
        FakeRendererProvider provider = null!;
        WebApplication app = null!;
        HttpClient client = null!;
        string webDirectory = null!;

        [SetUp]
        public async Task SetUp() {
            webDirectory = Path.Combine(Path.GetTempPath(), "yukiosm-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(webDirectory);
            await File.WriteAllTextAsync(Path.Combine(webDirectory, "index.html"), "<html>preview</html>");
            provider = new FakeRendererProvider("water-areas", "buildings", "roads-fill");
            app = TileServer.Build(new TileServerOptions { WebDirectory = webDirectory }, provider, builder => builder.WebHost.UseTestServer());
            await app.StartAsync();
            client = app.GetTestClient();
        }

        [TearDown]
        public async Task TearDown() {
            client.Dispose();
            await app.DisposeAsync();
            Directory.Delete(webDirectory, true);
        }

        static (byte R, byte G, int Width, int Stride) Decode(byte[] tile) {
            return (tile[0], tile[1], BitConverter.ToInt32(tile, 4), BitConverter.ToInt32(tile, 12));
        }

        [Test]
        public async Task ServesTilesFromRenderedMetatiles() {
            var response = await client.GetAsync("/tiles/12/3372/1552.png");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.Multiple(() => {
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/png"));
                Assert.That(response.Headers.CacheControl?.MaxAge, Is.EqualTo(TimeSpan.FromHours(1)));
            });
            var (r, g, width, stride) = Decode(await response.Content.ReadAsByteArrayAsync());
            var expected = FakeRenderer.Pixel(3372 * 256, 1552 * 256);
            Assert.Multiple(() => {
                Assert.That(r, Is.EqualTo(expected.R));
                Assert.That(g, Is.EqualTo(expected.G));
                Assert.That(width, Is.EqualTo(256));
                Assert.That(stride, Is.EqualTo(8 * 256 * 4));
            });

            // Another tile of the same metatile comes from the cache.
            var neighbour = await client.GetByteArrayAsync("/tiles/12/3375/1559.png");
            var expectedNeighbour = FakeRenderer.Pixel(3375 * 256, 1559 * 256);
            Assert.Multiple(() => {
                Assert.That(Decode(neighbour).R, Is.EqualTo(expectedNeighbour.R));
                Assert.That(Decode(neighbour).G, Is.EqualTo(expectedNeighbour.G));
                Assert.That(provider.Renderers["default"].Renders, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task ServesHighResolutionTiles() {
            var tile = await client.GetByteArrayAsync("/tiles/3/2/5@2x.png");
            var (r, g, width, stride) = Decode(tile);
            var expected = FakeRenderer.Pixel(2 * 512, 5 * 512);
            Assert.Multiple(() => {
                Assert.That(width, Is.EqualTo(512));
                Assert.That(stride, Is.EqualTo(8 * 512 * 4));
                Assert.That(r, Is.EqualTo(expected.R));
                Assert.That(g, Is.EqualTo(expected.G));
            });
        }

        [Test]
        public async Task ConcurrentRequestsShareOneRender() {
            var requests = Enumerable.Range(0, 8).SelectMany(x => Enumerable.Range(0, 8).Select(y => client.GetAsync($"/tiles/10/{x}/{y}.png")));
            var responses = await Task.WhenAll(requests);
            Assert.Multiple(() => {
                Assert.That(responses.Select(r => r.StatusCode), Is.All.EqualTo(HttpStatusCode.OK));
                Assert.That(provider.Renderers["default"].Renders, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task SelectsStyleVariantsFromTheQuery() {
            var response = await client.GetAsync("/tiles/5/1/1.png?transparent=1&keepFill=water-areas&keepFill=buildings&hideOutline=true&hideLayers=roads-fill");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(provider.Renderers.Keys, Is.EqualTo(new[] { "transparent;keep=buildings,water-areas;no-outline;hide=roads-fill" }));
            await client.GetAsync("/tiles/5/1/1.png?transparent");
            Assert.That(provider.Renderers.Keys, Does.Contain("transparent"));
        }

        [TestCase("/tiles/5/1/1.png?keepFill=lakes&transparent=1", HttpStatusCode.BadRequest)]
        [TestCase("/tiles/5/32/1.png", HttpStatusCode.NotFound)]
        [TestCase("/tiles/23/0/0.png", HttpStatusCode.NotFound)]
        [TestCase("/tiles/5/1/1.jpg", HttpStatusCode.NotFound)]
        [TestCase("/tiles/5/1/-1.png", HttpStatusCode.NotFound)]
        public async Task RejectsInvalidRequests(string url, HttpStatusCode status) {
            var response = await client.GetAsync(url);
            Assert.That(response.StatusCode, Is.EqualTo(status));
        }

        [Test]
        public async Task ServesLayersHealthAndPreview() {
            var layers = await client.GetFromJsonAsync<string[]>("/layers");
            Assert.Multiple(async () => {
                Assert.That(layers, Is.EqualTo(new[] { "buildings", "roads-fill", "water-areas" }));
                Assert.That(await client.GetStringAsync("/health"), Is.EqualTo("ok"));
                Assert.That(await client.GetStringAsync("/"), Is.EqualTo("<html>preview</html>"));
            });
        }

        [Test]
        public void ParsesTileAddresses() {
            Assert.Multiple(() => {
                Assert.That(TileAddress.TryParse(3, 2, "5.png", out var standard), Is.True);
                Assert.That(standard, Is.EqualTo(new TileAddress(3, 2, 5, 256)));
                Assert.That(TileAddress.TryParse(3, 2, "5@2x.png", out var retina), Is.True);
                Assert.That(retina.TileSize, Is.EqualTo(512));
                Assert.That(TileAddress.TryParse(3, 2, "8.png", out _), Is.False);
                Assert.That(TileAddress.TryParse(3, 2, "+5.png", out _), Is.False);
                Assert.That(TileAddress.TryParse(3, 2, "5@3x.png", out _), Is.False);
            });
        }

        [Test]
        public void ParsesStyleQueries() {
            var query = new QueryCollection(new Dictionary<string, StringValues> {
                ["transparent"] = "yes",
                ["keepFill"] = new StringValues(["water-areas,buildings", "piers-poly"]),
                ["hideOutline"] = "0",
            });
            var options = StyleQuery.Parse(query, CjkVariant.SimplifiedChinese);
            Assert.Multiple(() => {
                Assert.That(options.Transparent, Is.True);
                Assert.That(options.HideOutlines, Is.False);
                Assert.That(options.KeepFillLayers, Is.EquivalentTo(new[] { "water-areas", "buildings", "piers-poly" }));
                Assert.That(options.Cjk, Is.EqualTo(CjkVariant.SimplifiedChinese));
            });
        }

        [TestCase(null, CjkVariant.Korean)]
        [TestCase("", CjkVariant.Korean)]
        [TestCase("jp", CjkVariant.Japanese)]
        [TestCase("zh-Hant", CjkVariant.TraditionalChinese)]
        [TestCase("SC", CjkVariant.SimplifiedChinese)]
        public void ParsesTheCjkVariant(string? value, CjkVariant expected) {
            var parameters = new Dictionary<string, StringValues>();
            if (value != null) {
                parameters["cjk"] = value;
            }
            Assert.That(StyleQuery.Parse(new QueryCollection(parameters), CjkVariant.Korean).Cjk, Is.EqualTo(expected));
        }

        [Test]
        public async Task SelectsTheCjkVariantFromTheQuery() {
            var japanese = await client.GetAsync("/tiles/5/1/1.png?cjk=jp");
            var hongKong = await client.GetAsync("/tiles/5/1/1.png?cjk=hk&transparent=1");
            var invalid = await client.GetAsync("/tiles/5/1/1.png?cjk=cn");
            Assert.Multiple(async () => {
                Assert.That(japanese.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(hongKong.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(await invalid.Content.ReadAsStringAsync(), Does.Contain("cn"));
                Assert.That(provider.Renderers.Keys, Is.EquivalentTo(new[] { "cjk=jp", "transparent;cjk=hk" }));
            });
        }

        [Test]
        public async Task SelectsTheFormatFromTheQuery() {
            var standard = await client.GetAsync("/tiles/5/1/1.png");
            var fullColor = await client.GetAsync("/tiles/5/1/1.png?format=png32");
            var cached = await client.GetAsync("/tiles/5/1/2.png?format=PNG32");
            var invalid = await client.GetAsync("/tiles/5/1/1.png?format=jpeg");
            var renderer = provider.Renderers["default"];
            Assert.Multiple(async () => {
                Assert.That(standard.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(fullColor.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(cached.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(await invalid.Content.ReadAsStringAsync(), Does.Contain("jpeg"));
                Assert.That(renderer.Formats, Is.EquivalentTo(new[] { TileFormat.Png256, TileFormat.Png32 }));
                Assert.That(renderer.Renders, Is.EqualTo(2), "Each format renders its metatile once; both share the style variant.");
            });
        }

        [Test]
        public async Task RequestsWithoutFormatUseTheServerDefault() {
            var fullColorProvider = new FakeRendererProvider("buildings");
            await using var fullColorApp = TileServer.Build(new TileServerOptions { DefaultFormat = TileFormat.Png32 }, fullColorProvider,
                builder => builder.WebHost.UseTestServer());
            await fullColorApp.StartAsync();
            using var fullColorClient = fullColorApp.GetTestClient();
            await fullColorClient.GetAsync("/tiles/5/1/1.png");
            Assert.That(fullColorProvider.Renderers["default"].Formats, Is.EqualTo(new[] { TileFormat.Png32 }));
        }

        [Test]
        public async Task RequestsWithoutCjkUseTheServerDefault() {
            var japaneseProvider = new FakeRendererProvider("buildings");
            await using var japaneseApp = TileServer.Build(new TileServerOptions { DefaultCjk = CjkVariant.Japanese }, japaneseProvider,
                builder => builder.WebHost.UseTestServer());
            await japaneseApp.StartAsync();
            using var japaneseClient = japaneseApp.GetTestClient();
            await japaneseClient.GetAsync("/tiles/5/1/1.png");
            await japaneseClient.GetAsync("/tiles/5/1/1.png?cjk=sc");
            Assert.That(japaneseProvider.Renderers.Keys, Is.EquivalentTo(new[] { "cjk=jp", "default" }));
        }
    }
}
