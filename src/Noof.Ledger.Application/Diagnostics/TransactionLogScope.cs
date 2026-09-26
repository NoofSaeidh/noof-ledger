using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Application.Diagnostics;

public static class TransactionLogScope
{
    public static IDisposable? Begin(ILogger logger, Guid transactionId) =>
        logger.BeginScope(new Dictionary<string, object> { [TransactionStages.TransactionIdProperty] = transactionId });
}
