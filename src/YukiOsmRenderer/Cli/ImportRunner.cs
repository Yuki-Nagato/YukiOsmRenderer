using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core.Engine;
using YukiOsmRenderer.Core.Runtime;
using YukiOsmRenderer.Core.Vector;

namespace YukiOsmRenderer.Cli {
    internal static class ImportRunner {
        public static async Task<int> RunAsync(ImportSettings settings, ILoggerFactory loggerFactory, CancellationToken cancellationToken) {
            var common = settings.Common;
            Engines.Validate(common, null);
            if (common.Engine == EngineKind.Carto) {
                var options = RenderRunner.CreateEnvironmentOptions(common, 1);
                options = options with { Import = options.Import with { UpdateExisting = true } };
                await using var environment = await RenderEnvironment.StartAsync(options, loggerFactory, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            var layout = RuntimeLayout.Resolve(common.Home);
            var store = new VectorTileStore(layout, common.DataDirectory, loggerFactory.CreateLogger<VectorTileStore>());
            var build = common.VectorBuild with { Threads = common.Threads };
            if (settings.Output != null) {
                await store.BuildToFileAsync(common.Inputs, build, settings.Output, cancellationToken).ConfigureAwait(false);
            }
            else {
                await store.EnsureBuiltAsync(common.Inputs, build, cancellationToken).ConfigureAwait(false);
            }
            return 0;
        }
    }
}
