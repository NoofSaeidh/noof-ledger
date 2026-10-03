using Noof.Ledger.Application.Diagnostics.Integrity;

namespace Noof.Ledger.Persistence.Diagnostics.Integrity;

internal sealed class EfIntegrityChecks(LedgerDbContext db) : IIntegrityChecks
{
    public Task<IReadOnlyList<IntegrityFinding>> FindAllAsync(CancellationToken cancellationToken) =>
        FindAsync(IntegrityScope.All, cancellationToken);

    public Task<IReadOnlyList<IntegrityFinding>> FindForTransactionAsync(Guid transactionId, CancellationToken cancellationToken) =>
        FindAsync(IntegrityScope.For(transactionId), cancellationToken);

    // P-2, one check per record: a record whose stored facts contradict its kind also has postings that disagree with
    // them, so the cause is reported alone until it is fixed and one defect stays one finding. Hence I-2 before I-1.
    IIntegrityCheck[] RunOrder() => [new FactsMismatchKindCheck(db), new PostingsDisagreeCheck(db)];

    async Task<IReadOnlyList<IntegrityFinding>> FindAsync(IntegrityScope scope, CancellationToken cancellationToken)
    {
        HashSet<Guid> reported = [];
        List<IntegrityFinding> kept = [];
        foreach (var check in RunOrder())
        {
            var found = await check.FindAsync(scope, cancellationToken);
            kept.AddRange(found.Where(finding => finding.TransactionId is not { } id || !reported.Contains(id)));
            reported.UnionWith(found.Select(finding => finding.TransactionId).OfType<Guid>());
        }

        // A stable sort: each check's own order (by when the record was captured) survives.
        return [.. kept.OrderBy(finding => finding.Check)];
    }
}
