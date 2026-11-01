namespace YukiOsmRenderer.Core.Styling {
    /// <summary>
    /// Options that adapt the compiled OpenStreetMap Carto style. Instances are normalized so that equal options
    /// produce equal <see cref="Key"/>s, which are used to cache style variants.
    /// </summary>
    public sealed record StyleOptions {
        public static readonly StyleOptions Default = new();

        /// <summary>
        /// Drops the map background and all area fills and patterns so the map can be overlaid on imagery.
        /// Roads, lines, labels and icons are kept.
        /// </summary>
        public bool Transparent { get; init; }

        /// <summary>Layers whose fills (and outlines) are kept in transparent mode, e.g. water-areas or buildings.</summary>
        public IReadOnlySet<string> KeepFillLayers { get; init; } = new SortedSet<string>(StringComparer.Ordinal);

        /// <summary>In transparent mode, also drops the outlines of area layers.</summary>
        public bool HideOutlines { get; init; }

        /// <summary>Layers that are removed entirely (in any mode).</summary>
        public IReadOnlySet<string> HiddenLayers { get; init; } = new SortedSet<string>(StringComparer.Ordinal);

        /// <summary>Stable textual representation, suitable as a cache key.</summary>
        public string Key {
            get {
                var parts = new List<string>();
                if (Transparent) {
                    parts.Add("transparent");
                    if (KeepFillLayers.Count > 0) {
                        parts.Add("keep=" + string.Join(',', KeepFillLayers.Order(StringComparer.Ordinal)));
                    }
                    if (HideOutlines) {
                        parts.Add("no-outline");
                    }
                }
                if (HiddenLayers.Count > 0) {
                    parts.Add("hide=" + string.Join(',', HiddenLayers.Order(StringComparer.Ordinal)));
                }
                return parts.Count == 0 ? "default" : string.Join(';', parts);
            }
        }

        /// <summary>Creates normalized options. Fill and outline options are ignored unless the map is transparent.</summary>
        public static StyleOptions Create(bool transparent, IEnumerable<string>? keepFillLayers = null, bool hideOutlines = false, IEnumerable<string>? hiddenLayers = null) {
            return new StyleOptions {
                Transparent = transparent,
                KeepFillLayers = transparent ? Normalize(keepFillLayers) : Normalize(null),
                HideOutlines = transparent && hideOutlines,
                HiddenLayers = Normalize(hiddenLayers),
            };
        }

        /// <summary>Splits comma separated layer lists such as "water-areas,buildings".</summary>
        public static IEnumerable<string> SplitLayerList(IEnumerable<string>? values) {
            if (values == null) {
                yield break;
            }
            foreach (var value in values) {
                foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                    yield return part;
                }
            }
        }

        /// <summary>Throws if the options reference layers that do not exist in the style.</summary>
        public void Validate(IReadOnlyCollection<string> knownLayers) {
            var unknown = KeepFillLayers.Concat(HiddenLayers).Where(l => !knownLayers.Contains(l)).Distinct().ToList();
            if (unknown.Count > 0) {
                throw new ArgumentException($"Unknown layer(s): {string.Join(", ", unknown)}. Known layers: {string.Join(", ", knownLayers)}.");
            }
        }

        public bool Equals(StyleOptions? other) {
            return other is not null && Key == other.Key;
        }

        public override int GetHashCode() {
            return Key.GetHashCode(StringComparison.Ordinal);
        }

        public override string ToString() {
            return Key;
        }

        static SortedSet<string> Normalize(IEnumerable<string>? layers) {
            return new SortedSet<string>(SplitLayerList(layers), StringComparer.Ordinal);
        }
    }
}
