using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace YukiElevation {
    internal static class ElevationServer {
        /// <summary>Builds the web application. <paramref name="configure"/> allows tests to replace the server.</summary>
        public static WebApplication Build(ServiceOptions options, ElevationService service, Action<WebApplicationBuilder>? configure = null) {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "yukielevation", Args = [] });
            ConfigureLogging(builder.Logging);
            builder.WebHost.UseUrls($"http://{options.Host}:{options.Port}");
            builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ElevationJsonContext.Default));
            configure?.Invoke(builder);

            var app = builder.Build();
            app.Use(async (context, next) => {
                context.Response.Headers.AccessControlAllowOrigin = "*";
                await next(context);
            });
            ElevationEndpoints.Map(app, service, options);
            return app;
        }

        public static void ConfigureLogging(ILoggingBuilder logging) {
            logging.ClearProviders();
            logging.AddSimpleConsole(o => {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
            logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        }
    }
}
