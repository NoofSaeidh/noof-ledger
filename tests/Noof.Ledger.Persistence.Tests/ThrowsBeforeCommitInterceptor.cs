using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Noof.Ledger.Persistence.Tests;

sealed class ThrowsBeforeCommitInterceptor : DbTransactionInterceptor
{
    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Simulated failure between the writes and the commit.");
}
