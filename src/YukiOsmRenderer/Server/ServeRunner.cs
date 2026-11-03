using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Cli;
using YukiOsmRenderer.Core.Engine;
using YukiOsmRenderer.Core.Runtime;

namespace YukiOsmRenderer.Server {
    internal static class ServeRunner {
        public static async Task<int> RunAsync(ServeSettings settings, ILoggerFactory loggerFactory, CancellationToken cancellationToken) {
            var logger = loggerFactory.CreateLogger("serve");
            RuntimeLayout.Resolve(settings.Common.Home).Validate();
            await using var environment = await RenderEnvironment.StartAsync(
                RenderRunner.CreateEnvironmentOptions(settings.Common, settings.MaxStyleVariants), loggerFactory, cancellationToken).ConfigureAwait(false);
            var options = new TileServerOptions {
                Host = settings.Host,
                Port = settings.Port,
                WebDirectory = environment.Layout.WebDirectory,
                Verbose = settings.Common.Verbose,
                DefaultCjk = settings.Common.Cjk,
                DefaultTextScale = settings.Common.TextScale,
                DefaultFormat = settings.DefaultFormat,
                Tiles = new TileServiceOptions {
                    Concurrency = environment.Threads,
                    CacheBytes = settings.CacheMegabytes * 1024L * 1024,
                },
            };
            await using var app = TileServer.Build(options, environment);
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Serving tiles at http://{Host}:{Port}/tiles/{{z}}/{{x}}/{{y}}.png, preview at http://{Host}:{Port}/",
                settings.Host, settings.Port, settings.Host, settings.Port);
            try {
                await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                // Ctrl+C
            }
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
    }
}
