using YamlDotNet.RepresentationModel;

namespace YukiOsmRenderer.Core.Styling {
    public enum LayerGeometry {
        Unknown,
        Point,
        LineString,
        Polygon,
        Raster,
    }

    public sealed record MmlLayer(string Id, LayerGeometry Geometry);

    /// <summary>Layer metadata read from a CartoCSS project file (project.mml).</summary>
    public sealed class MmlProject {
        MmlProject(IReadOnlyList<MmlLayer> layers) {
            Layers = layers;
            LayerIds = layers.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        }

        public IReadOnlyList<MmlLayer> Layers { get; }

        public IReadOnlySet<string> LayerIds { get; }

        public static MmlProject Load(string path) {
            using var reader = File.OpenText(path);
            return Parse(reader);
        }

        public static MmlProject Parse(TextReader reader) {
            var stream = new YamlStream();
            stream.Load(reader);
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) {
                throw new InvalidDataException("The project file is not a YAML mapping.");
            }
            if (!root.Children.TryGetValue(new YamlScalarNode("Layer"), out var layerNode) || layerNode is not YamlSequenceNode layerSequence) {
                throw new InvalidDataException("The project file has no Layer list.");
            }
            var layers = new List<MmlLayer>();
            foreach (var item in layerSequence) {
                if (item is not YamlMappingNode layer) {
                    continue;
                }
                var id = Scalar(layer, "id") ?? throw new InvalidDataException("A layer has no id.");
                var geometry = Scalar(layer, "geometry") switch {
                    "point" => LayerGeometry.Point,
                    "linestring" => LayerGeometry.LineString,
                    "polygon" => LayerGeometry.Polygon,
                    "raster" => LayerGeometry.Raster,
                    _ => LayerGeometry.Unknown,
                };
                layers.Add(new MmlLayer(id, geometry));
            }
            return new MmlProject(layers);
        }

        public LayerGeometry GeometryOf(string layerId) {
            return Layers.FirstOrDefault(l => l.Id == layerId)?.Geometry ?? LayerGeometry.Unknown;
        }

        static string? Scalar(YamlMappingNode node, string key) {
            return node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value : null;
        }
    }
}
