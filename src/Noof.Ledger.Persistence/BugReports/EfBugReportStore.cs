using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Receipts;
using Npgsql;

namespace Noof.Ledger.Persistence.BugReports;

// The bug-report queue. Every change after the insert is one guarded UPDATE, so "only while Pending", "snapshot once",
// "deliver once" and "status changes once" hold without a lease (spec P-11).
internal sealed class EfBugReportStore(
    LedgerDbContext db, IIntegrityChecks checks, IFiscalVerificationUrl verificationUrl, TimeProvider timeProvider)
    : IBugReportStore
{
    BugReportEvidence Evidence => new(db, checks, verificationUrl);

    // Looked up first so a redelivered report burns no number; the unique index catches the concurrent duplicate that
    // slips past the lookup (spec P-9, R-2). A report with no ReplyTo is never a redelivery.
    public async Task<BugReportSaved> FileAsync(NewBugReport report, CancellationToken cancellationToken)
    {
        var explained = Explained(report);
        if (report.ReplyTo is { } address && await NumberFiledAsync(report.Source, address, cancellationToken) is { } existing)
            return new BugReportSaved(existing, Created: false);

        var now = timeProvider.GetUtcNow();
        var row = new BugReport
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            Source = report.Source,
            Text = verificationUrl.StripUrl(report.Text) is { } kept && !string.IsNullOrWhiteSpace(kept) ? kept : null,
            TransactionId = report.TransactionId,
            ReplyTo = report.ReplyTo,
            Status = BugReportStatus.Open,
            ExplanationState = BugExplanationState.Pending,
            ExplanationAttempts = 0,
            ExplanationNextAt = now,
        };
        if (explained is (var finding, var explanation))
            await SnapshotExplainedAsync(row, finding, explanation, cancellationToken);
        db.BugReports.Add(row);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new BugReportSaved(row.Number, Created: true);
        }
        catch (DbUpdateException ex) when (report.ReplyTo is { } replyTo && ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: BugReportConfiguration.ReplyToIndex,
        })
        {
            db.Entry(row).State = EntityState.Detached;
            var winner = await NumberFiledAsync(report.Source, replyTo, cancellationToken)
                ?? throw new InvalidOperationException("A duplicate report named one that cannot be found.");
            return new BugReportSaved(winner, Created: false);
        }
        catch
        {
            // The scope's context is shared (a dashboard circuit keeps its own), so a row left Added would be inserted
            // by the next SaveChanges.
            db.Entry(row).State = EntityState.Detached;
            throw;
        }
    }

    static (IntegrityFinding Finding, Explanation Explanation)? Explained(NewBugReport report) => report switch
    {
        { Finding: null, Explanation: null } => null,
        { Finding: { } finding, Explanation: { } explanation } => (finding, explanation),
        _ => throw new ArgumentException(
            "A report is filed explained with both its finding and its explanation, or with neither.", nameof(report)),
    };

    // What the operator saw on the dashboard, already explained; the record and its log lines as they are now.
    async Task SnapshotExplainedAsync(
        BugReport row, IntegrityFinding finding, Explanation explanation, CancellationToken cancellationToken)
    {
        var evidence = Evidence;
        var logLines = await evidence.LogLinesAsync(row.TransactionId, row.CreatedAt, cancellationToken);
        var summary = await evidence.RecordSummaryAsync(row.TransactionId, cancellationToken);

        row.SnapshotAt = row.CreatedAt;
        row.RecordSummary = summary.Summary;
        row.FindingsJson = BugReportJson.WriteFindings([finding], verificationUrl);
        row.LogLinesJson = logLines.Json;
        row.CollectionFailures = BugReportEvidence.Failures(logLines.Failure, summary.Failure);
        row.ExplanationState = BugExplanationState.Done;
        row.Explanation = verificationUrl.StripUrl(explanation.Text) ?? "";
        row.LooksLikeBug = explanation.LooksLikeBug;
    }

    public async Task<BugReportSnapshot> TakeSnapshotAsync(Guid reportId, CancellationToken cancellationToken)
    {
        var report = await FindAsync(reportId, cancellationToken);
        if (report.SnapshotAt is null)
        {
            var evidence = Evidence;
            var findings = await evidence.FindingsAsync(report.TransactionId, cancellationToken);
            var logLines = await evidence.LogLinesAsync(report.TransactionId, report.CreatedAt, cancellationToken);
            var summary = await evidence.RecordSummaryAsync(report.TransactionId, cancellationToken);
            var findingsJson = findings.Findings is { } taken ? BugReportJson.WriteFindings(taken, verificationUrl) : null;
            var failures = BugReportEvidence.Failures(findings.Failure, logLines.Failure, summary.Failure);
            DateTimeOffset? takenAt = timeProvider.GetUtcNow();

            await db.BugReports
                .Where(r => r.Id == reportId && r.SnapshotAt == null)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(r => r.SnapshotAt, takenAt)
                    .SetProperty(r => r.RecordSummary, summary.Summary)
                    .SetProperty(r => r.FindingsJson, findingsJson)
                    .SetProperty(r => r.LogLinesJson, logLines.Json)
                    .SetProperty(r => r.CollectionFailures, failures),
                    cancellationToken);

            report = await FindAsync(reportId, cancellationToken);
        }

        return SnapshotOf(report)
            ?? throw new InvalidOperationException($"Bug report #{report.Number} has no snapshot after one was taken.");
    }

    public async Task<BugReportToExplain?> NextDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = now.ToUniversalTime();
        var report = await db.BugReports.AsNoTracking()
            .Where(r => r.Status == BugReportStatus.Open
                && r.ExplanationState == BugExplanationState.Pending
                && r.ExplanationNextAt <= due)
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Number)
            .FirstOrDefaultAsync(cancellationToken);

        return report is null
            ? null
            : new BugReportToExplain(
                report.Id, report.Number, report.ExplanationAttempts, report.Text, report.TransactionId, SnapshotOf(report));
    }

    public async Task CompleteExplanationAsync(Guid reportId, Explanation explanation, CancellationToken cancellationToken)
    {
        // Stripped like every bug_reports column (spec §2), yet never null: Done needs an explanation
        // (ck_bug_reports_explanation_matches_state), even one that was all link.
        var text = verificationUrl.StripUrl(explanation.Text) ?? "";

        await db.BugReports
            .Where(r => r.Id == reportId && r.ExplanationState == BugExplanationState.Pending)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.ExplanationState, BugExplanationState.Done)
                .SetProperty(r => r.Explanation, text)
                .SetProperty(r => r.LooksLikeBug, (bool?)explanation.LooksLikeBug),
                cancellationToken);
    }

    public async Task<BugExplanationState> RecordFailedAttemptAsync(
        Guid reportId, DateTimeOffset nextAttemptAt, int maxAttempts, CancellationToken cancellationToken)
    {
        var next = nextAttemptAt.ToUniversalTime();

        // Every SET reads the row as it was, so the state is decided on the attempt this update counts.
        await db.BugReports
            .Where(r => r.Id == reportId && r.ExplanationState == BugExplanationState.Pending)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.ExplanationAttempts, r => r.ExplanationAttempts + 1)
                .SetProperty(r => r.ExplanationNextAt, next)
                .SetProperty(r => r.ExplanationState, r => r.ExplanationAttempts + 1 >= maxAttempts
                    ? BugExplanationState.Failed
                    : BugExplanationState.Pending),
                cancellationToken);

        return await db.BugReports.AsNoTracking()
            .Where(r => r.Id == reportId)
            .Select(r => r.ExplanationState)
            .SingleAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BugReportDelivery>> PendingDeliveriesAsync(CancellationToken cancellationToken)
    {
        var reports = await db.BugReports.AsNoTracking()
            .Where(r => r.ReplyTo != null
                && r.Status == BugReportStatus.Open
                && (r.ExplanationState == BugExplanationState.Done || r.ExplanationState == BugExplanationState.Failed)
                && r.DeliveredAs == null)
            .OrderBy(r => r.Number)
            .ToListAsync(cancellationToken);

        return
        [
            .. reports.Select(r => new BugReportDelivery(
                r.Id,
                r.Number,
                r.Source,
                r.ReplyTo ?? throw new InvalidOperationException($"Bug report #{r.Number} has no reply address."),
                r.ExplanationState,
                r.Explanation,
                r.LooksLikeBug,
                Linked: r.TransactionId is not null,
                FindingsCount: BugReportJson.ReadFindings(r.FindingsJson)?.Count)),
        ];
    }

    public async Task MarkDeliveredAsync(Guid reportId, string deliveredAs, CancellationToken cancellationToken) =>
        await db.BugReports
            .Where(r => r.Id == reportId && r.DeliveredAs == null)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.DeliveredAs, deliveredAs), cancellationToken);

    public async Task<IReadOnlyList<BugReportListItem>> ListAsync(bool includeClosed, CancellationToken cancellationToken) =>
        await Reports(includeClosed)
            .OrderByDescending(r => r.Number)
            .Select(r => new BugReportListItem(r.Number, r.CreatedAt, r.Source, r.Status, r.Text, r.TransactionId, r.ExplanationState))
            .ToListAsync(cancellationToken);

    public async Task<BugReportDocument?> GetAsync(int number, CancellationToken cancellationToken)
    {
        var report = await db.BugReports.AsNoTracking().SingleOrDefaultAsync(r => r.Number == number, cancellationToken);
        return report is null ? null : await DocumentAsync(report, cancellationToken);
    }

    public async Task<IReadOnlyList<BugReportDocument>> LoadForExportAsync(bool includeClosed, CancellationToken cancellationToken)
    {
        var reports = await Reports(includeClosed).OrderBy(r => r.Number).ToListAsync(cancellationToken);
        List<BugReportDocument> documents = [];
        foreach (var report in reports)
            documents.Add(await DocumentAsync(report, cancellationToken));

        return documents;
    }

    public async Task<bool> SetStatusAsync(int number, BugReportStatus status, CancellationToken cancellationToken)
    {
        DateTimeOffset? closedAt = status == BugReportStatus.Closed ? timeProvider.GetUtcNow() : null;

        var changed = await db.BugReports
            .Where(r => r.Number == number && r.Status != status)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.ClosedAt, closedAt),
                cancellationToken);
        return changed == 1;
    }

    public Task<int> CountOpenAsync(CancellationToken cancellationToken) =>
        db.BugReports.CountAsync(r => r.Status == BugReportStatus.Open, cancellationToken);

    // Revisions and the findings now are read live; both lose their fiscal links here, so every free-text field of the
    // document is already stripped, whatever reads it.
    async Task<BugReportDocument> DocumentAsync(BugReport report, CancellationToken cancellationToken)
    {
        IReadOnlyList<RevisionView> revisions = [];
        IReadOnlyList<IntegrityFinding>? findingsNow = null;
        if (report.TransactionId is { } transactionId)
        {
            revisions = await RevisionsAsync(transactionId, cancellationToken);
            findingsNow = (await Evidence.FindingsAsync(transactionId, cancellationToken)).Findings is { } live
                ? BugReportJson.WithoutLinks(live, verificationUrl)
                : null;
        }

        return new BugReportDocument(
            report.Number, report.CreatedAt, report.Source, report.Status, report.ClosedAt, report.Text, report.TransactionId,
            report.SnapshotAt, report.RecordSummary, revisions, BugReportJson.ReadFindings(report.FindingsJson), findingsNow,
            report.CollectionFailures, report.ExplanationState, report.Explanation, report.LooksLikeBug,
            BugReportJson.ReadLogLines(report.LogLinesJson));
    }

    // Spec P-23: not through ITransactionTrace, which builds the record's current summary first and throws on the very
    // transfer faults (a fee line in another currency than its leg) a report is filed about. Only At, ChangeKind and
    // Details are rendered, so the snapshot is not read.
    async Task<IReadOnlyList<RevisionView>> RevisionsAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var history = await db.TransactionRevisions.AsNoTracking()
            .Where(r => r.TransactionId == transactionId)
            .OrderBy(r => r.RevisionNumber)
            .Select(r => new { r.CreatedAt, r.Kind, r.Instruction, r.StatusBefore, r.StatusAfter })
            .ToListAsync(cancellationToken);

        return
        [
            .. history.Select(r => new RevisionView(
                r.CreatedAt, r.Kind.ToString(), verificationUrl.StripUrl(r.Instruction) ?? $"{r.StatusBefore} → {r.StatusAfter}")),
        ];
    }

    IQueryable<BugReport> Reports(bool includeClosed) => includeClosed
        ? db.BugReports.AsNoTracking()
        : db.BugReports.AsNoTracking().Where(r => r.Status == BugReportStatus.Open);

    Task<BugReport> FindAsync(Guid reportId, CancellationToken cancellationToken) =>
        db.BugReports.AsNoTracking().SingleAsync(r => r.Id == reportId, cancellationToken);

    static BugReportSnapshot? SnapshotOf(BugReport report) => report.SnapshotAt is { } takenAt
        ? new BugReportSnapshot(takenAt, report.RecordSummary, BugReportJson.ReadFindings(report.FindingsJson), report.CollectionFailures)
        : null;

    Task<int?> NumberFiledAsync(BugReportSource source, string replyTo, CancellationToken cancellationToken) =>
        db.BugReports.AsNoTracking()
            .Where(r => r.Source == source && r.ReplyTo == replyTo)
            .Select(r => (int?)r.Number)
            .SingleOrDefaultAsync(cancellationToken);
}
