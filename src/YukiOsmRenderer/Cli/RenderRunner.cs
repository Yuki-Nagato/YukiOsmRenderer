using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core;
using YukiOsmRenderer.Core.Engine;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Import;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Runtime;
using YukiOsmRenderer.Core.Styling;
using YukiOsmRenderer.Core.Tiff;

namespace YukiOsmRenderer.Cli {
    internal static class RenderRunner {
        public static async Task<int> RunAsync(RenderSettings settings, ILoggerFactory loggerFactory, CancellationToken cancellationToken) {
            var logger = loggerFactory.CreateLogger("render");
            var window = PixelWindow.FromBoundingBox(settings.BoundingBox, settings.Zoom, settings.TileSize);
            var transparent = settings.Style.Transparent;
            var estimate = AreaRenderer.CreateTiffOptions(window, transparent, null).EstimateUncompressedSize();
            if (window.PixelCount > settings.MaxPixels) {
                logger.LogError(
                    "The image would be {Width} x {Height} = {Pixels:N0} pixels (about {Size}, uncompressed), more than the limit of {Limit:N0} pixels. "
                    + "Choose a smaller area or zoom level, or raise the limit with --max-pixels.",
                    window.Width, window.Height, window.PixelCount, Formatting.Size(estimate), settings.MaxPixels);
                return 1;
            }
            logger.LogInformation("Output: {Width} x {Height} pixels with {Format} colors, at most {Size} uncompressed",
                window.Width, window.Height, settings.Format.Name(), Formatting.Size(estimate));

            // Check the style options before the potentially long import.
            Engines.Validate(settings.Common, settings.Style);

            await using var environment = await Engines.StartAsync(settings.Common, 1, loggerFactory, cancellationToken).ConfigureAwait(false);
            using var lease = environment.AcquireRenderer(settings.Style);
            var tiffOptions = AreaRenderer.CreateTiffOptions(window, transparent, environment.TemporaryDirectory);

            if (settings.Output == RenderSettings.StandardOutput) {
                await using var stdout = Console.OpenStandardOutput();
                using var writer = new GeoTiffWriter(stdout, tiffOptions);
                await AreaRenderer.RenderAsync(lease.Renderer, window, settings.Format, writer, environment.Threads, logger, cancellationToken).ConfigureAwait(false);
            }
            else {
                var path = Path.GetFullPath(settings.Output);
                var partial = path + ".partial";
                try {
                    await using (var file = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20)) {
                        using var writer = new GeoTiffWriter(file, tiffOptions);
                        await AreaRenderer.RenderAsync(lease.Renderer, window, settings.Format, writer, environment.Threads, logger, cancellationToken).ConfigureAwait(false);
                    }
                    File.Move(partial, path, overwrite: true);
                }
                finally {
                    if (File.Exists(partial)) {
                        File.Delete(partial);
                    }
                }
                logger.LogInformation("Wrote {Path} ({Size})", path, Formatting.Size(new FileInfo(path).Length));
            }
            return 0;
        }

        public static EnvironmentOptions CreateEnvironmentOptions(CommonSettings common, int maxStyleVariants) {
            return new EnvironmentOptions {
                InputFiles = common.Inputs,
                Home = common.Home,
                DataDirectory = common.DataDirectory,
                Threads = common.Threads,
                MaxStyleVariants = maxStyleVariants,
                Import = new ImportOptions {
                    Processes = common.Threads,
                    Force = common.ForceImport,
                },
            };
        }
    }
}
