using YukiOsmRenderer.Core.Engine;
using YukiOsmRenderer.Core.Vector;

namespace YukiOsmRenderer.Tests.Vector {
    public class MapPoolTests {
        sealed class FakeMap : IDisposable {
            public bool Disposed { get; private set; }

            public void Dispose() {
                Disposed = true;
            }
        }

        [Test]
        public void ReusesIdleMapsOfTheSameOwnerAndRatio() {
            using var pool = new MapPool<FakeMap>(4);
            var owner = new object();
            var map = pool.Rent(owner, 1, () => new FakeMap());
            pool.Return(owner, 1, map);

            Assert.That(pool.Rent(owner, 1, () => new FakeMap()), Is.SameAs(map));
            pool.Return(owner, 1, map);
            Assert.That(pool.Rent(owner, 2, () => new FakeMap()), Is.Not.SameAs(map));
            Assert.That(pool.Rent(new object(), 1, () => new FakeMap()), Is.Not.SameAs(map));
            Assert.That(pool.Count, Is.EqualTo(3));
        }

        [Test]
        public void EvictsTheLeastRecentlyUsedIdleMapAtCapacity() {
            using var pool = new MapPool<FakeMap>(2);
            object a = new(), b = new(), c = new();
            var mapA = pool.Rent(a, 1, () => new FakeMap());
            var mapB = pool.Rent(b, 1, () => new FakeMap());
            pool.Return(a, 1, mapA);
            pool.Return(b, 1, mapB);

            var mapC = pool.Rent(c, 1, () => new FakeMap());

            Assert.That(mapA.Disposed, Is.True);
            Assert.That(mapB.Disposed, Is.False);
            Assert.That(mapC.Disposed, Is.False);
            Assert.That(pool.Count, Is.EqualTo(2));
        }

        [Test]
        public void DestroysMapsThatAreNotKeptOrWhoseOwnerIsReleased() {
            var pool = new MapPool<FakeMap>(4);
            object a = new(), b = new();
            var failed = pool.Rent(a, 1, () => new FakeMap());
            pool.Return(a, 1, failed, keep: false);
            var mapA = pool.Rent(a, 1, () => new FakeMap());
            var mapB = pool.Rent(b, 1, () => new FakeMap());
            pool.Return(a, 1, mapA);
            pool.Return(b, 1, mapB);

            pool.Release(a);

            Assert.That(failed.Disposed, Is.True);
            Assert.That(mapA.Disposed, Is.True);
            Assert.That(mapB.Disposed, Is.False);
            Assert.That(pool.Count, Is.EqualTo(1));

            pool.Dispose();
            Assert.That(mapB.Disposed, Is.True);
            Assert.That(pool.Count, Is.Zero);
        }

        [Test]
        public void FailedCreationDoesNotCount() {
            using var pool = new MapPool<FakeMap>(1);
            Assert.Throws<InvalidOperationException>(() => pool.Rent(new object(), 1, () => throw new InvalidOperationException()));
            Assert.That(pool.Count, Is.Zero);
        }

        [TestCase(1, 1)]
        [TestCase(2, 1)]
        [TestCase(4, 2)]
        [TestCase(12, 4)]
        [TestCase(64, 8)]
        public void RendersConcurrentlyOnAboutAThirdOfTheThreads(int threads, int renders) {
            Assert.That(VectorEnvironment.RenderThreads(threads), Is.EqualTo(renders));
        }
    }
}
