using System.CommandLine;
using System.CommandLine.Parsing;
using YukiOsmRenderer.Core.Database;
using YukiOsmRenderer.Core.Geo;
using YukiOsmRenderer.Core.Styling;

namespace YukiOsmRenderer.Cli {
    /// <summary>Command line definition. Handlers are injectable so that parsing can be tested without rendering.</summary>
    internal static class Commands {
        public const string DataDirectoryEnvironmentVariable = "YUKIOSM_DATA_DIR";

        public static RootCommand Create(
            Func<RenderSettings, CancellationToken, Task<int>> render,
            Func<ServeSettings, CancellationToken, Task<int>> serve) {
            var inputs = new Option<string[]>("--input", "-i") {
                Description = "OpenStreetMap .osm.pbf file; repeat the option for several files.",
                Required = true,
                Arity = ArgumentArity.OneOrMore,
                AllowMultipleArgumentsPerToken = true,
                HelpName = "file",
            };
            var cjk = new Option<string>("--cjk") {
                Description = "Glyph forms of Han characters in labels: sc (Simplified Chinese), tc (Traditional Chinese), hk (Hong Kong), jp (Japanese) or kr (Korean). For serve, the default for requests without the cjk parameter.",
                DefaultValueFactory = _ => "sc",
                HelpName = "sc|tc|hk|jp|kr",
            };
            cjk.Validators.Add(result => {
                var text = result.GetValueOrDefault<string>();
                if (!CjkVariants.TryParse(text, out _)) {
                    result.AddError($"Unknown CJK variant '{text}'. Expected one of: {string.Join(", ", CjkVariants.Names)}.");
                }
            });
            var dataDirectory = new Option<string>("--data-dir") {
                Description = $"Directory for the database and caches; reused across runs with the same input files (default: {DataDirectoryEnvironmentVariable} or a per-user directory).",
                DefaultValueFactory = _ => Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable) is { Length: > 0 } fromEnvironment
                    ? fromEnvironment
                    : DataDirectory.DefaultPath,
                HelpName = "dir",
            };
            var home = new Option<string?>("--home") {
                Description = "Installation directory with runtime/ and assets/ (default: YUKIOSM_HOME or the executable's directory).",
                HelpName = "dir",
            };
            var threads = new Option<int>("--threads") {
                Description = "Number of concurrent render and import threads.",
                DefaultValueFactory = _ => Environment.ProcessorCount,
            };
            threads.Validators.Add(result => {
                if (result.GetValueOrDefault<int>() < 1) {
                    result.AddError("--threads must be at least 1.");
                }
            });
            var forceImport = new Option<bool>("--force-import") {
                Description = "Rebuild the database even if it already contains the input files.",
            };
            var verbose = new Option<bool>("--verbose", "-v") {
                Description = "Write detailed log messages.",
            };
            Option[] common = [inputs, cjk, dataDirectory, home, threads, forceImport, verbose];

            CommonSettings BindCommon(ParseResult result) {
                return new CommonSettings {
                    Inputs = result.GetRequiredValue(inputs),
                    Cjk = CjkVariants.Parse(result.GetRequiredValue(cjk)),
                    DataDirectory = result.GetRequiredValue(dataDirectory),
                    Home = result.GetValue(home),
                    Threads = result.GetValue(threads),
                    ForceImport = result.GetValue(forceImport),
                    Verbose = result.GetValue(verbose),
                };
            }

            // Style options for transparent rendering.
            var transparent = new Option<bool>("--transparent") {
                Description = "Transparent rendering for overlays: drop the background and all area fills, keep lines, labels and icons.",
            };
            var keepFill = new Option<string[]>("--keep-fill") {
                Description = "With --transparent: layers whose fills are kept, e.g. water-areas,buildings.",
                AllowMultipleArgumentsPerToken = true,
                HelpName = "layers",
            };
            var hideOutline = new Option<bool>("--hide-outline") {
                Description = "With --transparent: also drop the outlines of area layers.",
            };
            var hideLayer = new Option<string[]>("--hide-layer") {
                Description = "Layers to leave out entirely, e.g. buildings,amenity-points.",
                AllowMultipleArgumentsPerToken = true,
                HelpName = "layers",
            };

