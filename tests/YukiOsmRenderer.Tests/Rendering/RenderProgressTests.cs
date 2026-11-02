using Microsoft.Extensions.Logging;
using YukiOsmRenderer.Core;
using YukiOsmRenderer.Core.Rendering;

namespace YukiOsmRenderer.Tests.Rendering {
    public class RenderProgressTests {
        sealed class ListLogger : ILogger {
            public List<string> Messages { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                lock (Messages) {
                    Messages.Add(formatter(state, exception));
                }
            }
        }

        [Test]
        public void FormatsSizesAndDurations() {
            Assert.Multiple(() => {
                Assert.That(Formatting.Size(512), Is.EqualTo("512 B"));
                Assert.That(Formatting.Size(1536), Is.EqualTo("1.5 KB"));
                Assert.That(Formatting.Size(5L * 1024 * 1024 * 1024), Is.EqualTo("5 GB"));
                Assert.That(Formatting.Duration(TimeSpan.Zero), Is.EqualTo("0:00"));
                Assert.That(Formatting.Duration(TimeSpan.FromSeconds(83)), Is.EqualTo("1:23"));
                Assert.That(Formatting.Duration(TimeSpan.FromSeconds(59.6)), Is.EqualTo("1:00"));
                Assert.That(Formatting.Duration(TimeSpan.FromSeconds(3725)), Is.EqualTo("1:02:05"));
                Assert.That(Formatting.Duration(TimeSpan.FromHours(50)), Is.EqualTo("50:00:00"));
            });
        }

        [Test]
        public void ExtrapolatesFromTheWorkDone() {
            var progress = new RenderProgress(10, 40, 50, 300, 1000, TimeSpan.FromSeconds(20));
            Assert.Multiple(() => {
                Assert.That(progress.Fraction, Is.EqualTo(0.25));
                Assert.That(progress.EstimatedTotalBytes, Is.EqualTo(6000));
                Assert.That(progress.EstimatedTotalTime, Is.EqualTo(TimeSpan.FromSeconds(80)));
                Assert.That(progress.ToString(), Is.EqualTo("25.0 % | tiles 50/300 | size 1000 B/~5.9 KB | time 0:20/~1:20, 1:00 left | 2.5 tiles/s"));
            });
        }

        [Test]
        public void LeavesOutUnknownAndFinishedEstimates() {
            var started = new RenderProgress(1, 4, 0, 100, 0, TimeSpan.FromSeconds(5));
            var finished = new RenderProgress(4, 4, 1234, 1234, 2048, TimeSpan.FromSeconds(10));
            Assert.Multiple(() => {
                Assert.That(started.EstimatedTotalBytes, Is.Null);
                Assert.That(started.ToString(), Is.EqualTo("25.0 % | tiles 0/100 | size 0 B | time 0:05/~0:20, 0:15 left | 0.0 tiles/s"));
                Assert.That(finished.ToString(), Is.EqualTo("100.0 % | tiles 1,234/1,234 | size 2 KB | time 0:10 | 123.4 tiles/s"));
            });
        }

        [Test]
        public void HugeEstimatesDoNotOverflow() {
            var progress = new RenderProgress(1, 1L << 40, 0, 1, 0, TimeSpan.FromDays(1));
            Assert.Multiple(() => {
                Assert.That(progress.EstimatedTotalTime, Is.EqualTo(TimeSpan.MaxValue));
                Assert.That(progress.ToString(), Does.Contain(" left"));
            });
        }

        [Test]
        public void TrackerLogsTheFinalProgressOnce() {
            var logger = new ListLogger();
            var tracker = new ProgressTracker(3, 5, logger);
            Parallel.For(0, 3, metatile => {
                for (var tile = 0; tile < (metatile == 0 ? 3 : 1); tile++) {
                    tracker.AddTile(100);
                }
                tracker.CompleteMetatile();
            });
            Assert.Multiple(() => {
                Assert.That(tracker.Current.Tiles, Is.EqualTo(5));
                Assert.That(tracker.Current.Bytes, Is.EqualTo(500));
                Assert.That(logger.Messages, Has.Count.EqualTo(1));
                Assert.That(logger.Messages[0], Does.StartWith("Progress: 100.0 % | tiles 5/5 | size 500 B | time "));
            });
        }
    }
}
