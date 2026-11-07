namespace YukiElevation.Tests {
    sealed class FixedSource(string name, double? value, bool zeroMeansSea = false) : ElevationSource {
        public override string Name => name;

        public override bool ZeroMeansSea => zeroMeansSea;

        public List<(double Longitude, double Latitude, Interpolation Interpolation)> Calls { get; } = [];

        public bool Disposed { get; private set; }

        public override double? Sample(double longitude, double latitude, Interpolation interpolation) {
            Calls.Add((longitude, latitude, interpolation));
            return value;
        }

        public override void Dispose() {
            Disposed = true;
        }
    }

    public class ElevationServiceTests {
        static Elevation Lookup(double? detail, double? coarse) {
            using var service = new ElevationService([new FixedSource("copernicus", detail, zeroMeansSea: true), new FixedSource("gebco", coarse)]);
            return service.Lookup(30, 120, Interpolation.Bilinear);
        }

        [TestCase(123.4, -50.0, 123.4, "copernicus")]
        [TestCase(-3.5, -50.0, -3.5, "copernicus")]
        [TestCase(null, 456.0, 456.0, "gebco")]
        [TestCase(0.0, -20.0, -20.0, "gebco")]
        [TestCase(0.0, 4.0, 0.0, "copernicus")]
        [TestCase(0.0, 0.0, 0.0, "copernicus")]
        [TestCase(0.0, null, 0.0, "copernicus")]
        public void PrefersTheDetailedSourceExceptForSeaDepths(double? detail, double? coarse, double expected, string dataset) {
            Assert.That(Lookup(detail, coarse), Is.EqualTo(new Elevation(expected, dataset)));
        }

        [Test]
        public void HasNoElevationWithoutData() {
            Assert.That(Lookup(null, null), Is.EqualTo(new Elevation(null, null)));
        }

        [Test]
        public void PassesLongitudeLatitudeAndInterpolation() {
            var source = new FixedSource("gebco", 1);
            using var service = new ElevationService([source]);

            service.Lookup(30, 120, Interpolation.Nearest);

            Assert.That(source.Calls, Is.EqualTo(new[] { (120.0, 30.0, Interpolation.Nearest) }));
        }

        [Test]
        public void DisposesTheSources() {
            var source = new FixedSource("gebco", 1);
            var service = new ElevationService([source]);

            service.Dispose();

            Assert.That(source.Disposed);
        }
    }
}
