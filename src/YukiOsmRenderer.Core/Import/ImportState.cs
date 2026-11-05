using System.Security.Cryptography;
using YukiOsmRenderer.Core.Runtime;

namespace YukiOsmRenderer.Core.Import {
    /// <summary>Describes the content of the database, persisted in the data directory.</summary>
    public sealed record ImportState {
        public int Version { get; init; } = CurrentVersion;

        public const int CurrentVersion = 1;

        public string Fingerprint { get; init; } = "";

        /// <summary>OpenStreetMap Carto version whose import script created the tables.</summary>
        public string CartoVersion { get; init; } = "";

        /// <summary>SHA-256 of the osm2pgsql style that created the tables; null in states of earlier versions.</summary>
        public string? ImportScript { get; init; }

        /// <summary>SHA-256 of each SQL script run on the tables, by file name; null in states of earlier versions.</summary>
        public IReadOnlyDictionary<string, string>? SqlScripts { get; init; }

        public string ExternalData { get; init; } = "";

        public IReadOnlyList<InputFileInfo> Inputs { get; init; } = [];

        public bool Completed { get; init; }

        public DateTimeOffset? CompletedAt { get; init; }

        /// <summary>How the database differs from what the installation would import.</summary>
        public ImportDifference CompareWith(ImportInstallation installation) {
            // States of earlier versions only record the OpenStreetMap Carto version, which comes with its scripts.
            var sameCarto = CartoVersion == installation.CartoVersion;
            return new ImportDifference {
                ImportScript = Version != CurrentVersion || (ImportScript != null ? ImportScript != installation.ImportScript : !sameCarto),
                SqlScripts = [.. ImportInstallation.SqlScriptNames.Where(name =>
                    SqlScripts?.GetValueOrDefault(name) is { } hash ? hash != installation.SqlScripts[name] : !sameCarto)],
                ExternalData = ExternalData != installation.ExternalData,
            };
        }
    }

    /// <summary>What an installation imports: its OpenStreetMap Carto scripts and external data.</summary>
    public sealed record ImportInstallation(string CartoVersion, string ImportScript, IReadOnlyDictionary<string, string> SqlScripts, string ExternalData) {
        public const string ImportScriptName = "openstreetmap-carto-flex.lua";

        /// <summary>The SQL scripts run on the tables after osm2pgsql, in order.</summary>
        public static readonly IReadOnlyList<string> SqlScriptNames = ["indexes.sql", "functions.sql", "common-values.sql"];

        public static ImportInstallation Read(RuntimeLayout layout) {
            var manifest = layout.ReadManifest();
            string Hash(string name) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(layout.CartoDirectory, name))));
            return new ImportInstallation(manifest.OpenStreetMapCarto, Hash(ImportScriptName), SqlScriptNames.ToDictionary(name => name, Hash), manifest.ExternalData);
        }
    }

    /// <summary>How an imported database differs from what the installation would import.</summary>
    public sealed record ImportDifference {
        /// <summary>The tables come from another osm2pgsql style; only importing the input files again changes them.</summary>
        public bool ImportScript { get; init; }

        /// <summary>SQL scripts that differ; they can be run again on the existing tables.</summary>
        public IReadOnlyList<string> SqlScripts { get; init; } = [];

        /// <summary>The external data (water polygons etc.) is another snapshot; it can be loaded again.</summary>
        public bool ExternalData { get; init; }

        public bool IsEmpty => !ImportScript && SqlScripts.Count == 0 && !ExternalData;

        public bool CanUpdateInPlace => SqlScripts.Count > 0 || ExternalData;
    }
}
