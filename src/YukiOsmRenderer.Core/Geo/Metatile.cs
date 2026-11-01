namespace YukiOsmRenderer.Core.Geo {
    /// <summary>
    /// A block of tiles rendered in one pass, aligned to the global metatile grid like renderd does for
    /// tile.openstreetmap.org, so that label placement matches the tiles served there.
    /// </summary>
    /// <param name="Zoom">Zoom level.</param>
    /// <param name="X">Column of the top-left tile (a multiple of <paramref name="Size"/>).</param>
    /// <param name="Y">Row of the top-left tile (a multiple of <paramref name="Size"/>).</param>
    /// <param name="Size">Number of tiles along each side.</param>
    public readonly record struct Metatile(int Zoom, long X, long Y, int Size) {
        /// <summary>Returns the metatile containing the given tile.</summary>
        public static Metatile Containing(int zoom, long tileX, long tileY) {
            if (!WebMercator.IsValidTile(zoom, tileX, tileY)) {
                throw new ArgumentOutOfRangeException(nameof(tileX), $"Tile {zoom}/{tileX}/{tileY} does not exist.");
            }
            var size = WebMercator.MetatileSizeAt(zoom);
            return new Metatile(zoom, tileX / size * size, tileY / size * size, size);
        }

        public ProjectedExtent Extent => WebMercator.TileRangeExtent(Zoom, X, Y, Size, Size);

        public int PixelSize(int tileSize) {
            return Size * tileSize;
        }

        public bool Contains(long tileX, long tileY) {
            return tileX >= X && tileX < X + Size && tileY >= Y && tileY < Y + Size;
        }
    }
}
