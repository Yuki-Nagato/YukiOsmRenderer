using System.Buffers;
using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Tiff;

namespace YukiOsmRenderer.Core.Rendering {
    /// <summary>
    /// Renders a pixel window into a GeoTIFF. Metatiles aligned to the global grid are rendered exactly like the
    /// tiles of tile.openstreetmap.org, then cut to the window and written as TIFF tiles. With
    /// <see cref="TileFormat.Png256"/>, the pixels get the colors of the png256 tiles.
    /// </summary>
    public static class AreaRenderer {
        /// <summary>Creates writer options for a window: RGB for opaque maps, RGBA for transparent ones.</summary>
        public static GeoTiffOptions CreateTiffOptions(PixelWindow window, bool transparent, string? temporaryDirectory) {
            var extent = window.Extent;
            return new GeoTiffOptions {
                Width = window.Width,
                Height = window.Height,
                Channels = transparent ? 4 : 3,
                TileSize = 256,
                Compression = TiffCompression.Deflate,
                GeoReference = new GeoReference(extent.MinX, extent.MaxY, window.Resolution, window.Resolution),
                TemporaryDirectory = temporaryDirectory,
            };
        }

        public static async Task RenderAsync(IMapRenderer renderer, PixelWindow window, TileFormat format, GeoTiffWriter writer, int parallelism, ILogger logger,
            CancellationToken cancellationToken = default) {
            var tileSize = window.TileSize;
            var total = window.CoveringMetatileCount();
            var progress = new ProgressTracker(total, writer.Options.TileCount, logger);
            var assembler = new TiledImageAssembler(window.Width, window.Height, writer.Options.TileSize, writer.Options.Channels, (column, row, pixels) => {
                var data = writer.EncodeTile(pixels);
                writer.WriteEncodedTile(column, row, data);
                progress.AddTile(data.Length);
            });
            logger.LogInformation("Rendering {Width} x {Height} pixels at zoom {Zoom} ({Count:N0} metatiles)", window.Width, window.Height, window.Zoom, total);

            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(window.CoveringMetatiles(), options, (metatile, _) => {
                var size = metatile.PixelSize(tileSize);
                var length = size * size * 4;
                var buffer = ArrayPool<byte>.Shared.Rent(length);
                try {
                    var pixels = buffer.AsSpan(0, length);
                    renderer.RenderMetatile(metatile, tileSize, pixels);
                    if (format == TileFormat.Png256) {
                        Quantize(renderer, window, metatile, pixels);
                    }
                    assembler.Add(metatile.X * tileSize - window.X, metatile.Y * tileSize - window.Y, size, size, pixels);
                }
                finally {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                progress.CompleteMetatile();
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            writer.Finish();
            logger.LogInformation("Rendering finished in {Elapsed}", Formatting.Duration(progress.Current.Elapsed));
        }

        /// <summary>Replaces the pixels of the metatile's tiles inside the window by those of their png256 encoding.</summary>
        static void Quantize(IMapRenderer renderer, PixelWindow window, Metatile metatile, Span<byte> pixels) {
            var tileSize = window.TileSize;
            var stride = metatile.PixelSize(tileSize) * 4;
            var lastX = Math.Min(metatile.X + metatile.Size, (window.X + window.Width - 1) / tileSize + 1);
            var lastY = Math.Min(metatile.Y + metatile.Size, (window.Y + window.Height - 1) / tileSize + 1);
            for (var y = Math.Max(metatile.Y, window.Y / tileSize); y < lastY; y++) {
                for (var x = Math.Max(metatile.X, window.X / tileSize); x < lastX; x++) {
                    var tile = pixels[(int)((y - metatile.Y) * tileSize * stride + (x - metatile.X) * tileSize * 4)..];
                    var png = renderer.EncodeTile(tile, tileSize, tileSize, stride, TileFormat.Png256);
                    PalettePng.Decode(png, tile, tileSize, tileSize, stride);
                }
            }
        }
    }
}
