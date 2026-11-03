using System.Xml.Linq;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Tests.Styling {
    public class MapnikStyleTransformerTests {
        // Shaped like the output of carto for OpenStreetMap Carto.
        const string SampleXml = """
			<?xml version="1.0" encoding="utf-8"?>
			<!DOCTYPE Map[]>
			<Map srs="+proj=merc +a=6378137 +b=6378137" background-color="#f2efe9">
			<Parameters>
			  <Parameter name="scale">1</Parameter>
			</Parameters>
			<FontSet name="fontset-0">
			  <Font face-name="Noto Sans Regular" />
			  <Font face-name="Noto Sans CJK JP Regular" />
			</FontSet>
			<Style name="landcover" filter-mode="first">
			  <Rule>
			    <Filter>([feature] = 'landuse_forest')</Filter>
			    <PolygonPatternSymbolizer file="symbols/leaftype_unknown.svg" />
			    <PolygonSymbolizer fill="#add19e" />
			    <LineSymbolizer stroke="#9ec08f" />
			  </Rule>
			</Style>
			<Style name="water-areas" filter-mode="first">
			  <Rule>
			    <PolygonSymbolizer fill="#aad3df" />
			    <LineSymbolizer stroke="#aad3df" />
			  </Rule>
			</Style>
			<Style name="roads-fill" filter-mode="first">
			  <Rule>
			    <LineSymbolizer stroke="#f8d6e0" stroke-width="3" />
			  </Rule>
			</Style>
			<Style name="amenity-points" filter-mode="first">
			  <Rule>
			    <MarkersSymbolizer file="symbols/amenity/cafe.svg" />
			    <TextSymbolizer fontset-name="fontset-0"><![CDATA[[name]]]></TextSymbolizer>
			  </Rule>
			</Style>
			<Style name="buildings" filter-mode="first">
			  <Rule>
			    <PolygonSymbolizer fill="#d9d0c9" />
			    <LineSymbolizer stroke="#c4b6ab" />
			  </Rule>
			</Style>
			<Layer name="landcover" srs="+proj=merc">
			    <StyleName>landcover</StyleName>
			    <Datasource>
			       <Parameter name="type"><![CDATA[postgis]]></Parameter>
			       <Parameter name="dbname"><![CDATA[gis]]></Parameter>
			       <Parameter name="table"><![CDATA[(SELECT way FROM planet_osm_polygon) AS landcover]]></Parameter>
			    </Datasource>
			</Layer>
			<Layer name="water-areas" srs="+proj=merc">
			    <StyleName>water-areas</StyleName>
			    <Datasource>
			       <Parameter name="type"><![CDATA[postgis]]></Parameter>
			       <Parameter name="dbname"><![CDATA[gis]]></Parameter>
			    </Datasource>
			</Layer>
			<Layer name="roads-fill" srs="+proj=merc">
			    <StyleName>roads-fill</StyleName>
			    <Datasource>
			       <Parameter name="type"><![CDATA[postgis]]></Parameter>
			    </Datasource>
			</Layer>
			<Layer name="amenity-points" srs="+proj=merc">
			    <StyleName>amenity-points</StyleName>
			    <Datasource>
			       <Parameter name="type"><![CDATA[postgis]]></Parameter>
			    </Datasource>
			</Layer>
			<Layer name="buildings" srs="+proj=merc">
			    <StyleName>buildings</StyleName>
			    <Datasource>
			       <Parameter name="type"><![CDATA[postgis]]></Parameter>
			    </Datasource>
			</Layer>
			</Map>
			""";

        static readonly DatabaseConnection Connection = new("127.0.0.1", 54321, "gis", "yukiosm", "secret");

        static XElement Transform(StyleOptions options, string xml = SampleXml) {
            return XDocument.Parse(MapnikStyleTransformer.Transform(xml, MmlProjectTests.Sample, options, Connection, 16)).Root!;
        }

        static IEnumerable<string> Symbolizers(XElement map, string style) {
            return map.Elements("Style").Single(s => (string?)s.Attribute("name") == style).Descendants()
                .Where(e => e.Name.LocalName.EndsWith("Symbolizer", StringComparison.Ordinal)).Select(e => e.Name.LocalName);
        }

        static string Parameter(XElement layer, string name) {
            return layer.Element("Datasource")!.Elements("Parameter").Single(p => (string?)p.Attribute("name") == name).Value;
        }

        [Test]
        public void InjectsDatabaseConnection() {
            var map = Transform(StyleOptions.Default);
            foreach (var layer in map.Elements("Layer")) {
                Assert.Multiple(() => {
                    Assert.That(Parameter(layer, "host"), Is.EqualTo("127.0.0.1"));
                    Assert.That(Parameter(layer, "port"), Is.EqualTo("54321"));
                    Assert.That(Parameter(layer, "dbname"), Is.EqualTo("gis"));
                    Assert.That(Parameter(layer, "user"), Is.EqualTo("yukiosm"));
                    Assert.That(Parameter(layer, "password"), Is.EqualTo("secret"));
                    Assert.That(Parameter(layer, "max_size"), Is.EqualTo("16"));
                });
            }
            var landcover = map.Elements("Layer").First();
            Assert.That(Parameter(landcover, "table"), Is.EqualTo("(SELECT way FROM planet_osm_polygon) AS landcover"));
        }

        [Test]
        public void DefaultOptionsKeepTheStyle() {
            var map = Transform(StyleOptions.Default);
            Assert.Multiple(() => {
                Assert.That((string?)map.Attribute("background-color"), Is.EqualTo("#f2efe9"));
                Assert.That(map.Elements("Layer"), Has.Exactly(5).Items);
                Assert.That(Symbolizers(map, "landcover"), Is.EqualTo(new[] { "PolygonPatternSymbolizer", "PolygonSymbolizer", "LineSymbolizer" }));
                Assert.That(map.Element("FontSet"), Is.Not.Null);
            });
        }

        [Test]
        public void TransparentDropsBackgroundAndFills() {
            var map = Transform(StyleOptions.Create(true));
            Assert.Multiple(() => {
                Assert.That(map.Attribute("background-color"), Is.Null);
                Assert.That(Symbolizers(map, "landcover"), Is.EqualTo(new[] { "LineSymbolizer" }));
                Assert.That(Symbolizers(map, "water-areas"), Is.EqualTo(new[] { "LineSymbolizer" }));
                Assert.That(Symbolizers(map, "buildings"), Is.EqualTo(new[] { "LineSymbolizer" }));
                Assert.That(Symbolizers(map, "roads-fill"), Is.EqualTo(new[] { "LineSymbolizer" }));
                Assert.That(Symbolizers(map, "amenity-points"), Is.EqualTo(new[] { "MarkersSymbolizer", "TextSymbolizer" }));
                // Rules stay in place so that filter-mode="first" keeps its meaning.
                Assert.That(map.Elements("Style").Single(s => (string?)s.Attribute("name") == "landcover").Elements("Rule"), Has.Exactly(1).Items);
            });
        }

        [Test]
        public void TransparentKeepsWhitelistedFills() {
            var map = Transform(StyleOptions.Create(true, ["water-areas", "buildings"]));
            Assert.Multiple(() => {
                Assert.That(Symbolizers(map, "water-areas"), Is.EqualTo(new[] { "PolygonSymbolizer", "LineSymbolizer" }));
                Assert.That(Symbolizers(map, "buildings"), Is.EqualTo(new[] { "PolygonSymbolizer", "LineSymbolizer" }));
                Assert.That(Symbolizers(map, "landcover"), Is.EqualTo(new[] { "LineSymbolizer" }));
            });
        }

        [Test]
        public void HideOutlinesOnlyAffectsAreaLayers() {
            var map = Transform(StyleOptions.Create(true, ["buildings"], hideOutlines: true));
            Assert.Multiple(() => {
                Assert.That(Symbolizers(map, "landcover"), Is.Empty);
                Assert.That(Symbolizers(map, "water-areas"), Is.Empty);
                Assert.That(Symbolizers(map, "buildings"), Is.EqualTo(new[] { "PolygonSymbolizer", "LineSymbolizer" }));
                Assert.That(Symbolizers(map, "roads-fill"), Is.EqualTo(new[] { "LineSymbolizer" }));
            });
        }

        [Test]
        public void HiddenLayersAndTheirStylesAreRemoved() {
            var map = Transform(StyleOptions.Create(false, hiddenLayers: ["buildings", "amenity-points"]));
            Assert.Multiple(() => {
                Assert.That(map.Elements("Layer").Select(l => (string?)l.Attribute("name")), Is.EqualTo(new[] { "landcover", "water-areas", "roads-fill" }));
                Assert.That(map.Elements("Style").Select(s => (string?)s.Attribute("name")), Is.EqualTo(new[] { "landcover", "water-areas", "roads-fill" }));
                Assert.That((string?)map.Attribute("background-color"), Is.EqualTo("#f2efe9"));
            });
        }

        [Test]
        public void SharedStylesAreClonedWhenLayersNeedDifferentTreatment() {
            var xml = SampleXml.Replace("<StyleName>water-areas</StyleName>", "<StyleName>buildings</StyleName>");
            var map = Transform(StyleOptions.Create(true, ["buildings"]), xml);
            var water = map.Elements("Layer").Single(l => (string?)l.Attribute("name") == "water-areas");
            var buildings = map.Elements("Layer").Single(l => (string?)l.Attribute("name") == "buildings");
            var waterStyle = water.Element("StyleName")!.Value;
            var buildingStyle = buildings.Element("StyleName")!.Value;
            Assert.Multiple(() => {
                Assert.That(waterStyle, Is.Not.EqualTo(buildingStyle));
                Assert.That(Symbolizers(map, buildingStyle), Is.EqualTo(new[] { "PolygonSymbolizer", "LineSymbolizer" }));
                Assert.That(Symbolizers(map, waterStyle), Is.EqualTo(new[] { "LineSymbolizer" }));
            });
        }

        [Test]
        public void RejectsUnknownLayers() {
            Assert.That(() => Transform(StyleOptions.Create(true, ["lakes"])), Throws.ArgumentException);
        }

        [Test]
        public void ReadsLayerNamesAndBufferSize() {
            Assert.Multiple(() => {
                Assert.That(MapnikStyleTransformer.LayerNames(SampleXml), Has.Count.EqualTo(5));
                Assert.That(MapnikStyleTransformer.BufferSize(SampleXml), Is.Null);
                Assert.That(MapnikStyleTransformer.BufferSize(SampleXml.Replace("<Map ", "<Map buffer-size=\"64\" ")), Is.EqualTo(64));
            });
        }

        [Test]
        public void OutputHasNoXmlDeclaration() {
            var xml = MapnikStyleTransformer.Transform(SampleXml, MmlProjectTests.Sample, StyleOptions.Default, Connection, 4);
            Assert.That(xml, Does.StartWith("<Map"));
        }

        [Test]
        public void SimplifiedChineseUsesTheScFontAndMapniksShapingLanguage() {
            var map = Transform(StyleOptions.Default);
            Assert.Multiple(() => {
                Assert.That(FontNames(map), Is.EqualTo(new[] { "Noto Sans Regular", "Noto Sans CJK SC Regular" }));
                Assert.That(map.Descendants("TextSymbolizer").Single().Attribute("lang"), Is.Null);
            });
        }

        [TestCase(CjkVariant.Japanese, "JP", "ja")]
        [TestCase(CjkVariant.Korean, "KR", "ko")]
        [TestCase(CjkVariant.TraditionalChinese, "TC", "zh-hant")]
        [TestCase(CjkVariant.HongKong, "HK", "zh-hk")]
        public void OtherVariantsSwitchTheFontAndShapingLanguage(CjkVariant variant, string suffix, string language) {
            var xml = SampleXml.Replace("<MarkersSymbolizer file=\"symbols/amenity/cafe.svg\" />", "<ShieldSymbolizer file=\"shield.svg\"><![CDATA[[ref]]]></ShieldSymbolizer>");
            var map = Transform(StyleOptions.Create(true, ["water-areas"], cjk: variant), xml);
            Assert.Multiple(() => {
                Assert.That(FontNames(map), Is.EqualTo(new[] { "Noto Sans Regular", $"Noto Sans CJK {suffix} Regular" }));
                Assert.That((string?)map.Descendants("TextSymbolizer").Single().Attribute("lang"), Is.EqualTo(language));
                Assert.That((string?)map.Descendants("ShieldSymbolizer").Single().Attribute("lang"), Is.EqualTo(language));
                Assert.That(map.Descendants("PolygonSymbolizer").Attributes("lang"), Is.Empty);
                // Combines with the other options.
                Assert.That(map.Attribute("background-color"), Is.Null);
                Assert.That(Symbolizers(map, "water-areas"), Is.EqualTo(new[] { "PolygonSymbolizer", "LineSymbolizer" }));
            });
        }

        static IEnumerable<string?> FontNames(XElement map) {
            return map.Elements("FontSet").Elements("Font").Select(f => (string?)f.Attribute("face-name"));
        }

        const string LabelledXml = """
            <Map>
            <Style name="labels">
              <Rule>
                <MarkersSymbolizer file="symbols/amenity/cafe.svg" width="14" />
                <TextSymbolizer size="10" wrap-width="30" line-spacing="-1.5" character-spacing="0.5" margin="7" dy="11" halo-radius="1.5" spacing="750"><![CDATA[[name]]]></TextSymbolizer>
                <ShieldSymbolizer file="place-6.svg" size="11" margin="7.7" dx="4" dy="4" unlock-image="true"><![CDATA[[name]]]></ShieldSymbolizer>
                <ShieldSymbolizer file="shield.svg" size="10" transform="translate(1,2)"><![CDATA[[ref]]]></ShieldSymbolizer>
              </Rule>
            </Style>
            <Layer name="labels"><StyleName>labels</StyleName></Layer>
            </Map>
            """;

        [Test]
        public void TextScaleEnlargesLabelsAndShields() {
            var map = XDocument.Parse(MapnikStyleTransformer.Transform(LabelledXml, MmlProjectTests.Sample, StyleOptions.Create(false, textScale: 1.5), Connection, 4)).Root!;
            var text = map.Descendants("TextSymbolizer").Single();
            var shields = map.Descendants("ShieldSymbolizer").ToList();
            Assert.Multiple(() => {
                Assert.That((string?)text.Attribute("size"), Is.EqualTo("15"));
                Assert.That((string?)text.Attribute("wrap-width"), Is.EqualTo("45"));
                Assert.That((string?)text.Attribute("line-spacing"), Is.EqualTo("-2.25"));
                Assert.That((string?)text.Attribute("character-spacing"), Is.EqualTo("0.75"));
                // Offsets from icons, margins, halos and repetition distances stay.
                Assert.That((string?)text.Attribute("margin"), Is.EqualTo("7"));
                Assert.That((string?)text.Attribute("dy"), Is.EqualTo("11"));
                Assert.That((string?)text.Attribute("halo-radius"), Is.EqualTo("1.5"));
                Assert.That((string?)text.Attribute("spacing"), Is.EqualTo("750"));
                Assert.That((string?)map.Descendants("MarkersSymbolizer").Single().Attribute("width"), Is.EqualTo("14"));

                Assert.That((string?)shields[0].Attribute("size"), Is.EqualTo("16.5"));
                Assert.That((string?)shields[0].Attribute("margin"), Is.EqualTo("7.7"));
                Assert.That((string?)shields[0].Attribute("dx"), Is.EqualTo("6"));
                Assert.That((string?)shields[0].Attribute("dy"), Is.EqualTo("6"));
                Assert.That((string?)shields[0].Attribute("transform"), Is.EqualTo("scale(1.5)"));
                Assert.That((string?)shields[1].Attribute("transform"), Is.EqualTo("scale(1.5) translate(1,2)"));
            });
        }

        [Test]
        public void DefaultTextScaleKeepsLabels() {
            var map = XDocument.Parse(MapnikStyleTransformer.Transform(LabelledXml, MmlProjectTests.Sample, StyleOptions.Default, Connection, 4)).Root!;
            Assert.Multiple(() => {
                Assert.That((string?)map.Descendants("TextSymbolizer").Single().Attribute("size"), Is.EqualTo("10"));
                Assert.That(map.Descendants("ShieldSymbolizer").First().Attribute("transform"), Is.Null);
            });
        }
    }
}
