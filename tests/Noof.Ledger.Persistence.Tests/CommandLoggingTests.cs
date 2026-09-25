using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class CommandLoggingTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Executed_DbCommand_is_logged_at_Debug_not_Information()
    {
        // Not fixture.CreateDatabaseAsync(): its NpgsqlConnection.ConnectionString has already lost
        // the password by the time an open connection is handed back (confirmed empirically), and
        // AddNoofPersistence opens its own connection from this string rather than reusing one.
        var connectionString = await fixture.CreateDatabaseConnectionStringAsync();

        var capturing = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(capturing);
        });
        services.AddSingleton(TimeProvider.System);
        services.AddDataProtection();
        services.AddNoofPersistence(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Ledger"] = connectionString,
                })
                .Build(),
            maxJobAttempts: 8);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();

        await db.AppLogs.AsNoTracking().CountAsync(TestContext.Current.CancellationToken);

        capturing.Entries.Should().Contain(entry =>
            entry.EventId.Id == RelationalEventId.CommandExecuted.Id && entry.Level == LogLevel.Debug);
        capturing.Entries.Should().NotContain(entry =>
            entry.EventId.Id == RelationalEventId.CommandExecuted.Id && entry.Level == LogLevel.Information);
    }

    sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<(EventId EventId, LogLevel Level)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }

        sealed class CapturingLogger(ConcurrentBag<(EventId, LogLevel)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Add((eventId, logLevel));
        }
    }
}
