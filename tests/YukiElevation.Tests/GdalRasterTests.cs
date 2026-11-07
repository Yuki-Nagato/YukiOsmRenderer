using OSGeo.GDAL;

namespace YukiElevation.Tests {
    public class GdalRasterTests {
        string directory = null!;

        [OneTimeSetUp]
        public void ConfigureGdal() {
            GdalSetup.Configure(64);
        }

        [SetUp]
        public void SetUp() {
            directory = Path.Combine(Path.GetTempPath(), "yukielevation-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown() {
            Directory.Delete(directory, true);
        }

        static double Value(int column, int row) => column * 0.5 + row * 1000.25;

        string Write(string name, DataType type, int width, int height, double[] transform, double? noData, params string[] creationOptions) {
            var path = Path.Combine(directory, name);
            using var dataset = Gdal.GetDriverByName("GTiff").Create(path, width, height, 1, type, creationOptions);
            dataset.SetGeoTransform(transform);
            using var band = dataset.GetRasterBand(1);
            if (noData is { } value) {
                band.SetNoDataValue(value);
            }
            var values = new double[width * height];
            for (var row = 0; row < height; row++) {
                for (var column = 0; column < width; column++) {
                    values[row * width + column] = type == DataType.GDT_Int16 ? column + row * 100 : Value(column, row);
                }
            }
            if (noData is { } marker) {
                values[1] = marker;
            }
            band.WriteRaster(0, 0, width, height, values, width, height, 0, 0);
            dataset.FlushCache();
            return path;
        }

        [Test]
        public void ReadsStripedIntegersAndMarksNoData() {
            var path = Write("strips.tif", DataType.GDT_Int16, 4, 3, [100, 0.25, 0, 40, 0, -0.25], -32767);

            using var raster = GdalRaster.Open(path);
            Span<double> values = stackalloc double[4];
            raster.Read(0, 0, 2, 2, values);

            Assert.That((raster.Width, raster.Height), Is.EqualTo((4, 3)));
            Assert.That(raster.Transform, Is.EqualTo(new GeoTransform(100, 0.25, 40, -0.25)));
            Assert.That(values.ToArray(), Is.EqualTo(new[] { 0, double.NaN, 100, 101 }));
        }

        [Test]
        public void ReadsDeflateCompressedTilesWithTheFloatingPointPredictor() {
            var path = Write("tiles.tif", DataType.GDT_Float32, 40, 40, [116, 1 / 3600.0, 0, 40, 0, -1 / 3600.0], null,
                "TILED=YES", "BLOCKXSIZE=16", "BLOCKYSIZE=16", "COMPRESS=DEFLATE", "PREDICTOR=3");

            using var raster = GdalRaster.Open(path);
            Span<double> values = stackalloc double[4];
            raster.Read(15, 31, 2, 2, values);

            Assert.That(values.ToArray(), Is.EqualTo(new[] { Value(15, 31), Value(16, 31), Value(15, 32), Value(16, 32) }));
        }

        [Test]
        public void CanBeReadFromSeveralThreads() {
            var path = Write("tiles.tif", DataType.GDT_Float32, 64, 64, [0, 1, 0, 0, 0, -1], null,
                "TILED=YES", "BLOCKXSIZE=16", "BLOCKYSIZE=16", "COMPRESS=DEFLATE", "PREDICTOR=3");
            using var raster = GdalRaster.Open(path);

            var wrong = 0;
            Parallel.For(0, 4000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => {
                var column = i * 7 % 64;
                var row = i * 13 % 64;
                if (raster.Read(column, row) != Value(column, row)) {
                    Interlocked.Increment(ref wrong);
                }
            });

            Assert.That(wrong, Is.Zero);
        }

        [Test]
        public void FormsAGridFromSeveralFiles() {
            var west = Write("west.tif", DataType.GDT_Float32, 40, 40, [116, 0.25, 0, 40, 0, -0.25], null);
            var east = Write("east.tif", DataType.GDT_Float32, 40, 40, [126, 0.25, 0, 40, 0, -0.25], null);

            using var grid = GridSource.Create("grid", [GdalRaster.Open(west), GdalRaster.Open(east)]);

            // Between the last column of west.tif and the first of east.tif.
            var expected = (Value(39, 4) + Value(0, 4)) / 2;
            Assert.That(grid.Sample(126, 38.875, Interpolation.Bilinear), Is.EqualTo(expected).Within(1e-3));
        }

        [Test]
        public void ReportsFilesItCannotOpen() {
            Assert.Throws<IOException>(() => GdalRaster.Open(Path.Combine(directory, "missing.tif")));
        }
    }
}
