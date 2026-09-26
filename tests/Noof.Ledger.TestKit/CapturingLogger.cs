using Microsoft.Extensions.Logging;

namespace Noof.Ledger.TestKit;

public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<CapturedLogEntry> Entries { get; } = [];

    readonly List<IReadOnlyDictionary<string, object>> scopeStack = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        var values = ToDictionary(state);
        scopeStack.Add(values);
        return new Scope(this, values);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = ToDictionary(state);
        var scope = scopeStack.Count > 0 ? scopeStack[^1] : null;
        Entries.Add(new CapturedLogEntry(logLevel, eventId, properties, exception, scope));
    }

    static IReadOnlyDictionary<string, object> ToDictionary<TState>(TState state) =>
        state is IEnumerable<KeyValuePair<string, object>> pairs
            ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
            : new Dictionary<string, object>();

    sealed class Scope(CapturingLogger<T> owner, IReadOnlyDictionary<string, object> values) : IDisposable
    {
        public void Dispose() => owner.scopeStack.Remove(values);
    }
}

public sealed record CapturedLogEntry(
    LogLevel Level, EventId EventId, IReadOnlyDictionary<string, object> Properties,
    Exception? Exception, IReadOnlyDictionary<string, object>? Scope)
{
    public string? Stage => Properties.TryGetValue("Stage", out var value) ? value as string : null;
}
