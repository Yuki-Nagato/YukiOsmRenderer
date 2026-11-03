using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Cli;
using YukiOsmRenderer.Server;

namespace YukiOsmRenderer {
    internal static class Program {
        static async Task<int> Main(string[] args) {
            var root = Commands.Create(
                (settings, cancellationToken) => RunAsync(settings.Common.Verbose, loggerFactory => RenderRunner.RunAsync(settings, loggerFactory, cancellationToken)),
                (settings, cancellationToken) => RunAsync(settings.Common.Verbose, loggerFactory => ServeRunner.RunAsync(settings, loggerFactory, cancellationToken)),
                (settings, cancellationToken) => RunAsync(settings.Common.Verbose, loggerFactory => TilesRunner.RunAsync(settings, loggerFactory, cancellationToken)),
                (settings, cancellationToken) => RunAsync(settings.Common.Verbose, loggerFactory => ImportRunner.RunAsync(settings, loggerFactory, cancellationToken)));
            return await root.Parse(args).InvokeAsync();
        }

        static async Task<int> RunAsync(bool verbose, Func<ILoggerFactory, Task<int>> run) {
            using var loggerFactory = Logging.CreateFactory(verbose);
            var logger = loggerFactory.CreateLogger("yukiosm");
            try {
                return await run(loggerFactory);
            }
            catch (OperationCanceledException) {
                logger.LogWarning("Cancelled");
                return 130;
            }
            catch (Exception e) when (e is not OutOfMemoryException) {
                if (verbose) {
                    logger.LogError(e, "{Message}", e.Message);
                }
                else {
                    logger.LogError("{Message}", e.Message);
                }
                return 1;
            }
        }
    }
}
