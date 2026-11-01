using System.Buffers.Binary;
using System.Security.Cryptography;

namespace YukiOsmRenderer.Core.Import {
    public sealed record InputFileInfo(string Name, long Size, string Hash);

    /// <summary>
    /// Identifies a set of .osm.pbf files by content so that the database is only rebuilt when the input changes.
    /// Hashing the size together with the beginning and the end of each file is fast even for the planet and catches
    /// any realistic change, because the header block carries the replication timestamp and blocks are compressed.
    /// </summary>
    public static class InputFingerprint {
        internal const int SampleSize = 4 * 1024 * 1024;

        public static (string Fingerprint, IReadOnlyList<InputFileInfo> Files) Compute(IEnumerable<string> paths) {
            var files = new List<InputFileInfo>();
            var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var path in paths) {
                var fullPath = Path.GetFullPath(path);
                if (!seen.Add(fullPath)) {
                    throw new ArgumentException($"The input file '{path}' is given more than once.");
                }
                files.Add(ComputeFile(fullPath));
            }
            if (files.Count == 0) {
                throw new ArgumentException("At least one input file is required.");
            }
            // The order of the input files does not matter to osm2pgsql.
            var combined = string.Join('\n', files.Select(f => f.Hash).Order(StringComparer.Ordinal));
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(combined)));
            return (fingerprint, files);
        }

        static InputFileInfo ComputeFile(string path) {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            var size = stream.Length;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> sizeBytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(sizeBytes, size);
            hash.AppendData(sizeBytes);
            var buffer = new byte[(int)Math.Min(SampleSize, size)];
            stream.ReadExactly(buffer);
            hash.AppendData(buffer);
            if (size > SampleSize) {
                stream.Seek(Math.Max(SampleSize, size - SampleSize), SeekOrigin.Begin);
                var tail = new byte[(int)(size - stream.Position)];
                stream.ReadExactly(tail);
                hash.AppendData(tail);
            }
            return new InputFileInfo(Path.GetFileName(path), size, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
    }
}
