using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Core.Rendering {
    /// <summary>Supplies renderers for style variants.</summary>
    public interface IRendererProvider {
        /// <summary>Layer ids of the style, used to validate <see cref="StyleOptions"/>.</summary>
        IReadOnlySet<string> LayerIds { get; }

        /// <summary>Returns a renderer for the options; dispose the lease when done.</summary>
        IRendererLease AcquireRenderer(StyleOptions options);
    }

    public interface IRendererLease : IDisposable {
        IMapRenderer Renderer { get; }
    }
}
