using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Diagnostics.Integrity;
using static Noof.Ledger.Persistence.Tests.IntegritySeed;

namespace Noof.Ledger.Persistence.Tests;

// I-2, one test per condition of spec §1's list: data the write path produced, broken one way.
[Collection("postgres")]
public class FactsMismatchKindCheckTests(PostgresFixture fixture)
{
    static Task<IReadOnlyList<IntegrityFinding>> FindAsync(LedgerDbContext db) =>
        FindCheckingScopeAsync(new FactsMismatchKindCheck(db));

    static TextFact Condition(string text) => new("Condition", text);

    static async Task<(Guid Cash, Guid TopUp)> TopUpAsync(LedgerDbContext db)
    {
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var topUp = await RecordAsync(db, TransferOutcome(new TransferFacts(cash, Rsd(2000m), MainWalletId, Rsd(2000m), null, null, null)));
        return (cash, topUp);
    }

    static Task AddLegsAsync(LedgerDbContext db, Guid transactionId, Guid toWalletId) =>
        BreakAsync(db, $"""
            INSERT INTO transfers (transaction_id, from_wallet_id, to_wallet_id, from_amount, from_currency, to_amount, to_currency)
            VALUES ({transactionId}, {MainWalletId}, {toWalletId}, 100, 'RSD', 100, 'RSD')
            """);

    static Task AddCheckpointAsync(LedgerDbContext db, Guid transactionId) =>
        BreakAsync(db, $"""
            INSERT INTO balance_checks (transaction_id, wallet_id, computed_before, stated_amount, currency)
            VALUES ({transactionId}, {MainWalletId}, 0, 100, 'RSD')
            """);

    [Fact]
    public async Task Records_as_the_write_path_leaves_them_have_no_finding()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedEveryKindAsync(db);

        (await FindAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_transfer_without_its_legs_is_one_finding()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (cash, topUp) = await TopUpAsync(db);
        await BreakAsync(db, $"DELETE FROM transfers WHERE transaction_id = {topUp}");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, topUp, cash, Condition("Transfer without its legs"));
    }

    [Fact]
    public async Task Transfer_legs_on_a_spending_are_one_finding_naming_its_kind()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var spending = await RecordAsync(db, Expense(MainWalletId, Line(250m, CurrencyCode.Rsd)));
        await AddLegsAsync(db, spending, cash);

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, spending, MainWalletId,
            Condition("Transfer legs on a record that is not a transfer"), new TextFact("Kind", "Expense"));
    }

    [Fact]
    public async Task A_transfer_with_a_principal_line_is_one_finding_counting_them()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (cash, topUp) = await TopUpAsync(db);
        await AddRawLineAsync(db, topUp, EntryRole.Principal, 100m, "RSD");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, topUp, cash,
            Condition("Transfer with a principal line"), new CountFact("Principal lines", 1));
    }

    [Fact]
    public async Task A_statement_without_its_checkpoint_is_one_finding()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var statement = await RecordAsync(db, Statement(MainWalletId, 45000m, CurrencyCode.Rsd));
        await BreakAsync(db, $"DELETE FROM balance_checks WHERE transaction_id = {statement}");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, statement, MainWalletId, Condition("Statement without its checkpoint"));
    }

    [Fact]
    public async Task A_checkpoint_on_a_spending_is_one_finding_naming_its_kind()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var spending = await RecordAsync(db, Expense(MainWalletId, Line(250m, CurrencyCode.Rsd)));
        await AddCheckpointAsync(db, spending);

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, spending, MainWalletId,
            Condition("Checkpoint on a record that is not a statement"), new TextFact("Kind", "Expense"));
    }

    [Fact]
    public async Task A_record_with_two_faults_has_one_finding_per_condition_in_the_tables_order()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var spending = await RecordAsync(db, Expense(MainWalletId, Line(250m, CurrencyCode.Rsd)));
        await AddCheckpointAsync(db, spending);
        await AddLegsAsync(db, spending, cash);

        (await FindAsync(db)).Select(finding => finding.Facts[0]).Should().Equal(
            Condition("Transfer legs on a record that is not a transfer"),
            Condition("Checkpoint on a record that is not a statement"));
    }
}
