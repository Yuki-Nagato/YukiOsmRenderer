using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core.Engine;
using YukiOsmRenderer.Core.Runtime;
using YukiOsmRenderer.Core.Styling;
using YukiOsmRenderer.Core.Vector;

namespace YukiOsmRenderer.Cli {
    /// <summary>Starts the engine selected on the command line.</summary>
    internal static class Engines {
        public static async Task<IRenderEngine> StartAsync(CommonSettings common, int maxStyleVariants, ILoggerFactory loggerFactory, CancellationToken cancellationToken) {
            if (common.Engine == EngineKind.Vector) {
                return await VectorEnvironment.StartAsync(VectorOptions(common, maxStyleVariants), loggerFactory, cancellationToken).ConfigureAwait(false);
            }
            return await RenderEnvironment.StartAsync(RenderRunner.CreateEnvironmentOptions(common, maxStyleVariants), loggerFactory, cancellationToken).ConfigureAwait(false);
        }

        public static VectorEnvironmentOptions VectorOptions(CommonSettings common, int maxStyleVariants) {
            return new VectorEnvironmentOptions {
                InputFiles = common.Inputs,
                PmTiles = common.PmTiles,
                Home = common.Home,
                DataDirectory = common.DataDirectory,
                Threads = common.Threads,
                MaxStyleVariants = maxStyleVariants,
                Theme = common.Theme,
                Build = common.VectorBuild,
            };
        }

        /// <summary>Checks the installation and the style options before a potentially long import.</summary>
        public static void Validate(CommonSettings common, StyleOptions? style) {
            var layout = RuntimeLayout.Resolve(common.Home);
            if (common.Engine == EngineKind.Vector) {
                layout.ValidateVector(build: common.PmTiles == null && common.Inputs.Count > 0);
                var theme = common.Theme.Trim().ToLowerInvariant();
                var themes = VectorEnvironment.AvailableThemes(layout);
                if (!themes.Contains(theme)) {
                    throw new ArgumentException($"Unknown theme '{common.Theme}'. Available: {string.Join(", ", themes)}.");
                }
                if (style != null) {
                    VectorStyle.Load(Path.Combine(layout.VectorStylesDirectory, theme + ".json")).Validate(style);
                }
            }
            else {
                layout.Validate();
                style?.Validate(MmlProject.Load(layout.CartoProjectFile).LayerIds);
            }
        }
    }
}
