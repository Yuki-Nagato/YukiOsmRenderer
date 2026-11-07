namespace YukiElevation {
    /// <summary>
    /// Opens rasters on demand and keeps a limited number open. A lease keeps its raster open until it is disposed,
    /// so the cache may hold more rasters than its capacity while they are in use.
    /// </summary>
    internal sealed class RasterCache<TKey>(int capacity, Func<TKey, IRaster> open) : IDisposable where TKey : notnull {
        readonly Dictionary<TKey, Entry> entries = new();
        readonly LinkedList<Entry> idle = new();
        readonly Lock sync = new();

        internal sealed class Entry(TKey key, Lazy<IRaster> raster) {
            public TKey Key { get; } = key;

            public Lazy<IRaster> Raster { get; } = raster;

            public int Users { get; set; }

            public LinkedListNode<Entry>? IdleNode { get; set; }
        }

        public readonly struct Lease : IDisposable {
            readonly RasterCache<TKey> cache;
            readonly Entry entry;

            internal Lease(RasterCache<TKey> cache, Entry entry) {
                this.cache = cache;
                this.entry = entry;
            }

            /// <exception cref="IOException">The raster cannot be opened.</exception>
            public IRaster Raster => entry.Raster.Value;

            public void Dispose() {
                cache?.Release(entry);
            }
        }

        public int OpenCount {
            get {
                lock (sync) {
                    return entries.Count;
                }
            }
        }

        public Lease Acquire(TKey key) {
            lock (sync) {
                if (!entries.TryGetValue(key, out var entry)) {
                    entry = new Entry(key, new Lazy<IRaster>(() => open(key), LazyThreadSafetyMode.ExecutionAndPublication));
                    entries.Add(key, entry);
                }
                else if (entry.IdleNode != null) {
                    idle.Remove(entry.IdleNode);
                    entry.IdleNode = null;
                }
                entry.Users++;
                return new Lease(this, entry);
            }
        }

        void Release(Entry entry) {
            lock (sync) {
                if (--entry.Users > 0) {
                    return;
                }
                // A raster that failed to open is opened again next time.
                if (entry.Raster.IsValueCreated && entries.TryGetValue(entry.Key, out var current) && current == entry) {
                    entry.IdleNode = idle.AddLast(entry);
                }
                else {
                    entries.Remove(entry.Key);
                }
                while (entries.Count > capacity && idle.First is { } oldest) {
                    idle.RemoveFirst();
                    oldest.Value.IdleNode = null;
                    entries.Remove(oldest.Value.Key);
                    oldest.Value.Raster.Value.Dispose();
                }
            }
        }

        public void Dispose() {
            lock (sync) {
                foreach (var entry in entries.Values) {
                    if (entry.Raster.IsValueCreated) {
                        entry.Raster.Value.Dispose();
                    }
                }
                entries.Clear();
                idle.Clear();
            }
        }
    }
}
