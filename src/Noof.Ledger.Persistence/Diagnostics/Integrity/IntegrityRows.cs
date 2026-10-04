using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Noof.Ledger.Persistence.Diagnostics.Integrity;

internal static class IntegrityRows
{
    public static Task<List<TRow>> ReadAsync<TRow>(
        LedgerDbContext db, string sql, IntegrityScope scope, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<TRow>(sql, ScopeParameter(scope)).ToListAsync(cancellationToken);

    // Typed: PostgreSQL cannot infer the type of an untyped null in "@transactionId IS NULL".
    public static NpgsqlParameter ScopeParameter(IntegrityScope scope) =>
        new("transactionId", NpgsqlDbType.Uuid) { Value = (object?)scope.TransactionId ?? DBNull.Value };
}
