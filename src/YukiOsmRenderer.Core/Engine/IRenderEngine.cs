namespace YukiOsmRenderer.Core.Engine {
    /// <summary>What render, tiles and serve need from a rendering engine.</summary>
    public interface IRenderEngine : Rendering.IRendererProvider, IAsyncDisposable {
        /// <summary>Maximum number of concurrent renders.</summary>
        int Threads { get; }

        /// <summary>Directory for temporary files, e.g. large GeoTIFFs being assembled.</summary>
        string TemporaryDirectory { get; }
    }

    public enum EngineKind {
        /// <summary>OpenStreetMap Carto with Mapnik and PostGIS.</summary>
        Carto,

        /// <summary>Shortbread vector tiles built by Planetiler, drawn by MapLibre Native.</summary>
        Vector,
    }

    /// <summary>Vector tiles and the resources web clients need to draw them.</summary>
    public interface IVectorTileProvider {
        /// <summary>Names of the available themes.</summary>
        IReadOnlyList<string> Themes { get; }

        /// <summary>Directory with glyphs/ and sprites/.</summary>
        string AssetsDirectory { get; }

        /// <summary>The stored bytes of a tile and their compression, or null if there is no such tile.</summary>
        (byte[] Data, Vector.PmTilesCompression Compression)? GetVectorTile(int zoom, long x, long y);

        /// <summary>TileJSON document for tiles served below <paramref name="baseUrl"/>.</summary>
        string TileJson(string baseUrl);

        /// <summary>MapLibre style for web clients loading everything from below <paramref name="baseUrl"/>.</summary>
        /// <exception cref="ArgumentException">The options do not fit the style.</exception>
        string WebStyle(Styling.StyleOptions options, string baseUrl);
    }
}
