using YukiOsmRenderer.Core.Engine;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Styling;
using YukiOsmRenderer.Core.Vector;

namespace YukiOsmRenderer.Cli {
    /// <summary>Options shared by all commands.</summary>
    internal sealed record CommonSettings {
        /// <summary>Empty to use the data already imported into the data directory.</summary>
        public IReadOnlyList<string> Inputs { get; init; } = [];

        public CjkVariant Cjk { get; init; } = CjkVariant.SimplifiedChinese;

        public double TextScale { get; init; } = 1;

        public required string DataDirectory { get; init; }

        public string? Home { get; init; }

        public int Threads { get; init; } = Environment.ProcessorCount;

        public bool ForceImport { get; init; }

        public bool Verbose { get; init; }

        public EngineKind Engine { get; init; } = EngineKind.Carto;

        /// <summary>Vector engine: existing vector tiles to use instead of the data directory.</summary>
        public string? PmTiles { get; init; }

        /// <summary>Vector engine: default theme.</summary>
        public string Theme { get; init; } = VectorEnvironment.DefaultTheme;

        /// <summary>Vector engine: how vector tiles are built from the input files.</summary>
        public VectorBuildOptions VectorBuild { get; init; } = new();
    }

    internal sealed record ImportSettings {
        public required CommonSettings Common { get; init; }

        /// <summary>Vector engine: file to write the vector tiles to instead of the data directory.</summary>
        public string? Output { get; init; }
    }

    internal sealed record RenderSettings {
        /// <summary>Output path, or "-" for standard output.</summary>
        public const string StandardOutput = "-";

        public required CommonSettings Common { get; init; }

        public required int Zoom { get; init; }

        public GeoBoundingBox BoundingBox { get; init; } = GeoBoundingBox.World;

        public string Output { get; init; } = StandardOutput;

        /// <summary>256 for normal rendering, 512 for high-DPI (scale 2).</summary>
        public int TileSize { get; init; } = 256;

        /// <summary>Png256 gives the image the colors of the png256 tiles.</summary>
        public TileFormat Format { get; init; } = TileFormat.Png256;

        public StyleOptions Style { get; init; } = StyleOptions.Default;

        public long MaxPixels { get; init; } = DefaultMaxPixels;

        /// <summary>65536 x 65536 pixels, i.e. the whole world at zoom 8.</summary>
        public const long DefaultMaxPixels = 1L << 32;
    }

    internal sealed record TilesSettings {
        public required CommonSettings Common { get; init; }

        public required int MinZoom { get; init; }

        public required int MaxZoom { get; init; }

        public GeoBoundingBox BoundingBox { get; init; } = GeoBoundingBox.World;

        public required string OutputDirectory { get; init; }

        public TilePathTemplate Template { get; init; } = TilePathTemplate.Default;

        /// <summary>256 for normal rendering, 512 for high-DPI (scale 2).</summary>
        public int TileSize { get; init; } = 256;

        public TileFormat Format { get; init; } = TileFormat.Png256;

        public StyleOptions Style { get; init; } = StyleOptions.Default;
    }

    internal sealed record ServeSettings {
        public required CommonSettings Common { get; init; }

        public string Host { get; init; } = "localhost";

        public int Port { get; init; } = 8080;

        public int CacheMegabytes { get; init; } = 512;

        public int MaxStyleVariants { get; init; } = 8;

        /// <summary>Tile format of requests without the format parameter.</summary>
        public TileFormat DefaultFormat { get; init; } = TileFormat.Png256;

        /// <summary>URL clients reach the server at, for vector styles and TileJSON behind a reverse proxy.</summary>
        public string? PublicUrl { get; init; }

        public bool Preview { get; init; } = true;

        /// <summary>Headers for every response, replacing the server's own; an empty value removes the header.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];
    }
}
