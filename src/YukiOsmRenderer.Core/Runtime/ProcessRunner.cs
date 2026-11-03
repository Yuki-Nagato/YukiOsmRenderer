using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace YukiOsmRenderer.Core.Runtime {
    /// <summary>Describes an external process to run.</summary>
    public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments) {
        public string? WorkingDirectory { get; init; }

        public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

        /// <summary>When true, standard output is returned instead of being logged.</summary>
        public bool CaptureOutput { get; init; }

        /// <summary>Log level for lines written by the process.</summary>
        public LogLevel OutputLogLevel { get; init; } = LogLevel.Debug;

        /// <summary>Short name used in log messages; defaults to the executable name.</summary>
        public string? DisplayName { get; init; }

        /// <summary>
        /// Whether to wait until the output pipes are closed after the process has exited. Must be false for
        /// launchers such as pg_ctl whose detached children inherit the pipes.
        /// </summary>
        public bool WaitForOutputClose { get; init; } = true;

        /// <summary>Sees every output line first; lines for which it returns true are neither logged nor kept for error messages.</summary>
        public Func<string, bool>? InterceptLine { get; init; }

        /// <summary>Called once the process has started.</summary>
        public Action<Process>? Started { get; init; }
    }

    public sealed record ProcessResult(int ExitCode, string StandardOutput);

    public sealed class ProcessFailedException(string message, int exitCode) : Exception(message) {
        public int ExitCode { get; } = exitCode;
    }

    public static class ProcessRunner {
        const int ErrorTailLines = 40;

        static readonly TimeSpan DetachedOutputGrace = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Runs a process to completion and throws <see cref="ProcessFailedException"/> on a non-zero exit code.
        /// Output is never written to this process' standard output, which may carry the rendered image.
        /// </summary>
        public static async Task<ProcessResult> RunAsync(ProcessSpec spec, ILogger logger, CancellationToken cancellationToken = default) {
            var name = spec.DisplayName ?? Path.GetFileNameWithoutExtension(spec.FileName);
            var startInfo = new ProcessStartInfo(spec.FileName) {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in spec.Arguments) {
                startInfo.ArgumentList.Add(argument);
            }
            if (spec.WorkingDirectory != null) {
                startInfo.WorkingDirectory = spec.WorkingDirectory;
            }
            foreach (var (key, value) in spec.Environment) {
                if (value == null) {
                    startInfo.Environment.Remove(key);
                }
                else {
                    startInfo.Environment[key] = value;
                }
            }

            logger.LogDebug("Running {Command} {Arguments}", spec.FileName, string.Join(' ', spec.Arguments.Select(QuoteForLog)));
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) {
                throw new InvalidOperationException($"Failed to start {spec.FileName}.");
            }
            process.StandardInput.Close();
            spec.Started?.Invoke(process);

            var output = new StringBuilder();
            var errorTail = new Queue<string>();
            var readers = Task.WhenAll(
                ReadLinesAsync(process.StandardOutput, line => {
                    if (spec.CaptureOutput) {
                        lock (output) {
                            output.AppendLine(line);
                        }
                    }
                    else if (spec.InterceptLine?.Invoke(line) != true) {
                        Log(line);
                    }
                }),
                ReadLinesAsync(process.StandardError, line => {
                    if (spec.InterceptLine?.Invoke(line) == true) {
                        return;
                    }
                    lock (errorTail) {
                        errorTail.Enqueue(line);
                        if (errorTail.Count > ErrorTailLines) {
                            errorTail.Dequeue();
                        }
                    }
                    Log(line);
                }));

            try {
                // Output is read manually, so this only waits for the process itself to exit.
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                try {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) {
                    // The process has already exited.
                }
                throw;
            }
            if (spec.WaitForOutputClose) {
                await readers.ConfigureAwait(false);
            }
            else {
                await Task.WhenAny(readers, Task.Delay(DetachedOutputGrace, CancellationToken.None)).ConfigureAwait(false);
            }

            if (process.ExitCode != 0) {
                string tail;
                lock (errorTail) {
                    tail = string.Join(Environment.NewLine, errorTail);
                }
                throw new ProcessFailedException($"{name} exited with code {process.ExitCode}.{(tail.Length > 0 ? Environment.NewLine + tail : "")}", process.ExitCode);
            }
            lock (output) {
                return new ProcessResult(process.ExitCode, output.ToString());
            }

            void Log(string line) {
                if (line.Length > 0) {
                    logger.Log(spec.OutputLogLevel, "[{Process}] {Line}", name, line);
                }
            }
        }

        static async Task ReadLinesAsync(StreamReader reader, Action<string> handle) {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) {
                handle(line);
            }
        }

        static string QuoteForLog(string argument) {
            return argument.Length == 0 || argument.Contains(' ') ? $"\"{argument}\"" : argument;
        }
    }
}
