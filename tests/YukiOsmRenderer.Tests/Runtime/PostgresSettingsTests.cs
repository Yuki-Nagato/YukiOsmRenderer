using YukiOsmRenderer.Core.Database;

namespace YukiOsmRenderer.Tests.Runtime {
    public class PostgresSettingsTests {
        [Test]
        public void ScalesWithMemory() {
            var small = PostgresSettings.ForMemory(4096, 4, 4, windows: false);
            var large = PostgresSettings.ForMemory(256 * 1024, 32, 64, windows: false);
            Assert.Multiple(() => {
                Assert.That(small.SharedBuffersMegabytes, Is.EqualTo(512));
                Assert.That(small.MaxConnections, Is.EqualTo(100));
                Assert.That(large.SharedBuffersMegabytes, Is.EqualTo(8192));
                Assert.That(large.MaintenanceWorkMemMegabytes, Is.EqualTo(8192));
                Assert.That(large.MaxConnections, Is.EqualTo(148));
                Assert.That(large.MaxParallelMaintenanceWorkers, Is.EqualTo(8));
            });
        }

        [Test]
        public void StaysWithinTheWindowsLimits() {
            var settings = PostgresSettings.ForMemory(48 * 1024, 12, 12, windows: true);
            Assert.Multiple(() => {
                Assert.That(settings.MaintenanceWorkMemMegabytes, Is.EqualTo(2047));
                Assert.That(settings.WorkMemMegabytes, Is.LessThan(2048));
            });
        }

        [Test]
        public void ConfigurationListensOnLocalhostOnly() {
            var configuration = PostgresSettings.ForMemory(8192, 8, 8, windows: false).ToConfigurationFile(54321);
            Assert.Multiple(() => {
                Assert.That(configuration, Does.Contain("port = 54321\n"));
                Assert.That(configuration, Does.Contain("listen_addresses = '127.0.0.1'\n"));
                Assert.That(configuration, Does.Contain("unix_socket_directories = ''\n"));
                Assert.That(configuration, Does.Contain("jit = off\n"));
                Assert.That(configuration, Does.Contain("shared_buffers = '1024MB'\n"));
                Assert.That(configuration, Does.Not.Contain("\r"));
            });
        }
    }
}
