namespace YukiOsmRenderer.Core.Rendering {
    /// <summary>
    /// Keeps a bounded number of renderers for different style variants. Renderers are handed out as leases so that an
    /// evicted renderer is only disposed after its last user is done with it.
    /// </summary>
    public sealed class RendererCache<TRenderer>(int capacity) : IDisposable where TRenderer : class, IDisposable {
        readonly Dictionary<string, Entry> entries = [];
        readonly LinkedList<string> recency = [];
        readonly Lock sync = new();
        bool disposed;

        public int Count {
            get {
                lock (sync) {
                    return entries.Count;
                }
            }
        }

        /// <summary>Returns a lease on the renderer for the key, creating it with <paramref name="factory"/> if needed. Dispose the lease after use.</summary>
        public Lease Acquire(string key, Func<TRenderer> factory) {
            List<Entry>? evicted = null;
            Lease lease;
            lock (sync) {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (entries.TryGetValue(key, out var entry)) {
                    recency.Remove(entry.Node);
                    recency.AddFirst(entry.Node);
                }
                else {
                    // Created under the lock so concurrent requests for the same variant share one renderer.
                    entry = new Entry(factory(), new LinkedListNode<string>(key));
                    entries.Add(key, entry);
                    recency.AddFirst(entry.Node);
                    while (entries.Count > capacity && recency.Last is { } oldest) {
                        var victim = entries[oldest.Value];
                        entries.Remove(oldest.Value);
                        recency.RemoveLast();
                        victim.Evicted = true;
                        if (victim.Leases == 0) {
                            (evicted ??= []).Add(victim);
                        }
                    }
                }
                entry.Leases++;
                lease = new Lease(this, entry);
            }
            if (evicted != null) {
                foreach (var victim in evicted) {
                    victim.Renderer.Dispose();
                }
            }
            return lease;
        }

        public void Dispose() {
            List<Entry> idle;
            lock (sync) {
                if (disposed) {
                    return;
                }
                disposed = true;
                foreach (var entry in entries.Values) {
                    entry.Evicted = true;
                }
                idle = entries.Values.Where(e => e.Leases == 0).ToList();
                entries.Clear();
                recency.Clear();
            }
            foreach (var entry in idle) {
                entry.Renderer.Dispose();
            }
        }

        void Release(Entry entry) {
            bool dispose;
            lock (sync) {
                entry.Leases--;
                dispose = entry.Evicted && entry.Leases == 0;
            }
            if (dispose) {
                entry.Renderer.Dispose();
            }
        }

        internal sealed class Entry(TRenderer renderer, LinkedListNode<string> node) {
            public TRenderer Renderer { get; } = renderer;

            public LinkedListNode<string> Node { get; } = node;

            public int Leases { get; set; }

            public bool Evicted { get; set; }
        }

        public sealed class Lease : IDisposable {
            readonly RendererCache<TRenderer> owner;
            readonly Entry entry;
            int released;

            internal Lease(RendererCache<TRenderer> owner, Entry entry) {
                this.owner = owner;
                this.entry = entry;
            }

            public TRenderer Renderer => entry.Renderer;

            public void Dispose() {
                if (Interlocked.Exchange(ref released, 1) == 0) {
                    owner.Release(entry);
                }
            }
        }
    }
}
