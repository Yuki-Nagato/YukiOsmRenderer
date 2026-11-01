namespace YukiOsmRenderer.Core.Database {
    /// <summary>
    /// Directory that holds the PostgreSQL cluster, the import state and cached compiled styles. Only one process
    /// may use a data directory at a time; this is enforced with an exclusive lock file.
    /// </summary>
    public sealed class DataDirectory : IDisposable {
        readonly FileStream lockFile;

        DataDirectory(string root, FileStream lockFile) {
            Root = root;
            this.lockFile = lockFile;
        }

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "yukiosm");

        public string Root { get; }

        public string PostgresDirectory => Path.Combine(Root, "postgres");

        public string PasswordFile => Path.Combine(Root, "postgres.password");

        public string StylesDirectory => Path.Combine(Root, "styles");

        public string TemporaryDirectory => Path.Combine(Root, "tmp");

        public string LogDirectory => Path.Combine(Root, "logs");

        public string ImportStateFile => Path.Combine(Root, "import-state.json");

        public string FlatNodesFile => Path.Combine(Root, "flat-nodes.bin");

        /// <summary>Creates (if needed) and locks the data directory.</summary>
        public static DataDirectory Open(string path) {
            var root = Path.GetFullPath(path);
            Directory.CreateDirectory(root);
            FileStream lockFile;
            try {
                lockFile = new FileStream(Path.Combine(root, "yukiosm.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException e) {
                throw new IOException($"The data directory '{root}' is in use by another yukiosm process.", e);
            }
            var directory = new DataDirectory(root, lockFile);
            Directory.CreateDirectory(directory.StylesDirectory);
            Directory.CreateDirectory(directory.LogDirectory);
            if (Directory.Exists(directory.TemporaryDirectory)) {
                // Left over from an interrupted run; nothing else can be using it while the lock is held.
                Directory.Delete(directory.TemporaryDirectory, true);
            }
            Directory.CreateDirectory(directory.TemporaryDirectory);
            return directory;
        }

        public void Dispose() {
            lockFile.Dispose();
        }
    }
}
