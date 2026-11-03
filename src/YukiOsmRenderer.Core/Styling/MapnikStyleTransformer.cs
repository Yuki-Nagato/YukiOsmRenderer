using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace YukiOsmRenderer.Core.Styling {
    /// <summary>Connection parameters injected into the PostGIS datasources of the style.</summary>
    public sealed record DatabaseConnection(string Host, int Port, string Database, string User, string Password) {
        public override string ToString() {
            return $"{User}@{Host}:{Port}/{Database}";
        }
    }

    /// <summary>
    /// Adapts the Mapnik XML compiled by carto from OpenStreetMap Carto: injects database connection parameters,
    /// removes hidden layers, applies transparent rendering, selects the CJK glyph variant and scales labels.
    /// </summary>
    public static class MapnikStyleTransformer {
        static readonly HashSet<string> FillSymbolizers = ["PolygonSymbolizer", "PolygonPatternSymbolizer", "BuildingSymbolizer"];

        static readonly HashSet<string> OutlineSymbolizers = ["LineSymbolizer", "LinePatternSymbolizer"];

        static readonly HashSet<string> TextSymbolizers = ["TextSymbolizer", "ShieldSymbolizer"];

        // Pixel sizes that grow with the font. Margins stay: road shields have 40 px, and doubling them hides most shields.
        static readonly string[] TextSizeAttributes = ["size", "wrap-width", "line-spacing", "character-spacing"];

        /// <param name="xml">Mapnik XML produced by carto.</param>
        /// <param name="project">Layer metadata of the project the XML was compiled from.</param>
        /// <param name="options">Style options; must only reference layers of <paramref name="project"/>.</param>
        /// <param name="connection">Database connection for PostGIS datasources.</param>
        /// <param name="connectionPoolSize">Maximum number of database connections Mapnik may open.</param>
        public static string Transform(string xml, MmlProject project, StyleOptions options, DatabaseConnection connection, int connectionPoolSize) {
            ArgumentNullException.ThrowIfNull(xml);
            ArgumentNullException.ThrowIfNull(project);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(connection);
            options.Validate(project.LayerIds);

            var document = Parse(xml);
            var map = document.Root;
            if (map == null || map.Name.LocalName != "Map") {
                throw new InvalidDataException("The style is not a Mapnik XML document.");
            }

            foreach (var layer in map.Elements("Layer").Where(l => options.HiddenLayers.Contains(LayerName(l))).ToList()) {
                layer.Remove();
            }
            foreach (var datasource in map.Descendants("Datasource")) {
                SetConnection(datasource, connection, connectionPoolSize);
            }
            if (options.Transparent) {
                map.Attribute("background-color")?.Remove();
                map.Attribute("background-image")?.Remove();
                ApplyTransparency(map, project, options);
            }
            ApplyCjkVariant(map, options.Cjk);
            if (options.TextScale != 1) {
                ApplyTextScale(map, options.TextScale);
            }
            RemoveUnusedStyles(map);

            // The declaration is omitted because it would claim UTF-16 while the string is passed to Mapnik as UTF-8.
            using var writer = new StringWriter();
            using (var xmlWriter = XmlWriter.Create(writer, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = true })) {
                document.Save(xmlWriter);
            }
            return writer.ToString().TrimStart();
        }

        /// <summary>Names of the layers in a Mapnik XML document, in drawing order.</summary>
        public static IReadOnlyList<string> LayerNames(string xml) {
            return Parse(xml).Root?.Elements("Layer").Select(LayerName).ToList() ?? [];
        }

        /// <summary>Map-wide buffer size declared by the style, or null when the style does not set one.</summary>
        public static int? BufferSize(string xml) {
            var value = Parse(xml).Root?.Attribute("buffer-size")?.Value;
            return int.TryParse(value, out var size) ? size : null;
        }

        static XDocument Parse(string xml) {
            // carto emits "<!DOCTYPE Map[]>", which must not be processed.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader, LoadOptions.None);
        }

        static string LayerName(XElement layer) {
            return layer.Attribute("name")?.Value ?? "";
        }

        static void SetConnection(XElement datasource, DatabaseConnection connection, int connectionPoolSize) {
            var parameters = datasource.Elements("Parameter").ToList();
            var type = parameters.FirstOrDefault(p => (string?)p.Attribute("name") == "type")?.Value.Trim();
            if (type is not ("postgis" or "pgraster")) {
                return;
            }
            SetParameter(datasource, "host", connection.Host);
            SetParameter(datasource, "port", connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SetParameter(datasource, "dbname", connection.Database);
            SetParameter(datasource, "user", connection.User);
            SetParameter(datasource, "password", connection.Password);
            SetParameter(datasource, "max_size", connectionPoolSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        static void SetParameter(XElement datasource, string name, string value) {
            var parameter = datasource.Elements("Parameter").FirstOrDefault(p => (string?)p.Attribute("name") == name);
            if (parameter == null) {
                parameter = new XElement("Parameter", new XAttribute("name", name));
                datasource.Add(parameter);
            }
            parameter.ReplaceNodes(new XCData(value));
        }

        enum Treatment {
            Keep,
            RemoveFills,
            RemoveFillsAndOutlines,
        }

        static void ApplyTransparency(XElement map, MmlProject project, StyleOptions options) {
            var styles = map.Elements("Style").GroupBy(s => (string?)s.Attribute("name") ?? "").ToDictionary(g => g.Key, g => g.First());
            var layers = map.Elements("Layer").ToList();

            // Decide what to do with every style reference. A style shared by layers that need different
            // treatments is cloned so that each layer gets its own copy.
            var treatments = new Dictionary<(string Style, Treatment Treatment), List<XElement>>();
            foreach (var layer in layers) {
                var name = LayerName(layer);
                var treatment = options.KeepFillLayers.Contains(name)
                    ? Treatment.Keep
                    : options.HideOutlines && project.GeometryOf(name) == LayerGeometry.Polygon
                        ? Treatment.RemoveFillsAndOutlines
                        : Treatment.RemoveFills;
                foreach (var styleName in layer.Elements("StyleName")) {
                    var key = (styleName.Value.Trim(), treatment);
                    if (!treatments.TryGetValue(key, out var references)) {
                        treatments[key] = references = [];
                    }
                    references.Add(styleName);
                }
            }

            foreach (var group in treatments.GroupBy(t => t.Key.Style)) {
                if (!styles.TryGetValue(group.Key, out var style)) {
                    continue;
                }
                var variants = group.ToList();
                var pristine = new XElement(style);
                for (var i = 0; i < variants.Count; i++) {
                    var treatment = variants[i].Key.Treatment;
                    var target = style;
                    if (i > 0) {
                        var cloneName = $"{group.Key}@{treatment}";
                        target = new XElement(pristine);
                        target.SetAttributeValue("name", cloneName);
                        style.AddAfterSelf(target);
                        foreach (var reference in variants[i].Value) {
                            reference.Value = cloneName;
                        }
                    }
                    RemoveSymbolizers(target, treatment);
                }
            }
        }

        static void RemoveSymbolizers(XElement style, Treatment treatment) {
            if (treatment == Treatment.Keep) {
                return;
            }
            var symbolizers = style.Elements("Rule").Elements()
                .Where(e => FillSymbolizers.Contains(e.Name.LocalName)
                    || (treatment == Treatment.RemoveFillsAndOutlines && OutlineSymbolizers.Contains(e.Name.LocalName)))
                .ToList();
            foreach (var symbolizer in symbolizers) {
                symbolizer.Remove();
            }
        }

        /// <summary>
        /// Selects the CJK font of the variant, as scripts/change-fonts-cjk.sh does before compiling, and shapes labels
        /// with the matching language: Noto Sans CJK picks regional glyph forms from the language ('locl'), and Mapnik
        /// shapes Han text as zh-Hans unless told otherwise.
        /// </summary>
        static void ApplyCjkVariant(XElement map, CjkVariant variant) {
            foreach (var attribute in map.Descendants().Attributes("face-name")) {
                attribute.Value = CjkVariants.ApplyToFontNames(attribute.Value, variant);
            }
            if (variant.ShapingLanguage() is { } language) {
                foreach (var symbolizer in map.Elements("Style").Elements("Rule").Elements().Where(e => TextSymbolizers.Contains(e.Name.LocalName))) {
                    symbolizer.SetAttributeValue("lang", language);
                }
            }
        }

        /// <summary>
        /// Scales the text of labels and shields. Shields scale as a whole: road shields are sized to fit their text,
        /// and the text of place labels is offset from the dot by dx/dy. Icons of point labels keep their size, so the
        /// offsets of text symbolizers from them are kept too.
        /// </summary>
        static void ApplyTextScale(XElement map, double scale) {
            var factor = Format(scale);
            foreach (var symbolizer in map.Elements("Style").Elements("Rule").Elements().Where(e => TextSymbolizers.Contains(e.Name.LocalName))) {
                foreach (var name in TextSizeAttributes) {
                    ScaleAttribute(symbolizer.Attribute(name), scale);
                }
                if (symbolizer.Name.LocalName == "ShieldSymbolizer") {
                    ScaleAttribute(symbolizer.Attribute("dx"), scale);
                    ScaleAttribute(symbolizer.Attribute("dy"), scale);
                    var transform = (string?)symbolizer.Attribute("transform");
                    symbolizer.SetAttributeValue("transform", string.IsNullOrWhiteSpace(transform) ? $"scale({factor})" : $"scale({factor}) {transform}");
                }
            }
        }

        static void ScaleAttribute(XAttribute? attribute, double scale) {
            if (attribute != null && double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                attribute.Value = Format(value * scale);
            }
        }

        static string Format(double value) {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static void RemoveUnusedStyles(XElement map) {
            var used = map.Elements("Layer").Elements("StyleName").Select(s => s.Value.Trim()).ToHashSet(StringComparer.Ordinal);
            foreach (var style in map.Elements("Style").Where(s => !used.Contains((string?)s.Attribute("name") ?? "")).ToList()) {
                style.Remove();
            }
        }
    }
}
