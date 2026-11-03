using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Runtime;
using YukiOsmRenderer.Core.Styling;
using YukiOsmRenderer.Core.Vector;

namespace YukiOsmRenderer.Core.Engine {
    public sealed record VectorEnvironmentOptions {
        /// <summary>Input files to build vector tiles from; empty to use the tiles already built in the data directory.</summary>
        public IReadOnlyList<string> InputFiles { get; init; } = [];

        /// <summary>Existing PMTiles archive to use instead of the data directory.</summary>
        public string? PmTiles { get; init; }

        public string? Home { get; init; }

        public string DataDirectory { get; init; } = Database.DataDirectory.DefaultPath;

        public int Threads { get; init; } = Environment.ProcessorCount;

        public int MaxStyleVariants { get; init; } = 8;

        /// <summary>Theme used when the style options name none.</summary>
        public string Theme { get; init; } = VectorEnvironment.DefaultTheme;

        public VectorBuildOptions Build { get; init; } = new();
    }

    /// <summary>
    /// The vector engine: Shortbread vector tiles (built by Planetiler or given as a PMTiles file) drawn by MapLibre
    /// Native with a software Vulkan implementation. After start-up nothing is written; all caches are in memory.
    /// </summary>
    public sealed class VectorEnvironment : IRenderEngine, IVectorTileProvider {
        public const string DefaultTheme = "classic";

        readonly SemaphoreSlim renderSlots;
        readonly MapPool<MapLibreMap> maps;
        readonly RendererCache<MapLibreStyleRenderer> renderers;
        readonly Dictionary<string, VectorStyle> styles = new(StringComparer.Ordinal);
        readonly string defaultTheme;

        VectorEnvironment(RuntimeLayout layout, PmTilesArchive tiles, IReadOnlyList<string> themes, string defaultTheme, VectorEnvironmentOptions options) {
            Layout = layout;
            Tiles = tiles;
            Themes = themes;
            this.defaultTheme = defaultTheme;
            Threads = RenderThreads(options.Threads);
            renderSlots = new SemaphoreSlim(Threads, Threads);
            maps = new MapPool<MapLibreMap>(2 * Threads);
            renderers = new RendererCache<MapLibreStyleRenderer>(Math.Max(1, options.MaxStyleVariants));
        }

        public RuntimeLayout Layout { get; }

        public PmTilesArchive Tiles { get; }

        /// <summary>Names of the bundled themes.</summary>
        public IReadOnlyList<string> Themes { get; }

        /// <summary>Concurrent renders.</summary>
        public int Threads { get; }

        /// <summary>
        /// Concurrent renders for a number of threads. Each render keeps several threads busy (MapLibre's workers,
        /// SwiftShader's rasterizer), and more concurrent renders than about a third of the cores waste most of the
        /// time in contention (measured: 4 of 12 cores were fastest, 12 of 12 six times slower).
        /// </summary>
        public static int RenderThreads(int threads) {
            return Math.Clamp((threads + 2) / 3, 1, 8);
        }

        public string TemporaryDirectory => Path.Combine(Path.GetTempPath(), "yukiosm");

        public string AssetsDirectory => Layout.VectorDirectory;

        public (byte[] Data, PmTilesCompression Compression)? GetVectorTile(int zoom, long x, long y) {
            return Tiles.GetTile(zoom, x, y) is { } data ? (data, Tiles.Header.TileCompression) : null;
        }

        public IReadOnlyCollection<string> LayerIds => Style(null).LayerIds;

        /// <summary>Theme names of a layout, from assets/vector/styles/*.json.</summary>
        public static IReadOnlyList<string> AvailableThemes(RuntimeLayout layout) {
            return Directory.Exists(layout.VectorStylesDirectory)
                ? Directory.EnumerateFiles(layout.VectorStylesDirectory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal).ToList()
                : [];
        }

        public static async Task<VectorEnvironment> StartAsync(VectorEnvironmentOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken = default) {
            var logger = loggerFactory.CreateLogger<VectorEnvironment>();
            var layout = RuntimeLayout.Resolve(options.Home);
            var build = options.PmTiles == null && options.InputFiles.Count > 0;
            layout.ValidateVector(build);
            var themes = AvailableThemes(layout);
            var theme = options.Theme.Trim().ToLowerInvariant();
            if (!themes.Contains(theme)) {
                throw new ArgumentException($"Unknown theme '{options.Theme}'. Available: {string.Join(", ", themes)}.");
            }

            string path;
            if (options.PmTiles != null) {
                path = Path.GetFullPath(options.PmTiles);
                if (!File.Exists(path)) {
                    throw new FileNotFoundException($"The vector tiles '{path}' do not exist.", path);
                }
            }
            else {
                var store = new VectorTileStore(layout, options.DataDirectory, loggerFactory.CreateLogger<VectorTileStore>());
                path = await store.EnsureBuiltAsync(options.InputFiles, options.Build with { Threads = options.Threads }, cancellationToken).ConfigureAwait(false);
            }
            var tiles = PmTilesArchive.Open(path);
            try {
                if (tiles.Header.TileType != PmTilesTileType.Mvt) {
                    throw new InvalidDataException($"'{path}' does not contain vector tiles.");
                }
                logger.LogInformation("Vector tiles {Path}: zoom {Min}-{Max}", path, tiles.Header.MinZoom, tiles.Header.MaxZoom);
                Mapnik.Initialize(layout);
                // MapLibre's workers parse and lay out the tiles of all renders.
                MapLibre.Initialize(layout.MapLibreLibrary, layout.VulkanLibrary, Math.Max(1, options.Threads), loggerFactory.CreateLogger("MapLibre"));
                logger.LogInformation("MapLibre Native {Version} ready", MapLibre.Version);
                return new VectorEnvironment(layout, tiles, themes, theme, options);
            }
            catch {
                tiles.Dispose();
                throw;
            }
        }

