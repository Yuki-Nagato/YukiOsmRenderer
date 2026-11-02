using System.Buffers;
using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core.Geo;

namespace YukiOsmRenderer.Core.Rendering {
    /// <summary>
    /// Renders an area into a directory of XYZ tiles, one PNG file per tile. Like the tile server, whole metatiles are
    /// rendered and cut into tiles with the same encoding, so the files equal the served tiles.
    /// </summary>
    public static class TileSetRenderer {
        /// <summary>Number of tiles covering the bounding box at the zoom levels from <paramref name="minZoom"/> to <paramref name="maxZoom"/>.</summary>
        public static long CountTiles(GeoBoundingBox box, int minZoom, int maxZoom) {
            long count = 0;
            for (var zoom = minZoom; zoom <= maxZoom; zoom++) {
                var tiles = TileBounds(PixelWindow.FromBoundingBox(box, zoom));
                count += (tiles.MaxX - tiles.MinX + 1) * (tiles.MaxY - tiles.MinY + 1);
            }
            return count;
        }

        /// <summary>Renders the tiles covering the bounding box into files below <paramref name="directory"/>, replacing existing files.</summary>
        /// <param name="tileSize">256 for standard tiles or 512 for high resolution tiles of the same area.</param>
        /// <returns>The final progress, with the number and size of the tiles written.</returns>
        public static async Task<RenderProgress> RenderAsync(IMapRenderer renderer, GeoBoundingBox box, int minZoom, int maxZoom, int tileSize, string directory,
            TilePathTemplate template, int parallelism, ILogger logger, CancellationToken cancellationToken = default) {
            // Tile ranges come from the 256 px grid, so that standard and high resolution tile sets contain the same tiles.
            var windows = Enumerable.Range(minZoom, maxZoom - minZoom + 1).Select(zoom => PixelWindow.FromBoundingBox(box, zoom)).ToList();
            var total = windows.Sum(window => window.CoveringMetatileCount());
            var totalTiles = CountTiles(box, minZoom, maxZoom);
            var progress = new ProgressTracker(total, totalTiles, logger);
            logger.LogInformation("Rendering {Tiles:N0} tiles at zoom {Zoom} ({Count:N0} metatiles)", totalTiles,
                minZoom == maxZoom ? $"{minZoom}" : $"{minZoom}-{maxZoom}", total);

            var work = windows.SelectMany(window => {
                var tiles = TileBounds(window);
                return window.CoveringMetatiles().Select(metatile => (Tiles: tiles, Metatile: metatile));
            });
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(work, options, (item, _) => {
                var (tiles, metatile) = item;
                var size = metatile.PixelSize(tileSize);
                var length = size * size * 4;
                var buffer = ArrayPool<byte>.Shared.Rent(length);
                try {
                    renderer.RenderMetatile(metatile, tileSize, buffer.AsSpan(0, length));
                    for (var y = Math.Max(metatile.Y, tiles.MinY); y <= Math.Min(metatile.Y + metatile.Size - 1, tiles.MaxY); y++) {
                        for (var x = Math.Max(metatile.X, tiles.MinX); x <= Math.Min(metatile.X + metatile.Size - 1, tiles.MaxX); x++) {
                            var offset = (int)(((y - metatile.Y) * size + (x - metatile.X)) * tileSize * 4);
                            var png = renderer.EncodeTile(buffer.AsSpan(offset, length - offset), tileSize, tileSize, size * 4);
                            var path = Path.Combine(directory, template.Format(metatile.Zoom, x, y));
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                            File.WriteAllBytes(path, png);
                            progress.AddTile(png.Length);
                        }
                    }
                }
                finally {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                progress.CompleteMetatile();
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            var result = progress.Current;
            logger.LogInformation("Rendering finished in {Elapsed}", Formatting.Duration(result.Elapsed));
            return result;
        }

        /// <summary>Columns and rows of the tiles covering a window.</summary>
        static (long MinX, long MinY, long MaxX, long MaxY) TileBounds(PixelWindow window) {
            return (window.X / window.TileSize, window.Y / window.TileSize, (window.X + window.Width - 1) / window.TileSize, (window.Y + window.Height - 1) / window.TileSize);
        }
    }
}
