using Microsoft.Extensions.Logging;

namespace YukiOsmRenderer.Tests {
    /// <summary>Logger that keeps the formatted messages of level Information and above.</summary>
    sealed class ListLogger : ILogger {
        readonly List<string> messages = [];

        public IReadOnlyList<string> Messages {
            get {
                lock (messages) {
                    return [.. messages];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) {
            return logLevel >= LogLevel.Information;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
            if (IsEnabled(logLevel)) {
                lock (messages) {
                    messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
