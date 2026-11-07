using Microsoft.Extensions.Logging;

namespace YukiElevation {
    internal readonly record struct Elevation(double? Metres, string? Dataset);

    /// <summary>Looks up elevations in sources ordered from the most to the least detailed.</summary>
    internal sealed class ElevationService(IReadOnlyList<ElevationSource> sources) : IDisposable {
        public IReadOnlyList<ElevationSource> Sources => sources;

        /// <summary>Opens the data sets of the options with GDAL, the most detailed first.</summary>
        /// <exception cref="IOException">A file cannot be read.</exception>
        /// <exception cref="InvalidDataException">The files do not form a usable data set.</exception>
        public static ElevationService Open(ServiceOptions options, ILogger logger) {
            var opened = new List<ElevationSource>();
            try {
                if (options.Glo30 != null) {
                    var copernicus = CopernicusSource.Create("copernicus", options.Glo30, options.MaxOpenTiles, GdalRaster.Open);
                    opened.Add(copernicus);
                    if (copernicus.TileCount == 0) {
                        logger.LogWarning("No Copernicus DEM tiles (Copernicus_DSM_*_DEM.tif) below {Directory}", options.Glo30);
                    }
                    else {
                        logger.LogInformation("Copernicus DEM: {Count} tiles below {Directory}", copernicus.TileCount, options.Glo30);
                    }
                }
                if (options.Gebco != null) {
                    var paths = Directory.GetFiles(options.Gebco, "*.tif", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
                    if (paths.Length == 0) {
                        throw new InvalidDataException($"There are no GeoTIFFs (*.tif) in '{options.Gebco}'.");
                    }
                    Array.Sort(paths, StringComparer.Ordinal);
                    var rasters = new List<IRaster>();
                    try {
                        foreach (var path in paths) {
                            rasters.Add(GdalRaster.Open(path));
                        }
                        var grid = GridSource.Create("gebco", rasters);
                        opened.Add(grid);
                        logger.LogInformation("GEBCO: {Count} files, {Columns} x {Rows} pixels in {Directory}", grid.FileCount, grid.Columns, grid.Rows,
                            options.Gebco);
                    }
                    catch (Exception e) {
                        rasters.ForEach(r => r.Dispose());
                        if (e is InvalidDataException) {
                            throw new InvalidDataException($"'{options.Gebco}': {e.Message}", e);
                        }
                        throw;
                    }
                }
                return new ElevationService(opened);
            }
            catch {
                opened.ForEach(s => s.Dispose());
                throw;
            }
        }

        public Elevation Lookup(double latitude, double longitude, Interpolation interpolation) {
            string? seaLevelSource = null;
            foreach (var source in sources) {
                if (source.Sample(longitude, latitude, interpolation) is not { } value) {
                    continue;
                }
                if (source.ZeroMeansSea && value == 0) {
                    // The sea surface: a less detailed source may know the depth.
                    seaLevelSource ??= source.Name;
                    continue;
                }
                if (seaLevelSource != null && value >= 0) {
                    return new Elevation(0, seaLevelSource);
                }
                return new Elevation(value, source.Name);
            }
            return seaLevelSource != null ? new Elevation(0, seaLevelSource) : new Elevation(null, null);
        }

        public void Dispose() {
            foreach (var source in sources) {
                source.Dispose();
            }
        }
    }
}
