using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.BugReports;
using Noof.Ledger.Persistence.Diagnostics;
using Noof.Ledger.Persistence.Revisions;
using Npgsql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

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
        new(db, checks, VerificationUrl, clock);

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

    static IReadOnlyList<IntegrityFinding> Findings(params IntegrityFinding[] findings) => findings;

    static IntegrityFinding WaitingOn(Guid transactionId, string reason = "MissingReceivedAmount") => new(
        IntegrityCheck.NotApplied, IntegrityGroup.WaitingOnYou, transactionId, WalletId: null, JobId: null,
        [
            new TextFact("Waiting for", "A reply to the echo"),
            new TextFact("Status", "Failed"),
            new TextFact("Reason", reason),
            new SinceFact("Idle for", Filed.AddDays(-2)),
            new DateFact("Date", new DateOnly(2026, 9, 30)),
            new MoneyFact("Amount", 100m, CurrencyCode.Eur),
        ]);

    static AppLogEntry LogEntry(
        long id, DateTimeOffset at, LogSeverity level, string message, Guid? transactionId = null,
        string? exception = null, string? properties = null) => new()
    {
        Id = id,
        LoggedAt = at,
        Level = level,
        Source = "Noof.Ledger.Host.Workers.CategorizationWorker",
        Message = message,
        Template = message,
        Exception = exception,
        TransactionId = transactionId,
        PropertiesJson = properties,
    };

    static IEnumerable<string> LogMessages(BugReport row) =>
        (BugReportJson.ReadLogLines(row.LogLinesJson) ?? throw new InvalidOperationException("The report holds no log lines."))
        .Select(line => line.Message);

    // Closing is the row change itself, so the queue's tests do not depend on SetStatusAsync.
    static Task CloseAsync(LedgerDbContext db, int number) =>
        db.BugReports.Where(r => r.Number == number).ExecuteUpdateAsync(set => set
            .SetProperty(r => r.Status, BugReportStatus.Closed)
            .SetProperty(r => r.ClosedAt, (DateTimeOffset?)Filed), Ct);

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

    [Fact]
    public async Task A_linked_snapshot_keeps_the_records_findings_log_lines_and_summary()
    {
        await using var h = await CreateAsync();
        var recordId = await AddRecordAsync(h.Db);
        var otherId = await AddRecordAsync(h.Db);
        h.Db.AppLogs.AddRange(
            LogEntry(1, Filed.AddMinutes(-3), LogSeverity.Debug, "Claimed the job", recordId),
            LogEntry(2, Filed.AddMinutes(-2), LogSeverity.Warning, "No received amount", recordId),
            LogEntry(3, Filed.AddMinutes(-1), LogSeverity.Warning, "Another record's line", otherId));
        await h.Db.SaveChangesAsync(Ct);
        h.Checks.FindForTransactionAsync(recordId, Arg.Any<CancellationToken>()).Returns(Findings(WaitingOn(recordId)));
        var saved = await h.Store.SaveFromTelegramAsync(FromTelegram(transactionId: recordId), Ct);
        h.Clock.Advance(TimeSpan.FromSeconds(30));

        var snapshot = await h.Store.TakeSnapshotAsync(await IdOfAsync(h.Db, saved.Number), Ct);

        snapshot.TakenAt.Should().Be(Filed.AddSeconds(30));
        snapshot.Findings.Should().BeEquivalentTo(new[] { WaitingOn(recordId) }, options => options.PreferringRuntimeMemberTypes());
        snapshot.RecordSummary.Should().StartWith("Record: Expense · Failed · failure reason MissingReceivedAmount · captured from Text\n");
        snapshot.CollectionFailures.Should().BeNull();
        LogMessages(await RowAsync(h.Db, saved.Number)).Should().Equal("No received amount", "Claimed the job");
        await h.Checks.DidNotReceive().FindAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unlinked_snapshot_keeps_every_finding_and_the_warnings_of_the_hour_before_it_was_filed()
    {
        await using var h = await CreateAsync();
        var recordId = await AddRecordAsync(h.Db);
        h.Db.AppLogs.AddRange(
            LogEntry(1, Filed.AddMinutes(-90), LogSeverity.Error, "Too early"),
            LogEntry(2, Filed.AddMinutes(-30), LogSeverity.Warning, "In the hour"),
            LogEntry(3, Filed.AddMinutes(-20), LogSeverity.Information, "Below Warning"),
            LogEntry(4, Filed.AddMinutes(-10), LogSeverity.Error, "Also in the hour", recordId),
            LogEntry(5, Filed.AddMinutes(5), LogSeverity.Error, "After filing"));
        await h.Db.SaveChangesAsync(Ct);
        h.Checks.FindAllAsync(Arg.Any<CancellationToken>()).Returns(Findings(WaitingOn(recordId)));
        var saved = await h.Store.SaveFromTelegramAsync(FromTelegram("the dashboard total looks off"), Ct);
        h.Clock.Advance(TimeSpan.FromMinutes(10));

        var snapshot = await h.Store.TakeSnapshotAsync(await IdOfAsync(h.Db, saved.Number), Ct);

        snapshot.Findings.Should().ContainSingle();
        snapshot.RecordSummary.Should().BeNull();
        snapshot.CollectionFailures.Should().BeNull("an unlinked report has no record to summarise, which is no failure");
        LogMessages(await RowAsync(h.Db, saved.Number)).Should().Equal("Also in the hour", "In the hour");
        await h.Checks.DidNotReceive().FindForTransactionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_linked_snapshot_keeps_at_most_the_200_newest_log_lines()
    {
        await using var h = await CreateAsync();
        var recordId = await AddRecordAsync(h.Db);
        h.Db.AppLogs.AddRange(Enumerable.Range(1, 205)
            .Select(n => LogEntry(n, Filed.AddMinutes(n - 300), LogSeverity.Information, $"line {n}", recordId)));
        await h.Db.SaveChangesAsync(Ct);
        var saved = await h.Store.SaveFromTelegramAsync(FromTelegram(transactionId: recordId), Ct);

        await h.Store.TakeSnapshotAsync(await IdOfAsync(h.Db, saved.Number), Ct);

        var messages = LogMessages(await RowAsync(h.Db, saved.Number)).ToList();
        messages.Should().HaveCount(BugReportEvidence.LogLineLimit);
        (messages[0], messages[^1]).Should().Be(("line 205", "line 6"));
    }

    [Fact]
    public async Task A_second_snapshot_returns_the_one_already_stored()
    {
        await using var h = await CreateAsync();
        var recordId = await AddRecordAsync(h.Db);
        h.Checks.FindForTransactionAsync(recordId, Arg.Any<CancellationToken>()).Returns(Findings(WaitingOn(recordId)), NoFindings);
        var reportId = await IdOfAsync(h.Db, (await h.Store.SaveFromTelegramAsync(FromTelegram(transactionId: recordId), Ct)).Number);
        var first = await h.Store.TakeSnapshotAsync(reportId, Ct);
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        var second = await h.Store.TakeSnapshotAsync(reportId, Ct);

        second.TakenAt.Should().Be(first.TakenAt);
        second.Findings.Should().ContainSingle();
        await h.Checks.Received(1).FindForTransactionAsync(recordId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_part_that_fails_is_named_and_the_others_are_kept()
    {
        await using var h = await CreateAsync();
        var recordId = await AddRecordAsync(h.Db);
        h.Db.AppLogs.Add(LogEntry(1, Filed.AddMinutes(-1), LogSeverity.Warning, "No received amount", recordId));
        await h.Db.SaveChangesAsync(Ct);
        h.Checks.FindForTransactionAsync(recordId, Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());
        var saved = await h.Store.SaveFromTelegramAsync(FromTelegram(transactionId: recordId), Ct);

        var snapshot = await h.Store.TakeSnapshotAsync(await IdOfAsync(h.Db, saved.Number), Ct);

        snapshot.Findings.Should().BeNull("findings that could not be collected are never an empty list");
        snapshot.CollectionFailures.Should().Be("findings: check failed (TimeoutException)");
        snapshot.RecordSummary.Should().NotBeNull();
        LogMessages(await RowAsync(h.Db, saved.Number)).Should().Equal("No received amount");
    }

    [Fact]
    public async Task Every_part_that_fails_is_named_in_the_specs_order()
    {
        await using var h = await CreateAsync();
        var recordId = await AddRecordAsync(h.Db);
        h.Checks.FindForTransactionAsync(recordId, Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());
        var saved = await h.Store.SaveFromTelegramAsync(FromTelegram(transactionId: recordId), Ct);
        await h.Db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE public.app_log RENAME TO app_log_gone; "
            + "ALTER TABLE public.transaction_revisions RENAME TO transaction_revisions_gone;",
            Ct);

        var snapshot = await h.Store.TakeSnapshotAsync(await IdOfAsync(h.Db, saved.Number), Ct);

        snapshot.CollectionFailures.Should().Be(
            "findings: check failed (TimeoutException)\n"
            + "log lines: query failed (PostgresException)\n"
            + "record summary: query failed (PostgresException)");
        snapshot.RecordSummary.Should().BeNull();
        (await RowAsync(h.Db, saved.Number)).LogLinesJson.Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_snapshot_stores_nothing()
    {
        await using var h = await CreateAsync();
        var recordId = await AddRecordAsync(h.Db);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        h.Checks.FindForTransactionAsync(recordId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<IReadOnlyList<IntegrityFinding>>(cancellation.Token);
        });
        var saved = await h.Store.SaveFromTelegramAsync(FromTelegram(transactionId: recordId), Ct);
        var reportId = await IdOfAsync(h.Db, saved.Number);

        var act = () => h.Store.TakeSnapshotAsync(reportId, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await RowAsync(h.Db, saved.Number)).SnapshotAt.Should().BeNull("a cancellation is never recorded as a failed part");
    }

    // The snapshot's final UPDATE honours the token as well, so the fact above stays green even if a part swallowed the
    // cancellation as a failure; this one watches the part itself.
    [Fact]
    public async Task A_cancelled_part_propagates_and_is_never_named_as_a_failure()
    {
        await using var h = await CreateAsync();
        var recordId = await AddRecordAsync(h.Db);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        h.Checks.FindForTransactionAsync(recordId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<IReadOnlyList<IntegrityFinding>>(cancellation.Token);
        });

        var act = () => new BugReportEvidence(h.Db, h.Checks, VerificationUrl).FindingsAsync(recordId, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // The operator's text, the record's raw text, a revision's instruction, a failed job's last error (as I-4 reports
    // it - checks never strip) and a log row's message, exception and property all carry a fiscal link.
    [Fact]
    public async Task No_fiscal_link_survives_in_any_column()
    {
        await using var h = await CreateAsync();
        var recordId = await AddRecordAsync(h.Db, $"exchanged 100 eur {FiscalLink} at the office");
        var jobId = Guid.NewGuid();
        h.Db.TransactionRevisions.Add(new TransactionRevision
        {
            Id = Guid.NewGuid(),
            TransactionId = recordId,
            RevisionNumber = 1,
            Kind = RevisionKind.Correction,
            Instruction = $"it was 117 {FiscalLink} not 116",
            StatusBefore = TransactionStatus.Failed,
            StatusAfter = TransactionStatus.Failed,
            Snapshot = "{}",
            CreatedAt = Filed.AddDays(-1),
        });
        h.Db.CategorizationJobs.Add(new CategorizationJob
        {
            Id = jobId,
            TransactionId = recordId,
            Kind = JobKind.Correct,
            Instruction = "it was 117",
            Status = JobStatus.Failed,
            AttemptCount = 8,
            RunAfter = Filed.AddDays(-1),
            LastError = $"The fiscal fetch failed for {FiscalLink}",
            CreatedAt = Filed.AddDays(-1),
            UpdatedAt = Filed.AddDays(-1),
        });
        h.Db.AppLogs.Add(LogEntry(1, Filed.AddMinutes(-5), LogSeverity.Warning, $"Correction failed for {FiscalLink} today",
            recordId, exception: $"System.InvalidOperationException: {FiscalLink} unreachable",
            properties: $$"""{"Url":"{{FiscalLink}}","Stage":"StageFailed"}"""));
        await h.Db.SaveChangesAsync(Ct);
        h.Checks.FindForTransactionAsync(recordId, Arg.Any<CancellationToken>()).Returns(Findings(new IntegrityFinding(
            IntegrityCheck.NotApplied, IntegrityGroup.WaitingOnYou, recordId, WalletId: null, jobId,
            [
                new TextFact("Waiting for", "A correction that never applied"),
                new TextFact("Job", "Correct"),
                new TextFact("Last error", $"The fiscal fetch failed for {FiscalLink}"),
            ])));
        var saved = await h.Store.SaveFromTelegramAsync(FromTelegram($"wrong rate {FiscalLink} please", recordId), Ct);

        await h.Store.TakeSnapshotAsync(await IdOfAsync(h.Db, saved.Number), Ct);

        var columns = (await h.Db.Database.SqlQuery<string>(
            $"""
            SELECT concat_ws(' | ', text, record_summary, findings::text, log_lines::text, collection_failures, explanation) AS "Value"
            FROM public.bug_reports
            WHERE number = {saved.Number}
            """).ToListAsync(Ct)).Single();
        columns.Should().NotContain("suf.purs.gov.rs").And.NotContain("vl=");
        columns.Should().ContainAll(
            "wrong rate please",
            "Text: exchanged 100 eur at the office",
            "Correction · it was 117 not 116",
            "The fiscal fetch failed for",
            "Correction failed for today",
            "System.InvalidOperationException: unreachable");
    }

    // Spec P-22: a synthetic copy of CategorizationWorkerLog's real "{Stage} to bot message {BotMessageId}" event, as the
    // database sink stores it, keeps neither id in the column.
    [Fact]
    public async Task No_telegram_id_survives_in_the_log_lines_column()
    {
        await using var h = await CreateAsync();
        var recordId = await AddRecordAsync(h.Db);
        h.Db.AppLogs.Add(LogEntry(1, Filed.AddMinutes(-1), LogSeverity.Information, "Replied to bot message 4567", recordId,
            properties: """{"Stage":"Replied","BotMessageId":4567,"ChatId":111222333}"""));
        await h.Db.SaveChangesAsync(Ct);
        var saved = await h.Store.SaveFromTelegramAsync(FromTelegram(transactionId: recordId), Ct);

        await h.Store.TakeSnapshotAsync(await IdOfAsync(h.Db, saved.Number), Ct);

        var logLines = (await RowAsync(h.Db, saved.Number)).LogLinesJson;
        logLines.Should().NotContain("4567").And.NotContain("111222333");
        LogMessages(await RowAsync(h.Db, saved.Number)).Should().Equal("Replied to bot message [telegram id]");
    }

    [Fact]
    public async Task The_next_due_report_is_the_oldest_open_pending_one_whose_time_has_come()
    {
        await using var h = await CreateAsync();
        // The harness clock starts at Filed and a FakeTimeProvider never goes back, so this one starts earlier.
        var clock = new FakeTimeProvider(Filed.AddMinutes(-30));
        var store = NewStore(h.Db, clock, h.Checks);
        var closed = await store.SaveFromTelegramAsync(FromTelegram("closed"), Ct);
        var explained = await store.SaveFromTelegramAsync(FromTelegram("explained"), Ct);
        clock.SetUtcNow(Filed.AddMinutes(-20));
        var waiting = await store.SaveFromTelegramAsync(FromTelegram("waiting for its retry"), Ct);
        clock.SetUtcNow(Filed.AddMinutes(-10));
        var due = await store.SaveFromTelegramAsync(FromTelegram("due"), Ct);
        await store.SaveFromTelegramAsync(FromTelegram("filed in the same instant, after it"), Ct);
        await CloseAsync(h.Db, closed.Number);
        await store.CompleteExplanationAsync(await IdOfAsync(h.Db, explained.Number), new Explanation("Done.", false), Ct);
        await store.RecordFailedAttemptAsync(await IdOfAsync(h.Db, waiting.Number), Filed.AddMinutes(1), maxAttempts: 3, Ct);

        var next = await store.NextDueAsync(Filed, Ct);

        next.Should().Be(new BugReportToExplain(await IdOfAsync(h.Db, due.Number), due.Number, 0, "due", null, Snapshot: null));
    }

    [Fact]
    public async Task The_next_due_report_carries_the_snapshot_it_already_took()
    {
        await using var h = await CreateAsync();
        var recordId = await AddRecordAsync(h.Db);
        h.Checks.FindForTransactionAsync(recordId, Arg.Any<CancellationToken>()).Returns(Findings(WaitingOn(recordId)));
        var saved = await h.Store.SaveFromTelegramAsync(FromTelegram(transactionId: recordId), Ct);
        await h.Store.TakeSnapshotAsync(await IdOfAsync(h.Db, saved.Number), Ct);

        var next = await h.Store.NextDueAsync(Filed, Ct) ?? throw new InvalidOperationException("Nothing is due.");

        next.Should().BeEquivalentTo(new
        {
            saved.Number,
            TransactionId = (Guid?)recordId,
            Snapshot = new { TakenAt = Filed, Findings = new[] { WaitingOn(recordId) }, CollectionFailures = (string?)null },
        }, options => options.PreferringRuntimeMemberTypes());
    }

    [Fact]
    public async Task An_explanation_completes_a_pending_report_once_and_without_its_fiscal_link()
    {
        await using var h = await CreateAsync();
        var saved = await h.Store.SaveFromTelegramAsync(FromTelegram(), Ct);
        var reportId = await IdOfAsync(h.Db, saved.Number);

        await h.Store.CompleteExplanationAsync(reportId, new Explanation($"Reply with the amount. {FiscalLink}", LooksLikeBug: false), Ct);
        await h.Store.CompleteExplanationAsync(reportId, new Explanation("A second answer", LooksLikeBug: true), Ct);

        (await RowAsync(h.Db, saved.Number)).Should().BeEquivalentTo(new
        {
            ExplanationState = BugExplanationState.Done,
            Explanation = "Reply with the amount.",
            LooksLikeBug = (bool?)false,
        });
        (await h.Store.NextDueAsync(Filed.AddDays(1), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task An_explanation_that_is_nothing_but_a_fiscal_link_is_kept_as_an_empty_answer()
    {
        await using var h = await CreateAsync();
        var saved = await h.Store.SaveFromTelegramAsync(FromTelegram(), Ct);

        await h.Store.CompleteExplanationAsync(await IdOfAsync(h.Db, saved.Number), new Explanation(FiscalLink, LooksLikeBug: true), Ct);

        (await RowAsync(h.Db, saved.Number)).Should().BeEquivalentTo(new
        {
            ExplanationState = BugExplanationState.Done,
            Explanation = "",
            LooksLikeBug = (bool?)true,
        });
    }

    [Fact]
    public async Task A_failed_attempt_counts_and_waits_and_the_last_one_fails_the_report()
    {
        await using var h = await CreateAsync();
        var saved = await h.Store.SaveFromTelegramAsync(FromTelegram(), Ct);
        var reportId = await IdOfAsync(h.Db, saved.Number);
        List<BugExplanationState> states = [];

        for (var attempt = 1; attempt <= 4; attempt++)
            states.Add(await h.Store.RecordFailedAttemptAsync(reportId, Filed.AddMinutes(attempt), maxAttempts: 3, Ct));

        states.Should().Equal(
            BugExplanationState.Pending, BugExplanationState.Pending, BugExplanationState.Failed, BugExplanationState.Failed);
        var row = await RowAsync(h.Db, saved.Number);
        row.ExplanationAttempts.Should().Be(3, "an attempt on a report that is no longer pending changes nothing");
        row.ExplanationNextAt.Should().Be(Filed.AddMinutes(3));
        row.Explanation.Should().BeNull();
    }

    [Fact]
    public async Task Pending_deliveries_are_open_telegram_reports_with_an_answer_and_no_reply_yet()
    {
        await using var h = await CreateAsync();
        var recordId = await AddRecordAsync(h.Db);
        h.Checks.FindForTransactionAsync(recordId, Arg.Any<CancellationToken>())
            .Returns(Findings(WaitingOn(recordId), WaitingOn(recordId, "SameWallet")));
        h.Checks.FindAllAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());

        var explained = FromTelegram("linked", recordId);
        var explainedNumber = (await h.Store.SaveFromTelegramAsync(explained, Ct)).Number;
        var explainedId = await IdOfAsync(h.Db, explainedNumber);
        await h.Store.TakeSnapshotAsync(explainedId, Ct);
        await h.Store.CompleteExplanationAsync(explainedId, new Explanation("Reply with the amount.", LooksLikeBug: false), Ct);

        var gaveUp = FromTelegram("unlinked");
        var gaveUpNumber = (await h.Store.SaveFromTelegramAsync(gaveUp, Ct)).Number;
        var gaveUpId = await IdOfAsync(h.Db, gaveUpNumber);
        await h.Store.TakeSnapshotAsync(gaveUpId, Ct);
        await h.Store.RecordFailedAttemptAsync(gaveUpId, Filed, maxAttempts: 1, Ct);

        await h.Store.SaveFromTelegramAsync(FromTelegram("not explained yet"), Ct);
        var closedNumber = (await h.Store.SaveFromTelegramAsync(FromTelegram("closed first"), Ct)).Number;
        await h.Store.CompleteExplanationAsync(await IdOfAsync(h.Db, closedNumber), new Explanation("x", false), Ct);
        await CloseAsync(h.Db, closedNumber);
        var repliedId = await IdOfAsync(h.Db, (await h.Store.SaveFromTelegramAsync(FromTelegram("replied"), Ct)).Number);
        await h.Store.CompleteExplanationAsync(repliedId, new Explanation("y", false), Ct);
        await h.Store.MarkDeliveredAsync(repliedId, 4242, Ct);

        var deliveries = await h.Store.PendingDeliveriesAsync(Ct);

        deliveries.Should().Equal(
            new BugReportDelivery(explainedId, explainedNumber, ChatId, explained.MessageId, BugExplanationState.Done,
                "Reply with the amount.", false, Linked: true, FindingsCount: 2),
            new BugReportDelivery(gaveUpId, gaveUpNumber, ChatId, gaveUp.MessageId, BugExplanationState.Failed,
                null, null, Linked: false, FindingsCount: null));
    }

    [Fact]
    public async Task A_delivered_report_keeps_its_reply_and_leaves_the_delivery_list()
    {
        await using var h = await CreateAsync();
        var saved = await h.Store.SaveFromTelegramAsync(FromTelegram(), Ct);
        var reportId = await IdOfAsync(h.Db, saved.Number);
        await h.Store.CompleteExplanationAsync(reportId, new Explanation("Reply with the amount.", false), Ct);

        await h.Store.MarkDeliveredAsync(reportId, 4242, Ct);
        await h.Store.MarkDeliveredAsync(reportId, 4343, Ct);

        (await RowAsync(h.Db, saved.Number)).ReplyMessageId.Should().Be(4242);
        (await h.Store.PendingDeliveriesAsync(Ct)).Should().BeEmpty();
    }
}
