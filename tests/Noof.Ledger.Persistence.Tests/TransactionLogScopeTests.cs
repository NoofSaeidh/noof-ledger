using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Tests;

public class TransactionLogScopeTests
{
    [Fact]
    public void Begin_opens_a_scope_carrying_only_the_transaction_id()
    {
        var logger = new CapturingLogger();
        var transactionId = Guid.NewGuid();

        TransactionLogScope.Begin(logger, transactionId);

        var state = logger.LastScopeState.Should().BeAssignableTo<IReadOnlyDictionary<string, object>>().Subject;
        state.Should().HaveCount(1);
        state[TransactionStages.TransactionIdProperty].Should().Be(transactionId);
    }

    // A hand-written fake rather than NSubstitute: this project has no NSubstitute reference, and
    // ILogger.BeginScope<TState> is the only member this test needs, so a substitute buys nothing.
    sealed class CapturingLogger : ILogger
    {
        public object? LastScopeState { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            LastScopeState = state;
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }

        sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }
}
