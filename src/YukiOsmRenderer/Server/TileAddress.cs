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
        /// Reads style options from query parameters: transparent, keepFill, hideOutline, hideLayers, cjk and textScale.
        /// List parameters accept comma separated values and may be repeated.
        /// </summary>
        /// <param name="defaultCjk">Variant used when the query has no cjk parameter.</param>
        /// <param name="defaultTextScale">Text scale used when the query has no textScale parameter.</param>
        /// <exception cref="ArgumentException">A parameter has an invalid value.</exception>
        public static StyleOptions Parse(IQueryCollection query, CjkVariant defaultCjk, double defaultTextScale = 1) {
            var cjk = defaultCjk;
            if (query.TryGetValue("cjk", out var cjkValues) && cjkValues.LastOrDefault() is { Length: > 0 } cjkText && !CjkVariants.TryParse(cjkText, out cjk)) {
                throw new ArgumentException($"Unknown CJK variant '{cjkText}'. Expected one of: {string.Join(", ", CjkVariants.Names)}.");
            }
            var textScale = defaultTextScale;
            if (query.TryGetValue("textScale", out var scaleValues) && scaleValues.LastOrDefault() is { Length: > 0 } scaleText
                && !StyleOptions.TryParseTextScale(scaleText, out textScale)) {
                throw new ArgumentException($"Invalid textScale '{scaleText}'. {StyleOptions.TextScaleRange}");
            }
            return StyleOptions.Create(
                Flag(query, "transparent"),
                query["keepFill"].OfType<string>(),
                Flag(query, "hideOutline"),
                query["hideLayers"].OfType<string>(),
                cjk,
                textScale);
        }

        /// <summary>Reads the tile format from the format query parameter.</summary>
        /// <exception cref="ArgumentException">The parameter has an invalid value.</exception>
        public static TileFormat ParseFormat(IQueryCollection query, TileFormat defaultFormat) {
            var format = defaultFormat;
            if (query.TryGetValue("format", out var values) && values.LastOrDefault() is { Length: > 0 } text && !TileFormats.TryParse(text, out format)) {
                throw new ArgumentException($"Unknown tile format '{text}'. Expected one of: {string.Join(", ", TileFormats.Names)}.");
            }
            return format;
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
