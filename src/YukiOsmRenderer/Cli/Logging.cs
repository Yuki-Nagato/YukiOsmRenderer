using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace YukiOsmRenderer.Cli {
    internal static class Logging {
        /// <summary>Applies the logging setup shared by all commands. Everything goes to standard error.</summary>
        public static void Configure(ILoggingBuilder builder, bool verbose) {
            builder.ClearProviders();
            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
            builder.AddFilter("Microsoft", verbose ? LogLevel.Information : LogLevel.Warning);
            builder.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
            builder.AddFilter("Npgsql", LogLevel.Warning);
            builder.AddSimpleConsole(options => {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
            builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        }

        public static ILoggerFactory CreateFactory(bool verbose) {
            return LoggerFactory.Create(builder => Configure(builder, verbose));
        }
    }
}
