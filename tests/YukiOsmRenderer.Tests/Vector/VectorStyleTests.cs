using System.Text.Json.Nodes;
using YukiOsmRenderer.Core.Styling;
using YukiOsmRenderer.Core.Vector;

namespace YukiOsmRenderer.Tests.Vector {
    public class VectorStyleTests {
        const string Sample = """
            {
              "version": 8,
              "name": "classic",
              "glyphs": "yukiosm://glyphs/{fontstack}/{range}.pbf",
              "sprite": [{ "id": "basics", "url": "yukiosm://sprites/base" }],
              "sources": { "versatiles-shortbread": { "type": "vector", "url": "https://tiles.versatiles.org/tiles/osm.json" } },
              "sky": { "sky-color": "#fff" },
              "layers": [
                { "id": "background", "type": "background", "paint": { "background-color": "#f8f4f0" } },
                { "id": "water-ocean", "type": "fill", "source": "versatiles-shortbread", "source-layer": "ocean" },
                { "id": "land-park", "type": "fill", "source": "versatiles-shortbread", "source-layer": "land" },
                { "id": "building:outline", "type": "fill", "source": "versatiles-shortbread", "source-layer": "buildings", "paint": { "fill-outline-color": "#000" } },
                { "id": "building", "type": "fill", "source": "versatiles-shortbread", "source-layer": "buildings", "paint": { "fill-outline-color": "#111" } },
                { "id": "street-primary:outline", "type": "line", "source": "versatiles-shortbread", "source-layer": "streets" },
                { "id": "street-primary", "type": "line", "source": "versatiles-shortbread", "source-layer": "streets" },
                { "id": "label-street-primary", "type": "symbol", "source": "versatiles-shortbread", "source-layer": "street_labels",
                  "layout": { "text-field": "{name}", "text-font": ["noto_sans_regular"], "text-size": ["interpolate", ["linear"], ["zoom"], 12, 10, 15, 13] } },
                { "id": "label-place-city", "type": "symbol", "source": "versatiles-shortbread", "source-layer": "place_labels",
                  "layout": { "text-field": "{name}", "text-font": ["noto_sans_bold"], "text-size": 14 } },
                { "id": "label-address", "type": "symbol", "source": "versatiles-shortbread", "source-layer": "addresses",
                  "layout": { "text-field": "{housenumber}", "text-font": ["noto_sans_regular"] } },
                { "id": "poi-shop", "type": "symbol", "source": "versatiles-shortbread", "source-layer": "pois", "layout": { "icon-image": "shop" } }
              ]
            }
            """;

        static readonly VectorStyleResources Resources = new() {
            TileSource = new JsonObject { ["type"] = "vector", ["url"] = "pmtiles://file:///data/tiles.pmtiles" },
            AssetBase = "file:///home/assets/vector/",
        };

        static JsonObject Build(StyleOptions options, VectorStyleResources? resources = null) {
            return JsonNode.Parse(VectorStyle.Parse("classic", Sample).Build(options, resources ?? Resources))!.AsObject();
        }

        static IEnumerable<string> LayerIds(JsonObject style) {
            return style["layers"]!.AsArray().Select(l => (string)l!["id"]!);
        }

        [Test]
        public void ReplacesSourcesAndResourceUrls() {
            var style = Build(StyleOptions.Default);
            Assert.Multiple(() => {
                Assert.That(style["sources"]!["versatiles-shortbread"]!.ToJsonString(), Is.EqualTo("{\"type\":\"vector\",\"url\":\"pmtiles://file:///data/tiles.pmtiles\"}"));
                Assert.That((string?)style["glyphs"], Is.EqualTo("file:///home/assets/vector/glyphs/{fontstack}/{range}.pbf"));
                Assert.That((string?)style["sprite"]![0]!["url"], Is.EqualTo("file:///home/assets/vector/sprites/base"));
                Assert.That(style.ContainsKey("sky"), Is.False);
                Assert.That((string?)style["projection"]!["type"], Is.EqualTo("mercator"));
                Assert.That(style.ContainsKey("font-faces"), Is.False);
                Assert.That(LayerIds(style), Has.Exactly(11).Items);
            });
        }

        [Test]
        public void TransparentDropsAreasExceptKeptOnes() {
            var style = Build(StyleOptions.Create(true, ["water-*", "building*"]));
            Assert.That(LayerIds(style), Is.EqualTo(new[] {
                "water-ocean", "building:outline", "building", "street-primary:outline", "street-primary", "label-street-primary", "label-place-city", "label-address", "poi-shop",
            }));
        }

