using System.Globalization;

namespace YukiOsmRenderer.Core.Geo {
    /// <summary>Geographic bounding box in WGS 84 degrees.</summary>
    public readonly record struct GeoBoundingBox {
        public static readonly GeoBoundingBox World = new(-180, -WebMercator.MaxLatitude, 180, WebMercator.MaxLatitude);

        public GeoBoundingBox(double minLongitude, double minLatitude, double maxLongitude, double maxLatitude) {
            if (!double.IsFinite(minLongitude) || !double.IsFinite(minLatitude) || !double.IsFinite(maxLongitude) || !double.IsFinite(maxLatitude)) {
                throw new ArgumentException("Bounding box coordinates must be finite numbers.");
            }
            if (minLongitude < -180 || maxLongitude > 180) {
                throw new ArgumentException("Longitudes must be between -180 and 180.");
            }
            if (minLatitude < -90 || maxLatitude > 90) {
                throw new ArgumentException("Latitudes must be between -90 and 90.");
            }
            if (minLongitude >= maxLongitude) {
                throw new ArgumentException("The minimum longitude must be less than the maximum longitude (crossing the antimeridian is not supported).");
            }
            if (minLatitude >= maxLatitude) {
                throw new ArgumentException("The minimum latitude must be less than the maximum latitude.");
            }
            MinLongitude = minLongitude;
            MinLatitude = minLatitude;
            MaxLongitude = maxLongitude;
            MaxLatitude = maxLatitude;
        }

        public double MinLongitude { get; }

        public double MinLatitude { get; }

        public double MaxLongitude { get; }

        public double MaxLatitude { get; }

        /// <summary>Parses "minLon,minLat,maxLon,maxLat" using invariant culture.</summary>
        public static GeoBoundingBox Parse(string text) {
            ArgumentNullException.ThrowIfNull(text);
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 4) {
                throw new FormatException("A bounding box must be given as minLon,minLat,maxLon,maxLat.");
            }
            var values = new double[4];
            for (var i = 0; i < 4; i++) {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) {
                    throw new FormatException($"'{parts[i]}' is not a valid number.");
                }
            }
            return new GeoBoundingBox(values[0], values[1], values[2], values[3]);
        }

        public override string ToString() {
            return string.Create(CultureInfo.InvariantCulture, $"{MinLongitude},{MinLatitude},{MaxLongitude},{MaxLatitude}");
        }
    }
}
