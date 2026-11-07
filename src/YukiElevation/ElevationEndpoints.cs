using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace YukiElevation {
    internal sealed record PointQuery(double? Lat, double? Lon);

    internal sealed record PointElevation(double Lat, double Lon, double? Elevation, string? Dataset);

    internal sealed record BatchResponse(IReadOnlyList<PointElevation> Results);

    internal sealed record DatasetInfo(string Name, string Kind, int Files);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(PointQuery[]))]
    [JsonSerializable(typeof(PointElevation))]
    [JsonSerializable(typeof(BatchResponse))]
    [JsonSerializable(typeof(DatasetInfo[]))]
    internal sealed partial class ElevationJsonContext : JsonSerializerContext {
    }

    internal static class ElevationEndpoints {
        public static void Map(WebApplication app, ElevationService service, ServiceOptions options) {
            app.MapGet("/elevation", (HttpContext context) => {
                var query = context.Request.Query;
                if (!TryParseInterpolation(query["interpolation"], out var interpolation)) {
                    return Results.BadRequest("interpolation must be bilinear or nearest.");
                }
                var latitude = Coordinate(query["lat"]);
                var longitude = Coordinate(query["lon"]);
                if (Validate(latitude, longitude) is { } error) {
                    return Results.BadRequest(error);
                }
                return Results.Json(Lookup(service, latitude!.Value, longitude!.Value, interpolation), ElevationJsonContext.Default.PointElevation);
            });
            app.MapPost("/elevation", (PointQuery[]? points, HttpContext context) => {
                if (!TryParseInterpolation(context.Request.Query["interpolation"], out var interpolation)) {
                    return Results.BadRequest("interpolation must be bilinear or nearest.");
                }
                if (points == null) {
                    return Results.BadRequest("The body must be a JSON array of {\"lat\": ..., \"lon\": ...}.");
                }
                if (points.Length > options.MaxPoints) {
                    return Results.BadRequest($"At most {options.MaxPoints} points per request.");
                }
                for (var i = 0; i < points.Length; i++) {
                    if (Validate(points[i]?.Lat, points[i]?.Lon) is { } error) {
                        return Results.BadRequest($"Point {i}: {error}");
                    }
                }
                var results = points.Select(p => Lookup(service, p.Lat!.Value, p.Lon!.Value, interpolation)).ToArray();
                return Results.Json(new BatchResponse(results), ElevationJsonContext.Default.BatchResponse);
            });
            app.MapGet("/datasets", () => Results.Json(service.Sources.Select(Describe).ToArray(), ElevationJsonContext.Default.DatasetInfoArray));
            app.MapGet("/health", () => Results.Text("ok"));
        }

        static PointElevation Lookup(ElevationService service, double latitude, double longitude, Interpolation interpolation) {
            var elevation = service.Lookup(latitude, longitude, interpolation);
            return new PointElevation(latitude, longitude, elevation.Metres is { } metres ? Math.Round(metres, 2) : null, elevation.Dataset);
        }

        static DatasetInfo Describe(ElevationSource source) {
            return source switch {
                GridSource grid => new DatasetInfo(source.Name, "grid", grid.FileCount),
                CopernicusSource copernicus => new DatasetInfo(source.Name, "copernicus", copernicus.TileCount),
                _ => new DatasetInfo(source.Name, "other", 0),
            };
        }

        /// <summary>A coordinate parameter, NaN if it is not a number and null if it is missing.</summary>
        static double? Coordinate(string? value) {
            if (string.IsNullOrEmpty(value)) {
                return null;
            }
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : double.NaN;
        }

        static string? Validate(double? latitude, double? longitude) {
            if (latitude == null || longitude == null) {
                return "lat and lon are required.";
            }
            if (!(latitude >= -90 && latitude <= 90)) {
                return "lat must be a number from -90 to 90.";
            }
            if (!(longitude >= -180 && longitude <= 180)) {
                return "lon must be a number from -180 to 180.";
            }
            return null;
        }

        static bool TryParseInterpolation(string? value, out Interpolation interpolation) {
            interpolation = Interpolation.Bilinear;
            if (string.IsNullOrEmpty(value)) {
                return true;
            }
            return char.IsAsciiLetter(value[0]) && Enum.TryParse(value, ignoreCase: true, out interpolation);
        }
    }
}
