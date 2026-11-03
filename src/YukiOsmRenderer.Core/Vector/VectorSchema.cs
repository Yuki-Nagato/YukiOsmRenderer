using YamlDotNet.RepresentationModel;

namespace YukiOsmRenderer.Core.Vector {
    /// <summary>
    /// Prepares a Planetiler schema for a build: points the "ocean" source at the bundled water polygons and the "osm"
    /// source at the input file. Download URLs and test examples are removed, since builds are offline and run elsewhere.
    /// </summary>
    public static class VectorSchema {
        public const string OsmSource = "osm";
        public const string OceanSource = "ocean";

        /// <exception cref="InvalidDataException">The schema has no "osm" source.</exception>
        public static string Prepare(string yaml, string inputFile, string waterPolygons) {
            var stream = new YamlStream();
            using (var reader = new StringReader(yaml)) {
                stream.Load(reader);
            }
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root) {
                throw new InvalidDataException("The Planetiler schema must be a single YAML mapping.");
            }
            root.Children.Remove(new YamlScalarNode("examples"));
            if (!root.Children.TryGetValue(new YamlScalarNode("sources"), out var sourcesNode) || sourcesNode is not YamlMappingNode sources
                || !sources.Children.TryGetValue(new YamlScalarNode(OsmSource), out var osmNode) || osmNode is not YamlMappingNode osm) {
                throw new InvalidDataException($"The Planetiler schema must define the source '{OsmSource}' (type osm), which reads the input files.");
            }
            if (sources.Children.TryGetValue(new YamlScalarNode(OceanSource), out var oceanNode) && oceanNode is YamlMappingNode ocean) {
                SetLocalPath(ocean, waterPolygons);
            }
            SetLocalPath(osm, inputFile);

            using var writer = new StringWriter();
            stream.Save(writer, assignAnchors: false);
            return writer.ToString();
        }

        static void SetLocalPath(YamlMappingNode source, string path) {
            source.Children.Remove(new YamlScalarNode("url"));
            // Single-quoted so that Windows paths need no escaping.
            source.Children[new YamlScalarNode("local_path")] = new YamlScalarNode(Path.GetFullPath(path)) { Style = YamlDotNet.Core.ScalarStyle.SingleQuoted };
        }
    }
}
