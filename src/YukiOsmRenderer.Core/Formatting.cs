using System.Globalization;

namespace YukiOsmRenderer.Core {
    /// <summary>Human-readable sizes and durations for log messages.</summary>
    public static class Formatting {
        public static string Size(long bytes) {
            string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1) {
                value /= 1024;
                unit++;
            }
            return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
        }

        /// <summary>Formats as m:ss, or h:mm:ss from one hour on.</summary>
        public static string Duration(TimeSpan time) {
            var seconds = (long)Math.Round(time.TotalSeconds);
            var hours = seconds / 3600;
            return hours > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{seconds / 60 % 60:00}:{seconds % 60:00}")
                : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
        }
    }
}
