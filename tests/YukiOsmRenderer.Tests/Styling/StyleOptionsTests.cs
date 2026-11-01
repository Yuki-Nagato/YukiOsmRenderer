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
