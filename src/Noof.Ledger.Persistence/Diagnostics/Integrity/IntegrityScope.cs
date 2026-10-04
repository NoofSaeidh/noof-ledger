namespace Noof.Ledger.Persistence.Diagnostics.Integrity;

internal sealed record IntegrityScope(Guid? TransactionId)
{
    public static IntegrityScope All { get; } = new((Guid?)null);

    public static IntegrityScope For(Guid transactionId) => new(transactionId);
}
