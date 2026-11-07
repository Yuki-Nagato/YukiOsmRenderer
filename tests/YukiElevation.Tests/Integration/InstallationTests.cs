using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OSGeo.GDAL;
using OSGeo.OSR;

namespace YukiElevation.Tests.Integration {
    /// <summary>
    /// Runs yukielevation of a complete installation (YUKIOSM_HOME) on GeoTIFFs written for the test, laid out like
    /// GEBCO and a Copernicus DEM tile. Skipped without an installation.
    /// </summary>
    [TestFixture]
    [Category("Integration")]
    [NonParallelizable]
    public class InstallationTests {
        readonly StringBuilder output = new();
        string directory = null!;
        Process? process;
        HttpClient client = null!;

        // The values of the global 1° grid and of the tile at the pixel centres; bilinear interpolation keeps them.
        static double Grid(double lon, double lat) => lon + 2 * lat + 0.5;

        static double Tile(double lon, double lat) => 1000 + 10 * lon + lat;

        [OneTimeSetUp]
        public async Task Start() {
            var home = Environment.GetEnvironmentVariable("YUKIOSM_HOME");
            if (string.IsNullOrEmpty(home)) {
                Assert.Ignore("Set YUKIOSM_HOME to a complete installation.");
            }
            var executable = Path.Combine(home, OperatingSystem.IsWindows() ? "yukielevation.exe" : "yukielevation");
            Assert.That(File.Exists(executable), $"{executable} is missing");

            GdalSetup.Configure(64);
            directory = Path.Combine(Path.GetTempPath(), "yukielevation-tests", Guid.NewGuid().ToString("N"));
            var gebco = Directory.CreateDirectory(Path.Combine(directory, "gebco")).FullName;
            var glo30 = Directory.CreateDirectory(Path.Combine(directory, "glo30", "Copernicus_DSM_COG_10_N10_00_E010_00_DEM")).FullName;
            Write(Path.Combine(gebco, "grid.tif"), DataType.GDT_Int16, 360, 180, 1, -180, 90, false, (column, row) => column - 2 * row, []);
            Write(Path.Combine(glo30, "Copernicus_DSM_COG_10_N10_00_E010_00_DEM.tif"), DataType.GDT_Float32, 40, 40, 0.025, 10, 11, true,
                (column, row) => Tile(10 + column * 0.025, 11 - row * 0.025),
                ["TILED=YES", "BLOCKXSIZE=16", "BLOCKYSIZE=16", "COMPRESS=DEFLATE", "PREDICTOR=3"]);

            var port = FreePort();
            var start = new ProcessStartInfo(executable) {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "--gebco", gebco, "--glo30", Path.Combine(directory, "glo30"), "--host", "127.0.0.1", "--port", port.ToString() }) {
                start.ArgumentList.Add(argument);
            }
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(directory, "extracted");
            process = Process.Start(start)!;
            process.OutputDataReceived += (_, e) => Log(e.Data);
            process.ErrorDataReceived += (_, e) => Log(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            var deadline = DateTime.UtcNow.AddMinutes(1);
            while (true) {
                if (process.HasExited) {
                    Assert.Fail($"yukielevation exited with {process.ExitCode}:\n{Output()}");
                }
                try {
                    if ((await client.GetAsync("health")).IsSuccessStatusCode) {
                        break;
                    }
                }
                catch (HttpRequestException) {
                    // Not listening yet.
                }
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"yukielevation did not start:\n{Output()}");
                }
                await Task.Delay(200);
            }
        }

        [OneTimeTearDown]
        public void Stop() {
            client?.Dispose();
            if (process != null) {
                if (!process.HasExited) {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
                process.Dispose();
                TestContext.Out.WriteLine(Output());
            }
            if (directory != null) {
                try {
                    Directory.Delete(directory, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    TestContext.Out.WriteLine($"Cannot delete {directory}: {e.Message}");
                }
            }
        }

        void Log(string? line) {
            lock (output) {
                output.AppendLine(line);
            }
        }

        string Output() {
            lock (output) {
                return output.ToString();
            }
        }

        static int FreePort() {
            using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }

        /// <summary>A GeoTIFF whose pixel centres start at (west, north), stored as pixel-is-point like Copernicus DEM tiles or not.</summary>
        static void Write(string path, DataType type, int width, int height, double pixelSize, double west, double north, bool pixelIsPoint,
            Func<int, int, double> value, string[] creationOptions) {
            using var dataset = Gdal.GetDriverByName("GTiff").Create(path, width, height, 1, type, creationOptions);
            if (pixelIsPoint) {
                dataset.SetMetadataItem("AREA_OR_POINT", "Point", "");
            }
            var centred = pixelIsPoint ? pixelSize / 2 : 0;
            dataset.SetGeoTransform([west - centred, pixelSize, 0, north + centred, 0, -pixelSize]);
            using (var srs = new SpatialReference("")) {
                srs.ImportFromEPSG(4326);
                srs.ExportToWkt(out var wkt, null);
                dataset.SetProjection(wkt);
            }
            var values = new double[width * height];
            for (var row = 0; row < height; row++) {
                for (var column = 0; column < width; column++) {
                    values[row * width + column] = value(column, row);
                }
            }
            using var band = dataset.GetRasterBand(1);
            band.WriteRaster(0, 0, width, height, values, width, height, 0, 0);
            dataset.FlushCache();
        }

        async Task<JsonElement> GetAsync(string url) {
            using var json = JsonDocument.Parse(await client.GetStringAsync(url));
            return json.RootElement.Clone();
        }

        [Test]
        public async Task PrefersTheCopernicusTile() {
            var point = await GetAsync("elevation?lat=10.5&lon=10.5");

            Assert.That(point.GetProperty("dataset").GetString(), Is.EqualTo("copernicus"));
            Assert.That(point.GetProperty("elevation").GetDouble(), Is.EqualTo(Tile(10.5, 10.5)).Within(0.01));
        }

        [Test]
        public async Task FallsBackToTheGrid() {
            var point = await GetAsync("elevation?lat=-20.6&lon=50.3");

            Assert.That(point.GetProperty("dataset").GetString(), Is.EqualTo("gebco"));
            Assert.That(point.GetProperty("elevation").GetDouble(), Is.EqualTo(Grid(50.3, -20.6)).Within(0.01));
        }

        [Test]
        public async Task LooksUpSeveralPoints() {
            var response = await client.PostAsJsonAsync("elevation?interpolation=nearest", new[] { new { lat = 10.5, lon = 10.5 }, new { lat = -20.6, lon = 50.3 } });
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var results = json.RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("dataset").GetString()).ToArray();

            Assert.That(results, Is.EqualTo(new[] { "copernicus", "gebco" }));
        }

        [Test]
        public async Task ReportsTheVersion() {
            var start = new ProcessStartInfo(process!.StartInfo.FileName, "--version") { UseShellExecute = false, RedirectStandardOutput = true };
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(directory, "extracted");
            using var version = Process.Start(start)!;
            var text = await version.StandardOutput.ReadToEndAsync();
            await version.WaitForExitAsync();

            Assert.That(version.ExitCode, Is.Zero);
            Assert.That(text.Trim(), Is.Not.Empty);
        }
    }
}
