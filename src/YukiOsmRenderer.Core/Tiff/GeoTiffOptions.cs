using System.IO.Compression;

namespace YukiOsmRenderer.Core.Tiff {
    public enum TiffCompression {
        None,
        Deflate,
    }

    public enum BigTiffMode {
        /// <summary>Use BigTIFF only when the file could exceed the 4 GiB limit of classic TIFF.</summary>
        Auto,
        Always,
        Never,
    }

    /// <summary>Placement of the raster in EPSG:3857.</summary>
    /// <param name="OriginX">Projected X coordinate of the left edge of the image.</param>
    /// <param name="OriginY">Projected Y coordinate of the top edge of the image.</param>
    /// <param name="PixelSizeX">Width of a pixel in meters.</param>
    /// <param name="PixelSizeY">Height of a pixel in meters (positive).</param>
    public sealed record GeoReference(double OriginX, double OriginY, double PixelSizeX, double PixelSizeY);

    public sealed record GeoTiffOptions {
        public required long Width { get; init; }

        public required long Height { get; init; }

        /// <summary>3 for RGB or 4 for RGBA with unassociated alpha.</summary>
        public int Channels { get; init; } = 4;

        /// <summary>Tile width and height; must be a multiple of 16.</summary>
        public int TileSize { get; init; } = 256;

        public TiffCompression Compression { get; init; } = TiffCompression.Deflate;

        public CompressionLevel CompressionLevel { get; init; } = CompressionLevel.Optimal;

        public BigTiffMode BigTiff { get; init; } = BigTiffMode.Auto;

        public GeoReference? GeoReference { get; init; }

        public string Software { get; init; } = "YukiOsmRenderer";

        /// <summary>Directory for the temporary file used when the output stream cannot seek; defaults to the system temp directory.</summary>
        public string? TemporaryDirectory { get; init; }

        public long TileColumns => (Width + TileSize - 1) / TileSize;

        public long TileRows => (Height + TileSize - 1) / TileSize;

        public long TileCount => TileColumns * TileRows;

        /// <summary>Bytes of one uncompressed tile.</summary>
        public int TileByteCount => TileSize * TileSize * Channels;

        /// <summary>Upper bound of the file size without compression, used to choose between TIFF and BigTIFF.</summary>
        public long EstimateUncompressedSize() {
            // Tile data plus offset/byte count arrays and a generous allowance for the IFD and deflate overhead.
            var data = TileCount * TileByteCount;
            return data + data / 100 + TileCount * 16 + 64 * 1024;
        }

        internal void Validate() {
            if (Width <= 0 || Height <= 0) {
                throw new ArgumentException("Image width and height must be positive.");
            }
            if (Width > uint.MaxValue || Height > uint.MaxValue) {
                throw new ArgumentException("Image width and height must fit in 32 bits.");
            }
            if (Channels is not (3 or 4)) {
                throw new ArgumentException("Only RGB (3 channels) and RGBA (4 channels) images are supported.");
            }
            if (TileSize <= 0 || TileSize % 16 != 0) {
                throw new ArgumentException("The tile size must be a positive multiple of 16.");
            }
        }
    }
}
