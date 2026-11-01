using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core.Runtime;

namespace YukiOsmRenderer.Core.Styling {
    /// <summary>
    /// Compiles OpenStreetMap Carto (CartoCSS) into Mapnik XML with the bundled carto compiler, after switching
    /// the CJK fonts to the requested variant. Results are cached on disk.
    /// </summary>
    public sealed class CartoCompiler(RuntimeLayout layout, string cacheDirectory, ILogger logger) {
        const string FontsMss = "fonts.mss";

        /// <summary>Returns the compiled Mapnik XML for the given CJK variant.</summary>
        public async Task<string> CompileAsync(CjkVariant variant, CancellationToken cancellationToken = default) {
            var manifest = layout.ReadManifest();
            var sources = ReadSources(variant);
            var key = ComputeKey(sources, manifest);
            var cachePath = Path.Combine(cacheDirectory, $"carto-{variant.ShortName()}-{key}.xml");
            if (File.Exists(cachePath)) {
                logger.LogDebug("Using compiled style {Path}", cachePath);
                return await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(cacheDirectory);
            var workDirectory = Path.Combine(cacheDirectory, "work-" + Guid.NewGuid().ToString("N"));
            try {
                // carto reads the project and its stylesheets from the work directory; file references such as
                // symbols stay relative and are resolved against the Carto directory when Mapnik loads the style.
                foreach (var (relativePath, content) in sources) {
                    var path = Path.Combine(workDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
                }
                logger.LogInformation("Compiling OpenStreetMap Carto {Version} with CJK variant {Variant}", manifest.OpenStreetMapCarto, variant.FontSuffix());
                var result = await ProcessRunner.RunAsync(new ProcessSpec(layout.NodeExecutable, [layout.CartoCompilerScript, "-a", manifest.MapnikApi, "project.mml"]) {
                    WorkingDirectory = workDirectory,
                    CaptureOutput = true,
                    OutputLogLevel = LogLevel.Debug,
                    DisplayName = "carto",
                }, logger, cancellationToken).ConfigureAwait(false);
                if (!result.StandardOutput.Contains("<Map", StringComparison.Ordinal)) {
                    throw new InvalidDataException("carto did not produce a Mapnik XML document.");
                }
                var temporary = cachePath + ".tmp";
                await File.WriteAllTextAsync(temporary, result.StandardOutput, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, cachePath, overwrite: true);
                return result.StandardOutput;
            }
            finally {
                if (Directory.Exists(workDirectory)) {
                    Directory.Delete(workDirectory, true);
                }
            }
        }

        /// <summary>Reads project.mml and the stylesheets, with the CJK variant applied to fonts.mss.</summary>
        internal IReadOnlyList<(string RelativePath, string Content)> ReadSources(CjkVariant variant) {
            var root = layout.CartoDirectory;
            var sources = new List<(string, string)> { ("project.mml", File.ReadAllText(layout.CartoProjectFile)) };
            var styleDirectory = Path.Combine(root, "style");
            foreach (var file in Directory.EnumerateFiles(styleDirectory, "*.mss").Order(StringComparer.Ordinal)) {
                var content = File.ReadAllText(file);
                if (Path.GetFileName(file) == FontsMss) {
                    content = CjkVariants.ApplyToFontsMss(content, variant);
                }
                sources.Add((Path.Combine("style", Path.GetFileName(file)), content));
            }
            return sources;
        }

        internal static string ComputeKey(IReadOnlyList<(string RelativePath, string Content)> sources, AssetManifest manifest) {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes($"{manifest.OpenStreetMapCarto}\n{manifest.Carto}\n{manifest.MapnikApi}\n"));
            foreach (var (relativePath, content) in sources) {
                hash.AppendData(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/') + "\n"));
                hash.AppendData(Encoding.UTF8.GetBytes(content));
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
        }
    }
}
