namespace YukiOsmRenderer.Core.Import {
    /// <summary>Describes the content of the database, persisted in the data directory.</summary>
    public sealed record ImportState {
        public int Version { get; init; } = CurrentVersion;

        public const int CurrentVersion = 1;

        public string Fingerprint { get; init; } = "";

        /// <summary>OpenStreetMap Carto version whose import scripts and SQL functions were used.</summary>
        public string CartoVersion { get; init; } = "";

        public string ExternalData { get; init; } = "";

        public IReadOnlyList<InputFileInfo> Inputs { get; init; } = [];

        public bool Completed { get; init; }

        public DateTimeOffset? CompletedAt { get; init; }

        /// <summary>Whether the database already contains exactly what the given import would produce.</summary>
        public bool Matches(string fingerprint, string cartoVersion, string externalData) {
            return IsUsable(cartoVersion, externalData) && Fingerprint == fingerprint;
        }

        /// <summary>Whether the database is complete and was built for the given installation, whatever the input files.</summary>
        public bool IsUsable(string cartoVersion, string externalData) {
            return Completed && Version == CurrentVersion && CartoVersion == cartoVersion && ExternalData == externalData;
        }
    }
}
