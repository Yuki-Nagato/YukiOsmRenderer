using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core.Rendering;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Server {
    internal sealed record TileServerOptions {
        public string Host { get; init; } = "localhost";

        public int Port { get; init; } = 8080;

        public TileServiceOptions Tiles { get; init; } = new();

        /// <summary>CJK variant of requests without the cjk parameter.</summary>
        public CjkVariant DefaultCjk { get; init; } = CjkVariant.SimplifiedChinese;

        /// <summary>Tile format of requests without the format parameter.</summary>
        public TileFormat DefaultFormat { get; init; } = TileFormat.Png256;

        /// <summary>Directory with the preview page (index.html and Leaflet); optional.</summary>
        public string? WebDirectory { get; init; }

        public bool Verbose { get; init; }
    }

    internal static class TileServer {
        /// <summary>Builds the web application. <paramref name="configure"/> allows tests to replace the server.</summary>
        public static WebApplication Build(TileServerOptions options, IRendererProvider provider, Action<WebApplicationBuilder>? configure = null) {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "yukiosm", Args = [] });
            Cli.Logging.Configure(builder.Logging, options.Verbose);
            builder.WebHost.UseUrls($"http://{options.Host}:{options.Port}");
            builder.Services.AddSingleton(provider);
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(options.Tiles);
            builder.Services.AddSingleton<TileService>();
            configure?.Invoke(builder);

            var app = builder.Build();
            app.Use(async (context, next) => {
                context.Response.Headers.AccessControlAllowOrigin = "*";
                await next(context);
            });
            if (options.WebDirectory != null && Directory.Exists(options.WebDirectory)) {
                var files = new PhysicalFileProvider(Path.GetFullPath(options.WebDirectory));
                app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
                app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
            }

            app.MapGet("/tiles/{z:int}/{x:long}/{y}", GetTileAsync);
            app.MapGet("/layers", (IRendererProvider renderers) => Results.Json(renderers.LayerIds.Order(StringComparer.Ordinal).ToArray()));
            app.MapGet("/health", () => Results.Text("ok"));
            return app;
        }

        static async Task<IResult> GetTileAsync(int z, long x, string y, HttpContext context, TileService tiles, IRendererProvider renderers, TileServerOptions options,
            ILoggerFactory loggerFactory) {
            if (!TileAddress.TryParse(z, x, y, out var address)) {
                return Results.NotFound();
            }
            StyleOptions style;
            TileFormat format;
            try {
                style = StyleQuery.Parse(context.Request.Query, options.DefaultCjk);
                style.Validate(renderers.LayerIds);
                format = StyleQuery.ParseFormat(context.Request.Query, options.DefaultFormat);
            }
            catch (ArgumentException e) {
                return Results.BadRequest(e.Message);
            }
            try {
                var png = await tiles.GetTileAsync(address, style, format, context.RequestAborted);
                context.Response.Headers.CacheControl = "public, max-age=3600";
                return Results.Bytes(png, "image/png");
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) {
                return Results.Empty;
            }
            catch (Exception e) {
                loggerFactory.CreateLogger(typeof(TileServer)).LogError(e, "Failed to render tile {Zoom}/{X}/{Y}", z, x, y);
                return Results.Problem("Rendering failed.", statusCode: StatusCodes.Status500InternalServerError);
            }
        }
    }
}
