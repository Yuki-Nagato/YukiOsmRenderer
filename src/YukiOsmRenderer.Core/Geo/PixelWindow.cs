namespace YukiOsmRenderer.Core.Geo {
    /// <summary>
    /// Rectangular region of the global pixel grid at a zoom level. The global grid has its origin at the
    /// top-left corner of the Web Mercator world and is <c>2^zoom * tileSize</c> pixels wide.
    /// </summary>
    public readonly record struct PixelWindow {
        // Tolerance that keeps floating point noise from adding a row or column of pixels.
        const double Epsilon = 1e-6;

        public PixelWindow(int zoom, int tileSize, long x, long y, long width, long height) {
            WebMercator.ValidateZoom(zoom);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileSize);
            var worldSize = WebMercator.WorldSizeInPixels(zoom, tileSize);
            if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > worldSize || y + height > worldSize) {
                throw new ArgumentOutOfRangeException(nameof(x), "The pixel window must lie within the world.");
            }
            Zoom = zoom;
            TileSize = tileSize;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public int Zoom { get; }

        public int TileSize { get; }

        /// <summary>Global pixel column of the left edge.</summary>
        public long X { get; }

        /// <summary>Global pixel row of the top edge.</summary>
        public long Y { get; }

        public long Width { get; }

        public long Height { get; }

        public long PixelCount => Width * Height;

        /// <summary>Projected extent covered by the window.</summary>
        public ProjectedExtent Extent => new(
            WebMercator.PixelToX(X, Zoom, TileSize),
            WebMercator.PixelToY(Y + Height, Zoom, TileSize),
            WebMercator.PixelToX(X + Width, Zoom, TileSize),
            WebMercator.PixelToY(Y, Zoom, TileSize));

        /// <summary>Size of one pixel in projected meters.</summary>
        public double Resolution => WebMercator.Resolution(Zoom, TileSize);

        /// <summary>Smallest pixel window that covers the bounding box.</summary>
        public static PixelWindow FromBoundingBox(GeoBoundingBox box, int zoom, int tileSize = WebMercator.DefaultTileSize) {
            var worldSize = WebMercator.WorldSizeInPixels(zoom, tileSize);
            var left = WebMercator.XToPixel(WebMercator.LongitudeToX(box.MinLongitude), zoom, tileSize);
            var right = WebMercator.XToPixel(WebMercator.LongitudeToX(box.MaxLongitude), zoom, tileSize);
            var top = WebMercator.YToPixel(WebMercator.LatitudeToY(box.MaxLatitude), zoom, tileSize);
            var bottom = WebMercator.YToPixel(WebMercator.LatitudeToY(box.MinLatitude), zoom, tileSize);
            var x0 = Math.Clamp((long)Math.Floor(left + Epsilon), 0, worldSize - 1);
            var y0 = Math.Clamp((long)Math.Floor(top + Epsilon), 0, worldSize - 1);
            var x1 = Math.Clamp((long)Math.Ceiling(right - Epsilon), x0 + 1, worldSize);
            var y1 = Math.Clamp((long)Math.Ceiling(bottom - Epsilon), y0 + 1, worldSize);
            return new PixelWindow(zoom, tileSize, x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>Metatiles intersecting the window, in row-major order.</summary>
        public IEnumerable<Metatile> CoveringMetatiles() {
            var size = WebMercator.MetatileSizeAt(Zoom);
            var firstColumn = X / TileSize / size * size;
            var lastColumn = (X + Width - 1) / TileSize;
            var firstRow = Y / TileSize / size * size;
            var lastRow = (Y + Height - 1) / TileSize;
            for (var row = firstRow; row <= lastRow; row += size) {
                for (var column = firstColumn; column <= lastColumn; column += size) {
                    yield return new Metatile(Zoom, column, row, size);
                }
            }
        }

        public long CoveringMetatileCount() {
            var size = WebMercator.MetatileSizeAt(Zoom);
            var columns = (X + Width - 1) / TileSize / size - X / TileSize / size + 1;
            var rows = (Y + Height - 1) / TileSize / size - Y / TileSize / size + 1;
            return columns * rows;
        }
    }
}
