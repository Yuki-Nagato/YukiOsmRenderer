namespace YukiOsmRenderer.Core.Vector {
    /// <summary>
    /// Idle MapLibre maps of all style variants, limited in number because every map holds its own tiles, glyphs and
    /// Vulkan device. When the limit is reached, the least recently used idle map is destroyed to make room.
    /// </summary>
    /// <param name="capacity">Maximum number of maps, busy or idle; at least the number of concurrent renders.</param>
    public sealed class MapPool<TMap>(int capacity) : IDisposable where TMap : class, IDisposable {
        readonly Lock sync = new();
        // Least recently used first.
        readonly LinkedList<(object Owner, float PixelRatio, TMap Map)> idle = new();
        int count;
        bool disposed;

        public int Capacity { get; } = Math.Max(1, capacity);

        /// <summary>Maps alive, busy or idle.</summary>
        public int Count {
            get {
                lock (sync) {
                    return count;
                }
            }
        }

        /// <summary>An idle map of <paramref name="owner"/> with the pixel ratio, or a new one made with <paramref name="create"/>.</summary>
        public TMap Rent(object owner, float pixelRatio, Func<TMap> create) {
            TMap? evicted = null;
            lock (sync) {
                ObjectDisposedException.ThrowIf(disposed, this);
                for (var node = idle.Last; node != null; node = node.Previous) {
                    if (node.Value.Owner == owner && node.Value.PixelRatio == pixelRatio) {
                        idle.Remove(node);
                        return node.Value.Map;
                    }
                }
                if (count >= Capacity && idle.First is { } oldest) {
                    idle.RemoveFirst();
                    evicted = oldest.Value.Map;
                    count--;
                }
                count++;
            }
            evicted?.Dispose();
            try {
                return create();
            }
            catch {
                lock (sync) {
                    count--;
                }
                throw;
            }
        }

        /// <summary>Makes a rented map available again, or destroys it if its owner or the pool is gone.</summary>
        public void Return(object owner, float pixelRatio, TMap map, bool keep = true) {
            lock (sync) {
                if (keep && !disposed) {
                    idle.AddLast((owner, pixelRatio, map));
                    return;
                }
                count--;
            }
            map.Dispose();
        }

        /// <summary>Destroys the idle maps of <paramref name="owner"/>.</summary>
        public void Release(object owner) {
            var maps = new List<TMap>();
            lock (sync) {
                for (var node = idle.First; node != null;) {
                    var next = node.Next;
                    if (node.Value.Owner == owner) {
                        maps.Add(node.Value.Map);
                        idle.Remove(node);
                        count--;
                    }
                    node = next;
                }
            }
            maps.ForEach(map => map.Dispose());
        }

        public void Dispose() {
            List<TMap> maps;
            lock (sync) {
                disposed = true;
                maps = idle.Select(entry => entry.Map).ToList();
                count -= maps.Count;
                idle.Clear();
            }
            maps.ForEach(map => map.Dispose());
        }
    }
}
