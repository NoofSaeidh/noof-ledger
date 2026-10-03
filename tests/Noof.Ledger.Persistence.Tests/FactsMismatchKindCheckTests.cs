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

    static Task AddChargeAsync(LedgerDbContext db, Guid transactionId, string currency) =>
        BreakAsync(db, $"""
            INSERT INTO charges (transaction_id, currency, charged_amount, fee_amount, rate_used, source)
            VALUES ({transactionId}, {currency}, 100, 0, 1, 0)
            """);

    static async Task<(Guid Cash, Guid Withdrawal)> WithdrawalAsync(LedgerDbContext db)
    {
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var withdrawal = await RecordAsync(db, TransferOutcome(
            new TransferFacts(MainWalletId, Rsd(10150m), cash, Rsd(10000m), Rsd(150m), TransferLeg.From, null)));
        return (cash, withdrawal);
    }

    [Fact]
    public async Task Charges_on_an_income_are_one_finding_naming_its_kind_and_each_charge()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wise = await AddWalletAsync(db, "Wise EUR", CurrencyCode.Eur);
        var salary = await RecordAsync(db, Income(wise, Line(2000m, CurrencyCode.Eur)));
        await AddChargeAsync(db, salary, "USD");
        await AddChargeAsync(db, salary, "RSD");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, salary, wise,
            Condition("Charge on a record that is not an expense"), new TextFact("Kind", "Income"),
            new TextFact("Charge currency", "RSD"), new TextFact("Charge currency", "USD"));
    }

    [Fact]
    public async Task A_charge_in_the_wallets_own_currency_is_one_finding()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var spending = await RecordAsync(db, Expense(MainWalletId, Line(250m, CurrencyCode.Rsd)));
        await AddChargeAsync(db, spending, "RSD");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, spending, MainWalletId,
            Condition("Charge in the wallet's own currency"), new TextFact("Charge currency", "RSD"));
    }

    [Fact]
    public async Task An_expense_fee_line_in_another_currency_than_its_wallets_is_one_finding()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var spending = await RecordAsync(db, Expense(MainWalletId, Line(250m, CurrencyCode.Rsd)));
        await AddRawLineAsync(db, spending, EntryRole.Fee, 2m, "EUR");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, spending, MainWalletId,
            Condition("Expense fee line in another currency than the wallet's"),
            new TextFact("Fee line currency", "EUR"), new TextFact("Wallet currency", "RSD"));
    }

    // Spec §1: a fee leg has exactly one fee line, not merely at least one.
    [Fact]
    public async Task A_fee_leg_with_a_second_fee_line_is_one_finding()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (_, withdrawal) = await WithdrawalAsync(db);
        await AddRawLineAsync(db, withdrawal, EntryRole.Fee, 10m, "RSD");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, withdrawal, MainWalletId,
            Condition("Fee leg without exactly one fee line"), new TextFact("Fee leg", "From"), new CountFact("Fee lines", 2));
    }

    [Fact]
    public async Task A_fee_leg_without_its_fee_line_is_one_finding()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (_, withdrawal) = await WithdrawalAsync(db);
        await BreakAsync(db, $"DELETE FROM line_items WHERE transaction_id = {withdrawal} AND role = 1");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, withdrawal, MainWalletId,
            Condition("Fee leg without exactly one fee line"), new TextFact("Fee leg", "From"), new CountFact("Fee lines", 0));
    }

    [Fact]
    public async Task A_fee_line_on_a_transfer_without_a_fee_leg_is_one_finding()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (cash, topUp) = await TopUpAsync(db);
        await AddRawLineAsync(db, topUp, EntryRole.Fee, 10m, "RSD");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, topUp, cash,
            Condition("Fee line on a transfer without a fee leg"), new CountFact("Fee lines", 1));
    }

    [Fact]
    public async Task A_transfer_leg_in_another_currency_than_its_wallets_is_one_finding()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (cash, topUp) = await TopUpAsync(db);
        await BreakAsync(db, $"UPDATE transfers SET to_currency = 'EUR' WHERE transaction_id = {topUp}");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, topUp, cash,
            Condition("Transfer leg in another currency than its wallet's"),
            new TextFact("Leg", "To"), new TextFact("Leg currency", "EUR"), new TextFact("Wallet currency", "RSD"));
    }

    [Fact]
    public async Task A_checkpoint_on_another_wallet_than_the_records_is_one_finding()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var statement = await RecordAsync(db, Statement(MainWalletId, 45000m, CurrencyCode.Rsd));
        await BreakAsync(db, $"UPDATE balance_checks SET wallet_id = {cash} WHERE transaction_id = {statement}");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, statement, MainWalletId,
            Condition("Checkpoint on another wallet than the record's"),
            new TextFact("Record wallet", MainWalletName), new TextFact("Checkpoint wallet", "Cash RSD"));
    }

    [Fact]
    public async Task A_transfer_whose_record_has_no_wallet_is_one_finding_naming_none()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (_, topUp) = await TopUpAsync(db);
        await BreakAsync(db, $"UPDATE transactions SET wallet_id = NULL WHERE id = {topUp}");

        (await FindAsync(db)).Should().ContainSingle().Which.ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, topUp, null,
            Condition("Transfer source is not the record's wallet"),
            new TextFact("Record wallet", "(none)"), new TextFact("Source wallet", "Cash RSD"));
    }

    [Fact]
    public async Task A_failure_reason_on_a_completed_or_captured_record_is_one_finding_each()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var spending = await RecordAsync(db, Expense(MainWalletId, Line(250m, CurrencyCode.Rsd)));
        var captured = await CaptureAsync(db, Day.AddDays(1));
        await BreakAsync(db, $"UPDATE transactions SET failure_reason = 1 WHERE id = {spending}");
        await BreakAsync(db, $"UPDATE transactions SET failure_reason = 6 WHERE id = {captured}");

        var findings = await FindAsync(db);

        findings.Should().HaveCount(2);
        findings[0].ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, spending, MainWalletId,
            Condition("Failure reason on a record that is not failed"),
            new TextFact("Status", "Completed"), new TextFact("Failure reason", "MissingReceivedAmount"));
        findings[1].ShouldBeBug(
            IntegrityCheck.FactsMismatchKind, captured, null,
            Condition("Failure reason on a record that is not failed"),
            new TextFact("Status", "Captured"), new TextFact("Failure reason", "SlipIncomplete"));
    }

    // IR-11: Cancel keeps the reason, so the Cancelled echo and Restore can show it.
    [Fact]
    public async Task A_failed_or_cancelled_record_may_keep_its_failure_reason()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var failed = await CaptureAsync(db, Day);
        await MarkFailedAsync(db, failed, RecordFailureReason.MissingReceivedAmount);
        var cancelled = await CaptureAsync(db, Day);
        await MarkFailedAsync(db, cancelled, RecordFailureReason.SameWallet);
        await CancelAsync(db, cancelled);

        (await FindAsync(db)).Should().BeEmpty();
    }
}
