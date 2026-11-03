using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Core.Vector {
    /// <summary>Where the resources of a vector style are loaded from.</summary>
    public sealed record VectorStyleResources {
        /// <summary>Definition of the vector tile source, e.g. a pmtiles:// URL or a TileJSON URL.</summary>
        public required JsonObject TileSource { get; init; }

        /// <summary>Base URL of the glyphs/ and sprites/ directories, ending with a slash.</summary>
        public required string AssetBase { get; init; }

        /// <summary>
        /// file:// URLs of the regular and bold Noto Sans CJK collections. MapLibre Native draws CJK characters from
        /// them (selecting the face of the CJK variant); web clients, which use local fonts for CJK, leave them null.
        /// </summary>
        public (string Regular, string Bold)? CjkFonts { get; init; }
    }

    /// <summary>
    /// A MapLibre style for the Shortbread vector tiles, adapted to <see cref="StyleOptions"/>. Layer lists in the
    /// options are style layer ids, where * matches any characters.
    /// </summary>
    public sealed class VectorStyle {
        /// <summary>Prefix of resource URLs in the bundled styles, replaced by <see cref="VectorStyleResources.AssetBase"/>.</summary>
        public const string AssetPlaceholder = "yukiosm://";

        static readonly HashSet<string> AreaLayerTypes = ["background", "fill", "fill-extrusion", "hillshade", "raster", "color-relief"];

        // Characters drawn from Noto Sans CJK: CJK symbols, kana, bopomofo, ideographs, hangul and full-width forms.
        static readonly string[] CjkRanges = ["U+1100-11FF", "U+2E80-9FFF", "U+A960-A97F", "U+AC00-D7FF", "U+F900-FAFF", "U+FE30-FE4F", "U+FF00-FFEF"];

        const double DefaultTextSize = 16;

        readonly JsonObject template;

        VectorStyle(string name, JsonObject template) {
            Name = name;
            this.template = template;
            LayerIds = Layers(template).Select(l => (string?)l["id"] ?? "").ToList();
        }

        public string Name { get; }

        /// <summary>Layer ids in drawing order.</summary>
        public IReadOnlyList<string> LayerIds { get; }

        public static VectorStyle Load(string path) {
            return Parse(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path));
        }

        /// <exception cref="InvalidDataException">The JSON is not a MapLibre style.</exception>
        public static VectorStyle Parse(string name, string json) {
            JsonObject? root;
            try {
                root = JsonNode.Parse(json) as JsonObject;
            }
            catch (JsonException e) {
                throw new InvalidDataException($"The style '{name}' is not valid JSON: {e.Message}", e);
            }
            if (root?["layers"] is not JsonArray || root["sources"] is not JsonObject) {
                throw new InvalidDataException($"The style '{name}' is not a MapLibre style.");
            }
            return new VectorStyle(name, root);
        }

        /// <summary>Throws if a layer pattern of the options matches no layer of the style.</summary>
        public void Validate(StyleOptions options) {
            var unknown = options.KeepFillLayers.Concat(options.HiddenLayers).Where(p => !LayerIds.Any(id => Matches(p, id))).Distinct().ToList();
            if (unknown.Count > 0) {
                throw new ArgumentException($"No layer of the style '{Name}' matches: {string.Join(", ", unknown)}. Layers: {string.Join(", ", LayerIds)}.");
            }
        }

        /// <summary>Builds the style JSON for the options.</summary>
        public string Build(StyleOptions options, VectorStyleResources resources) {
            Validate(options);
            var style = (JsonObject)template.DeepClone();

            foreach (var (id, source) in style["sources"]!.AsObject().ToList()) {
                if ((string?)source?["type"] == "vector") {
                    style["sources"]![id] = resources.TileSource.DeepClone();
                }
            }
            ReplacePlaceholders(style, resources.AssetBase);
            style.Remove("sky");
            style.Remove("terrain");
            style["projection"] = new JsonObject { ["type"] = "mercator" };

            var layers = style["layers"]!.AsArray();
            foreach (var layer in Layers(style).Where(l => !Keep(l, options)).ToList()) {
                layers.Remove(layer);
            }
            if (options.HideOutlines) {
                foreach (var layer in Layers(style)) {
                    (layer["paint"] as JsonObject)?.Remove("fill-outline-color");
                }
            }
            if (options.TextScale != 1) {
                foreach (var layout in Layers(style).Where(l => (string?)l["type"] == "symbol").Select(l => l["layout"]).OfType<JsonObject>()) {
                    if (layout.ContainsKey("text-field")) {
                        layout["text-size"] = ScaleSize(layout["text-size"] ?? JsonValue.Create(DefaultTextSize), options.TextScale);
                    }
                }
            }
            if (resources.CjkFonts is var (regular, bold)) {
                var index = options.Cjk.CollectionIndex();
                var faces = new JsonObject();
                foreach (var font in FontNames(style)) {
                    var file = font.Contains("bold", StringComparison.OrdinalIgnoreCase) || font.Contains("black", StringComparison.OrdinalIgnoreCase) ? bold : regular;
                    faces[font] = new JsonArray(new JsonObject {
                        ["url"] = $"{file}#{index.ToString(CultureInfo.InvariantCulture)}",
                        ["unicode-range"] = new JsonArray(CjkRanges.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
                    });
                }
                style["font-faces"] = faces;
            }
            return style.ToJsonString();
        }

        /// <summary>Whether a layer id matches a pattern in which * stands for any characters.</summary>
        public static bool Matches(string pattern, string id) {
            var parts = pattern.Split('*');
            if (parts.Length == 1) {
                return string.Equals(pattern, id, StringComparison.Ordinal);
            }
            if (!id.StartsWith(parts[0], StringComparison.Ordinal) || !id.EndsWith(parts[^1], StringComparison.Ordinal)
                || id.Length < parts[0].Length + parts[^1].Length) {
                return false;
            }
            var position = parts[0].Length;
            var end = id.Length - parts[^1].Length;
            foreach (var part in parts[1..^1]) {
                var found = id.IndexOf(part, position, end - position, StringComparison.Ordinal);
                if (found < 0) {
                    return false;
                }
                position = found + part.Length;
            }
            return true;
        }

        /// <summary>
        /// Multiplies a text-size value: numbers, legacy functions with stops, and the outputs of zoom interpolations
        /// and steps (zoom expressions may not be nested in other expressions); other expressions are wrapped in a product.
        /// </summary>
        internal static JsonNode ScaleSize(JsonNode size, double scale) {
            switch (size) {
                case JsonValue value when value.TryGetValue<double>(out var number):
                    return JsonValue.Create(Math.Round(number * scale, 3));
                case JsonArray array when array.Count > 0 && (string?)array[0] is { } op:
                    var first = op switch {
                        "interpolate" or "interpolate-hcl" or "interpolate-lab" => 4,
                        "step" => 2,
                        _ => -1,
                    };
                    var scaled = (JsonArray)array.DeepClone();
                    if (first > 0) {
                        for (var i = first; i < scaled.Count; i += 2) {
                            scaled[i] = ScaleSize(scaled[i]!, scale);
                        }
                        return scaled;
                    }
                    // Zoom expressions are only allowed at the top level of interpolate and step.
                    if (array.ToJsonString().Contains("\"zoom\"", StringComparison.Ordinal)) {
                        return scaled;
                    }
                    return new JsonArray(JsonValue.Create("*"), JsonValue.Create(scale), scaled);
                case JsonObject function when function["stops"] is JsonArray stops:
                    var copy = (JsonObject)function.DeepClone();
                    foreach (var stop in copy["stops"]!.AsArray().OfType<JsonArray>().Where(s => s.Count == 2)) {
                        stop[1] = ScaleSize(stop[1]!, scale);
                    }
                    return copy;
                default:
                    return size.DeepClone();
            }
        }

        static bool Keep(JsonObject layer, StyleOptions options) {
            var id = (string?)layer["id"] ?? "";
            if (options.HiddenLayers.Any(p => Matches(p, id))) {
                return false;
            }
            if (options.Transparent && AreaLayerTypes.Contains((string?)layer["type"] ?? "")) {
                return options.KeepFillLayers.Any(p => Matches(p, id))
                    && !(options.HideOutlines && id.EndsWith(":outline", StringComparison.Ordinal));
            }
            return true;
        }

        static IEnumerable<JsonObject> Layers(JsonObject style) {
            return style["layers"]!.AsArray().OfType<JsonObject>();
        }

        /// <summary>Font names used by text-font, also inside literal arrays and expressions.</summary>
        static SortedSet<string> FontNames(JsonObject style) {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            void Collect(JsonNode? node) {
                switch (node) {
                    case JsonArray array:
                        foreach (var item in array) {
                            Collect(item);
                        }
                        break;
                    case JsonObject function when function["stops"] is JsonArray stops:
                        foreach (var stop in stops.OfType<JsonArray>()) {
                            Collect(stop.Count == 2 ? stop[1] : null);
                        }
                        break;
                    case JsonValue value when value.TryGetValue<string>(out var text) && text is not ("literal" or "case" or "match" or "step" or "zoom" or "get" or "coalesce"):
                        names.Add(text);
                        break;
                }
            }
            foreach (var layer in Layers(style)) {
                Collect(layer["layout"]?["text-font"]);
            }
            return names;
        }

        static void ReplacePlaceholders(JsonNode node, string assetBase) {
            switch (node) {
                case JsonObject obj:
                    foreach (var (key, value) in obj.ToList()) {
                        if (value is JsonValue v && v.TryGetValue<string>(out var text) && text.StartsWith(AssetPlaceholder, StringComparison.Ordinal)) {
                            obj[key] = assetBase + text[AssetPlaceholder.Length..];
                        }
                        else if (value != null) {
                            ReplacePlaceholders(value, assetBase);
                        }
                    }
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++) {
                        if (array[i] is JsonValue v && v.TryGetValue<string>(out var text) && text.StartsWith(AssetPlaceholder, StringComparison.Ordinal)) {
                            array[i] = assetBase + text[AssetPlaceholder.Length..];
                        }
                        else if (array[i] is { } child) {
                            ReplacePlaceholders(child, assetBase);
                        }
                    }
                    break;
            }
        }
    }
}
