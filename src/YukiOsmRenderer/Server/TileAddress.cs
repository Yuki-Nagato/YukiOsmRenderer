using System.Globalization;
using Microsoft.AspNetCore.Http;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Server {
    /// <summary>Address of an XYZ tile; <see cref="TileSize"/> is 512 for "@2x" tiles.</summary>
    internal readonly record struct TileAddress(int Zoom, long X, long Y, int TileSize) {
        /// <summary>Parses the last path segment, "{y}.png" or "{y}@2x.png", together with zoom and column.</summary>
        public static bool TryParse(int zoom, long x, string ySegment, out TileAddress address) {
            address = default;
            int tileSize;
            string number;
            if (ySegment.EndsWith("@2x.png", StringComparison.Ordinal)) {
                tileSize = TileSizes.HighResolution;
                number = ySegment[..^"@2x.png".Length];
            }
            else if (ySegment.EndsWith(".png", StringComparison.Ordinal)) {
                tileSize = TileSizes.Standard;
                number = ySegment[..^".png".Length];
            }
            else {
                return false;
            }
            if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var y) || !WebMercator.IsValidTile(zoom, x, y)) {
                return false;
            }
            address = new TileAddress(zoom, x, y, tileSize);
            return true;
        }
    }

    internal static class StyleQuery {
        /// <summary>
        /// Reads style options from query parameters: transparent, keepFill, hideOutline and hideLayers. List
        /// parameters accept comma separated values and may be repeated.
        /// </summary>
        public static StyleOptions Parse(IQueryCollection query) {
            return StyleOptions.Create(
                Flag(query, "transparent"),
                query["keepFill"].OfType<string>(),
                Flag(query, "hideOutline"),
                query["hideLayers"].OfType<string>());
        }

        static bool Flag(IQueryCollection query, string name) {
            if (!query.TryGetValue(name, out var values)) {
                return false;
            }
            var value = values.LastOrDefault() ?? "";
            return value.Length == 0 || value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
