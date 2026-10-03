using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Diagnostics.Integrity;
using static Noof.Ledger.Persistence.Tests.IntegritySeed;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfIntegrityChecksTests(PostgresFixture fixture)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static EfIntegrityChecks ChecksOn(LedgerDbContext db) => new(db);

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
}
