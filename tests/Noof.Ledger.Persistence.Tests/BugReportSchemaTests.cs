using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.BugReports;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class BugReportSchemaTests(PostgresFixture fixture)
{
    static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    static int nextMessageId = 50_000;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static BugReport FromTelegram(int? messageId = null, bool withMessage = true, Guid? transactionId = null) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = Now,
        Source = BugReportSource.Telegram,
        TransactionId = transactionId,
        TelegramChatId = withMessage ? 111 : null,
        TelegramMessageId = withMessage ? messageId ?? Interlocked.Increment(ref nextMessageId) : null,
        Status = BugReportStatus.Open,
        ExplanationState = BugExplanationState.Pending,
        ExplanationAttempts = 0,
        ExplanationNextAt = Now,
    };

    static BugReport FromDashboard(long? chatId = null) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = Now,
        Source = BugReportSource.Dashboard,
        TelegramChatId = chatId,
        Status = BugReportStatus.Open,
        ExplanationState = BugExplanationState.Done,
        ExplanationAttempts = 0,
        ExplanationNextAt = Now,
        Explanation = "Looks like our bug.",
        LooksLikeBug = true,
    };

    static async Task<string?> ViolatedConstraintAsync(LedgerDbContext db, BugReport report)
    {
        db.BugReports.Add(report);
        try
        {
            await db.SaveChangesAsync(Ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException postgres)
        {
            return postgres.ConstraintName;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    [Fact]
    public async Task The_database_numbers_reports_in_filing_order()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var first = FromTelegram();
        var second = FromDashboard();

        db.BugReports.Add(first);
        await db.SaveChangesAsync(Ct);
        db.BugReports.Add(second);
        await db.SaveChangesAsync(Ct);

        first.Number.Should().BePositive();
        second.Number.Should().Be(first.Number + 1);
    }

    [Fact]
    public async Task No_caller_can_choose_a_number()
    {
        await using var db = await fixture.CreateMigratedContextAsync();

        var act = () => db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO public.bug_reports
                (id, number, created_at, source, status, explanation_state, explanation_attempts, explanation_next_at,
                 explanation, looks_like_bug)
            VALUES ({Guid.NewGuid()}, 7, {Now}, 1, 0, 1, 0, {Now}, 'x', TRUE)
            """,
            Ct);

        // 428C9 is generated_always: a GENERATED ALWAYS identity refuses any value a caller supplies.
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("428C9");
    }

    [Fact]
    public async Task A_telegram_report_carries_its_message_and_a_dashboard_report_none()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        const string rule = "ck_bug_reports_telegram_ids_match_source";
        var repliedOnTheDashboard = FromDashboard();
        repliedOnTheDashboard.ReplyMessageId = 9;

        (await ViolatedConstraintAsync(db, FromTelegram(withMessage: false))).Should().Be(rule);
        (await ViolatedConstraintAsync(db, FromDashboard(chatId: 111))).Should().Be(rule);
        (await ViolatedConstraintAsync(db, repliedOnTheDashboard)).Should().Be(rule);
        (await ViolatedConstraintAsync(db, FromTelegram())).Should().BeNull();
        (await ViolatedConstraintAsync(db, FromDashboard())).Should().BeNull();
    }

    [Fact]
    public async Task Closed_at_is_set_exactly_when_a_report_is_closed()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        const string rule = "ck_bug_reports_closed_at_matches_status";
        var closedWithoutTime = FromTelegram();
        closedWithoutTime.Status = BugReportStatus.Closed;
        var openWithTime = FromTelegram();
        openWithTime.ClosedAt = Now;
        var closed = FromTelegram();
        closed.Status = BugReportStatus.Closed;
        closed.ClosedAt = Now;

        (await ViolatedConstraintAsync(db, closedWithoutTime)).Should().Be(rule);
        (await ViolatedConstraintAsync(db, openWithTime)).Should().Be(rule);
        (await ViolatedConstraintAsync(db, closed)).Should().BeNull();
    }

    [Fact]
    public async Task Only_a_done_report_holds_an_explanation_and_its_verdict()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        const string rule = "ck_bug_reports_explanation_matches_state";
        var doneWithoutText = FromDashboard();
        doneWithoutText.Explanation = null;
        var doneWithoutVerdict = FromDashboard();
        doneWithoutVerdict.LooksLikeBug = null;
        var failedWithAnAnswer = FromTelegram();
        failedWithAnAnswer.ExplanationState = BugExplanationState.Failed;
        failedWithAnAnswer.Explanation = "x";
        failedWithAnAnswer.LooksLikeBug = false;

        (await ViolatedConstraintAsync(db, doneWithoutText)).Should().Be(rule);
        (await ViolatedConstraintAsync(db, doneWithoutVerdict)).Should().Be(rule);
        (await ViolatedConstraintAsync(db, failedWithAnAnswer)).Should().Be(rule);
    }

    [Fact]
    public async Task One_telegram_message_files_one_report_and_dashboard_reports_never_collide()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var first = FromTelegram();
        db.BugReports.AddRange(first, FromDashboard(), FromDashboard());
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        (await ViolatedConstraintAsync(db, FromTelegram(first.TelegramMessageId)))
            .Should().Be(BugReportConfiguration.TelegramMessageIndex);
    }

    [Fact]
    public async Task A_record_a_report_is_about_cannot_be_deleted()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var record = new Transaction
        {
            Id = Guid.NewGuid(),
            RawText = "exchanged 100 eur",
            Status = TransactionStatus.Failed,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = Now,
            OccurredOn = new DateOnly(2026, 10, 2),
            TelegramChatId = 111,
            TelegramMessageId = Interlocked.Increment(ref nextMessageId),
            CreatedAt = Now,
        };
        db.Transactions.Add(record);
        db.BugReports.Add(FromTelegram(transactionId: record.Id));
        await db.SaveChangesAsync(Ct);

        var act = () => db.Database.ExecuteSqlAsync($"DELETE FROM public.transactions WHERE id = {record.Id}", Ct);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("FK_bug_reports_transactions_transaction_id");
    }
}
