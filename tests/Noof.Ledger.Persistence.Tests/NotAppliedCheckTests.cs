using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Diagnostics.Integrity;
using Noof.Ledger.Persistence.Receipts;
using static Noof.Ledger.Persistence.Tests.PipelineSeed;
using AppReceipts = Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class NotAppliedCheckTests(PostgresFixture fixture)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static NotAppliedCheck NewCheck(LedgerDbContext db, DateTimeOffset now)
    {
        var clock = new FakeTimeProvider(now);
        return new NotAppliedCheck(db, new EfReceiptStore(db, clock), clock);
    }

    static Task<IReadOnlyList<IntegrityFinding>> AllAsync(LedgerDbContext db, DateTimeOffset? now = null) =>
        NewCheck(db, now ?? Now).FindAsync(IntegrityScope.All, Ct);

    [Fact]
    public async Task A_failed_exchange_waiting_over_a_day_for_its_received_amount_is_one_finding_with_its_facts()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var capturedAt = Now.AddDays(-2);
        var id = await TextRecordAsync(
            db, capturedAt, TransactionStatus.Failed, RecordFailureReason.MissingReceivedAmount, rawText: "exchanged 100 eur");

        var finding = (await AllAsync(db)).Should().ContainSingle().Which;

        finding.Check.Should().Be(IntegrityCheck.NotApplied);
        finding.Group.Should().Be(IntegrityGroup.WaitingOnYou);
        finding.TransactionId.Should().Be(id);
        finding.WalletId.Should().BeNull();
        finding.JobId.Should().BeNull();
        finding.Facts.Should().Equal(
            new TextFact("Waiting for", "A reply to the echo"),
            new TextFact("Status", "Failed"),
            new TextFact("Reason", "MissingReceivedAmount"),
            new SinceFact("Idle for", capturedAt),
            new DateFact("Date", Day));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(23)]
    public async Task A_failed_record_idle_for_a_day_or_less_is_not_waiting_yet(int hours)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await TextRecordAsync(db, Now.AddHours(-hours), TransactionStatus.Failed);

        (await AllAsync(db)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Claimed)]
    public async Task A_queued_or_running_job_means_it_is_still_being_worked_on(JobStatus status)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var id = await TextRecordAsync(db, Now.AddDays(-3), TransactionStatus.Failed);
        await JobAsync(db, id, JobKind.Correct, status, Now.AddDays(-3));

        (await AllAsync(db)).Should().BeEmpty();
    }

    // A record restored a minute ago is not flagged until its new idle period passes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_held_receipt_or_slip_waits_for_Record_anyway_only_while_captured_and_a_Restore_restarts_the_wait(
        bool slip)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var capturedAt = Now.AddDays(-3);
        var id = slip
            ? await SlipAsync(db, capturedAt, AppReceipts.SlipDisposition.Hold)
            : await ReceiptWithoutCategorizationAsync(db, capturedAt, ReceiptSource.Vision);

        var held = (await AllAsync(db)).Should().ContainSingle().Which;
        held.Facts.Should().Equal(
            new TextFact("Waiting for", "Record anyway"),
            new TextFact("Status", "Captured"),
            new SinceFact("Idle for", capturedAt.AddMinutes(1)),
            new DateFact("Date", Day),
            new MoneyFact("Amount", slip ? 11650.00m : 1250.00m, CurrencyCode.Rsd));

        await CancelAsync(db, id, Now.AddMinutes(-2));
        (await AllAsync(db)).Should().BeEmpty("Cancel is the operator's answer");

        var restoredAt = Now.AddMinutes(-1);
        await RestoreAsync(db, id, restoredAt);
        (await AllAsync(db)).Should().BeEmpty("a record restored a minute ago has not waited a day");

        var later = (await AllAsync(db, Now.AddHours(25))).Should().ContainSingle().Which;
        later.Facts[0].Should().Be(new TextFact("Waiting for", "Record anyway"));
        later.Facts.Should().Contain(new SinceFact("Idle for", restoredAt));
    }

    [Fact]
    public async Task An_incomplete_slip_waits_for_a_reply_naming_its_reason_and_total()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var capturedAt = Now.AddDays(-2);
        await SlipAsync(db, capturedAt, AppReceipts.SlipDisposition.Incomplete);

        var finding = (await AllAsync(db)).Should().ContainSingle().Which;

        finding.Facts.Should().Equal(
            new TextFact("Waiting for", "A reply to the echo"),
            new TextFact("Status", "Failed"),
            new TextFact("Reason", "SlipIncomplete"),
            new SinceFact("Idle for", capturedAt.AddMinutes(1)),
            new DateFact("Date", Day),
            new MoneyFact("Amount", 11650.00m, CurrencyCode.Rsd));
    }

    // Spec Testing, awaiting confirmation: a slip with a RecordExchange job — queued with the slip (a ready one) or by
    // Record anyway — never waits for Record anyway again, even once that job has failed; an applied slip waits for
    // nothing.
    [Fact]
    public async Task A_slip_with_its_RecordExchange_job_never_waits_for_Record_anyway()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var ready = await SlipAsync(db, Now.AddDays(-3), AppReceipts.SlipDisposition.Record);
        var pressedAt = Now.AddDays(-3).AddHours(1);
        var pressed = await SlipAsync(db, pressedAt, AppReceipts.SlipDisposition.Hold);
        (await new EfReceiptStore(db, new FakeTimeProvider(pressedAt.AddMinutes(2)))
            .EnqueueCategorizationAsync(pressed, sourceMessageId: 1, Ct)).Should().BeTrue();
        db.ChangeTracker.Clear();
        var applied = await SlipAsync(db, Now.AddDays(-3).AddHours(2), AppReceipts.SlipDisposition.Record);
        await SettleRecordExchangeAsync(db, ready, JobStatus.Failed);
        await SettleRecordExchangeAsync(db, pressed, JobStatus.Failed);
        await SettleRecordExchangeAsync(db, applied, JobStatus.Succeeded, TransactionStatus.Completed);

        var findings = await AllAsync(db);

        findings.Select(finding => (finding.TransactionId, Text(finding, "Waiting for"), Text(finding, "Job"))).Should().Equal(
            ((Guid?)ready, "A correction that never applied", "RecordExchange"),
            ((Guid?)pressed, "A correction that never applied", "RecordExchange"));
    }

    static string Text(IntegrityFinding finding, string name) =>
        finding.Facts.OfType<TextFact>().Single(fact => fact.Name == name).Text;

    [Fact]
    public async Task A_failed_correction_on_a_completed_record_names_its_job_its_errors_first_line_and_its_amounts()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wallet = await WalletAsync(db, "Raiffeisen RSD", CurrencyCode.Rsd);
        var id = await TextRecordAsync(db, Now.AddDays(-5), TransactionStatus.Completed, walletId: wallet);
        await LineAsync(db, id, new Money(250.00m, CurrencyCode.Rsd), 1);
        await LineAsync(db, id, new Money(3.50m, CurrencyCode.Eur), 2);
        await LineAsync(db, id, new Money(1.25m, CurrencyCode.Eur), 3);
        await LineAsync(db, id, new Money(10.00m, CurrencyCode.Rsd), 4, EntryRole.Fee);
        var failedAt = Now.AddDays(-3);
        var job = await JobAsync(db, id, JobKind.Correct, JobStatus.Failed, failedAt,
            lastError: "  The model call failed.\n   at Noof.Ledger.Ai.ChatCategorizer.CategorizeAsync()\n");

        var finding = (await AllAsync(db)).Should().ContainSingle().Which;

        finding.JobId.Should().Be(job);
        finding.WalletId.Should().Be(wallet);
        finding.Facts.Should().Equal(
            new TextFact("Waiting for", "A correction that never applied"),
            new TextFact("Job", "Correct"),
            new TextFact("Last error", "The model call failed."),
            new SinceFact("Idle for", failedAt),
            new DateFact("Date", Day),
            new MoneyFact("Amount", 4.75m, CurrencyCode.Eur),
            new MoneyFact("Amount", 250.00m, CurrencyCode.Rsd));
    }

    [Fact]
    public async Task A_failed_correction_followed_by_a_successful_one_is_not_waiting()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var id = await TextRecordAsync(db, Now.AddDays(-5), TransactionStatus.Completed);
        await JobAsync(db, id, JobKind.Correct, JobStatus.Failed, Now.AddDays(-3), lastError: "The model call failed.");
        await JobAsync(db, id, JobKind.Correct, JobStatus.Succeeded, Now.AddDays(-3).AddHours(1));

        (await AllAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_latest_failed_job_no_success_followed_is_the_one_named()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var id = await TextRecordAsync(db, Now.AddDays(-5), TransactionStatus.Completed);
        await JobAsync(db, id, JobKind.Correct, JobStatus.Failed, Now.AddDays(-4), lastError: "first");
        await JobAsync(db, id, JobKind.Correct, JobStatus.Succeeded, Now.AddDays(-4).AddHours(1));
        var latest = await JobAsync(db, id, JobKind.Reinterpret, JobStatus.Failed, Now.AddDays(-3), lastError: "second");

        var finding = (await AllAsync(db)).Should().ContainSingle().Which;

        finding.JobId.Should().Be(latest);
        finding.Facts.Should().ContainInOrder(new TextFact("Job", "Reinterpret"), new TextFact("Last error", "second"));
    }

    [Fact]
    public async Task A_last_error_over_500_characters_is_cut_and_a_missing_one_reads_none()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var longError = await TextRecordAsync(db, Now.AddDays(-5), TransactionStatus.Completed);
        await JobAsync(db, longError, JobKind.Reinterpret, JobStatus.Failed, Now.AddDays(-3), lastError: new string('x', 600));
        var noError = await TextRecordAsync(db, Now.AddDays(-4), TransactionStatus.Completed);
        await JobAsync(db, noError, JobKind.Reinterpret, JobStatus.Failed, Now.AddDays(-3));

        var findings = await AllAsync(db);

        findings.Select(finding => finding.Facts.OfType<TextFact>().Single(fact => fact.Name == "Last error").Text)
            .Should().Equal(new string('x', 499) + "…", "(none)");
    }

    [Fact]
    public async Task A_failed_job_on_a_failed_record_is_its_failure_and_on_a_cancelled_record_is_nothing()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var failed = await TextRecordAsync(db, Now.AddDays(-3), TransactionStatus.Failed);
        await JobAsync(db, failed, JobKind.Categorize, JobStatus.Failed, Now.AddDays(-3), lastError: "The model call failed.");
        var cancelled = await TextRecordAsync(db, Now.AddDays(-3), TransactionStatus.Completed);
        await JobAsync(db, cancelled, JobKind.Correct, JobStatus.Failed, Now.AddDays(-3), lastError: "The model call failed.");
        await CancelAsync(db, cancelled, Now.AddDays(-2));

        var finding = (await AllAsync(db)).Should().ContainSingle().Which;

        finding.TransactionId.Should().Be(failed);
        finding.JobId.Should().BeNull("bullet 1 comes before the failed-job bullet");
        finding.Facts.Take(3).Should().Equal(
            new TextFact("Waiting for", "A reply to the echo"),
            new TextFact("Status", "Failed"),
            new TextFact("Reason", "None"));
    }

    [Fact]
    public async Task A_record_restored_after_a_cancel_that_nothing_will_process_waits_for_cancel_or_a_correction()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var id = await TextRecordAsync(db, Now.AddDays(-3));
        await CancelAsync(db, id, Now.AddDays(-3).AddMinutes(1));
        var restoredAt = Now.AddDays(-3).AddMinutes(2);
        await RestoreAsync(db, id, restoredAt);

        var finding = (await AllAsync(db)).Should().ContainSingle().Which;

        finding.Facts.Should().Equal(
            new TextFact("Waiting for", "Cancel, or a correction reply"),
            new TextFact("Status", "Captured"),
            new SinceFact("Idle for", restoredAt),
            new DateFact("Date", Day));
    }

    [Fact]
    public async Task A_transfer_names_its_source_leg_as_the_amount()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var eur = await WalletAsync(db, "Cash EUR", CurrencyCode.Eur);
        var rsd = await WalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var id = await TextRecordAsync(
            db, Now.AddDays(-5), TransactionStatus.Completed, kind: TransactionKind.Transfer, walletId: eur,
            rawText: "exchanged 100 eur for 11700");
        await TransferAsync(db, id, eur, new Money(100.00m, CurrencyCode.Eur), rsd, new Money(11700.00m, CurrencyCode.Rsd));
        await JobAsync(db, id, JobKind.Correct, JobStatus.Failed, Now.AddDays(-3), lastError: "The model call failed.");

        var finding = (await AllAsync(db)).Should().ContainSingle().Which;

        finding.Facts.OfType<MoneyFact>().Should().Equal(new MoneyFact("Amount", 100.00m, CurrencyCode.Eur));
    }

    // Demo data or a clock change can put any part of a record's clock ahead of now.
    [Fact]
    public async Task A_record_whose_clock_lies_in_the_future_is_never_waiting()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await TextRecordAsync(db, Now.AddDays(1), TransactionStatus.Failed);
        var retriedLater = await TextRecordAsync(db, Now.AddDays(-3), TransactionStatus.Failed);
        await JobAsync(db, retriedLater, JobKind.Categorize, JobStatus.Failed, Now.AddDays(-3), updatedAt: Now.AddDays(1));

        (await AllAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_retry_an_hour_ago_restarts_the_wait()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var id = await TextRecordAsync(db, Now.AddDays(-3), TransactionStatus.Failed);
        await JobAsync(db, id, JobKind.Categorize, JobStatus.Failed, Now.AddDays(-3), updatedAt: Now.AddHours(-1));

        (await AllAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task Records_nothing_waits_on_and_records_I3_owns_are_not_waiting()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var completed = await TextRecordAsync(db, Now.AddDays(-3), TransactionStatus.Completed);
        await JobAsync(db, completed, JobKind.Categorize, JobStatus.Succeeded, Now.AddDays(-3));
        await TextRecordAsync(db, Now.AddDays(-3));
        await TextRecordAsync(db, Now.AddDays(-3), TransactionStatus.Cancelled);

        (await AllAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_scope_for_one_record_sees_only_that_record()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await TextRecordAsync(db, Now.AddDays(-3), TransactionStatus.Failed);
        var second = await TextRecordAsync(db, Now.AddDays(-2), TransactionStatus.Failed);

        var findings = await NewCheck(db, Now).FindAsync(IntegrityScope.For(second), Ct);

        findings.Select(finding => finding.TransactionId).Should().Equal(second);
    }

    // As for I-3: the whole integrity run lives inside one health check's 5 s, so the per-candidate calls stop as soon
    // as the token is cancelled.
    [Fact]
    public async Task The_awaiting_calls_stop_once_the_token_is_cancelled()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await ReceiptWithoutCategorizationAsync(db, Now.AddDays(-3), ReceiptSource.Vision);
        await ReceiptWithoutCategorizationAsync(db, Now.AddDays(-2), ReceiptSource.Vision);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var receipts = Substitute.For<AppReceipts.IReceiptStore>();
        receipts.IsAwaitingConfirmationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromResult(false);
            });
        var check = new NotAppliedCheck(db, receipts, new FakeTimeProvider(Now));

        var act = () => check.FindAsync(IntegrityScope.All, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await receipts.Received(1).IsAwaitingConfirmationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
