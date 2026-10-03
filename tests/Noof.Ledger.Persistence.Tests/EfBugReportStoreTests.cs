using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.BugReports;
using Npgsql;
using NSubstitute;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfBugReportStoreTests(PostgresFixture fixture)
{
    const string FiscalLink = "https://suf.purs.gov.rs/v/?vl=QUJDREVGR0hJSktMTU5PUFFSU1RVVldY";
    const long ChatId = 111;
    static readonly DateTimeOffset Filed = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    static readonly IReadOnlyList<IntegrityFinding> NoFindings = [];
    static readonly IFiscalVerificationUrl VerificationUrl =
        new FiscalVerificationUrl(new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" });
    static int nextMessageId = 80_000;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed record Harness(LedgerDbContext Db, FakeTimeProvider Clock, IIntegrityChecks Checks, EfBugReportStore Store)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    // The one place the store is built.
    static EfBugReportStore NewStore(LedgerDbContext db, FakeTimeProvider clock, IIntegrityChecks checks) =>
        new(db, VerificationUrl, clock);

    static IIntegrityChecks Checks()
    {
        var checks = Substitute.For<IIntegrityChecks>();
        checks.FindAllAsync(Arg.Any<CancellationToken>()).Returns(NoFindings);
        checks.FindForTransactionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(NoFindings);
        return checks;
    }

    async Task<Harness> CreateAsync()
    {
        var db = await fixture.CreateMigratedContextAsync();
        var clock = new FakeTimeProvider(Filed);
        var checks = Checks();
        return new Harness(db, clock, checks, NewStore(db, clock, checks));
    }

    static LedgerDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options);

    static async Task<Guid> AddRecordAsync(LedgerDbContext db, string rawText = "exchanged 100 eur")
    {
        var record = new Transaction
        {
            Id = Guid.NewGuid(),
            RawText = rawText,
            Status = TransactionStatus.Failed,
            FailureReason = RecordFailureReason.MissingReceivedAmount,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = Filed.AddDays(-2),
            OccurredOn = new DateOnly(2026, 9, 30),
            TelegramChatId = ChatId,
            TelegramMessageId = Interlocked.Increment(ref nextMessageId),
            CreatedAt = Filed.AddDays(-2),
        };
        db.Transactions.Add(record);
        await db.SaveChangesAsync(Ct);
        return record.Id;
    }

    static TelegramBugReport FromTelegram(string? text = "the amount is wrong", Guid? transactionId = null) =>
        new(ChatId, Interlocked.Increment(ref nextMessageId), text, transactionId);

    static Task<BugReport> RowAsync(LedgerDbContext db, int number) =>
        db.BugReports.AsNoTracking().SingleAsync(r => r.Number == number, Ct);

    static Task<Guid> IdOfAsync(LedgerDbContext db, int number) =>
        db.BugReports.AsNoTracking().Where(r => r.Number == number).Select(r => r.Id).SingleAsync(Ct);

    [Fact]
    public async Task A_telegram_report_is_saved_open_and_pending_with_its_message()
    {
        await using var h = await CreateAsync();
        var recordId = await AddRecordAsync(h.Db);
        var report = FromTelegram("the amount is wrong", recordId);

        var saved = await h.Store.SaveFromTelegramAsync(report, Ct);

        saved.Created.Should().BeTrue();
        (await RowAsync(h.Db, saved.Number)).Should().BeEquivalentTo(new
        {
            CreatedAt = Filed,
            Source = BugReportSource.Telegram,
            Text = "the amount is wrong",
            TransactionId = (Guid?)recordId,
            TelegramChatId = (long?)ChatId,
            TelegramMessageId = (int?)report.MessageId,
            Status = BugReportStatus.Open,
            ClosedAt = (DateTimeOffset?)null,
            SnapshotAt = (DateTimeOffset?)null,
            ExplanationState = BugExplanationState.Pending,
            ExplanationAttempts = 0,
            ExplanationNextAt = Filed,
            Explanation = (string?)null,
            LooksLikeBug = (bool?)null,
            ReplyMessageId = (int?)null,
        });
    }

    [Fact]
    public async Task A_redelivered_bug_command_finds_its_report_files_nothing_and_skips_no_number()
    {
        await using var h = await CreateAsync();
        var report = FromTelegram();

        var first = await h.Store.SaveFromTelegramAsync(report, Ct);
        var again = await h.Store.SaveFromTelegramAsync(report with { Text = "edited before the retry" }, Ct);
        var next = await h.Store.SaveFromTelegramAsync(FromTelegram(), Ct);

        first.Created.Should().BeTrue();
        again.Should().Be(new BugReportSaved(first.Number, Created: false));
        next.Number.Should().Be(first.Number + 1);
        (await RowAsync(h.Db, first.Number)).Text.Should().Be("the amount is wrong");
    }

    // The race backstop, made deterministic: a rival transaction holds the same message's index entry uncommitted, so
    // the lookup finds nothing and the insert waits on the index until the rival commits, then fails with 23505.
    [Fact]
    public async Task A_duplicate_committed_between_the_lookup_and_the_insert_is_caught_and_only_its_row_detached()
    {
        var connectionString = await fixture.CreateDatabaseConnectionStringAsync();
        await using var db = Context(connectionString);
        await using var rival = Context(connectionString);
        var report = FromTelegram();
        var recordId = await AddRecordAsync(db);
        var rivalRow = new BugReport
        {
            Id = Guid.NewGuid(),
            CreatedAt = Filed,
            Source = BugReportSource.Telegram,
            TelegramChatId = report.ChatId,
            TelegramMessageId = report.MessageId,
            Status = BugReportStatus.Open,
            ExplanationState = BugExplanationState.Pending,
            ExplanationAttempts = 0,
            ExplanationNextAt = Filed,
        };
        await using var rivalTransaction = await rival.Database.BeginTransactionAsync(Ct);
        rival.BugReports.Add(rivalRow);
        await rival.SaveChangesAsync(Ct);

        var saving = NewStore(db, new FakeTimeProvider(Filed), Checks()).SaveFromTelegramAsync(report, Ct);
        await LockWaits.UntilABackendWaitsOnALockAsync(rival, Ct);
        await rivalTransaction.CommitAsync(Ct);
        var saved = await saving;

        saved.Should().Be(new BugReportSaved(rivalRow.Number, Created: false));
        db.ChangeTracker.Entries<BugReport>().Should().BeEmpty("the row the store tried to add is detached");
        db.ChangeTracker.Entries<Transaction>().Should().ContainSingle(entry => entry.Entity.Id == recordId,
            "the scope's context is shared with the caller, so the store never clears what else it tracks");
        (await rival.BugReports.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task A_report_that_cannot_be_saved_leaves_no_row_behind()
    {
        await using var h = await CreateAsync();

        var act = () => h.Store.SaveFromTelegramAsync(FromTelegram(transactionId: Guid.NewGuid()), Ct);

        (await act.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        h.Db.ChangeTracker.Entries<BugReport>().Should().BeEmpty("a row left Added would be inserted by the next SaveChanges");
    }

    [Theory]
    [InlineData("the amount is wrong " + FiscalLink, "the amount is wrong")]
    [InlineData(FiscalLink, null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public async Task The_text_is_kept_without_its_fiscal_link_and_a_blank_one_as_none(string? text, string? stored)
    {
        await using var h = await CreateAsync();

        var saved = await h.Store.SaveFromTelegramAsync(FromTelegram(text), Ct);

        (await RowAsync(h.Db, saved.Number)).Text.Should().Be(stored);
    }
}
