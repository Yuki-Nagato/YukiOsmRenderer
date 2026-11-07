using OSGeo.GDAL;

namespace YukiElevation {
    /// <summary>An affine transform without rotation, as GDAL reports it: the origin is the outer corner of the top-left pixel.</summary>
    internal readonly record struct GeoTransform(double OriginX, double PixelWidth, double OriginY, double PixelHeight) {
        /// <summary>Pixel coordinates of a point, with pixel centres at whole numbers.</summary>
        public (double X, double Y) ToPixel(double x, double y) {
            return ((x - OriginX) / PixelWidth - 0.5, (y - OriginY) / PixelHeight - 0.5);
        }

        public (double X, double Y) CenterOf(long column, long row) {
            return (OriginX + (column + 0.5) * PixelWidth, OriginY + (row + 0.5) * PixelHeight);
        }
    }

    /// <summary>A single-band elevation raster that can be read from several threads at once.</summary>
    internal interface IRaster : IDisposable {
        int Width { get; }

        int Height { get; }

        GeoTransform Transform { get; }

        /// <summary>Reads a window row by row; pixels without data become NaN.</summary>
        void Read(int x, int y, int width, int height, Span<double> values);
    }

    internal static class RasterExtensions {
        public static bool Contains(this IRaster raster, long column, long row) {
            return column >= 0 && row >= 0 && column < raster.Width && row < raster.Height;
        }

        public static double Read(this IRaster raster, long column, long row) {
            Span<double> value = stackalloc double[1];
            raster.Read((int)column, (int)row, 1, 1, value);
            return value[0];
        }
    }

    internal sealed class GdalRaster : IRaster {
        readonly Dataset dataset;
        readonly Band band;
        readonly double? noData;

        GdalRaster(string path, Dataset dataset) {
            Path = path;
            this.dataset = dataset;
            Width = dataset.RasterXSize;
            Height = dataset.RasterYSize;
            var transform = new double[6];
            dataset.GetGeoTransform(transform);
            if (transform[2] != 0 || transform[4] != 0) {
                throw new InvalidDataException($"'{path}' is rotated, which is not supported.");
            }
            Transform = new GeoTransform(transform[0], transform[1], transform[3], transform[5]);
            band = dataset.GetRasterBand(1);
            band.GetNoDataValue(out var value, out var hasValue);
            noData = hasValue != 0 ? value : null;
        }

        public string Path { get; }

        public int Width { get; }

        public int Height { get; }

        public GeoTransform Transform { get; }

        /// <exception cref="IOException">GDAL cannot open the file.</exception>
        public static GdalRaster Open(string path) {
            var flags = (uint)(GdalConst.OF_RASTER | GdalConst.OF_READONLY | GdalConst.OF_THREAD_SAFE);
            Dataset? dataset;
            try {
                dataset = Gdal.OpenEx(path, flags, null, null, null);
            }
            catch (ApplicationException e) {
                throw new IOException($"Cannot open '{path}': {e.Message}", e);
            }
            if (dataset == null) {
                throw new IOException($"Cannot open '{path}': {Gdal.GetLastErrorMsg()}");
            }
            try {
                return new GdalRaster(path, dataset);
            }
            catch {
                dataset.Dispose();
                throw;
            }
        }

        public void Read(int x, int y, int width, int height, Span<double> values) {
            var buffer = new double[width * height];
            band.ReadRaster(x, y, width, height, buffer, width, height, 0, 0);
            for (var i = 0; i < buffer.Length; i++) {
                values[i] = buffer[i] == noData ? double.NaN : buffer[i];
            }
        }

        public void Dispose() {
            band.Dispose();
            dataset.Dispose();
        }
    }
}
