using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YukiElevation;

// Anywhere on the command line, as the Docker image puts --host before the arguments.
if (args.Contains("--version")) {
    Console.WriteLine(typeof(ServiceOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    return 0;
}

ServiceOptions? options;
try {
    options = ServiceOptions.Parse(args);
}
catch (ArgumentException e) {
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(ServiceOptions.Usage);
    return 2;
}
if (options == null) {
    Console.WriteLine(ServiceOptions.Usage);
    return 0;
}

using var loggerFactory = LoggerFactory.Create(ElevationServer.ConfigureLogging);
var logger = loggerFactory.CreateLogger("yukielevation");

GdalSetup.Configure(options.CacheMegabytes);
logger.LogInformation("GDAL {Version}", GdalSetup.Version);
ElevationService service;
try {
    service = ElevationService.Open(options, logger);
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) {
    logger.LogError("{Message}", e.Message);
    return 1;
}

using (service) {
    await using var app = ElevationServer.Build(options, service);
    await app.StartAsync();
    logger.LogInformation("Serving elevations at http://{Host}:{Port}/elevation", options.Host, options.Port);
    await app.WaitForShutdownAsync();
}
return 0;
