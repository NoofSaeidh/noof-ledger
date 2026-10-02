using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

static class LockWaits
{
    // Polls from a connection of its own: a backend's view of pg_stat_activity stays fixed for the rest of its
    // transaction, so a context holding the lock would never see the other one start waiting.
    public static async Task UntilABackendWaitsOnALockAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(timeout.Token);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'",
            connection);

        while (await command.ExecuteScalarAsync(timeout.Token) is 0L)
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
    }
}
