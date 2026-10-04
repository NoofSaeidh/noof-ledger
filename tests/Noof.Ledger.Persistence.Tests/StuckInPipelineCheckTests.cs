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
public class StuckInPipelineCheckTests(PostgresFixture fixture)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static StuckInPipelineCheck NewCheck(LedgerDbContext db, DateTimeOffset now)
    {
        var clock = new FakeTimeProvider(now);
        return new StuckInPipelineCheck(db, new EfReceiptStore(db, clock), clock);
    }

    static Task<IReadOnlyList<IntegrityFinding>> AllAsync(LedgerDbContext db) =>
        NewCheck(db, Now).FindAsync(IntegrityScope.All, Ct);

    [Fact]
    public async Task A_captured_record_with_no_job_idle_over_ten_minutes_is_one_bug_with_its_facts()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var capturedAt = Now.AddMinutes(-11);
        var id = await TextRecordAsync(db, capturedAt);

        var finding = (await AllAsync(db)).Should().ContainSingle().Which;

        finding.Check.Should().Be(IntegrityCheck.StuckInPipeline);
        finding.Group.Should().Be(IntegrityGroup.Bug);
        finding.TransactionId.Should().Be(id);
        finding.WalletId.Should().BeNull();
        finding.JobId.Should().BeNull();
        finding.Facts.Should().Equal(
            new TextFact("Status", "Captured"),
            new SinceFact("Idle for", capturedAt),
            new TextFact("Jobs", "none"));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(9)]
    public async Task A_captured_record_idle_for_ten_minutes_or_less_is_not_stuck_yet(int minutes)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await TextRecordAsync(db, Now.AddMinutes(-minutes));

        (await AllAsync(db)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Claimed)]
    [InlineData(JobStatus.Failed)]
    public async Task A_queued_running_or_failed_job_means_the_record_is_not_stuck(JobStatus status)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var id = await TextRecordAsync(db, Now.AddHours(-1));
        await JobAsync(db, id, JobKind.Categorize, status, Now.AddHours(-1));

        (await AllAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task Succeeded_jobs_are_listed_in_creation_order_and_the_latest_change_is_the_clock()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var id = await TextRecordAsync(db, Now.AddHours(-2));
        await JobAsync(db, id, JobKind.Categorize, JobStatus.Succeeded, Now.AddHours(-2), Now.AddHours(-2).AddMinutes(1));
        await JobAsync(db, id, JobKind.Correct, JobStatus.Succeeded, Now.AddMinutes(-90), Now.AddHours(-1));

        var finding = (await AllAsync(db)).Should().ContainSingle().Which;

        finding.Facts.Should().Equal(
            new TextFact("Status", "Captured"),
            new SinceFact("Idle for", Now.AddHours(-1)),
            new TextFact("Jobs", "Categorize Succeeded, Correct Succeeded"));
    }

    [Fact]
    public async Task A_job_changed_a_few_minutes_ago_restarts_the_wait()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var id = await TextRecordAsync(db, Now.AddHours(-1));
        await JobAsync(db, id, JobKind.Categorize, JobStatus.Succeeded, Now.AddHours(-1), Now.AddMinutes(-5));

        (await AllAsync(db)).Should().BeEmpty();
    }

    // Demo data or a clock change can put a record's clock ahead of now.
    [Fact]
    public async Task A_record_whose_clock_lies_in_the_future_is_never_stuck()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await TextRecordAsync(db, Now.AddDays(1));

        (await AllAsync(db)).Should().BeEmpty();
    }

    // P-5: Restore never queues work, so a cancelled-then-restored record is I-4's, not a write-path bug.
    [Fact]
    public async Task A_record_ever_cancelled_is_never_stuck()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var id = await TextRecordAsync(db, Now.AddDays(-3));
        await CancelAsync(db, id, Now.AddDays(-3).AddMinutes(1));
        await RestoreAsync(db, id, Now.AddDays(-3).AddMinutes(2));

        (await AllAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_held_vision_receipt_is_not_stuck_but_a_fiscal_receipt_left_without_its_job_is()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await ReceiptWithoutCategorizationAsync(db, Now.AddHours(-2), ReceiptSource.Vision);
        var fiscal = await ReceiptWithoutCategorizationAsync(db, Now.AddHours(-1), ReceiptSource.FiscalQr);

        var finding = (await AllAsync(db)).Should().ContainSingle().Which;

        finding.TransactionId.Should().Be(fiscal, "a fiscal QR receipt never waits for confirmation");
        finding.Facts.Should().Contain(new TextFact("Jobs", "ExtractReceipt Succeeded"));
    }

    [Theory]
    [InlineData(AppReceipts.SlipDisposition.Hold)]
    [InlineData(AppReceipts.SlipDisposition.Incomplete)]
    [InlineData(AppReceipts.SlipDisposition.Record)]
    public async Task No_exchange_slip_is_stuck_whatever_its_disposition(AppReceipts.SlipDisposition disposition)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SlipAsync(db, Now.AddDays(-2), disposition);

        (await AllAsync(db)).Should().BeEmpty("held waits for Record anyway, incomplete is Failed, a ready one has its job");
    }

    [Theory]
    [InlineData(TransactionStatus.Completed)]
    [InlineData(TransactionStatus.Failed)]
    [InlineData(TransactionStatus.Cancelled)]
    public async Task Only_a_captured_record_can_be_stuck(TransactionStatus status)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await TextRecordAsync(db, Now.AddDays(-2), status);

        (await AllAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_scope_for_one_record_sees_only_that_record()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await TextRecordAsync(db, Now.AddHours(-2));
        var second = await TextRecordAsync(db, Now.AddHours(-1));

        var findings = await NewCheck(db, Now).FindAsync(IntegrityScope.For(second), Ct);

        findings.Select(finding => finding.TransactionId).Should().Equal(second);
    }

    // The whole integrity run lives inside one health check's 5 s, so the per-candidate calls stop as soon as the
    // token is cancelled.
    [Fact]
    public async Task The_awaiting_calls_stop_once_the_token_is_cancelled()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await ReceiptWithoutCategorizationAsync(db, Now.AddHours(-2), ReceiptSource.Vision);
        await ReceiptWithoutCategorizationAsync(db, Now.AddHours(-1), ReceiptSource.Vision);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var receipts = Substitute.For<AppReceipts.IReceiptStore>();
        receipts.IsAwaitingConfirmationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromResult(false);
            });
        var check = new StuckInPipelineCheck(db, receipts, new FakeTimeProvider(Now));

        var act = () => check.FindAsync(IntegrityScope.All, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await receipts.Received(1).IsAwaitingConfirmationAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
