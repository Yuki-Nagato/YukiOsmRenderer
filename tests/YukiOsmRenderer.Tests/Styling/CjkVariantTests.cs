using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Tests.Styling {
    public class CjkVariantTests {
        [TestCase("sc", CjkVariant.SimplifiedChinese)]
        [TestCase("SC", CjkVariant.SimplifiedChinese)]
        [TestCase("zh-Hans", CjkVariant.SimplifiedChinese)]
        [TestCase("tc", CjkVariant.TraditionalChinese)]
        [TestCase("zh-Hant-TW", CjkVariant.TraditionalChinese)]
        [TestCase("hk", CjkVariant.HongKong)]
        [TestCase("zh-Hant-HK", CjkVariant.HongKong)]
        [TestCase("jp", CjkVariant.Japanese)]
        [TestCase("ja", CjkVariant.Japanese)]
        [TestCase("kr", CjkVariant.Korean)]
        [TestCase("ko", CjkVariant.Korean)]
        public void ParsesNamesAndLanguageTags(string text, CjkVariant expected) {
            Assert.That(CjkVariants.Parse(text), Is.EqualTo(expected));
        }

        [Test]
        public void RejectsUnknownNames() {
            Assert.Multiple(() => {
                Assert.That(CjkVariants.TryParse("cn", out _), Is.False);
                Assert.That(CjkVariants.TryParse(null, out _), Is.False);
                Assert.That(() => CjkVariants.Parse("xx"), Throws.TypeOf<FormatException>());
            });
        }

        [Test]
        public void ShortNamesRoundTrip() {
            foreach (var variant in Enum.GetValues<CjkVariant>()) {
                Assert.That(CjkVariants.Parse(variant.ShortName()), Is.EqualTo(variant));
            }
        }

        [Test]
        public void ReplacesCjkFontsLikeTheUpstreamScript() {
            const string fonts = """
				@book-fonts:    "Noto Sans Regular",
				                "Noto Sans CJK JP Regular",
				                "HanaMinA Regular";
				@bold-fonts:    "Noto Sans Bold",
				                "Noto Sans CJK JP Bold";
				/* Noto Sans CJK KR is mentioned in a comment */
				""";
            var result = CjkVariants.ApplyToFontsMss(fonts, CjkVariant.SimplifiedChinese);
            Assert.Multiple(() => {
                Assert.That(result, Does.Contain("\"Noto Sans CJK SC Regular\""));
                Assert.That(result, Does.Contain("\"Noto Sans CJK SC Bold\""));
                Assert.That(result, Does.Contain("Noto Sans CJK SC is mentioned"));
                Assert.That(result, Does.Not.Contain("CJK JP"));
                Assert.That(result, Does.Contain("\"Noto Sans Regular\""));
                Assert.That(result, Does.Contain("HanaMinA Regular"));
            });
        }
    }
}
