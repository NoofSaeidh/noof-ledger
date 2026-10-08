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

    static string NextReplyTo() => $"111:{Interlocked.Increment(ref nextMessageId)}";

    static BugReport FromTelegram(string? replyTo = null, Guid? transactionId = null) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = Now,
        Source = BugReportSource.Telegram,
        TransactionId = transactionId,
        ReplyTo = replyTo ?? NextReplyTo(),
        Status = BugReportStatus.Open,
        ExplanationState = BugExplanationState.Pending,
        ExplanationAttempts = 0,
        ExplanationNextAt = Now,
    };

    static BugReport FromDashboard(string? replyTo = null, BugReportSource source = BugReportSource.Dashboard) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = Now,
        Source = source,
        ReplyTo = replyTo,
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
            VALUES ({Guid.NewGuid()}, 7, {Now}, 2, 1, 2, 0, {Now}, 'x', TRUE)
            """,
            Ct);

        // 428C9 is generated_always: a GENERATED ALWAYS identity refuses any value a caller supplies.
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("428C9");
    }

    // Spec R-1 and R-2: the source is never Unknown, and a reply is delivered only to an address. Which source needs an
    // address is its own layer's business, so a new source needs no schema change.
    [Fact]
    public async Task A_report_names_its_source_and_is_delivered_only_to_its_reply_address()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        const string rule = "ck_bug_reports_source_and_reply_to";
        var unknownSource = FromDashboard(source: BugReportSource.Unknown);
        var deliveredNowhere = FromDashboard();
        deliveredNowhere.DeliveredAs = "9";
        var delivered = FromTelegram();
        delivered.DeliveredAs = "9";

        (await ViolatedConstraintAsync(db, unknownSource)).Should().Be(rule);
        (await ViolatedConstraintAsync(db, deliveredNowhere)).Should().Be(rule);
        (await ViolatedConstraintAsync(db, delivered)).Should().BeNull();
        (await ViolatedConstraintAsync(db, FromDashboard())).Should().BeNull();
        (await ViolatedConstraintAsync(db, FromDashboard(NextReplyTo()))).Should().BeNull();
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
        var unknownStatus = FromTelegram();
        unknownStatus.Status = BugReportStatus.Unknown;
        var closed = FromTelegram();
        closed.Status = BugReportStatus.Closed;
        closed.ClosedAt = Now;

        (await ViolatedConstraintAsync(db, closedWithoutTime)).Should().Be(rule);
        (await ViolatedConstraintAsync(db, openWithTime)).Should().Be(rule);
        (await ViolatedConstraintAsync(db, unknownStatus)).Should().Be(rule);
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
        var unknownState = FromTelegram();
        unknownState.ExplanationState = BugExplanationState.Unknown;
        var failed = FromTelegram();
        failed.ExplanationState = BugExplanationState.Failed;

        (await ViolatedConstraintAsync(db, doneWithoutText)).Should().Be(rule);
        (await ViolatedConstraintAsync(db, doneWithoutVerdict)).Should().Be(rule);
        (await ViolatedConstraintAsync(db, failedWithAnAnswer)).Should().Be(rule);
        (await ViolatedConstraintAsync(db, unknownState)).Should().Be(rule);
        (await ViolatedConstraintAsync(db, failed)).Should().BeNull();
        (await ViolatedConstraintAsync(db, FromDashboard())).Should().BeNull();
    }

    [Fact]
    public async Task One_reply_address_files_one_report_per_source_and_reports_without_one_never_collide()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var first = FromTelegram();
        db.BugReports.AddRange(first, FromDashboard(), FromDashboard());
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        (await ViolatedConstraintAsync(db, FromTelegram(first.ReplyTo)))
            .Should().Be(BugReportConfiguration.ReplyToIndex);
        (await ViolatedConstraintAsync(db, FromDashboard(first.ReplyTo))).Should().BeNull();
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
