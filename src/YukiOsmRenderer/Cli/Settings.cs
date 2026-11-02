using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Cli {
    /// <summary>Options shared by all commands.</summary>
    internal sealed record CommonSettings {
        /// <summary>Empty to use the data already imported into the data directory.</summary>
        public IReadOnlyList<string> Inputs { get; init; } = [];

        public CjkVariant Cjk { get; init; } = CjkVariant.SimplifiedChinese;

        public required string DataDirectory { get; init; }

        public string? Home { get; init; }

        public int Threads { get; init; } = Environment.ProcessorCount;

        public bool ForceImport { get; init; }

        public bool Verbose { get; init; }
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

        public StyleOptions Style { get; init; } = StyleOptions.Default;

        public long MaxPixels { get; init; } = DefaultMaxPixels;

        /// <summary>65536 x 65536 pixels, i.e. the whole world at zoom 8.</summary>
        public const long DefaultMaxPixels = 1L << 32;
    }

    internal sealed record ServeSettings {
        public required CommonSettings Common { get; init; }

        public string Host { get; init; } = "localhost";

        public int Port { get; init; } = 8080;

        public int CacheMegabytes { get; init; } = 512;

        public int MaxStyleVariants { get; init; } = 8;
    }
}
