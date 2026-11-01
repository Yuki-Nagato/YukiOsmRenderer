using YukiOsmRenderer.Core.Rendering;

namespace YukiOsmRenderer.Tests.Rendering {
    public class RendererCacheTests {
        sealed class Disposable : IDisposable {
            public bool Disposed { get; private set; }

            public void Dispose() {
                Disposed = true;
            }
        }

        [Test]
        public void ReusesRenderersByKey() {
            using var cache = new RendererCache<Disposable>(4);
            var created = 0;
            using var a = cache.Acquire("a", () => { created++; return new Disposable(); });
            using var b = cache.Acquire("a", () => { created++; return new Disposable(); });
            Assert.Multiple(() => {
                Assert.That(created, Is.EqualTo(1));
                Assert.That(b.Renderer, Is.SameAs(a.Renderer));
            });
        }

        [Test]
        public void EvictsLeastRecentlyUsedRenderers() {
            using var cache = new RendererCache<Disposable>(2);
            Disposable first;
            using (var lease = cache.Acquire("a", () => new Disposable())) {
                first = lease.Renderer;
            }
            cache.Acquire("b", () => new Disposable()).Dispose();
            cache.Acquire("a", () => new Disposable()).Dispose();
            var third = cache.Acquire("c", () => new Disposable());
            third.Dispose();
            Assert.Multiple(() => {
                Assert.That(cache.Count, Is.EqualTo(2));
                Assert.That(first.Disposed, Is.False, "'a' was used more recently than 'b'.");
            });
            using var again = cache.Acquire("b", () => new Disposable());
            Assert.That(again.Renderer.Disposed, Is.False);
        }

        [Test]
        public void DisposesEvictedRenderersOnlyAfterTheLastLease() {
            using var cache = new RendererCache<Disposable>(1);
            var lease = cache.Acquire("a", () => new Disposable());
            var renderer = lease.Renderer;
            cache.Acquire("b", () => new Disposable()).Dispose();
            Assert.That(renderer.Disposed, Is.False);
            lease.Dispose();
            Assert.That(renderer.Disposed, Is.True);
            lease.Dispose();
        }

        [Test]
        public void DisposingTheCacheDisposesIdleRenderers() {
            var cache = new RendererCache<Disposable>(4);
            var idle = cache.Acquire("a", () => new Disposable());
            idle.Dispose();
            var busy = cache.Acquire("b", () => new Disposable());
            cache.Dispose();
            Assert.Multiple(() => {
                Assert.That(idle.Renderer.Disposed, Is.True);
                Assert.That(busy.Renderer.Disposed, Is.False);
            });
            busy.Dispose();
            Assert.Multiple(() => {
                Assert.That(busy.Renderer.Disposed, Is.True);
                Assert.That(() => cache.Acquire("c", () => new Disposable()), Throws.TypeOf<ObjectDisposedException>());
            });
        }
    }
}
