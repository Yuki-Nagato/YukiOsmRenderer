using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Core.Rendering {
    /// <summary>Supplies renderers for style variants.</summary>
    public interface IRendererProvider {
        /// <summary>Layer ids of the (default) style, which <see cref="StyleOptions"/> refer to.</summary>
        IReadOnlyCollection<string> LayerIds { get; }

        /// <summary>Throws an <see cref="ArgumentException"/> if the options do not fit the style.</summary>
        void Validate(StyleOptions options);

        /// <summary>Returns a renderer for the options; dispose the lease when done.</summary>
        IRendererLease AcquireRenderer(StyleOptions options);
    }

    public interface IRendererLease : IDisposable {
        IMapRenderer Renderer { get; }
    }
}