        /// <summary>The style of a theme, or of the default theme for null.</summary>
        /// <exception cref="ArgumentException">There is no such theme.</exception>
        public VectorStyle Style(string? theme) {
            theme ??= defaultTheme;
            lock (styles) {
                if (styles.TryGetValue(theme, out var style)) {
                    return style;
                }
                if (!Themes.Contains(theme)) {
                    throw new ArgumentException($"Unknown theme '{theme}'. Available: {string.Join(", ", Themes)}.");
                }
                return styles[theme] = VectorStyle.Load(Path.Combine(Layout.VectorStylesDirectory, theme + ".json"));
            }
        }

        public void Validate(StyleOptions options) {
            Style(options.Theme).Validate(options);
        }

        public IRendererLease AcquireRenderer(StyleOptions options) {
            var style = Style(options.Theme);
            style.Validate(options);
            var lease = renderers.Acquire(options.Key, () => new MapLibreStyleRenderer(style.Build(options, NativeResources()), maps, renderSlots, Math.Max(1, options.TextScale)));
            return new Lease(lease);
        }

        /// <summary>The style for web clients, loading tiles, glyphs and sprites from the server at <paramref name="baseUrl"/>.</summary>
        public string WebStyle(StyleOptions options, string baseUrl) {
            return Style(options.Theme).Build(options, new VectorStyleResources {
                TileSource = new JsonObject { ["type"] = "vector", ["url"] = baseUrl + "vector/tiles.json" },
                AssetBase = baseUrl + "vector/",
            });
        }

        /// <summary>TileJSON 3.0 document of the vector tiles served at <paramref name="baseUrl"/>.</summary>
        public string TileJson(string baseUrl) {
            var header = Tiles.Header;
            var document = new JsonObject {
                ["tilejson"] = "3.0.0",
                ["name"] = "Shortbread",
                ["scheme"] = "xyz",
                ["tiles"] = new JsonArray(JsonValue.Create(baseUrl + "vector/{z}/{x}/{y}.pbf")),
                ["minzoom"] = header.MinZoom,
                ["maxzoom"] = header.MaxZoom,
                ["bounds"] = new JsonArray(header.MinLongitude, header.MinLatitude, header.MaxLongitude, header.MaxLatitude),
                ["center"] = new JsonArray(header.CenterLongitude, header.CenterLatitude, header.CenterZoom),
                ["attribution"] = "<a href=\"https://www.openstreetmap.org/copyright\">&copy; OpenStreetMap contributors</a>",
            };
            try {
                if (JsonNode.Parse(Tiles.ReadMetadata()) is JsonObject metadata && metadata["vector_layers"] is JsonArray layers) {
                    document["vector_layers"] = layers.DeepClone();
                }
            }
            catch (System.Text.Json.JsonException) {
                // Metadata is optional.
            }
            return document.ToJsonString();
        }

        VectorStyleResources NativeResources() {
            return new VectorStyleResources {
                TileSource = new JsonObject {
                    ["type"] = "vector",
                    ["url"] = "pmtiles://" + FileUrl(Tiles.Path),
                    ["attribution"] = "© OpenStreetMap contributors",
                },
                AssetBase = FileUrl(Layout.VectorDirectory) + "/",
                CjkFonts = (FileUrl(Layout.CjkFont(false)), FileUrl(Layout.CjkFont(true))),
            };
        }

        static string FileUrl(string path) {
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }

        public async ValueTask DisposeAsync() {
            renderers.Dispose();
            maps.Dispose();
            Tiles.Dispose();
            renderSlots.Dispose();
            await ValueTask.CompletedTask.ConfigureAwait(false);
        }

        sealed class Lease(RendererCache<MapLibreStyleRenderer>.Lease inner) : IRendererLease {
            public IMapRenderer Renderer => inner.Renderer;

            public void Dispose() {
                inner.Dispose();
            }
        }
    }
}
