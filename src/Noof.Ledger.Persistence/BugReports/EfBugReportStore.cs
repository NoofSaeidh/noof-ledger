using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Receipts;
using Npgsql;

namespace Noof.Ledger.Persistence.BugReports;

// The bug-report queue. Every change after the insert is one guarded UPDATE, so "only while Pending", "snapshot once",
// "deliver once" and "status changes once" hold without a lease (spec P-11).
internal sealed class EfBugReportStore(LedgerDbContext db, IFiscalVerificationUrl verificationUrl, TimeProvider timeProvider)
{
    // Looked up first so a redelivered /bug burns no number; the unique index catches the concurrent duplicate that
    // slips past the lookup (spec P-9).
    public async Task<BugReportSaved> SaveFromTelegramAsync(TelegramBugReport report, CancellationToken cancellationToken)
    {
        if (await NumberOfMessageAsync(report.ChatId, report.MessageId, cancellationToken) is { } existing)
            return new BugReportSaved(existing, Created: false);

        var now = timeProvider.GetUtcNow();
        var row = new BugReport
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            Source = BugReportSource.Telegram,
            Text = verificationUrl.StripUrl(report.Text) is { } kept && !string.IsNullOrWhiteSpace(kept) ? kept : null,
            TransactionId = report.TransactionId,
            TelegramChatId = report.ChatId,
            TelegramMessageId = report.MessageId,
            Status = BugReportStatus.Open,
            ExplanationState = BugExplanationState.Pending,
            ExplanationAttempts = 0,
            ExplanationNextAt = now,
        };
        db.BugReports.Add(row);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new BugReportSaved(row.Number, Created: true);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: BugReportConfiguration.TelegramMessageIndex,
        })
        {
            db.Entry(row).State = EntityState.Detached;
            var winner = await NumberOfMessageAsync(report.ChatId, report.MessageId, cancellationToken)
                ?? throw new InvalidOperationException("A duplicate /bug named a report that cannot be found.");
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

    Task<int?> NumberOfMessageAsync(long chatId, int messageId, CancellationToken cancellationToken) =>
        db.BugReports.AsNoTracking()
            .Where(r => r.TelegramChatId == chatId && r.TelegramMessageId == messageId)
            .Select(r => (int?)r.Number)
            .SingleOrDefaultAsync(cancellationToken);
}
