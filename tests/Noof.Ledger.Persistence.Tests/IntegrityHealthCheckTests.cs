using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Diagnostics.Integrity;
using Noof.Ledger.Persistence.Receipts;
using static Noof.Ledger.Persistence.Tests.PipelineSeed;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class IntegrityHealthCheckTests(PostgresFixture fixture)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static IDatabaseGate GateWith(DatabaseState state)
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.State.Returns(state);
        return gate;
    }

    static IIntegrityChecks ChecksFinding(int bugs, int waiting)
    {
        IReadOnlyList<IntegrityFinding> findings =
        [
            .. Enumerable.Range(0, bugs).Select(_ => Finding(IntegrityCheck.PostingsDisagree, IntegrityGroup.Bug)),
            .. Enumerable.Range(0, waiting).Select(_ => Finding(IntegrityCheck.NotApplied, IntegrityGroup.WaitingOnYou)),
        ];
        var checks = Substitute.For<IIntegrityChecks>();
        checks.FindAllAsync(Arg.Any<CancellationToken>()).Returns(findings);
        return checks;
    }

    static IntegrityFinding Finding(IntegrityCheck check, IntegrityGroup group) =>
        new(check, group, Guid.NewGuid(), null, null, []);

    static IntegrityHealthCheck OverTheLedger(LedgerDbContext db)
    {
        var clock = new FakeTimeProvider(Now);
        return new IntegrityHealthCheck(
            new EfIntegrityChecks(db, new EfReceiptStore(db, clock), clock), GateWith(DatabaseState.Ready));
    }

    [Fact]
    public void Reports_its_name_order_and_log_category()
    {
        var check = new IntegrityHealthCheck(ChecksFinding(0, 0), GateWith(DatabaseState.Ready));

        check.Name.Should().Be("Integrity");
        check.Name.Should().Be(IntegrityHealth.CheckName);
        check.Order.Should().Be(90);
        check.LogCategory.Should().Be("Noof.Ledger.Persistence.Diagnostics.Integrity");
    }

    // While PostgreSQL is down every page runs the health checks; this one must not wait out its 5 s.
    [Theory]
    [InlineData(DatabaseState.Waiting)]
    [InlineData(DatabaseState.Migrating)]
    [InlineData(DatabaseState.Failed)]
    public async Task Reports_a_warning_without_running_the_checks_when_the_gate_is_not_ready(DatabaseState state)
    {
        var checks = ChecksFinding(1, 0);
        var check = new IntegrityHealthCheck(checks, GateWith(state));

        var result = await check.CheckAsync(Ct);

        result.Should().Be(HealthOutcome.Warning("Waiting for the database"));
        await checks.DidNotReceiveWithAnyArgs().FindAllAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, 0, HealthLevel.Ok, "No findings")]
    [InlineData(1, 0, HealthLevel.Failing, "1 bug found")]
    [InlineData(2, 3, HealthLevel.Failing, "2 bugs found")]
    [InlineData(0, 1, HealthLevel.Warning, "1 waiting on you")]
    [InlineData(0, 2, HealthLevel.Warning, "2 waiting on you")]
    public async Task A_bug_is_failing_and_anything_waiting_is_a_warning(int bugs, int waiting, HealthLevel level, string summary)
    {
        var check = new IntegrityHealthCheck(ChecksFinding(bugs, waiting), GateWith(DatabaseState.Ready));

        var result = await check.CheckAsync(Ct);

        result.Should().Be(new HealthOutcome(level, summary));
    }

    [Fact]
    public async Task A_finding_in_no_known_group_is_an_error()
    {
        var checks = Substitute.For<IIntegrityChecks>();
        checks.FindAllAsync(Arg.Any<CancellationToken>())
            .Returns([Finding(IntegrityCheck.NotApplied, IntegrityGroup.Unknown)]);

        var run = () => new IntegrityHealthCheck(checks, GateWith(DatabaseState.Ready)).CheckAsync(Ct);

        await run.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Runs_the_checks_under_the_health_runs_own_token()
    {
        var checks = ChecksFinding(0, 0);
        using var run = new CancellationTokenSource();

        await new IntegrityHealthCheck(checks, GateWith(DatabaseState.Ready)).CheckAsync(run.Token);

        await checks.Received(1).FindAllAsync(run.Token);
    }

    // Spec acceptance 1.
    [Fact]
    public async Task An_entry_that_disagrees_with_its_line_turns_integrity_red_and_its_repair_turns_it_green()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wallet = await WalletAsync(db, "Raiffeisen RSD", CurrencyCode.Rsd);
        var id = await TextRecordAsync(db, Now.AddDays(-3), TransactionStatus.Completed, walletId: wallet);
        await LineAsync(db, id, new Money(250.00m, CurrencyCode.Rsd), 1);
        var wrong = await EntryAsync(db, id, wallet, new Money(-200.00m, CurrencyCode.Rsd));
        var check = OverTheLedger(db);

        var broken = await check.CheckAsync(Ct);
        await db.Entries.Where(entry => entry.Id == wrong).ExecuteDeleteAsync(Ct);
        await EntryAsync(db, id, wallet, new Money(-250.00m, CurrencyCode.Rsd));
        var repaired = await check.CheckAsync(Ct);

        broken.Should().Be(HealthOutcome.Failing("1 bug found"));
        repaired.Should().Be(HealthOutcome.Ok("No findings"));
    }

    // Spec acceptance 2.
    [Fact]
    public async Task A_failed_exchange_waiting_a_day_for_its_amount_turns_integrity_amber_and_is_no_bug()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await TextRecordAsync(
            db, Now.AddDays(-2), TransactionStatus.Failed, RecordFailureReason.MissingReceivedAmount, rawText: "exchanged 100 eur");

        var result = await OverTheLedger(db).CheckAsync(Ct);

        result.Should().Be(HealthOutcome.Warning("1 waiting on you"));
    }
}
