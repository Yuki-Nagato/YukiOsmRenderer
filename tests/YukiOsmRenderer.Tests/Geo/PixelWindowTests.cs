using YukiOsmRenderer.Core.Geo;

namespace YukiOsmRenderer.Tests.Geo {
    public class PixelWindowTests {
        [Test]
        public void TileAlignedBoxMapsToExactTilePixels() {
            var extent = WebMercator.TileRangeExtent(10, 843, 387, 3, 2);
            var box = new GeoBoundingBox(
                WebMercator.XToLongitude(extent.MinX), WebMercator.YToLatitude(extent.MinY),
                WebMercator.XToLongitude(extent.MaxX), WebMercator.YToLatitude(extent.MaxY));
            var window = PixelWindow.FromBoundingBox(box, 10);
            Assert.Multiple(() => {
                Assert.That(window.X, Is.EqualTo(843 * 256));
                Assert.That(window.Y, Is.EqualTo(387 * 256));
                Assert.That(window.Width, Is.EqualTo(3 * 256));
                Assert.That(window.Height, Is.EqualTo(2 * 256));
            });
        }

        [Test]
        public void WindowCoversTheBoundingBox() {
            var box = new GeoBoundingBox(115.4, 39.4, 117.5, 41.1);
            var window = PixelWindow.FromBoundingBox(box, 12);
            var extent = window.Extent;
            Assert.Multiple(() => {
                Assert.That(extent.MinX, Is.LessThanOrEqualTo(WebMercator.LongitudeToX(box.MinLongitude)));
                Assert.That(extent.MaxX, Is.GreaterThanOrEqualTo(WebMercator.LongitudeToX(box.MaxLongitude)));
                Assert.That(extent.MinY, Is.LessThanOrEqualTo(WebMercator.LatitudeToY(box.MinLatitude)));
                Assert.That(extent.MaxY, Is.GreaterThanOrEqualTo(WebMercator.LatitudeToY(box.MaxLatitude)));
                // The window is at most one pixel larger than the box on each side.
                Assert.That(extent.Width - (WebMercator.LongitudeToX(box.MaxLongitude) - WebMercator.LongitudeToX(box.MinLongitude)),
                    Is.LessThan(2 * window.Resolution));
            });
        }

        [Test]
        public void HighDpiWindowDoublesThePixels() {
            var box = new GeoBoundingBox(116.3, 39.85, 116.45, 39.95);
            var normal = PixelWindow.FromBoundingBox(box, 15);
            var retina = PixelWindow.FromBoundingBox(box, 15, 512);
            Assert.Multiple(() => {
                Assert.That(retina.Width, Is.InRange(normal.Width * 2 - 2, normal.Width * 2 + 2));
                Assert.That(retina.Height, Is.InRange(normal.Height * 2 - 2, normal.Height * 2 + 2));
            });
        }

        [Test]
        public void TinyBoxStillProducesOnePixel() {
            var box = new GeoBoundingBox(116.4, 39.9, 116.4000001, 39.9000001);
            var window = PixelWindow.FromBoundingBox(box, 0);
            Assert.Multiple(() => {
                Assert.That(window.Width, Is.EqualTo(1));
                Assert.That(window.Height, Is.EqualTo(1));
            });
        }

        [Test]
        public void CoveringMetatilesAreAlignedAndCoverTheWindow() {
            var window = new PixelWindow(10, 256, 7 * 256 + 100, 15 * 256 + 5, 9 * 256, 2 * 256);
            var metatiles = window.CoveringMetatiles().ToList();
            Assert.That(metatiles, Has.Count.EqualTo(window.CoveringMetatileCount()));
            Assert.That(metatiles, Is.EqualTo(new[] {
                new Metatile(10, 0, 8, 8),
                new Metatile(10, 8, 8, 8),
                new Metatile(10, 16, 8, 8),
                new Metatile(10, 0, 16, 8),
                new Metatile(10, 8, 16, 8),
                new Metatile(10, 16, 16, 8),
            }));
        }

        [Test]
        public void MetatileContainingTile() {
            var metatile = Metatile.Containing(10, 853, 395);
            Assert.Multiple(() => {
                Assert.That(metatile, Is.EqualTo(new Metatile(10, 848, 392, 8)));
                Assert.That(metatile.Contains(853, 395), Is.True);
                Assert.That(metatile.Contains(856, 395), Is.False);
                Assert.That(metatile.PixelSize(512), Is.EqualTo(4096));
            });
        }

        [Test]
        public void RejectsWindowsOutsideTheWorld() {
            Assert.That(() => new PixelWindow(0, 256, 0, 0, 257, 1), Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }
}
