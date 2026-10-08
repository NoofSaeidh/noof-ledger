using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Noof.Ledger.Persistence.Tests;

sealed class CountingReaderInterceptor : DbCommandInterceptor
{
    int readers;

    public int Readers => Volatile.Read(ref readers);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref readers);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
