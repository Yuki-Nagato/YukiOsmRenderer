namespace YukiElevation.Tests {
    public class CopernicusSourceTests {
        static double Plane(double lon, double lat) => 3 * lon + 2 * lat;

        string directory = null!;
        Dictionary<string, ArrayRaster> rasters = null!;
        List<string> opened = null!;

        [SetUp]
        public void SetUp() {
            directory = Path.Combine(Path.GetTempPath(), "yukielevation-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            rasters = new();
            opened = [];
        }

        [TearDown]
        public void TearDown() {
            Directory.Delete(directory, true);
        }

        /// <summary>A tile as on AWS: 4 x 4 pixel centres from the north-west corner, without the southern and eastern edges.</summary>
        void AddTile(int latitude, int longitude, int resolution = 10, Func<double, double, double>? value = null) {
            var name = $"Copernicus_DSM_COG_{resolution}_{(latitude < 0 ? 'S' : 'N')}{Math.Abs(latitude):00}_00_{(longitude < 0 ? 'W' : 'E')}{Math.Abs(longitude):000}_00_DEM";
            var folder = Path.Combine(directory, name);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name + ".tif");
            File.WriteAllBytes(path, []);
            rasters[path] = ArrayRaster.FromCenters(longitude, latitude + 1, 0.25, 4, 4, value ?? Plane);
        }

        CopernicusSource Create(int maxOpenTiles = 16) {
            return CopernicusSource.Create("copernicus", directory, maxOpenTiles, path => {
                opened.Add(Path.GetFileName(path));
                return rasters[path];
            });
        }

        void AddSquare() {
            AddTile(0, 0);
            AddTile(0, 1);
            AddTile(1, 0);
            AddTile(1, 1);
        }

        [TestCase(0.4, 0.6)]
        [TestCase(0.9, 0.6)]
        [TestCase(0.4, 1.1)]
        [TestCase(0.9, 1.1)]
        [TestCase(1.0, 1.0)]
        [TestCase(0.0, 0.25)]
        public void InterpolatesBilinearlyAcrossTileEdges(double lon, double lat) {
            AddSquare();
            using var source = Create();

            Assert.That(source.TileCount, Is.EqualTo(4));
            Assert.That(source.Sample(lon, lat, Interpolation.Bilinear), Is.EqualTo(Plane(lon, lat)).Within(1e-9));
        }

        [Test]
        public void NearestTakesThePixelContainingThePoint() {
            AddSquare();
            using var source = Create();

            Assert.That(source.Sample(0.9, 1.1, Interpolation.Nearest), Is.EqualTo(Plane(1.0, 1.0)));
            Assert.That(source.Sample(0.3, 0.6, Interpolation.Nearest), Is.EqualTo(Plane(0.25, 0.5)));
        }

        [Test]
        public void HasNoDataWithoutATile() {
            AddSquare();
            using var source = Create();

            Assert.That(source.Sample(5.5, 5.5, Interpolation.Bilinear), Is.Null);
            Assert.That(opened, Is.Empty);
        }

        [Test]
        public void LeavesOutMissingNeighbourTiles() {
            AddTile(0, 0);
            AddTile(0, 1);
            using var source = Create();

            Assert.That(source.Sample(1.8, 0.5, Interpolation.Bilinear), Is.EqualTo(Plane(1.75, 0.5)).Within(1e-9));
            Assert.That(source.Sample(1.95, 0.5, Interpolation.Bilinear), Is.Null);
        }

        [Test]
        public void ReadsTheSouthernAndWesternHemispheres() {
            AddTile(-1, -1);
            using var source = Create();

            Assert.That(source.Sample(-0.6, -0.4, Interpolation.Bilinear), Is.EqualTo(Plane(-0.6, -0.4)).Within(1e-9));
        }

        [Test]
        public void WrapsAroundTheAntimeridian() {
            AddTile(0, 179);
            AddTile(0, -180, value: (lon, lat) => 1000 + lat);
            using var source = Create();

            Assert.That(source.Sample(179.9, 0.5, Interpolation.Bilinear), Is.EqualTo(0.4 * Plane(179.75, 0.5) + 0.6 * (1000 + 0.5)).Within(1e-9));
            Assert.That(source.Sample(180, 0.5, Interpolation.Bilinear), Is.EqualTo(1000.5).Within(1e-9));
        }

        [Test]
        public void PrefersGlo30ToGlo90() {
            AddTile(10, 10, resolution: 30, value: (_, _) => 90);
            AddTile(10, 10, resolution: 10, value: (_, _) => 30);
            using var source = Create();

            Assert.That(source.TileCount, Is.EqualTo(1));
            Assert.That(source.Sample(10.5, 10.5, Interpolation.Bilinear), Is.EqualTo(30));
            Assert.That(opened, Is.EqualTo(new[] { "Copernicus_DSM_COG_10_N10_00_E010_00_DEM.tif" }));
        }

        [Test]
        public void IgnoresOtherFiles() {
            AddTile(0, 0);
            File.WriteAllBytes(Path.Combine(directory, "Copernicus_DSM_COG_10_N00_00_E000_00_WBM.tif"), []);
            File.WriteAllBytes(Path.Combine(directory, "readme.tif"), []);

            using var source = Create();

            Assert.That(source.TileCount, Is.EqualTo(1));
        }

        [Test]
        public void ClosesTilesBeyondTheLimit() {
            AddSquare();
            using var source = Create(maxOpenTiles: 1);

            source.Sample(0.4, 0.6, Interpolation.Bilinear);
            source.Sample(1.4, 1.6, Interpolation.Bilinear);

            Assert.That(source.OpenTileCount, Is.EqualTo(1));
            Assert.That(rasters.Values.Count(r => r.Disposed), Is.EqualTo(1));
        }
    }
}
