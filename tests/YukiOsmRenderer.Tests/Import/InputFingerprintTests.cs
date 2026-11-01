using YukiOsmRenderer.Core.Import;

namespace YukiOsmRenderer.Tests.Import {
    public class InputFingerprintTests {
        string directory = null!;

        [SetUp]
        public void SetUp() {
            directory = Path.Combine(Path.GetTempPath(), "yukiosm-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown() {
            Directory.Delete(directory, true);
        }

        string CreateFile(string name, int size, byte seed) {
            var path = Path.Combine(directory, name);
            var data = new byte[size];
            for (var i = 0; i < size; i++) {
                data[i] = (byte)(i * 31 + seed);
            }
            File.WriteAllBytes(path, data);
            return path;
        }

        [Test]
        public void IsIndependentOfOrderAndLocation() {
            var a = CreateFile("a.osm.pbf", 1000, 1);
            var b = CreateFile("b.osm.pbf", 2000, 2);
            var (first, files) = InputFingerprint.Compute([a, b]);
            var (second, _) = InputFingerprint.Compute([b, a]);
            var moved = Path.Combine(directory, "moved.osm.pbf");
            File.Copy(a, moved);
            var (third, _) = InputFingerprint.Compute([moved, b]);
            Assert.Multiple(() => {
                Assert.That(second, Is.EqualTo(first));
                Assert.That(third, Is.EqualTo(first));
                Assert.That(files.Select(f => f.Name), Is.EqualTo(new[] { "a.osm.pbf", "b.osm.pbf" }));
                Assert.That(files.Select(f => f.Size), Is.EqualTo(new long[] { 1000, 2000 }));
            });
        }

        [Test]
        public void DetectsChangesAtTheStartAndEndOfLargeFiles() {
            var size = InputFingerprint.SampleSize * 3;
            var path = CreateFile("large.osm.pbf", size, 7);
            var (original, _) = InputFingerprint.Compute([path]);

            var data = File.ReadAllBytes(path);
            data[^1] ^= 0xff;
            File.WriteAllBytes(path, data);
            var (changedEnd, _) = InputFingerprint.Compute([path]);

            data[^1] ^= 0xff;
            data[0] ^= 0xff;
            File.WriteAllBytes(path, data);
            var (changedStart, _) = InputFingerprint.Compute([path]);

            Assert.Multiple(() => {
                Assert.That(changedEnd, Is.Not.EqualTo(original));
                Assert.That(changedStart, Is.Not.EqualTo(original));
            });
        }

        [Test]
        public void DetectsAddedFiles() {
            var a = CreateFile("a.osm.pbf", 100, 1);
            var b = CreateFile("b.osm.pbf", 100, 2);
            Assert.That(InputFingerprint.Compute([a]).Fingerprint, Is.Not.EqualTo(InputFingerprint.Compute([a, b]).Fingerprint));
        }

        [Test]
        public void RejectsDuplicatesAndEmptyInput() {
            var a = CreateFile("a.osm.pbf", 100, 1);
            Assert.Multiple(() => {
                Assert.That(() => InputFingerprint.Compute([a, a]), Throws.ArgumentException);
                Assert.That(() => InputFingerprint.Compute([]), Throws.ArgumentException);
            });
        }
    }
}
