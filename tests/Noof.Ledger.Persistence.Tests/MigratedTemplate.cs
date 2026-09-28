using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Persistence.Tests;
using Noof.Ledger.TestKit;

[assembly: AssemblyFixture(typeof(MigratedTemplate))]

namespace Noof.Ledger.Persistence.Tests;

// The database every Persistence clone is copied from, migrated from empty once per test run.
// It replaced cloning the shared noof_ledger_test_template, which is only ever migrated forward:
// EF records a migration as applied by id, so an edit to an applied migration's SQL never reached
// it, and a comparison of migration ids could not notice. A template built by this run's own
// migrations cannot be behind them. An assembly fixture, not part of PostgresFixture, because
// "postgres" and "postgres-serial" each get their own PostgresFixture and would build two.
// Its name keeps the noof_test_ prefix so ops/clean-test-databases.ps1 drops one a killed run left.
public sealed class MigratedTemplate : IAsyncDisposable
{
    readonly string name = $"noof_test_tpl_{Guid.NewGuid():N}";
    readonly Lazy<Task> migrated;

    public MigratedTemplate() => migrated = new(CreateAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    public async Task<string> NameAsync()
    {
        await migrated.Value;
        return name;
    }

    // Pooling must be off: CREATE DATABASE ... TEMPLATE fails with 55006 "source database is being
    // accessed by other users" against ANY live backend on the template, including a connection this
    // process only returned to Npgsql's pool rather than truly closed.
    async Task CreateAsync()
    {
        await DatabaseSettings.CreateEmptyDatabaseAsync(name, TestContext.Current.CancellationToken);

        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(PostgresFixture.WithoutPooling(DatabaseSettings.For(name)))
            .Options;
        await using var db = new LedgerDbContext(options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);    }

    public async ValueTask DisposeAsync()
    {
        if (migrated.IsValueCreated)
            await DatabaseSettings.DropDatabaseAsync(name, CancellationToken.None);
    }
}
