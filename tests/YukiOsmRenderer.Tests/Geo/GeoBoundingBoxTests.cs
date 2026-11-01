using YukiOsmRenderer.Core.Geo;

namespace YukiOsmRenderer.Tests.Geo {
    public class GeoBoundingBoxTests {
        [Test]
        public void ParsesInvariantNumbers() {
            var box = GeoBoundingBox.Parse(" 115.4, 39.4,117.5 ,41.1");
            Assert.Multiple(() => {
                Assert.That(box.MinLongitude, Is.EqualTo(115.4));
                Assert.That(box.MinLatitude, Is.EqualTo(39.4));
                Assert.That(box.MaxLongitude, Is.EqualTo(117.5));
                Assert.That(box.MaxLatitude, Is.EqualTo(41.1));
                Assert.That(box.ToString(), Is.EqualTo("115.4,39.4,117.5,41.1"));
            });
        }

        [TestCase("1,2,3")]
        [TestCase("1,2,3,4,5")]
        [TestCase("a,2,3,4")]
        [TestCase("1;2;3;4")]
        public void RejectsMalformedText(string text) {
            Assert.That(() => GeoBoundingBox.Parse(text), Throws.TypeOf<FormatException>());
        }

        [TestCase("170,0,-170,10", TestName = "Antimeridian")]
        [TestCase("10,20,10,30", TestName = "EmptyLongitudeRange")]
        [TestCase("10,30,20,20", TestName = "InvertedLatitudes")]
        [TestCase("-181,0,10,10", TestName = "LongitudeOutOfRange")]
        [TestCase("0,-91,10,10", TestName = "LatitudeOutOfRange")]
        public void RejectsInvalidBoxes(string text) {
            Assert.That(() => GeoBoundingBox.Parse(text), Throws.ArgumentException);
        }

        [Test]
        public void WorldCoversTheProjectedWorld() {
            var window = PixelWindow.FromBoundingBox(GeoBoundingBox.World, 3);
            Assert.Multiple(() => {
                Assert.That(window.X, Is.Zero);
                Assert.That(window.Y, Is.Zero);
                Assert.That(window.Width, Is.EqualTo(2048));
                Assert.That(window.Height, Is.EqualTo(2048));
            });
        }
    }
}
