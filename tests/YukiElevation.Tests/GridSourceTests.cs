namespace YukiElevation.Tests {
    public class GridSourceTests {
        static double Plane(double lon, double lat) => 2 * lon + lat;

        /// <summary>A global grid of 10° pixels in two files split at the prime meridian.</summary>
        static (GridSource Grid, ArrayRaster West, ArrayRaster East) GlobalGrid() {
            var west = ArrayRaster.FromCenters(-175, 85, 10, 18, 18, Plane);
            var east = ArrayRaster.FromCenters(5, 85, 10, 18, 18, Plane);
            return (GridSource.Create("grid", [east, west]), west, east);
        }

        [Test]
        public void FindsTheGridOfTheFiles() {
            var (grid, _, _) = GlobalGrid();

            Assert.That((grid.Columns, grid.Rows, grid.FileCount), Is.EqualTo((36L, 18L, 2)));
        }

        [TestCase(25, 35)]
        [TestCase(-123.4, 56.7)]
        [TestCase(0, 35)]
        [TestCase(0.3, -12.5)]
        [TestCase(-4, 0)]
        public void InterpolatesBilinearlyAcrossFiles(double lon, double lat) {
            var (grid, _, _) = GlobalGrid();

            Assert.That(grid.Sample(lon, lat, Interpolation.Bilinear), Is.EqualTo(Plane(lon, lat)).Within(1e-9));
        }

        [Test]
        public void ReadsAllFourPixelsInsideOneFileAtOnce() {
            var (grid, west, east) = GlobalGrid();

            grid.Sample(-123.4, 56.7, Interpolation.Bilinear);

            Assert.That((west.Reads, east.Reads), Is.EqualTo((1, 0)));
        }

        [Test]
        public void WrapsAroundTheAntimeridian() {
            var (grid, _, _) = GlobalGrid();

            // Between the pixel centres 175 and -175 (= 185).
            var expected = 0.6 * Plane(175, 35) + 0.4 * Plane(-175, 35);
            Assert.That(grid.Sample(179, 35, Interpolation.Bilinear), Is.EqualTo(expected).Within(1e-9));
            Assert.That(grid.Sample(-180, 35, Interpolation.Bilinear), Is.EqualTo(0.5 * Plane(175, 35) + 0.5 * Plane(-175, 35)).Within(1e-9));
        }

        [Test]
        public void UsesTheOuterRowAtThePoles() {
            var (grid, _, _) = GlobalGrid();

            Assert.That(grid.Sample(25, 90, Interpolation.Bilinear), Is.EqualTo(Plane(25, 85)).Within(1e-9));
            Assert.That(grid.Sample(25, -90, Interpolation.Bilinear), Is.EqualTo(Plane(25, -85)).Within(1e-9));
        }

        [Test]
        public void KeepsPointsOnTheOuterEdgeDespiteRoundingOfTheOrigin() {
            // As in GEBCO, whose northern files start at 89.99999999999999.
            var grid = GridSource.Create("grid", [ArrayRaster.FromCenters(-175, 84.99999999999999, 10, 36, 18, Plane)]);

            Assert.That(grid.Sample(25, 90, Interpolation.Bilinear), Is.EqualTo(Plane(25, 85)).Within(1e-9));
            Assert.That(grid.Sample(25, 90, Interpolation.Nearest), Is.EqualTo(Plane(25, 85)).Within(1e-9));
        }

        [Test]
        public void NearestTakesThePixelContainingThePoint() {
            var (grid, _, _) = GlobalGrid();

            Assert.That(grid.Sample(21, 39, Interpolation.Nearest), Is.EqualTo(Plane(25, 35)));
            Assert.That(grid.Sample(179.9, 0.1, Interpolation.Nearest), Is.EqualTo(Plane(175, 5)));
        }

        [Test]
        public void LeavesOutPixelsWithoutData() {
            var (grid, _, east) = GlobalGrid();
            east.Set(2, 5, double.NaN); // centre (25, 35)

            // The pixel (35, 35) containing the point has data: the others are weighted without (25, 35).
            var value = grid.Sample(32, 37, Interpolation.Bilinear);
            var expected = (0.06 * Plane(25, 45) + 0.14 * Plane(35, 45) + 0.56 * Plane(35, 35)) / 0.76;
            Assert.That(value, Is.EqualTo(expected).Within(1e-9));
            Assert.That(grid.Sample(26, 36, Interpolation.Bilinear), Is.Null);
            Assert.That(grid.Sample(26, 36, Interpolation.Nearest), Is.Null);
        }

        [Test]
        public void RegionalGridsHaveNoDataOutside() {
            var raster = ArrayRaster.FromCenters(100, 40, 1, 10, 10, Plane);
            var grid = GridSource.Create("grid", [raster]);

            Assert.That(grid.Sample(105.2, 35.5, Interpolation.Bilinear), Is.EqualTo(Plane(105.2, 35.5)).Within(1e-9));
            Assert.That(grid.Sample(99.9, 35.5, Interpolation.Bilinear), Is.EqualTo(Plane(100, 35.5)).Within(1e-9));
            Assert.That(grid.Sample(99, 35.5, Interpolation.Bilinear), Is.Null);
            Assert.That(grid.Sample(-80, 35.5, Interpolation.Bilinear), Is.Null);
        }

        [Test]
        public void RejectsRastersThatDoNotFitOneGrid() {
            var a = ArrayRaster.FromCenters(0, 10, 1, 4, 4, Plane);

            Assert.Throws<InvalidDataException>(() => GridSource.Create("grid", [a, ArrayRaster.FromCenters(4.5, 10, 1, 4, 4, Plane)]));
            Assert.Throws<InvalidDataException>(() => GridSource.Create("grid", [a, ArrayRaster.FromCenters(4, 10, 0.5, 4, 4, Plane)]));
            Assert.Throws<InvalidDataException>(() => GridSource.Create("grid", []));
        }
    }
}