            var zoom = new Option<int>("--zoom", "-z") {
                Description = $"Zoom level (0-{WebMercator.MaxZoom}).",
                Required = true,
            };
            zoom.Validators.Add(result => {
                var value = result.GetValueOrDefault<int>();
                if (value < 0 || value > WebMercator.MaxZoom) {
                    result.AddError($"--zoom must be between 0 and {WebMercator.MaxZoom}.");
                }
            });
            var bbox = new Option<GeoBoundingBox>("--bbox") {
                Description = "Area as minLon,minLat,maxLon,maxLat in WGS 84 degrees (default: the whole world).",
                DefaultValueFactory = _ => GeoBoundingBox.World,
                CustomParser = result => {
                    try {
                        return GeoBoundingBox.Parse(result.Tokens.Single().Value);
                    }
                    catch (Exception e) when (e is FormatException or ArgumentException) {
                        result.AddError($"Invalid --bbox: {e.Message}");
                        return default;
                    }
                },
                HelpName = "minLon,minLat,maxLon,maxLat",
            };
            var output = new Option<string>("--output", "-o") {
                Description = "Output GeoTIFF file, or - for standard output.",
                DefaultValueFactory = _ => RenderSettings.StandardOutput,
                HelpName = "file",
            };
            var scale = new Option<int>("--scale") {
                Description = "Scale factor: 1 for standard resolution, 2 for high-DPI (twice the pixels).",
                DefaultValueFactory = _ => 1,
            };
            scale.AcceptOnlyFromAmong("1", "2");
            var maxPixels = new Option<long>("--max-pixels") {
                Description = "Refuse to render images with more pixels than this.",
                DefaultValueFactory = _ => RenderSettings.DefaultMaxPixels,
            };

            var renderCommand = new Command("render", "Render an area at one zoom level into a GeoTIFF (EPSG:3857).");
            foreach (var option in common.Concat([zoom, bbox, output, scale, transparent, keepFill, hideOutline, hideLayer, maxPixels])) {
                renderCommand.Options.Add(option);
            }
            renderCommand.SetAction((result, cancellationToken) => render(new RenderSettings {
                Common = BindCommon(result),
                Zoom = result.GetValue(zoom),
                BoundingBox = result.GetValue(bbox),
                Output = result.GetRequiredValue(output),
                TileSize = 256 * result.GetValue(scale),
                Style = StyleOptions.Create(result.GetValue(transparent), result.GetValue(keepFill), result.GetValue(hideOutline), result.GetValue(hideLayer),
                    CjkVariants.Parse(result.GetRequiredValue(cjk))),
                MaxPixels = result.GetValue(maxPixels),
            }, cancellationToken));

            var host = new Option<string>("--host") {
                Description = "Address to listen on, e.g. localhost, 0.0.0.0 or *.",
                DefaultValueFactory = _ => "localhost",
            };
            var port = new Option<int>("--port", "-p") {
                Description = "TCP port to listen on.",
                DefaultValueFactory = _ => 8080,
            };
            port.Validators.Add(result => {
                var value = result.GetValueOrDefault<int>();
                if (value is < 1 or > 65535) {
                    result.AddError("--port must be between 1 and 65535.");
                }
            });
            var cacheSize = new Option<int>("--cache-size") {
                Description = "Memory for cached tiles in MB.",
                DefaultValueFactory = _ => 512,
            };
            var maxVariants = new Option<int>("--max-style-variants") {
                Description = "Number of style variants (transparency options) kept loaded at the same time.",
                DefaultValueFactory = _ => 8,
            };
            var serveCommand = new Command("serve", "Serve Web Mercator XYZ tiles over HTTP.");
            foreach (var option in common.Concat([host, port, cacheSize, maxVariants])) {
                serveCommand.Options.Add(option);
            }
            serveCommand.SetAction((result, cancellationToken) => serve(new ServeSettings {
                Common = BindCommon(result),
                Host = result.GetRequiredValue(host),
                Port = result.GetValue(port),
                CacheMegabytes = Math.Max(0, result.GetValue(cacheSize)),
                MaxStyleVariants = Math.Max(1, result.GetValue(maxVariants)),
            }, cancellationToken));

            var root = new RootCommand("Self-contained OpenStreetMap renderer reproducing the standard openstreetmap.org map style.");
            root.Subcommands.Add(renderCommand);
            root.Subcommands.Add(serveCommand);
            return root;
        }
    }
}
