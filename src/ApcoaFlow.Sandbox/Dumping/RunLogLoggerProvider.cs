using Microsoft.Extensions.Logging;

namespace ApcoaFlow.Sandbox.Dumping;

/// <summary>Writes the client's own log lines (token refresh, re-login, retries) into the run's <c>run.log</c>.</summary>
public sealed class RunLogLoggerProvider(DumpRun run) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RunLogLogger(run, categoryName);

    public void Dispose() { }

    private sealed class RunLogLogger(DumpRun run, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var shortCategory = category[(category.LastIndexOf('.') + 1)..];
            run.Log($"[{logLevel}] {shortCategory}: {formatter(state, exception)}" + (exception is null ? "" : $" — {exception.Message}"));
        }
    }
}
