namespace YukiOsmRenderer.Core.Rendering {
    /// <summary>Encoding of PNG tiles.</summary>
    public enum TileFormat {
        /// <summary>At most 256 colors per tile, as on tile.openstreetmap.org.</summary>
        Png256,

        /// <summary>Full color RGBA.</summary>
        Png32,
    }

    public static class TileFormats {
        public static readonly IReadOnlyList<string> Names = ["png256", "png32"];

        public static string Name(this TileFormat format) {
            return format switch {
                TileFormat.Png256 => "png256",
                TileFormat.Png32 => "png32",
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            };
        }

        /// <summary>Mapnik image format string; png256 uses the settings of renderd for tile.openstreetmap.org.</summary>
        public static string MapnikFormat(this TileFormat format) {
            return format switch {
                TileFormat.Png256 => "png256:z=5",
                TileFormat.Png32 => "png32",
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            };
        }

        public static bool TryParse(string? text, out TileFormat format) {
            switch (text?.Trim().ToLowerInvariant()) {
                case "png256":
                    format = TileFormat.Png256;
                    return true;
                case "png32":
                    format = TileFormat.Png32;
                    return true;
                default:
                    format = default;
                    return false;
            }
        }

        public static TileFormat Parse(string text) {
            return TryParse(text, out var format)
                ? format
                : throw new FormatException($"Unknown tile format '{text}'. Expected one of: {string.Join(", ", Names)}.");
        }
    }
}