        [Test]
        public void HideOutlinesDropsOutlinesOfKeptAreas() {
            var style = Build(StyleOptions.Create(true, ["building*"], hideOutlines: true));
            Assert.Multiple(() => {
                Assert.That(LayerIds(style), Does.Not.Contain("building:outline"));
                Assert.That(LayerIds(style), Does.Contain("street-primary:outline"));
                Assert.That(style["layers"]!.AsArray().Single(l => (string?)l!["id"] == "building")!["paint"]!.AsObject().ContainsKey("fill-outline-color"), Is.False);
            });
        }

        [Test]
        public void HidesLayersByPattern() {
            var style = Build(StyleOptions.Create(false, hiddenLayers: ["label-*", "poi-shop"]));
            Assert.That(LayerIds(style), Is.EqualTo(new[] { "background", "water-ocean", "land-park", "building:outline", "building", "street-primary:outline", "street-primary" }));
        }

        [Test]
        public void RejectsPatternsWithoutLayers() {
            var style = VectorStyle.Parse("classic", Sample);
            Assert.Multiple(() => {
                Assert.That(() => style.Validate(StyleOptions.Create(true, ["lakes"])), Throws.ArgumentException.With.Message.Contains("lakes"));
                Assert.That(() => style.Validate(StyleOptions.Create(false, hiddenLayers: ["label-*", "x*"])), Throws.ArgumentException.With.Message.Contains("x*"));
                Assert.That(() => style.Validate(StyleOptions.Create(true, ["*"])), Throws.Nothing);
            });
        }

        [Test]
        public void TextScaleMultipliesLabelSizes() {
            var style = Build(StyleOptions.Create(false, textScale: 1.5));
            string Size(string id) => style["layers"]!.AsArray().Single(l => (string?)l!["id"] == id)!["layout"]!["text-size"]!.ToJsonString();
            Assert.Multiple(() => {
                Assert.That(Size("label-street-primary"), Is.EqualTo("[\"interpolate\",[\"linear\"],[\"zoom\"],12,15,15,19.5]"));
                Assert.That(Size("label-place-city"), Is.EqualTo("21"));
                Assert.That(Size("label-address"), Is.EqualTo("24"));
                Assert.That(style["layers"]!.AsArray().Single(l => (string?)l!["id"] == "poi-shop")!["layout"]!.AsObject().ContainsKey("text-size"), Is.False);
            });
        }

        [TestCase("[\"step\",[\"zoom\"],10,14,12]", "[\"step\",[\"zoom\"],20,14,24]")]
        [TestCase("{\"stops\":[[10,8],[16,12]]}", "{\"stops\":[[10,16],[16,24]]}")]
        [TestCase("[\"get\",\"size\"]", "[\"*\",2,[\"get\",\"size\"]]")]
        [TestCase("[\"case\",true,[\"interpolate\",[\"linear\"],[\"zoom\"],1,2,3,4],5]", "[\"case\",true,[\"interpolate\",[\"linear\"],[\"zoom\"],1,2,3,4],5]")]
        public void ScalesSizeExpressions(string size, string expected) {
            Assert.That(VectorStyle.ScaleSize(JsonNode.Parse(size)!, 2).ToJsonString(), Is.EqualTo(expected));
        }

        [TestCase(CjkVariant.SimplifiedChinese, 2)]
        [TestCase(CjkVariant.Japanese, 0)]
        [TestCase(CjkVariant.HongKong, 4)]
        public void AddsCjkFontFacesOfTheVariant(CjkVariant variant, int index) {
            var style = Build(StyleOptions.Create(false, cjk: variant), Resources with { CjkFonts = ("file:///fonts/R.ttc", "file:///fonts/B.ttc") });
            var faces = style["font-faces"]!.AsObject();
            Assert.Multiple(() => {
                Assert.That(faces.Select(f => f.Key), Is.EqualTo(new[] { "noto_sans_bold", "noto_sans_regular" }));
                Assert.That((string?)faces["noto_sans_regular"]![0]!["url"], Is.EqualTo($"file:///fonts/R.ttc#{index}"));
                Assert.That((string?)faces["noto_sans_bold"]![0]!["url"], Is.EqualTo($"file:///fonts/B.ttc#{index}"));
                Assert.That(faces["noto_sans_regular"]![0]!["unicode-range"]!.AsArray().Select(r => (string?)r), Does.Contain("U+2E80-9FFF"));
            });
        }

        [TestCase("water-*", "water-ocean", true)]
        [TestCase("water-*", "land-water", false)]
        [TestCase("*:outline", "building:outline", true)]
        [TestCase("label-*-primary", "label-street-primary", true)]
        [TestCase("label-*-primary", "label-primary", false)]
        [TestCase("a*a", "a", false)]
        [TestCase("*", "anything", true)]
        [TestCase("building", "building:outline", false)]
        public void MatchesPatterns(string pattern, string id, bool expected) {
            Assert.That(VectorStyle.Matches(pattern, id), Is.EqualTo(expected));
        }
    }
}
