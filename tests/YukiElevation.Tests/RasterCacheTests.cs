namespace YukiElevation.Tests {
    public class RasterCacheTests {
        static ArrayRaster NewRaster() => ArrayRaster.FromCenters(0, 0, 1, 1, 1, (_, _) => 0);

        [Test]
        public void ClosesTheLeastRecentlyUsedIdleRasters() {
            var rasters = new Dictionary<int, ArrayRaster>();
            using var cache = new RasterCache<int>(2, key => rasters[key] = NewRaster());

            foreach (var key in new[] { 1, 2, 1, 3 }) {
                using var lease = cache.Acquire(key);
                _ = lease.Raster;
            }

            Assert.That(cache.OpenCount, Is.EqualTo(2));
            Assert.That(rasters.Where(r => r.Value.Disposed).Select(r => r.Key), Is.EqualTo(new[] { 2 }));
        }

        [Test]
        public void KeepsRastersInUseOpen() {
            var rasters = new Dictionary<int, ArrayRaster>();
            using var cache = new RasterCache<int>(1, key => rasters[key] = NewRaster());

            using (var held = cache.Acquire(1)) {
                _ = held.Raster;
                foreach (var key in new[] { 2, 3 }) {
                    using var lease = cache.Acquire(key);
                    _ = lease.Raster;
                }
                Assert.That(rasters[1].Disposed, Is.False);
            }

            Assert.That(rasters.Where(r => r.Value.Disposed).Select(r => r.Key), Is.EquivalentTo(new[] { 2, 3 }));
            Assert.That(cache.OpenCount, Is.EqualTo(1));
        }

        [Test]
        public void OpensAgainAfterAFailure() {
            var attempts = 0;
            using var cache = new RasterCache<int>(4, _ => ++attempts == 1 ? throw new IOException("busy") : NewRaster());

            using (var lease = cache.Acquire(1)) {
                Assert.Throws<IOException>(() => _ = lease.Raster);
            }
            using (var lease = cache.Acquire(1)) {
                Assert.That(lease.Raster, Is.Not.Null);
            }

            Assert.That(attempts, Is.EqualTo(2));
        }

        [Test]
        public void DisposesAllRasters() {
            var rasters = new List<ArrayRaster>();
            var cache = new RasterCache<int>(4, _ => {
                var raster = NewRaster();
                rasters.Add(raster);
                return raster;
            });
            foreach (var key in new[] { 1, 2 }) {
                using var lease = cache.Acquire(key);
                _ = lease.Raster;
            }

            cache.Dispose();

            Assert.That(rasters.All(r => r.Disposed));
        }
    }
}
