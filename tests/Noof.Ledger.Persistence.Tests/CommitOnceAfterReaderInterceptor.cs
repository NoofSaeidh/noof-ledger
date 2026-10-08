using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Noof.Ledger.Persistence.Tests;

// What another connection commits between two reads of one integrity run: the action runs once, right after the first
// reader whose command text contains the marker has executed, before the run's next read.
sealed class CommitOnceAfterReaderInterceptor(string marker, Func<Task> commit) : DbCommandInterceptor
{
    int fired;

    public bool Fired => Volatile.Read(ref fired) == 1;

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains(marker, StringComparison.Ordinal) && Interlocked.Exchange(ref fired, 1) == 0)
            await commit();
        return result;
    }
}
