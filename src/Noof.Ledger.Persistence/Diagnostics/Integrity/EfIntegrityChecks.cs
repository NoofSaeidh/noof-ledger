using System.Data;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Persistence.Diagnostics.Integrity;

internal sealed class EfIntegrityChecks(LedgerDbContext db, IReceiptStore receiptStore, TimeProvider timeProvider)
    : IIntegrityChecks
{
    public Task<IReadOnlyList<IntegrityFinding>> FindAllAsync(CancellationToken cancellationToken) =>
        FindAsync(IntegrityScope.All, cancellationToken);

    public Task<IReadOnlyList<IntegrityFinding>> FindForTransactionAsync(Guid transactionId, CancellationToken cancellationToken) =>
        FindAsync(IntegrityScope.For(transactionId), cancellationToken);

    // P-2, one check per record: a record whose stored facts contradict its kind also has postings that disagree with
    // them, so the cause is reported alone until it is fixed and one defect stays one finding. Hence I-2 before I-1,
    // and both before the waiting checks.
    IIntegrityCheck[] RunOrder() =>
    [
        new FactsMismatchKindCheck(db),
        new PostingsDisagreeCheck(db),
        new StuckInPipelineCheck(db, receiptStore, timeProvider),
        new NotAppliedCheck(db, receiptStore, timeProvider),
    ];

    async Task<IReadOnlyList<IntegrityFinding>> FindAsync(IntegrityScope scope, CancellationToken cancellationToken)
    {
        // One snapshot for every check and every IsAwaitingConfirmationAsync read on this context, so a Record anyway
        // or a Cancel committed mid-run cannot make a candidate a false finding. Read-only: disposed, never committed.
        // A caller's own open transaction is joined instead.
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            : null;

        HashSet<Guid> reported = [];
        List<IntegrityFinding> kept = [];
        foreach (var check in RunOrder())
        {
            // SystemHealth's 5 s can only cancel: a run cancelled between checks sends no further query.
            cancellationToken.ThrowIfCancellationRequested();
            var found = await check.FindAsync(scope, cancellationToken);
            kept.AddRange(found.Where(finding => finding.TransactionId is not { } id || !reported.Contains(id)));
            reported.UnionWith(found.Select(finding => finding.TransactionId).OfType<Guid>());
        }

        // A stable sort: each check's own order (by when the record was captured) survives.
        return [.. kept.OrderBy(finding => finding.Check)];
    }
}
