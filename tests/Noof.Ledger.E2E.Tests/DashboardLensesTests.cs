using System.Globalization;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.E2E.Tests;

// One clone per class (CookieModeHostFixture), shared by every test here and seeded at the real clock: "This month"
// holds what the other tests of this class seeded too, so a test compares before with after, or looks for its own
// rows, and never asserts an absolute total.
public sealed class DashboardLensesTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    const string TimeZone = "Europe/Belgrade";
    static readonly Guid GroceriesId = new("00000000-0000-0000-0001-000000000001");
    static readonly Guid SalaryId = new("00000000-0000-0000-0001-000000000022");
    static readonly Guid FeesAndChargesId = new("00000000-0000-0000-0001-000000000013");

    [Fact]
    public async Task This_month_moves_only_by_a_transfers_fee_and_the_transfer_is_listed_on_its_own()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var cancellationToken = TestContext.Current.CancellationToken;
        var marker = Guid.NewGuid().ToString("N")[..8];
        var cashEur = await SeedWalletAsync($"Cash EUR {marker}", CurrencyCode.Eur);
        var cashRsd = await SeedWalletAsync($"Cash RSD {marker}", CurrencyCode.Rsd);
        var now = DateTimeOffset.UtcNow;

        await using (var db = OpenDb())
        {
            _ = AddRecord(db, cashRsd, TransactionKind.Expense, $"market {marker}", now.AddMinutes(-2), new Money(2500.00m, CurrencyCode.Rsd), GroceriesId);
            _ = AddRecord(db, cashEur, TransactionKind.Income, $"salary {marker}", now.AddMinutes(-1), new Money(2800.00m, CurrencyCode.Eur), SalaryId);
            await db.SaveChangesAsync(cancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator("#month-received-EUR")).ToContainTextAsync("Salary");
        var spentEurBefore = await TotalsAsync("#month-totals-EUR");
        var spentRsdBefore = await TotalsAsync("#month-totals-RSD");
        var receivedEurBefore = await TotalsAsync("#month-received-EUR");
        var receivedRsdBefore = await TotalsAsync("#month-received-RSD");

        // 100 EUR out of Cash EUR; 11 550 RSD into Cash RSD after a 150 RSD fee taken on that side (11 700 at 117).
        var exchangeId = Guid.NewGuid();
        await using (var db = OpenDb())
        {
            AddTransfer(db, exchangeId, cashEur, new Money(100.00m, CurrencyCode.Eur), cashRsd, new Money(11550.00m, CurrencyCode.Rsd),
                $"exchange {marker}", now, fee: new Money(150.00m, CurrencyCode.Rsd), feeLeg: TransferLeg.To);
            // A principal line the write path never leaves on a transfer (spec §2): This month filters by role, so a
            // report that trusted the kind instead would count these 100 EUR as Groceries.
            db.LineItems.Add(new LineItem
            {
                Id = Guid.NewGuid(),
                TransactionId = exchangeId,
                Description = $"stray principal {marker}",
                Amount = new Money(100.00m, CurrencyCode.Eur),
                CategoryId = GroceriesId,
                CategorizedBy = CategorizationAuthority.Model,
                MerchantId = null,
                Ordinal = 2,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator($"#month-transfer-{exchangeId}")).ToContainTextAsync(
            $"Cash EUR {marker} → Cash RSD {marker} · 100.00 EUR → 11,550.00 RSD · 1 EUR = 117.0000 RSD");
        await Expect(Page.Locator($"#month-transfer-{exchangeId}")).ToContainTextAsync($"Fee 150.00 RSD from Cash RSD {marker}");

        (await TotalsAsync("#month-totals-EUR")).Should().BeEquivalentTo(spentEurBefore,
            "a transfer's principal is never spending (T-9, acceptance 6)");
        (await TotalsAsync("#month-received-EUR")).Should().BeEquivalentTo(receivedEurBefore, "nor income");
        (await TotalsAsync("#month-received-RSD")).Should().BeEquivalentTo(receivedRsdBefore, "nor income");
        var spentRsdAfter = await TotalsAsync("#month-totals-RSD");
        spentRsdAfter.GetValueOrDefault("Fees & Charges").Should().Be(spentRsdBefore.GetValueOrDefault("Fees & Charges") + 150.00m,
            "the fee the transfer cost is the one part of it that is spending (T-5)");
        spentRsdAfter.Where(total => total.Key != "Fees & Charges")
            .Should().BeEquivalentTo(spentRsdBefore.Where(total => total.Key != "Fees & Charges"));
    }

    [Fact]
    public async Task A_transfers_fee_is_spending_in_fees_and_charges_and_is_listed_with_the_transfer()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var cancellationToken = TestContext.Current.CancellationToken;
        var marker = Guid.NewGuid().ToString("N")[..8];
        var raiffeisen = await SeedWalletAsync($"Raiffeisen {marker}", CurrencyCode.Rsd);
        var cash = await SeedWalletAsync($"Cash RSD {marker}", CurrencyCode.Rsd);
        var withdrawalId = Guid.NewGuid();

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator("#month-transfers")).ToBeVisibleAsync();
        var rsdFeesBefore = await TransferFeesAsync(CurrencyCode.Rsd);

        await using (var db = OpenDb())
        {
            AddTransfer(db, withdrawalId, raiffeisen, new Money(10150.00m, CurrencyCode.Rsd), cash, new Money(10000.00m, CurrencyCode.Rsd),
                $"withdrawal {marker}", DateTimeOffset.UtcNow, fee: new Money(150.00m, CurrencyCode.Rsd));
            // A second RSD fee of its own, so the per-currency sum is proved here, not only when another test of
            // this class already left a fee in the shared clone.
            AddTransfer(db, Guid.NewGuid(), raiffeisen, new Money(5050.00m, CurrencyCode.Rsd), cash, new Money(5000.00m, CurrencyCode.Rsd),
                $"withdrawal 2 {marker}", DateTimeOffset.UtcNow, fee: new Money(50.00m, CurrencyCode.Rsd));
            await db.SaveChangesAsync(cancellationToken);
        }

        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator("#month-totals-RSD")).ToContainTextAsync("Fees & Charges");
        await Expect(Page.Locator($"#month-transfer-{withdrawalId}")).ToContainTextAsync(
            $"Raiffeisen {marker} → Cash RSD {marker} · 10,150.00 RSD → 10,000.00 RSD");
        await Expect(Page.Locator($"#month-transfer-{withdrawalId}")).ToContainTextAsync($"Fee 150.00 RSD from Raiffeisen {marker}");
        (await TransferFeesAsync(CurrencyCode.Rsd)).Should().Be(rsdFeesBefore + 200.00m,
            "the section's fee line sums every transfer's fee per currency");
    }

    [Fact]
    public async Task The_recent_view_links_show_spending_and_income_transfers_or_everything()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var cancellationToken = TestContext.Current.CancellationToken;
        var marker = Guid.NewGuid().ToString("N")[..8];
        var wise = await SeedWalletAsync($"Wise {marker}", CurrencyCode.Eur);
        var cash = await SeedWalletAsync($"Cash EUR {marker}", CurrencyCode.Eur);
        var now = DateTimeOffset.UtcNow;
        var withdrawalId = Guid.NewGuid();
        var failedTransferId = Guid.NewGuid();
        Guid expenseId;

        await using (var db = OpenDb())
        {
            expenseId = AddRecord(db, wise, TransactionKind.Expense, $"lunch {marker}", now.AddMinutes(-1),
                new Money(12.00m, CurrencyCode.Eur), GroceriesId);
            db.LineItems.Add(new LineItem
            {
                Id = Guid.NewGuid(),
                TransactionId = expenseId,
                Description = "Bank charge",
                Amount = new Money(0.50m, CurrencyCode.Eur),
                CategoryId = FeesAndChargesId,
                CategorizedBy = CategorizationAuthority.Rule,
                MerchantId = null,
                Ordinal = 2,
                Role = EntryRole.Fee,
            });
            AddTransfer(db, withdrawalId, wise, new Money(200.00m, CurrencyCode.Eur), cash, new Money(200.00m, CurrencyCode.Eur),
                $"withdrawal {marker}", now);
            // A transfer whose first reading failed has its kind but no transfers row, so the row has no legs to show.
            var failedTransfer = NewTransaction(failedTransferId, wise, TransactionKind.Transfer, $"moved some {marker}", now.AddMinutes(-2));
            failedTransfer.Status = TransactionStatus.Failed;
            failedTransfer.FailureReason = RecordFailureReason.MissingReceivedAmount;
            db.Transactions.Add(failedTransfer);
            await db.SaveChangesAsync(cancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator($"#txn-{expenseId}")).ToContainTextAsync("Spending");
        await Expect(Page.Locator($"#txn-{expenseId}")).ToContainTextAsync("Bank charge · fee",
            new() { Timeout = 10_000 });
        await Expect(Page.Locator($"#txn-{withdrawalId}")).ToHaveCountAsync(0);
        await Expect(Page.Locator($"#txn-{failedTransferId}")).ToHaveCountAsync(0);
        await Expect(Page.Locator("#recent-view-spending")).ToHaveAttributeAsync("aria-current", "page");

        await Page.ClickAsync("#recent-view-transfers");
        await Expect(Page).ToHaveURLAsync(new Regex(@"/\?view=transfers$"));
        await Expect(Page.Locator($"#txn-{withdrawalId}")).ToContainTextAsync(
            $"Wise {marker} → Cash EUR {marker} · 200.00 EUR → 200.00 EUR");
        await Expect(Page.Locator($"#txn-{withdrawalId}")).ToContainTextAsync("Transfer");
        await Expect(Page.Locator($"#txn-{failedTransferId}")).ToContainTextAsync("Categorisation failed. Nothing was recorded");
        await Expect(Page.Locator($"#txn-{failedTransferId}")).Not.ToContainTextAsync("→");
        await Expect(Page.Locator($"#txn-{expenseId}")).ToHaveCountAsync(0);
        await Expect(Page.Locator("#recent-view-transfers")).ToHaveAttributeAsync("aria-current", "page");

        await Page.GotoAsync(fixture.BaseUrl + "/?view=all");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator($"#txn-{withdrawalId}")).ToBeVisibleAsync();
        await Expect(Page.Locator($"#txn-{expenseId}")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Income_and_a_balance_statement_are_marked_by_kind_and_never_read_as_spending()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var cancellationToken = TestContext.Current.CancellationToken;
        var marker = Guid.NewGuid().ToString("N")[..8];
        var wallet = await SeedWalletAsync($"Raiffeisen {marker}", CurrencyCode.Rsd);
        var now = DateTimeOffset.UtcNow;
        var statementId = Guid.NewGuid();
        Guid salaryId;

        await using (var db = OpenDb())
        {
            salaryId = AddRecord(db, wallet, TransactionKind.Income, $"salary {marker}", now.AddMinutes(-1),
                new Money(150000.00m, CurrencyCode.Rsd), SalaryId);
            db.Transactions.Add(NewTransaction(statementId, wallet, TransactionKind.BalanceCheck, $"raif has 172000 {marker}", now));
            await db.SaveChangesAsync(cancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator($"#txn-{statementId}")).ToContainTextAsync("Balance statement");
        await Expect(Page.Locator($"#txn-{statementId}")).ToContainTextAsync("A balance statement - it sets what the wallet holds");
        await Expect(Page.Locator($"#txn-{statementId}")).Not.ToContainTextAsync("Read - nothing here looked like spending");
        await Expect(Page.Locator($"#txn-{salaryId}")).ToContainTextAsync("Income");
    }

    async Task<Guid> SeedWalletAsync(string name, CurrencyCode currency)
    {
        var walletId = Guid.NewGuid();
        await using var db = OpenDb();
        db.Wallets.Add(new Wallet
        {
            Id = walletId,
            Name = name,
            Currency = currency,
            Aliases = [],
            IsDefaultForCurrency = false,
            Archived = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return walletId;
    }

    static Transaction NewTransaction(Guid id, Guid walletId, TransactionKind kind, string rawText, DateTimeOffset occurredAt) => new()
    {
        Id = id,
        WalletId = walletId,
        RawText = rawText,
        CaptureKind = CaptureKind.Manual,
        Kind = kind,
        Status = TransactionStatus.Completed,
        TimeZoneId = TimeZone,
        OccurredAt = occurredAt,
        OccurredOn = ZonedClock.LocalDate(occurredAt, TimeZone),
        TelegramChatId = null,
        TelegramMessageId = null,
        CreatedAt = occurredAt,
    };

    static Guid AddRecord(
        LedgerDbContext db, Guid walletId, TransactionKind kind, string rawText, DateTimeOffset occurredAt, Money amount, Guid categoryId)
    {
        var id = Guid.NewGuid();
        db.Transactions.Add(NewTransaction(id, walletId, kind, rawText, occurredAt));
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = id,
            Description = rawText,
            Amount = amount,
            CategoryId = categoryId,
            CategorizedBy = CategorizationAuthority.Model,
            MerchantId = null,
            Ordinal = 1,
        });
        return id;
    }

    // Category → amount of one month table; an absent table (no spending or no income in that currency) is empty.
    async Task<Dictionary<string, decimal>> TotalsAsync(string tableSelector)
    {
        var totals = new Dictionary<string, decimal>();
        foreach (var row in await Page.Locator($"{tableSelector} tr").AllAsync())
        {
            var cells = await row.Locator("td").AllInnerTextsAsync();
            totals[cells[0].Trim()] = decimal.Parse(cells[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture);
        }

        return totals;
    }

    // One currency's figure on the "Fees:" line of Transfers this month; no line, or no fee in that currency, is 0.
    async Task<decimal> TransferFeesAsync(CurrencyCode currency)
    {
        var line = Page.Locator("#month-transfer-fees");
        if (await line.CountAsync() == 0)
            return 0m;

        var match = Regex.Match(await line.InnerTextAsync(), $@"([\d,]+\.\d{{2}}) {currency}\b");
        return match.Success ? decimal.Parse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture) : 0m;
    }

    // The transfers row and the fee line only: Home reads line items and transfers, never entries.
    static void AddTransfer(
        LedgerDbContext db, Guid id, Guid fromWalletId, Money from, Guid toWalletId, Money to, string rawText,
        DateTimeOffset occurredAt, Money? fee = null, TransferLeg feeLeg = TransferLeg.From)
    {
        db.Transactions.Add(NewTransaction(id, fromWalletId, TransactionKind.Transfer, rawText, occurredAt));
        db.Transfers.Add(new Transfer
        {
            TransactionId = id,
            FromWalletId = fromWalletId,
            From = from,
            ToWalletId = toWalletId,
            To = to,
            FeeLeg = fee is null ? null : feeLeg,
        });

        if (fee is { } amount)
        {
            db.LineItems.Add(new LineItem
            {
                Id = Guid.NewGuid(),
                TransactionId = id,
                Description = "Fee",
                Amount = amount,
                CategoryId = FeesAndChargesId,
                CategorizedBy = CategorizationAuthority.Rule,
                MerchantId = null,
                Ordinal = 1,
                Role = EntryRole.Fee,
            });
        }
    }

    LedgerDbContext OpenDb() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    async Task SignInAsync()
    {
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForURLAsync("**/account/login*");
        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(fixture.BaseUrl + "/");
    }
}
