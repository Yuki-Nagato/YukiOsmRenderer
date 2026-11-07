namespace YukiElevation {
    /// <summary>
    /// Rasters in degrees that are parts of one grid, such as the eight GeoTIFFs of GEBCO. A grid that spans all
    /// longitudes wraps around at the antimeridian.
    /// </summary>
    internal sealed class GridSource : ElevationSource {
        readonly Tile[] tiles;
        readonly double originX;
        readonly double originY;
        readonly double pixelWidth;
        readonly double pixelHeight;
        readonly long columns;
        readonly bool global;

        sealed record Tile(IRaster Raster, long Column, long Row) {
            public bool Contains(long column, long row) {
                return Raster.Contains(column - Column, row - Row);
            }
        }

        GridSource(string name, Tile[] tiles, double originX, double originY, double pixelWidth, double pixelHeight, long columns, long rows) {
            Name = name;
            this.tiles = tiles;
            this.originX = originX;
            this.originY = originY;
            this.pixelWidth = pixelWidth;
            this.pixelHeight = pixelHeight;
            this.columns = columns;
            Rows = rows;
            global = Math.Abs(columns * pixelWidth - 360) < pixelWidth / 2;
        }

        public override string Name { get; }

        public int FileCount => tiles.Length;

        public long Columns => columns;

        public long Rows { get; }

        /// <summary>Takes ownership of <paramref name="rasters"/> if it succeeds.</summary>
        /// <exception cref="InvalidDataException">The rasters do not fit one grid.</exception>
        public static GridSource Create(string name, IReadOnlyList<IRaster> rasters) {
            if (rasters.Count == 0) {
                throw new InvalidDataException("There are no rasters.");
            }
            var pixelWidth = rasters[0].Transform.PixelWidth;
            var pixelHeight = -rasters[0].Transform.PixelHeight;
            if (pixelWidth <= 0 || pixelHeight <= 0) {
                throw new InvalidDataException("The rasters must be north up.");
            }
            foreach (var raster in rasters) {
                if (Math.Abs(raster.Transform.PixelWidth - pixelWidth) > pixelWidth * 1e-6
                    || Math.Abs(-raster.Transform.PixelHeight - pixelHeight) > pixelHeight * 1e-6) {
                    throw new InvalidDataException("The rasters have different resolutions.");
                }
            }
            var originX = rasters.Min(r => r.Transform.OriginX);
            var originY = rasters.Max(r => r.Transform.OriginY);
            var tiles = rasters.Select(r => new Tile(r, Offset((r.Transform.OriginX - originX) / pixelWidth),
                Offset((originY - r.Transform.OriginY) / pixelHeight))).ToArray();
            var columns = tiles.Max(t => t.Column + t.Raster.Width);
            var rows = tiles.Max(t => t.Row + t.Raster.Height);
            return new GridSource(name, tiles, originX, originY, pixelWidth, pixelHeight, columns, rows);
        }

        static long Offset(double pixels) {
            var rounded = Math.Round(pixels);
            if (Math.Abs(pixels - rounded) > 0.01) {
                throw new InvalidDataException("The rasters are not aligned to one grid.");
            }
            return (long)rounded;
        }

        public override double? Sample(double longitude, double latitude, Interpolation interpolation) {
            var x = (longitude - originX) / pixelWidth - 0.5;
            var y = (originY - latitude) / pixelHeight - 0.5;
            var x0 = (long)Math.Floor(x);
            var y0 = (long)Math.Floor(y);
            Span<double> values = stackalloc double[4];
            var column = Wrap(x0);
            if (Wrap(x0 + 1) == column + 1 && Find(column, y0) is { } tile && tile.Contains(column + 1, y0 + 1)) {
                tile.Raster.Read((int)(column - tile.Column), (int)(y0 - tile.Row), 2, 2, values);
            }
            else {
                for (var i = 0; i < 4; i++) {
                    values[i] = Pixel(x0 + (i & 1), y0 + (i >> 1));
                }
            }
            return Combine(values, x - x0, y - y0, interpolation);
        }

        double Pixel(long column, long row) {
            column = Wrap(column);
            return Find(column, row) is { } tile ? tile.Raster.Read(column - tile.Column, row - tile.Row) : double.NaN;
        }

        long Wrap(long column) {
            return global ? (column % columns + columns) % columns : column;
        }

        Tile? Find(long column, long row) {
            foreach (var tile in tiles) {
                if (tile.Contains(column, row)) {
                    return tile;
                }
            }
            return null;
        }

        public override void Dispose() {
            foreach (var tile in tiles) {
                tile.Raster.Dispose();
            }
        }
    }
}
