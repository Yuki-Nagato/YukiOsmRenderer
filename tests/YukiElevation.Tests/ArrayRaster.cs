namespace YukiElevation.Tests {
    /// <summary>A raster in memory, indexed [row, column]; NaN marks pixels without data.</summary>
    sealed class ArrayRaster(double[,] values, GeoTransform transform) : IRaster {
        int reads;

        public int Width => values.GetLength(1);

        public int Height => values.GetLength(0);

        public GeoTransform Transform => transform;

        public int Reads => reads;

        public bool Disposed { get; private set; }

        /// <summary>A raster in degrees whose pixel centres start at (west, north), with the value of a function of the centres.</summary>
        public static ArrayRaster FromCenters(double west, double north, double pixelSize, int width, int height, Func<double, double, double> value) {
            var values = new double[height, width];
            for (var row = 0; row < height; row++) {
                for (var column = 0; column < width; column++) {
                    values[row, column] = value(west + column * pixelSize, north - row * pixelSize);
                }
            }
            return new ArrayRaster(values, new GeoTransform(west - pixelSize / 2, pixelSize, north + pixelSize / 2, -pixelSize));
        }

        public void Set(int column, int row, double value) {
            values[row, column] = value;
        }

        public void Read(int x, int y, int width, int height, Span<double> output) {
            Assert.That(x >= 0 && y >= 0 && x + width <= Width && y + height <= Height, $"read {x},{y} {width}x{height} outside the raster");
            Interlocked.Increment(ref reads);
            for (var row = 0; row < height; row++) {
                for (var column = 0; column < width; column++) {
                    output[row * width + column] = values[y + row, x + column];
                }
            }
        }

        public void Dispose() {
            Disposed = true;
        }
    }
}
