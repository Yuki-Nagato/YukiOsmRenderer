using YukiOsmRenderer.Core.Geo;

namespace YukiOsmRenderer.Tests.Geo {
    public class WebMercatorTests {
        [Test]
        public void ProjectsWorldBounds() {
            Assert.Multiple(() => {
                Assert.That(WebMercator.LongitudeToX(180), Is.EqualTo(WebMercator.OriginShift).Within(1e-6));
                Assert.That(WebMercator.LongitudeToX(-180), Is.EqualTo(-WebMercator.OriginShift).Within(1e-6));
                Assert.That(WebMercator.LatitudeToY(WebMercator.MaxLatitude), Is.EqualTo(WebMercator.OriginShift).Within(1e-3));
                Assert.That(WebMercator.LatitudeToY(-WebMercator.MaxLatitude), Is.EqualTo(-WebMercator.OriginShift).Within(1e-3));
                Assert.That(WebMercator.LatitudeToY(0), Is.EqualTo(0).Within(1e-9));
            });
        }

        [TestCase(116.391, 39.907)]
        [TestCase(-73.97, 40.78)]
        [TestCase(151.21, -33.86)]
        public void RoundTripsCoordinates(double longitude, double latitude) {
            var x = WebMercator.LongitudeToX(longitude);
            var y = WebMercator.LatitudeToY(latitude);
            Assert.Multiple(() => {
                Assert.That(WebMercator.XToLongitude(x), Is.EqualTo(longitude).Within(1e-9));
                Assert.That(WebMercator.YToLatitude(y), Is.EqualTo(latitude).Within(1e-9));
            });
        }

        [Test]
        public void ClampsLatitudesBeyondTheProjectionLimit() {
            Assert.That(WebMercator.LatitudeToY(90), Is.EqualTo(WebMercator.LatitudeToY(WebMercator.MaxLatitude)));
        }

        [Test]
        public void TileExtentOfZoomZeroIsTheWorld() {
            var extent = WebMercator.TileExtent(0, 0, 0);
            Assert.Multiple(() => {
                Assert.That(extent.MinX, Is.EqualTo(-WebMercator.OriginShift).Within(1e-6));
                Assert.That(extent.MinY, Is.EqualTo(-WebMercator.OriginShift).Within(1e-6));
                Assert.That(extent.MaxX, Is.EqualTo(WebMercator.OriginShift).Within(1e-6));
                Assert.That(extent.MaxY, Is.EqualTo(WebMercator.OriginShift).Within(1e-6));
            });
        }

        [Test]
        public void TileExtentMatchesPixelConversion() {
            var extent = WebMercator.TileExtent(12, 3372, 1552);
            Assert.Multiple(() => {
                Assert.That(WebMercator.XToPixel(extent.MinX, 12), Is.EqualTo(3372 * 256).Within(1e-6));
                Assert.That(WebMercator.YToPixel(extent.MaxY, 12), Is.EqualTo(1552 * 256).Within(1e-6));
                Assert.That(extent.Width, Is.EqualTo(WebMercator.Resolution(12) * 256).Within(1e-6));
            });
        }

        [TestCase(0, 1)]
        [TestCase(1, 2)]
        [TestCase(2, 4)]
        [TestCase(3, 8)]
        [TestCase(18, 8)]
        public void MetatileSizeIsLimitedByTheWorld(int zoom, int expected) {
            Assert.That(WebMercator.MetatileSizeAt(zoom), Is.EqualTo(expected));
        }

        [Test]
        public void RejectsInvalidZoomLevels() {
            Assert.Multiple(() => {
                Assert.That(() => WebMercator.TilesPerSide(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(() => WebMercator.TilesPerSide(WebMercator.MaxZoom + 1), Throws.TypeOf<ArgumentOutOfRangeException>());
            });
        }

        [Test]
        public void ValidatesTiles() {
            Assert.Multiple(() => {
                Assert.That(WebMercator.IsValidTile(0, 0, 0), Is.True);
                Assert.That(WebMercator.IsValidTile(1, 2, 0), Is.False);
                Assert.That(WebMercator.IsValidTile(3, 7, 7), Is.True);
                Assert.That(WebMercator.IsValidTile(3, -1, 0), Is.False);
                Assert.That(WebMercator.IsValidTile(23, 0, 0), Is.False);
            });
        }
    }
}
