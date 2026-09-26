using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Host.Tests.Diagnostics;

sealed class RecordingLoggerFactory : ILoggerFactory
{
    public List<(string Category, EventId EventId, LogLevel Level, Exception? Exception)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    sealed class RecordingLogger(RecordingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Add((category, eventId, logLevel, exception));
    }
}
