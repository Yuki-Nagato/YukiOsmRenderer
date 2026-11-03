using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core;
using YukiOsmRenderer.Core.Engine;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Runtime;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Cli {
    internal static class TilesRunner {
        public static async Task<int> RunAsync(TilesSettings settings, ILoggerFactory loggerFactory, CancellationToken cancellationToken) {
            var logger = loggerFactory.CreateLogger("tiles");
            var directory = Path.GetFullPath(settings.OutputDirectory);
            logger.LogInformation("Output: {Count:N0} {Format} tiles as {Template} in {Directory}",
                TileSetRenderer.CountTiles(settings.BoundingBox, settings.MinZoom, settings.MaxZoom), settings.Format.Name(), settings.Template, directory);

            // Check the style options and the output directory before the potentially long import.
            Engines.Validate(settings.Common, settings.Style);
            Directory.CreateDirectory(directory);

            await using var environment = await Engines.StartAsync(settings.Common, 1, loggerFactory, cancellationToken).ConfigureAwait(false);
            using var lease = environment.AcquireRenderer(settings.Style);
            var result = await TileSetRenderer.RenderAsync(lease.Renderer, settings.BoundingBox, settings.MinZoom, settings.MaxZoom, settings.TileSize, settings.Format,
                directory, settings.Template, environment.Threads, logger, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Wrote {Count:N0} tiles ({Size}) to {Directory}", result.Tiles, Formatting.Size(result.Bytes), directory);
            return 0;
        }
    }
}
