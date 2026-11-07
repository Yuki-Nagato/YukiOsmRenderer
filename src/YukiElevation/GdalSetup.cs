using System.Globalization;
using MaxRev.Gdal.Core;
using OSGeo.GDAL;

namespace YukiElevation {
    internal static class GdalSetup {
        static readonly Lock Sync = new();
        static bool configured;

        public static void Configure(int cacheMegabytes) {
            lock (Sync) {
                if (configured) {
                    return;
                }
                GdalBase.ConfigureAll();
                Gdal.UseExceptions();
                Gdal.SetConfigOption("GDAL_CACHEMAX", cacheMegabytes.ToString(CultureInfo.InvariantCulture));
                // Opening a file must not list its directory, which may hold tens of thousands of Copernicus tiles.
                Gdal.SetConfigOption("GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR");
                configured = true;
            }
        }

        public static string Version => Gdal.VersionInfo("RELEASE_NAME");
    }
}
