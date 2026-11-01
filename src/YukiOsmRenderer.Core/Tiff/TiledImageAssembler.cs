namespace YukiOsmRenderer.Core.Tiff {
    /// <summary>
    /// Collects rendered pixel blocks (for example metatiles) that are not aligned to the TIFF tile grid and hands
    /// out each TIFF tile as soon as all of its pixels have arrived, so that memory usage stays bounded.
    /// </summary>
    public sealed class TiledImageAssembler {
        readonly long imageWidth;
        readonly long imageHeight;
        readonly int tileSize;
        readonly int channels;
        readonly long tileColumns;
        readonly Action<long, long, byte[]> tileCompleted;
        readonly Dictionary<long, PendingTile> pending = [];
        readonly Lock sync = new();
        long completedTiles;

        /// <param name="imageWidth">Image width in pixels.</param>
        /// <param name="imageHeight">Image height in pixels.</param>
        /// <param name="tileSize">TIFF tile width and height.</param>
        /// <param name="channels">3 to drop the alpha channel of the input, 4 to keep it.</param>
        /// <param name="tileCompleted">Receives tile column, tile row and the tile pixels; called outside of any lock.</param>
        public TiledImageAssembler(long imageWidth, long imageHeight, int tileSize, int channels, Action<long, long, byte[]> tileCompleted) {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageWidth);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(imageHeight);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileSize);
            if (channels is not (3 or 4)) {
                throw new ArgumentOutOfRangeException(nameof(channels), "Only 3 or 4 channels are supported.");
            }
            ArgumentNullException.ThrowIfNull(tileCompleted);
            this.imageWidth = imageWidth;
            this.imageHeight = imageHeight;
            this.tileSize = tileSize;
            this.channels = channels;
            this.tileCompleted = tileCompleted;
            tileColumns = (imageWidth + tileSize - 1) / tileSize;
            TotalTiles = tileColumns * ((imageHeight + tileSize - 1) / tileSize);
        }

        public long TotalTiles { get; }

        public long CompletedTiles {
            get {
                lock (sync) {
                    return completedTiles;
                }
            }
        }

        public int PendingTiles {
            get {
                lock (sync) {
                    return pending.Count;
                }
            }
        }

        /// <summary>
        /// Adds a block of straight RGBA pixels whose top-left corner is at (<paramref name="x"/>, <paramref name="y"/>)
        /// in image coordinates. Parts outside of the image are ignored. Every image pixel must be added exactly once.
        /// Thread-safe.
        /// </summary>
        public void Add(long x, long y, int width, int height, ReadOnlySpan<byte> rgba) {
            if (rgba.Length < (long)width * height * 4) {
                throw new ArgumentException("The pixel buffer is smaller than width * height * 4 bytes.", nameof(rgba));
            }
            var left = Math.Max(x, 0);
            var top = Math.Max(y, 0);
            var right = Math.Min(x + width, imageWidth);
            var bottom = Math.Min(y + height, imageHeight);
            if (left >= right || top >= bottom) {
                return;
            }
            List<(long Column, long Row, byte[] Pixels)>? completed = null;
            lock (sync) {
                for (var row = top / tileSize; row <= (bottom - 1) / tileSize; row++) {
                    for (var column = left / tileSize; column <= (right - 1) / tileSize; column++) {
                        var tileLeft = column * tileSize;
                        var tileTop = row * tileSize;
                        var overlapLeft = Math.Max(left, tileLeft);
                        var overlapRight = Math.Min(right, tileLeft + tileSize);
                        var overlapTop = Math.Max(top, tileTop);
                        var overlapBottom = Math.Min(bottom, tileTop + tileSize);
                        var key = row * tileColumns + column;
                        if (!pending.TryGetValue(key, out var tile)) {
                            var visibleWidth = Math.Min(tileSize, imageWidth - tileLeft);
                            var visibleHeight = Math.Min(tileSize, imageHeight - tileTop);
                            tile = new PendingTile(new byte[tileSize * tileSize * channels], visibleWidth * visibleHeight);
                            pending.Add(key, tile);
                        }
                        var count = (int)(overlapRight - overlapLeft);
                        for (var line = overlapTop; line < overlapBottom; line++) {
                            var source = rgba.Slice((int)(((line - y) * width + (overlapLeft - x)) * 4), count * 4);
                            var destination = tile.Pixels.AsSpan((int)(((line - tileTop) * tileSize + (overlapLeft - tileLeft)) * channels), count * channels);
                            CopyPixels(source, destination);
                        }
                        tile.Remaining -= count * (overlapBottom - overlapTop);
                        if (tile.Remaining < 0) {
                            throw new InvalidOperationException($"Pixels of tile {column},{row} were added more than once.");
                        }
                        if (tile.Remaining == 0) {
                            pending.Remove(key);
                            completedTiles++;
                            (completed ??= []).Add((column, row, tile.Pixels));
                        }
                    }
                }
            }
            if (completed != null) {
                foreach (var (column, row, pixels) in completed) {
                    tileCompleted(column, row, pixels);
                }
            }
        }

        void CopyPixels(ReadOnlySpan<byte> rgba, Span<byte> destination) {
            if (channels == 4) {
                rgba.CopyTo(destination);
                return;
            }
            for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3) {
                destination[j] = rgba[i];
                destination[j + 1] = rgba[i + 1];
                destination[j + 2] = rgba[i + 2];
            }
        }

        sealed class PendingTile(byte[] pixels, long remaining) {
            public byte[] Pixels { get; } = pixels;

            public long Remaining { get; set; } = remaining;
        }
    }
}
