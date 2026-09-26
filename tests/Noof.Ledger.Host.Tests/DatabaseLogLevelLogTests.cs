using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Logging;

namespace Noof.Ledger.Host.Tests;

public class DatabaseLogLevelLogTests
{
    [Fact]
    public void DatabaseLogLevelIs_renders_Off_for_a_null_level_instead_of_null()
    {
        var logger = new CapturingLogger();

        logger.DatabaseLogLevelIs(null);

        logger.LastMessage.Should().Be("Database log level is Off");
    }

    [Fact]
    public void DatabaseLogLevelChanged_renders_Off_for_a_null_level_instead_of_null()
    {
        var logger = new CapturingLogger();

        logger.DatabaseLogLevelChanged(LogSeverity.Information, null);

        logger.LastMessage.Should().Be("Database log level changed from Information to Off");
    }

    [Fact]
    public void DatabaseLogLevelChanged_still_renders_a_real_level_by_name()
    {
        var logger = new CapturingLogger();

        logger.DatabaseLogLevelChanged(null, LogSeverity.Debug);

        logger.LastMessage.Should().Be("Database log level changed from Off to Debug");
    }

    sealed class CapturingLogger : ILogger
    {
        public string? LastMessage { get; private set; }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            LastMessage = formatter(state, exception);
    }
}
