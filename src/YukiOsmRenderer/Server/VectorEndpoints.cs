using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using YukiOsmRenderer.Core.Engine;
using YukiOsmRenderer.Core.Styling;
using YukiOsmRenderer.Core.Vector;

namespace YukiOsmRenderer.Server {
    /// <summary>Endpoints for web clients that draw the vector tiles themselves (e.g. MapLibre GL JS).</summary>
    internal static partial class VectorEndpoints {
        const string TileContentType = "application/vnd.mapbox-vector-tile";

        public static void Map(WebApplication app, IVectorTileProvider vector, TileServerOptions options) {
            var assets = new PhysicalFileProvider(Path.GetFullPath(vector.AssetsDirectory));

            app.MapGet("/themes", () => Results.Json(vector.Themes.ToArray()));
            app.MapGet("/vector/tiles.json", (HttpContext context) => Results.Text(vector.TileJson(BaseUrl(context, options)), "application/json"));
            app.MapGet("/vector/{z:int}/{x:long}/{y}", (int z, long x, string y, HttpContext context) => GetTile(vector, z, x, y, context));
            app.MapGet("/vector/styles/{file}", (string file, HttpContext context) => {
                if (!file.EndsWith(".json", StringComparison.Ordinal)) {
                    return Results.NotFound();
                }
                try {
                    var style = StyleQuery.Parse(context.Request.Query, options.DefaultCjk, options.DefaultTextScale, file[..^".json".Length]);
                    return Results.Text(vector.WebStyle(style, BaseUrl(context, options)), "application/json");
                }
                catch (ArgumentException e) {
                    return Results.BadRequest(e.Message);
                }
            });
            app.MapGet("/vector/glyphs/{fontstack}/{range}", (string fontstack, string range) => {
                if (!GlyphRange().IsMatch(range)) {
                    return Results.NotFound();
                }
                // Font stacks are combined by clients as "a,b"; serve the first font that has the range.
                foreach (var font in fontstack.Split(',', StringSplitOptions.TrimEntries)) {
                    if (FontName().IsMatch(font) && assets.GetFileInfo($"glyphs/{font}/{range}") is { Exists: true, PhysicalPath: { } path }) {
                        return Results.File(path, "application/x-protobuf");
                    }
                }
                // CJK ranges are not bundled as glyphs: browsers draw them with local fonts. An empty message has no glyphs.
                return Results.Bytes([], "application/x-protobuf");
            });
            app.MapGet("/vector/sprites/{file}", (string file) => {
                if (!SpriteFile().IsMatch(file) || assets.GetFileInfo($"sprites/{file}") is not { Exists: true, PhysicalPath: { } path }) {
                    return Results.NotFound();
                }
                return Results.File(path, file.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "application/json");
            });
        }

        static IResult GetTile(IVectorTileProvider vector, int z, long x, string y, HttpContext context) {
            if (!y.EndsWith(".pbf", StringComparison.Ordinal) || !long.TryParse(y.AsSpan(0, y.Length - 4), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var row) || !Core.Geo.WebMercator.IsValidTile(z, x, row)) {
                return Results.NotFound();
            }
            context.Response.Headers.CacheControl = "public, max-age=3600";
            if (vector.GetVectorTile(z, x, row) is not var (data, compression)) {
                // No data here (e.g. beyond the extract or the maximum zoom): clients treat this as an empty tile.
                return Results.NoContent();
            }
            if (compression == PmTilesCompression.Gzip && context.Request.Headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase)) {
                context.Response.Headers.ContentEncoding = "gzip";
                return Results.Bytes(data, TileContentType);
            }
            return Results.Bytes(PmTilesArchive.Decompress(data, compression), TileContentType);
        }

        /// <summary>The URL clients reach the server at: --public-url, or the scheme and host of the request.</summary>
        internal static string BaseUrl(HttpContext context, TileServerOptions options) {
            var url = options.PublicUrl ?? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
            return url.EndsWith('/') ? url : url + "/";
        }

        [GeneratedRegex(@"^\d{1,5}-\d{1,5}\.pbf$")]
        private static partial Regex GlyphRange();

        [GeneratedRegex(@"^[A-Za-z0-9_ -]{1,100}$")]
        private static partial Regex FontName();

        [GeneratedRegex(@"^[A-Za-z0-9_-]{1,100}(@2x)?\.(json|png)$")]
        private static partial Regex SpriteFile();
    }
}
