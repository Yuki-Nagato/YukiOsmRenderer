using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Tests.Styling {
    public class StyleOptionsTests {
        [Test]
        public void DefaultKey() {
            Assert.Multiple(() => {
                Assert.That(StyleOptions.Default.Key, Is.EqualTo("default"));
                Assert.That(StyleOptions.Create(false).Key, Is.EqualTo("default"));
            });
        }

        [Test]
        public void NormalizesLayerLists() {
            var a = StyleOptions.Create(true, ["water-areas, buildings", "buildings"], true, ["amenity-points"]);
            var b = StyleOptions.Create(true, ["buildings", "water-areas"], true, ["amenity-points,"]);
            Assert.Multiple(() => {
                Assert.That(a.Key, Is.EqualTo("transparent;keep=buildings,water-areas;no-outline;hide=amenity-points"));
                Assert.That(a, Is.EqualTo(b));
                Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
                Assert.That(a.KeepFillLayers, Is.EquivalentTo(new[] { "buildings", "water-areas" }));
            });
        }

        [Test]
        public void CjkVariantIsPartOfTheKey() {
            Assert.Multiple(() => {
                Assert.That(StyleOptions.Create(false, cjk: CjkVariant.SimplifiedChinese), Is.EqualTo(StyleOptions.Default));
                Assert.That(StyleOptions.Create(false, cjk: CjkVariant.Japanese).Key, Is.EqualTo("cjk=jp"));
                Assert.That(StyleOptions.Create(true, ["buildings"], cjk: CjkVariant.HongKong).Key, Is.EqualTo("transparent;keep=buildings;cjk=hk"));
                Assert.That(StyleOptions.Create(false, cjk: CjkVariant.Korean), Is.Not.EqualTo(StyleOptions.Create(false, cjk: CjkVariant.Japanese)));
            });
        }

        [Test]
        public void TextScaleIsRoundedAndPartOfTheKey() {
            Assert.Multiple(() => {
                Assert.That(StyleOptions.Create(false, textScale: 1), Is.EqualTo(StyleOptions.Default));
                Assert.That(StyleOptions.Create(false, textScale: 1.5).Key, Is.EqualTo("text=1.5"));
                Assert.That(StyleOptions.Create(true, cjk: CjkVariant.Japanese, textScale: 1.2501).Key, Is.EqualTo("transparent;cjk=jp;text=1.25"));
                Assert.That(StyleOptions.Create(false, textScale: 0.749).TextScale, Is.EqualTo(0.75));
            });
        }

        [TestCase(0.49)]
        [TestCase(3.01)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void RejectsTextScalesOutOfRange(double scale) {
            Assert.That(() => StyleOptions.Create(false, textScale: scale), Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [TestCase("1.25", true, 1.25)]
        [TestCase("0.5", true, 0.5)]
        [TestCase("3", true, 3.0)]
        [TestCase("1,5", false, 0.0)]
        [TestCase("0.4", false, 0.0)]
        [TestCase("NaN", false, 0.0)]
        [TestCase("", false, 0.0)]
        [TestCase(null, false, 0.0)]
        public void ParsesTextScales(string? text, bool valid, double expected) {
            Assert.That(StyleOptions.TryParseTextScale(text, out var scale), Is.EqualTo(valid));
            if (valid) {
                Assert.That(scale, Is.EqualTo(expected));
            }
        }

        [Test]
        public void IgnoresFillOptionsWithoutTransparency() {
            var options = StyleOptions.Create(false, ["water-areas"], true, ["buildings"]);
            Assert.Multiple(() => {
                Assert.That(options.KeepFillLayers, Is.Empty);
                Assert.That(options.HideOutlines, Is.False);
                Assert.That(options.HiddenLayers, Is.EquivalentTo(new[] { "buildings" }));
                Assert.That(options.Key, Is.EqualTo("hide=buildings"));
            });
        }

        [Test]
        public void ValidatesLayerNames() {
            var known = new[] { "water-areas", "buildings" };
            Assert.Multiple(() => {
                Assert.That(() => StyleOptions.Create(true, ["water-areas"]).Validate(known), Throws.Nothing);
                Assert.That(() => StyleOptions.Create(true, ["lakes"]).Validate(known), Throws.ArgumentException.With.Message.Contains("lakes"));
                Assert.That(() => StyleOptions.Create(false, hiddenLayers: ["roads"]).Validate(known), Throws.ArgumentException.With.Message.Contains("roads"));
            });
        }
    }
}
