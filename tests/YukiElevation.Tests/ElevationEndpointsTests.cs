using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace YukiElevation.Tests {
    public class ElevationEndpointsTests {
        FixedSource detail = null!;
        FixedSource coarse = null!;
        ElevationService service = null!;
        WebApplication app = null!;
        HttpClient client = null!;

        [SetUp]
        public async Task SetUp() {
            detail = new FixedSource("copernicus", null, zeroMeansSea: true);
            coarse = new FixedSource("gebco", 12.3456);
            service = new ElevationService([detail, coarse]);
            app = ElevationServer.Build(new ServiceOptions { Gebco = ".", MaxPoints = 3 }, service, builder => builder.WebHost.UseTestServer());
            await app.StartAsync();
            client = app.GetTestClient();
        }

        [TearDown]
        public async Task TearDown() {
            client.Dispose();
            await app.DisposeAsync();
            service.Dispose();
            detail.Dispose();
            coarse.Dispose();
        }

        [Test]
        public async Task LooksUpOnePoint() {
            var response = await client.GetAsync("/elevation?lat=39.9042&lon=116.4074");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.GetValues("Access-Control-Allow-Origin"), Is.EqualTo(new[] { "*" }));
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.That(json.RootElement.ToString(), Is.EqualTo("""{"lat":39.9042,"lon":116.4074,"elevation":12.35,"dataset":"gebco"}"""));
            Assert.That(coarse.Calls.Single(), Is.EqualTo((116.4074, 39.9042, Interpolation.Bilinear)));
        }

        [Test]
        public async Task PassesTheInterpolation() {
            await client.GetAsync("/elevation?lat=1&lon=2&interpolation=Nearest");

            Assert.That(coarse.Calls.Single().Interpolation, Is.EqualTo(Interpolation.Nearest));
        }

        [TestCase("/elevation?lon=2")]
        [TestCase("/elevation?lat=1")]
        [TestCase("/elevation?lat=x&lon=2")]
        [TestCase("/elevation?lat=91&lon=2")]
        [TestCase("/elevation?lat=1&lon=-180.5")]
        [TestCase("/elevation?lat=NaN&lon=2")]
        [TestCase("/elevation?lat=1&lon=2&interpolation=cubic")]
        [TestCase("/elevation?lat=1&lon=2&interpolation=1")]
        public async Task RejectsInvalidQueries(string url) {
            var response = await client.GetAsync(url);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(coarse.Calls, Is.Empty);
        }

        [Test]
        public async Task LooksUpSeveralPointsInOrder() {
            var response = await client.PostAsJsonAsync("/elevation", new[] { new { lat = 1.5, lon = 2.0 }, new { lat = -3.0, lon = 4.25 } });

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.That(json.RootElement.ToString(), Is.EqualTo(
                """{"results":[{"lat":1.5,"lon":2,"elevation":12.35,"dataset":"gebco"},{"lat":-3,"lon":4.25,"elevation":12.35,"dataset":"gebco"}]}"""));
        }

        [TestCase("""[{"lat":1,"lon":2},{"lat":1,"lon":2},{"lat":1,"lon":2},{"lat":1,"lon":2}]""", "At most 3")]
        [TestCase("""[{"lat":1,"lon":2},{"lat":100,"lon":2}]""", "Point 1")]
        [TestCase("""[{"lat":1}]""", "Point 0")]
        [TestCase("""[null]""", "Point 0")]
        [TestCase("""{"lat":1,"lon":2}""", "")]
        public async Task RejectsInvalidBatches(string body, string message) {
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/elevation", content);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain(message));
            Assert.That(coarse.Calls, Is.Empty);
        }

        [Test]
        public async Task ListsTheDatasetsAndReportsHealth() {
            Assert.That(await client.GetStringAsync("/datasets"),
                Is.EqualTo("""[{"name":"copernicus","kind":"other","files":0},{"name":"gebco","kind":"other","files":0}]"""));
            Assert.That(await client.GetStringAsync("/health"), Is.EqualTo("ok"));
        }
    }
}
