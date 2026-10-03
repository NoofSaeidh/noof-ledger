using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Persistence.Diagnostics;

namespace Noof.Ledger.Persistence.BugReports;

// What a report keeps as it was when filed (spec §3, bug_reports). Each part is best-effort: one that throws becomes its
// line in collection_failures and the report goes on without it - never with an empty list in its place. A cancelled
// token is not a failed part; it always propagates.
internal sealed class BugReportEvidence(LedgerDbContext db, IIntegrityChecks checks, IFiscalVerificationUrl verificationUrl)
{
    public const int LogLineLimit = 200;

    static readonly TimeSpan UnlinkedLogWindow = TimeSpan.FromHours(1);

    public async Task<(IReadOnlyList<IntegrityFinding>? Findings, string? Failure)> FindingsAsync(
        Guid? transactionId, CancellationToken cancellationToken)
    {
        try
        {
            var findings = transactionId is { } id
                ? await checks.FindForTransactionAsync(id, cancellationToken)
                : await checks.FindAllAsync(cancellationToken);
            return (findings, null);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"findings: check failed ({exception.GetType().Name})");
        }
    }

    public async Task<(string? Json, string? Failure)> LogLinesAsync(
        Guid? transactionId, DateTimeOffset filedAt, CancellationToken cancellationToken)
    {
        var filter = transactionId is { } id
            ? new LogFilter(MinLevel: LogSeverity.Verbose, TransactionId: id)
            : new LogFilter(MinLevel: LogSeverity.Warning, From: filedAt - UnlinkedLogWindow, To: filedAt);

        try
        {
            var page = await new EfLogQuery(db).QueryAsync(filter, pageIndex: 0, pageSize: LogLineLimit, cancellationToken);
            return (BugReportJson.WriteLogLines(page.Rows, verificationUrl), null);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"log lines: query failed ({exception.GetType().Name})");
        }
    }

    public async Task<(string? Summary, string? Failure)> RecordSummaryAsync(
        Guid? transactionId, CancellationToken cancellationToken)
    {
        if (transactionId is not { } id)
            return (null, null);

        try
        {
            return (await new RecordSummaryComposer(db, verificationUrl).ComposeAsync(id, cancellationToken), null);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"record summary: query failed ({exception.GetType().Name})");
        }
    }

    public static string? Failures(params string?[] failures) =>
        failures.OfType<string>().ToList() is { Count: > 0 } named ? string.Join('\n', named) : null;
}
