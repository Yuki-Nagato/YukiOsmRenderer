using System.Globalization;
using System.Text.RegularExpressions;

namespace YukiElevation {
    /// <summary>
    /// Copernicus DEM tiles of 1° by 1° (GLO-30 or GLO-90, as published on AWS or by Copernicus), found by their file
    /// names anywhere below a directory. There are no tiles over the open sea, and the sea surface is 0 in the others.
    /// </summary>
    internal sealed partial class CopernicusSource : ElevationSource {
        readonly Dictionary<(int Latitude, int Longitude), string> files;
        readonly RasterCache<(int Latitude, int Longitude)> cache;

        CopernicusSource(string name, Dictionary<(int Latitude, int Longitude), string> files, int maxOpenTiles, Func<string, IRaster> open) {
            Name = name;
            this.files = files;
            cache = new RasterCache<(int Latitude, int Longitude)>(maxOpenTiles, key => open(files[key]));
        }

        public override string Name { get; }

        public override bool ZeroMeansSea => true;

        public int TileCount => files.Count;

        public int OpenTileCount => cache.OpenCount;

        /// <summary>Indexes the tiles below <paramref name="directory"/>; where both GLO-30 and GLO-90 have a tile, GLO-30 is used.</summary>
        public static CopernicusSource Create(string name, string directory, int maxOpenTiles, Func<string, IRaster> open) {
            var found = new Dictionary<(int Latitude, int Longitude), (string Path, int Resolution)>();
            var options = new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true };
            foreach (var path in Directory.EnumerateFiles(directory, "*.tif", options)) {
                var match = TileName().Match(Path.GetFileName(path));
                if (!match.Success) {
                    continue;
                }
                var latitude = int.Parse(match.Groups["lat"].ValueSpan, CultureInfo.InvariantCulture);
                var longitude = int.Parse(match.Groups["lon"].ValueSpan, CultureInfo.InvariantCulture);
                var key = (match.Groups["ns"].Value is "S" or "s" ? -latitude : latitude, match.Groups["ew"].Value is "W" or "w" ? -longitude : longitude);
                var resolution = int.Parse(match.Groups["resolution"].ValueSpan, CultureInfo.InvariantCulture);
                if (!found.TryGetValue(key, out var existing) || resolution < existing.Resolution) {
                    found[key] = (path, resolution);
                }
            }
            return new CopernicusSource(name, found.ToDictionary(e => e.Key, e => e.Value.Path), maxOpenTiles, open);
        }

        public override double? Sample(double longitude, double latitude, Interpolation interpolation) {
            if (longitude >= 180) {
                longitude -= 360;
            }
            var key = ((int)Math.Floor(latitude), (int)Math.Floor(longitude));
            if (!files.ContainsKey(key)) {
                return null;
            }
            using var lease = cache.Acquire(key);
            var raster = lease.Raster;
            var (x, y) = raster.Transform.ToPixel(longitude, latitude);
            var x0 = (long)Math.Floor(x);
            var y0 = (long)Math.Floor(y);
            Span<double> values = stackalloc double[4];
            if (raster.Contains(x0, y0) && raster.Contains(x0 + 1, y0 + 1)) {
                raster.Read((int)x0, (int)y0, 2, 2, values);
            }
            else {
                for (var i = 0; i < 4; i++) {
                    values[i] = Pixel(raster, x0 + (i & 1), y0 + (i >> 1));
                }
            }
            return Combine(values, x - x0, y - y0, interpolation);
        }

        /// <summary>A pixel of a tile, or beyond its edges the pixel of a neighbouring tile nearest to that position.</summary>
        double Pixel(IRaster raster, long column, long row) {
            if (raster.Contains(column, row)) {
                return raster.Read(column, row);
            }
            var (longitude, latitude) = raster.Transform.CenterOf(column, row);
            return NearestPixel(longitude, latitude);
        }

        double NearestPixel(double longitude, double latitude) {
            // Depending on the publisher, neighbouring tiles share their edge pixels or not, so a position on a whole
            // degree may be in either tile.
            foreach (var tileLatitude in Candidates(latitude)) {
                foreach (var tileLongitude in Candidates(longitude)) {
                    var shift = tileLongitude < -180 ? 360 : tileLongitude >= 180 ? -360 : 0;
                    var key = (tileLatitude, tileLongitude + shift);
                    if (!files.ContainsKey(key)) {
                        continue;
                    }
                    using var lease = cache.Acquire(key);
                    var raster = lease.Raster;
                    var (x, y) = raster.Transform.ToPixel(longitude + shift, latitude);
                    var column = (long)Math.Floor(x + 0.5);
                    var row = (long)Math.Floor(y + 0.5);
                    if (raster.Contains(column, row)) {
                        return raster.Read(column, row);
                    }
                }
            }
            return double.NaN;
        }

        static IEnumerable<int> Candidates(double degrees) {
            var floor = (int)Math.Floor(degrees);
            yield return floor;
            var below = (int)Math.Ceiling(degrees) - 1;
            if (below != floor) {
                yield return below;
            }
        }

        public override void Dispose() {
            cache.Dispose();
        }

        [GeneratedRegex(@"^Copernicus_DSM_(?:COG_)?(?<resolution>10|30)_(?<ns>[NS])(?<lat>\d{2})_00_(?<ew>[EW])(?<lon>\d{3})_00_DEM\.tif$", RegexOptions.IgnoreCase)]
        private static partial Regex TileName();
    }
}
