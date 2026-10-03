using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Diagnostics.Integrity;
using static Noof.Ledger.Persistence.Tests.IntegritySeed;

namespace Noof.Ledger.Persistence.Tests;

// I-1 over data the write path produced, broken one way per test. A defect is one finding, whatever number of
// (wallet, currency, role) keys it moves.
[Collection("postgres")]
public class PostingsDisagreeCheckTests(PostgresFixture fixture)
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static Task<IReadOnlyList<IntegrityFinding>> FindAsync(LedgerDbContext db) =>
        FindCheckingScopeAsync(new PostingsDisagreeCheck(db));

    [Fact]
    public async Task Records_as_the_write_path_leaves_them_have_no_finding()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedEveryKindAsync(db);

        (await FindAsync(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_entry_is_one_finding_naming_what_was_expected()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var spending = await RecordAsync(db, Expense(MainWalletId, Line(250m, CurrencyCode.Rsd)));
        await BreakAsync(db, $"DELETE FROM entries WHERE transaction_id = {spending}");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.PostingsDisagree, spending, MainWalletId,
            new TextFact("Wallet", MainWalletName), new TextFact("Role", "Principal"),
            new MoneyFact("Expected", -250m, CurrencyCode.Rsd), new MoneyFact("Posted", 0m, CurrencyCode.Rsd));
    }

    [Fact]
    public async Task An_entry_nothing_expects_is_one_finding()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var spending = await RecordAsync(db, Expense(MainWalletId, Line(250m, CurrencyCode.Rsd)));
        await BreakAsync(db, $"""
            INSERT INTO entries (id, transaction_id, wallet_id, amount, currency, role)
            VALUES ({Guid.NewGuid()}, {spending}, {MainWalletId}, -5, 'RSD', 1)
            """);

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.PostingsDisagree, spending, MainWalletId,
            new TextFact("Wallet", MainWalletName), new TextFact("Role", "Fee"),
            new MoneyFact("Expected", 0m, CurrencyCode.Rsd), new MoneyFact("Posted", -5m, CurrencyCode.Rsd));
    }

    [Fact]
    public async Task An_entry_of_the_wrong_amount_is_one_finding()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var spending = await RecordAsync(db, Expense(MainWalletId, Line(250m, CurrencyCode.Rsd)));
        await BreakAsync(db, $"UPDATE entries SET amount = -200 WHERE transaction_id = {spending}");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.PostingsDisagree, spending, MainWalletId,
            new TextFact("Wallet", MainWalletName), new TextFact("Role", "Principal"),
            new MoneyFact("Expected", -250m, CurrencyCode.Rsd), new MoneyFact("Posted", -200m, CurrencyCode.Rsd));
    }

    [Fact]
    public async Task A_transfer_fee_posted_on_the_other_leg_is_one_finding_naming_both_wallets()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var withdrawal = await RecordAsync(db, TransferOutcome(
            new TransferFacts(MainWalletId, Rsd(10150m), cash, Rsd(10000m), Rsd(150m), TransferLeg.From, null)));
        await BreakAsync(db, $"UPDATE entries SET wallet_id = {cash} WHERE transaction_id = {withdrawal} AND role = 1");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.PostingsDisagree, withdrawal, MainWalletId,
            new TextFact("Wallet", "Cash RSD"), new TextFact("Role", "Fee"),
            new MoneyFact("Expected", 0m, CurrencyCode.Rsd), new MoneyFact("Posted", -150m, CurrencyCode.Rsd),
            new TextFact("Wallet", MainWalletName), new TextFact("Role", "Fee"),
            new MoneyFact("Expected", -150m, CurrencyCode.Rsd), new MoneyFact("Posted", 0m, CurrencyCode.Rsd));
    }

    [Fact]
    public async Task An_entry_moved_to_a_wallet_the_record_does_not_use_is_one_finding_that_also_names_it_misplaced()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var spending = await RecordAsync(db, Expense(MainWalletId, Line(250m, CurrencyCode.Rsd)));
        await BreakAsync(db, $"UPDATE entries SET wallet_id = {cash} WHERE transaction_id = {spending}");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.PostingsDisagree, spending, MainWalletId,
            new TextFact("Wallet", "Cash RSD"), new TextFact("Role", "Principal"),
            new MoneyFact("Expected", 0m, CurrencyCode.Rsd), new MoneyFact("Posted", -250m, CurrencyCode.Rsd),
            new TextFact("Wallet", MainWalletName), new TextFact("Role", "Principal"),
            new MoneyFact("Expected", -250m, CurrencyCode.Rsd), new MoneyFact("Posted", 0m, CurrencyCode.Rsd),
            new TextFact("Entry on another wallet", "Cash RSD"), new MoneyFact("Entry", -250m, CurrencyCode.Rsd));
    }

    // The disagreeing rows are a FULL JOIN: an entry nothing expects has no expected side, so the scope must filter on
    // the posted side's record too.
    [Fact]
    public async Task The_scope_keeps_a_record_whose_only_disagreement_is_an_entry_nothing_expects()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var spending = await RecordAsync(db, Expense(MainWalletId, Line(250m, CurrencyCode.Rsd)));
        var sound = await RecordAsync(db, Expense(MainWalletId, Line(80m, CurrencyCode.Rsd)));
        await BreakAsync(db, $"""
            INSERT INTO entries (id, transaction_id, wallet_id, amount, currency, role)
            VALUES ({Guid.NewGuid()}, {spending}, {MainWalletId}, -5, 'RSD', 1)
            """);

        var check = new PostingsDisagreeCheck(db);
        (await check.FindAsync(IntegrityScope.For(spending), Ct)).Should().ContainSingle().Which.TransactionId.Should().Be(spending);
        (await check.FindAsync(IntegrityScope.For(sound), Ct)).Should().BeEmpty();
    }
}
