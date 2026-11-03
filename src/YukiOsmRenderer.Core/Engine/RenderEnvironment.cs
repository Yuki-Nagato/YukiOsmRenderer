using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core.Database;
using YukiOsmRenderer.Core.Import;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Runtime;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Core.Engine {
    public sealed record EnvironmentOptions {
        /// <summary>Empty to use the data already imported into the data directory.</summary>
        public IReadOnlyList<string> InputFiles { get; init; } = [];

        /// <summary>Installation directory; see <see cref="RuntimeLayout.Resolve"/>.</summary>
        public string? Home { get; init; }

        public string DataDirectory { get; init; } = Database.DataDirectory.DefaultPath;

        /// <summary>Maximum number of concurrent renders.</summary>
        public int Threads { get; init; } = Environment.ProcessorCount;

        public ImportOptions Import { get; init; } = new();

        /// <summary>Maximum number of style variants (transparency options, CJK variants) kept loaded at the same time.</summary>
        public int MaxStyleVariants { get; init; } = 8;
    }

    /// <summary>
    /// Everything needed to render: a locked data directory, a running PostgreSQL instance with the imported input
    /// files, the compiled style and the Mapnik library.
    /// </summary>
    public sealed class RenderEnvironment : IRendererProvider, IAsyncDisposable {
        readonly DataDirectory data;
        readonly PostgresInstance postgres;
        readonly string baseStyle;
        readonly SemaphoreSlim renderSlots;
        readonly RendererCache<MapnikStyleRenderer> renderers;

        RenderEnvironment(RuntimeLayout layout, DataDirectory data, PostgresInstance postgres, MmlProject project, string baseStyle, EnvironmentOptions options) {
            Layout = layout;
            this.data = data;
            this.postgres = postgres;
            Project = project;
            this.baseStyle = baseStyle;
            Threads = Math.Max(1, options.Threads);
            renderSlots = new SemaphoreSlim(Threads, Threads);
            renderers = new RendererCache<MapnikStyleRenderer>(Math.Max(1, options.MaxStyleVariants));
        }

        public RuntimeLayout Layout { get; }

        public MmlProject Project { get; }

        public IReadOnlySet<string> LayerIds => Project.LayerIds;

        public int Threads { get; }

        public string TemporaryDirectory => data.TemporaryDirectory;

        public static async Task<RenderEnvironment> StartAsync(EnvironmentOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken = default) {
            var logger = loggerFactory.CreateLogger<RenderEnvironment>();
            var layout = RuntimeLayout.Resolve(options.Home);
            layout.Validate();
            var project = MmlProject.Load(layout.CartoProjectFile);

            var data = DataDirectory.Open(options.DataDirectory);
            PostgresInstance? postgres = null;
            try {
                logger.LogInformation("Using data directory {Directory}", data.Root);
                postgres = await PostgresInstance.StartAsync(layout, data, PostgresSettings.ForMachine(options.Threads), loggerFactory.CreateLogger<PostgresInstance>(), cancellationToken).ConfigureAwait(false);
                var importer = new OsmImporter(layout, data, postgres, loggerFactory.CreateLogger<OsmImporter>());
                await importer.EnsureImportedAsync(options.InputFiles, options.Import, cancellationToken).ConfigureAwait(false);
                var compiler = new CartoCompiler(layout, data.StylesDirectory, loggerFactory.CreateLogger<CartoCompiler>());
                var style = await compiler.CompileAsync(cancellationToken).ConfigureAwait(false);
                Mapnik.Initialize(layout);
                logger.LogInformation("Mapnik {Version} ready", Mapnik.Version);
                return new RenderEnvironment(layout, data, postgres, project, style, options);
            }
            catch {
                if (postgres != null) {
                    await postgres.DisposeAsync().ConfigureAwait(false);
                }
                data.Dispose();
                throw;
            }
        }

        public IRendererLease AcquireRenderer(StyleOptions options) {
            options.Validate(Project.LayerIds);
            return new Lease(renderers.Acquire(options.Key,
                () => new MapnikStyleRenderer(BuildStyle(options), Layout.CartoDirectory, renderSlots, Math.Max(1, options.TextScale))), options);
        }

        /// <summary>Mapnik XML for the given options, as used for rendering.</summary>
        public string BuildStyle(StyleOptions options) {
            // Mapnik keeps one connection pool per distinct set of connection parameters; allow every render slot one
            // connection plus a few spare ones.
            return MapnikStyleTransformer.Transform(baseStyle, Project, options, postgres.Connection, Threads + 4);
        }

        public async ValueTask DisposeAsync() {
            renderers.Dispose();
            await postgres.DisposeAsync().ConfigureAwait(false);
            data.Dispose();
            renderSlots.Dispose();
        }

        sealed class Lease(RendererCache<MapnikStyleRenderer>.Lease inner, StyleOptions options) : IRendererLease {
            public IMapRenderer Renderer => inner.Renderer;

            public StyleOptions Options => options;

            public void Dispose() {
                inner.Dispose();
            }
        }
    }
}
