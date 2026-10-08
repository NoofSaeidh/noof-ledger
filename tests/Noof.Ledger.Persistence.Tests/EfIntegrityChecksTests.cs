using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Diagnostics.Integrity;
using Noof.Ledger.Persistence.Receipts;
using static Noof.Ledger.Persistence.Tests.IntegritySeed;
using AppReceipts = Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfIntegrityChecksTests(PostgresFixture fixture)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static EfIntegrityChecks ChecksOn(LedgerDbContext db) => new(db, new EfReceiptStore(db, Clock), Clock);

    static EfIntegrityChecks ChecksAt(LedgerDbContext db, DateTimeOffset now)
    {
        var clock = new FakeTimeProvider(now);
        return new EfIntegrityChecks(db, new EfReceiptStore(db, clock), clock);
    }

    // A transfers row on a spending: an I-2 fault whose legs I-1 also expects as postings nothing made.
    static async Task<Guid> SpendingWithLegsAsync(LedgerDbContext db)
    {
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var spending = await RecordAsync(db, Expense(MainWalletId, Line(250m, CurrencyCode.Rsd)));
        await BreakAsync(db, $"""
            INSERT INTO transfers (transaction_id, from_wallet_id, to_wallet_id, from_amount, from_currency, to_amount, to_currency)
            VALUES ({spending}, {MainWalletId}, {cash}, 100, 'RSD', 100, 'RSD')
            """);
        return spending;
    }

    [Fact]
    public async Task AddNoofPersistence_registers_the_checks_and_an_empty_ledger_has_no_finding()
    {
        var connectionString = await fixture.CreateDatabaseConnectionStringAsync();

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddNoofPersistence(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ledger"] = connectionString })
                .Build(),
            maxJobAttempts: 8);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var checks = scope.ServiceProvider.GetRequiredService<IIntegrityChecks>();

        checks.Should().BeOfType<EfIntegrityChecks>();
        (await checks.FindAllAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_fault_in_a_records_facts_hides_the_disagreeing_postings_it_causes()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var spending = await SpendingWithLegsAsync(db);

        (await new PostingsDisagreeCheck(db).FindAsync(IntegrityScope.All, Ct)).Should().ContainSingle(
            "without the precedence rule this one defect would be a Bug of both checks");
        (await ChecksOn(db).FindAllAsync(Ct)).Select(finding => (finding.Check, finding.TransactionId))
            .Should().Equal((IntegrityCheck.FactsMismatchKind, (Guid?)spending));
    }

    [Fact]
    public async Task Findings_come_in_check_order_then_by_when_the_record_was_captured()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var reasonOnCompleted = await RecordAsync(db, Expense(MainWalletId, Line(100m, CurrencyCode.Rsd)), new DateOnly(2026, 9, 10));
        await BreakAsync(db, $"UPDATE transactions SET failure_reason = 1 WHERE id = {reasonOnCompleted}");
        var later = await RecordAsync(db, Expense(MainWalletId, Line(300m, CurrencyCode.Rsd)), new DateOnly(2026, 9, 12));
        await BreakAsync(db, $"UPDATE entries SET amount = -299 WHERE transaction_id = {later}");
        var earlier = await RecordAsync(db, Expense(MainWalletId, Line(200m, CurrencyCode.Rsd)), new DateOnly(2026, 9, 11));
        await BreakAsync(db, $"DELETE FROM entries WHERE transaction_id = {earlier}");

        (await ChecksOn(db).FindAllAsync(Ct)).Select(finding => (finding.Check, finding.TransactionId)).Should().Equal(
            (IntegrityCheck.PostingsDisagree, (Guid?)earlier),
            (IntegrityCheck.PostingsDisagree, (Guid?)later),
            (IntegrityCheck.FactsMismatchKind, (Guid?)reasonOnCompleted));
    }

    [Fact]
    public async Task FindForTransactionAsync_sees_only_its_record_and_keeps_the_precedence()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var shapeFault = await SpendingWithLegsAsync(db);
        var postingsFault = await RecordAsync(db, Expense(MainWalletId, Line(200m, CurrencyCode.Rsd)));
        await BreakAsync(db, $"DELETE FROM entries WHERE transaction_id = {postingsFault}");

        (await ChecksOn(db).FindForTransactionAsync(postingsFault, Ct)).Select(finding => (finding.Check, finding.TransactionId))
            .Should().Equal((IntegrityCheck.PostingsDisagree, (Guid?)postingsFault));
        (await ChecksOn(db).FindForTransactionAsync(shapeFault, Ct)).Select(finding => (finding.Check, finding.TransactionId))
            .Should().Equal((IntegrityCheck.FactsMismatchKind, (Guid?)shapeFault));
    }

    // P-2: I-2 runs first, so a Captured record holding a failure reason shows that fault, not also I-3's.
    [Fact]
    public async Task The_waiting_checks_follow_the_bug_checks_and_a_record_keeps_only_its_earliest_checks_finding()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var now = PipelineSeed.Now;
        var misreasoned = await PipelineSeed.TextRecordAsync(db, now.AddHours(-3), reason: RecordFailureReason.MissingReceivedAmount);
        var stuck = await PipelineSeed.TextRecordAsync(db, now.AddHours(-2));
        var waiting = await PipelineSeed.TextRecordAsync(
            db, now.AddDays(-2), TransactionStatus.Failed, RecordFailureReason.MissingReceivedAmount);

        var findings = await ChecksAt(db, now).FindAllAsync(Ct);

        findings.Select(finding => (finding.Check, finding.TransactionId)).Should().Equal(
            (IntegrityCheck.FactsMismatchKind, (Guid?)misreasoned),
            (IntegrityCheck.StuckInPipeline, (Guid?)stuck),
            (IntegrityCheck.NotApplied, (Guid?)waiting));
    }

    // Spec Testing: Failed → Cancel → Restore gives no Bug; a held receipt and a held slip give an I-4 finding only
    // while Captured, never a Bug. A slip cancelled while it was read keeps its outcome: an incomplete one restores as
    // Failed (EfRecordEditor.RestoreAsync), so its stored SlipIncomplete is never I-2's reason on a Captured record.
    [Theory]
    [InlineData("failed record", "A reply to the echo")]
    [InlineData("failed record waiting for its amount", "A reply to the echo")]
    [InlineData("held receipt", "Record anyway")]
    [InlineData("held slip", "Record anyway")]
    [InlineData("held slip saved while cancelled", "Record anyway")]
    [InlineData("incomplete slip saved while cancelled", "A reply to the echo")]
    public async Task A_record_cancelled_and_restored_is_never_a_bug(string record, string waitingFor)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var now = PipelineSeed.Now;
        var capturedAt = now.AddDays(-3);
        var cancelledWhileRead = capturedAt.AddSeconds(30);
        var id = record switch
        {
            "failed record" => await PipelineSeed.TextRecordAsync(db, capturedAt, TransactionStatus.Failed),
            "failed record waiting for its amount" => await PipelineSeed.TextRecordAsync(
                db, capturedAt, TransactionStatus.Failed, RecordFailureReason.MissingReceivedAmount),
            "held receipt" => await PipelineSeed.ReceiptWithoutCategorizationAsync(db, capturedAt, ReceiptSource.Vision),
            "held slip" => await PipelineSeed.SlipAsync(db, capturedAt, AppReceipts.SlipDisposition.Hold),
            "held slip saved while cancelled" => await PipelineSeed.SlipAsync(
                db, capturedAt, AppReceipts.SlipDisposition.Hold, cancelledBeforeExtractionAt: cancelledWhileRead),
            _ => await PipelineSeed.SlipAsync(
                db, capturedAt, AppReceipts.SlipDisposition.Incomplete, cancelledBeforeExtractionAt: cancelledWhileRead),
        };
        if (!record.EndsWith("saved while cancelled", StringComparison.Ordinal))
            await PipelineSeed.CancelAsync(db, id, now.AddHours(-2));

        var whileCancelled = await ChecksAt(db, now.AddDays(1)).FindAllAsync(Ct);
        await PipelineSeed.RestoreAsync(db, id, now.AddHours(-1));
        var soon = await ChecksAt(db, now).FindAllAsync(Ct);
        var later = await ChecksAt(db, now.AddDays(1)).FindAllAsync(Ct);

        whileCancelled.Should().BeEmpty("Cancel clears every I-4 finding on its record");
        soon.Should().BeEmpty("its wait restarted an hour ago");
        var finding = later.Should().ContainSingle().Which;
        (finding.Check, finding.TransactionId).Should().Be((IntegrityCheck.NotApplied, (Guid?)id));
        finding.Facts[0].Should().Be(new TextFact("Waiting for", waitingFor));
    }

    [Fact]
    public async Task FindForTransactionAsync_runs_the_waiting_checks_for_that_record_only()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var now = PipelineSeed.Now;
        await PipelineSeed.TextRecordAsync(db, now.AddHours(-2));
        var waiting = await PipelineSeed.TextRecordAsync(db, now.AddDays(-2), TransactionStatus.Failed);

        var findings = await ChecksAt(db, now).FindForTransactionAsync(waiting, Ct);

        findings.Select(finding => (finding.Check, finding.TransactionId)).Should().Equal((IntegrityCheck.NotApplied, (Guid?)waiting));
    }

    // SystemHealth's 5 s timeout can only cancel; a cancelled run must send nothing more.
    [Fact]
    public async Task A_cancelled_token_stops_the_run_before_any_query()
    {
        var connectionString = await fixture.CreateDatabaseConnectionStringAsync();
        var counter = new CountingReaderInterceptor();
        await using var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(counter)
            .Options);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => ChecksAt(db, PipelineSeed.Now).FindAllAsync(cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        counter.Readers.Should().Be(0);
    }

    // One run reads one snapshot: the operator presses Record anyway while a health run is under way. Committed after
    // I-3 read its candidates and before it asked IsAwaitingConfirmationAsync, the new job would make that predicate
    // answer false and the held receipt a Bug - unless the whole run reads the snapshot it started from.
    [Fact]
    public async Task Record_anyway_pressed_during_a_run_never_turns_the_held_receipt_into_a_bug()
    {
        var connectionString = await fixture.CreateDatabaseConnectionStringAsync();
        var plain = new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options;
        Guid held;
        await using (var seeding = new LedgerDbContext(plain))
            held = await PipelineSeed.ReceiptWithoutCategorizationAsync(seeding, PipelineSeed.Now.AddHours(-2), ReceiptSource.Vision);

        var recordAnyway = new CommitOnceAfterReaderInterceptor("AS \"AwaitingCandidate\"", async () =>
        {
            await using var pressing = new LedgerDbContext(plain);
            await PipelineSeed.JobAsync(pressing, held, JobKind.CategorizeReceipt, JobStatus.Pending, PipelineSeed.Now);
        });
        await using var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(recordAnyway)
            .Options);

        var findings = await ChecksAt(db, PipelineSeed.Now).FindAllAsync(Ct);

        recordAnyway.Fired.Should().BeTrue("the job must be committed in the middle of the run, or this proves nothing");
        findings.Should().BeEmpty("the run judges the receipt on the snapshot it started from: held for Record anyway");
    }

    [Fact]
    public async Task A_run_inside_the_callers_transaction_joins_it()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await using var callers = await db.Database.BeginTransactionAsync(Ct);

        var findings = await ChecksAt(db, PipelineSeed.Now).FindAllAsync(Ct);

        findings.Should().BeEmpty();
        db.Database.CurrentTransaction.Should().BeSameAs(callers);
    }
}
