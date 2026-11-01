namespace YukiOsmRenderer.Core.Geo {
    /// <summary>
    /// Spherical Web Mercator (EPSG:3857) helpers using the XYZ tile scheme with the origin at the top-left corner.
    /// </summary>
    public static class WebMercator {
        public const double EarthRadius = 6378137.0;

        /// <summary>Half the width of the projected world in meters.</summary>
        public const double OriginShift = Math.PI * EarthRadius;

        /// <summary>Latitude at which the projected world becomes square.</summary>
        public const double MaxLatitude = 85.05112877980659;

        public const int MaxZoom = 22;

        public const int DefaultTileSize = 256;

        /// <summary>Number of tiles along one side of a metatile, as used by renderd for tile.openstreetmap.org.</summary>
        public const int MetatileSize = 8;

        public static double LongitudeToX(double longitude) {
            return longitude * OriginShift / 180.0;
        }

        public static double LatitudeToY(double latitude) {
            latitude = Math.Clamp(latitude, -MaxLatitude, MaxLatitude);
            return Math.Log(Math.Tan((90.0 + latitude) * Math.PI / 360.0)) * EarthRadius;
        }

        public static double XToLongitude(double x) {
            return x / OriginShift * 180.0;
        }

        public static double YToLatitude(double y) {
            return Math.Atan(Math.Exp(y / EarthRadius)) * 360.0 / Math.PI - 90.0;
        }

        public static long TilesPerSide(int zoom) {
            ValidateZoom(zoom);
            return 1L << zoom;
        }

        /// <summary>Width and height of the whole world in pixels.</summary>
        public static long WorldSizeInPixels(int zoom, int tileSize = DefaultTileSize) {
            return TilesPerSide(zoom) * tileSize;
        }

        /// <summary>Converts a projected X coordinate to a global pixel column (fractional).</summary>
        public static double XToPixel(double x, int zoom, int tileSize = DefaultTileSize) {
            return (x + OriginShift) / (2 * OriginShift) * WorldSizeInPixels(zoom, tileSize);
        }

        /// <summary>Converts a projected Y coordinate to a global pixel row (fractional), growing southwards.</summary>
        public static double YToPixel(double y, int zoom, int tileSize = DefaultTileSize) {
            return (OriginShift - y) / (2 * OriginShift) * WorldSizeInPixels(zoom, tileSize);
        }

        public static double PixelToX(double pixel, int zoom, int tileSize = DefaultTileSize) {
            return pixel / WorldSizeInPixels(zoom, tileSize) * 2 * OriginShift - OriginShift;
        }

        public static double PixelToY(double pixel, int zoom, int tileSize = DefaultTileSize) {
            return OriginShift - pixel / WorldSizeInPixels(zoom, tileSize) * 2 * OriginShift;
        }

        /// <summary>Size of one pixel in projected meters.</summary>
        public static double Resolution(int zoom, int tileSize = DefaultTileSize) {
            return 2 * OriginShift / WorldSizeInPixels(zoom, tileSize);
        }

        /// <summary>Projected extent of a range of tiles.</summary>
        public static ProjectedExtent TileRangeExtent(int zoom, long minTileX, long minTileY, long tileCountX, long tileCountY) {
            var n = TilesPerSide(zoom);
            var span = 2 * OriginShift / n;
            return new ProjectedExtent(
                -OriginShift + minTileX * span,
                OriginShift - (minTileY + tileCountY) * span,
                -OriginShift + (minTileX + tileCountX) * span,
                OriginShift - minTileY * span);
        }

        public static ProjectedExtent TileExtent(int zoom, long tileX, long tileY) {
            return TileRangeExtent(zoom, tileX, tileY, 1, 1);
        }

        public static bool IsValidTile(int zoom, long x, long y) {
            if (zoom < 0 || zoom > MaxZoom) {
                return false;
            }
            var n = 1L << zoom;
            return x >= 0 && x < n && y >= 0 && y < n;
        }

        /// <summary>Number of tiles along one side of a metatile at the given zoom (smaller than 8 at zoom 0 to 2).</summary>
        public static int MetatileSizeAt(int zoom) {
            return (int)Math.Min(MetatileSize, TilesPerSide(zoom));
        }

        public static void ValidateZoom(int zoom) {
            if (zoom < 0 || zoom > MaxZoom) {
                throw new ArgumentOutOfRangeException(nameof(zoom), zoom, $"Zoom level must be between 0 and {MaxZoom}.");
            }
        }
    }

    /// <summary>Rectangle in EPSG:3857 meters.</summary>
    public readonly record struct ProjectedExtent(double MinX, double MinY, double MaxX, double MaxY) {
        public double Width => MaxX - MinX;

        public double Height => MaxY - MinY;
    }
}
