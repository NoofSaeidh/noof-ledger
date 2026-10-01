using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;

namespace Noof.Ledger.Persistence.Tests;

// Spec §2 "Mapping a foreign-currency spending", through EfCategorizationStore.ApplyAsync on a real clone.
// Every wallet here holds KZT; the spending is in USD or EUR.
[Collection("postgres")]
public class ForeignChargeTests(PostgresFixture fixture)
{
    static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    static readonly Guid GroceriesId = new("00000000-0000-0000-0001-000000000001");
    static readonly Guid CoffeeId = new("00000000-0000-0000-0001-000000000017");
    static readonly Guid FeesChargesId = new("00000000-0000-0000-0001-000000000013");
    static readonly DateOnly Day = new(2026, 9, 30);
    static int nextMessageId = 170_000;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed record ChargeRow(
        string Currency, decimal Charged, decimal Fee, decimal RateUsed,
        decimal? FeePercent, decimal? FeeFixed, decimal? FeeMinimum, ChargeSource Source);

    static async Task<Guid> AddKztWalletAsync(LedgerDbContext db, string name)
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), Name = name, Currency = CurrencyCode.Kzt, CreatedAt = Clock.GetUtcNow() };
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(Ct);
        return wallet.Id;
    }

    static async Task SetTermsAsync(
        LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal rate,
        decimal? feePercent = null, decimal? feeFixed = null, decimal? feeMinimum = null)
    {
        db.WalletFxTerms.Add(new WalletFxTerms
        {
            WalletId = walletId, Currency = currency, Rate = rate,
            FeePercent = feePercent, FeeFixed = feeFixed, FeeMinimum = feeMinimum,
        });
        await db.SaveChangesAsync(Ct);
    }

    static async Task<Guid> CaptureAsync(LedgerDbContext db)
    {
        var sentAt = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            RawText = "30 долларов с каспи",
            Status = TransactionStatus.Captured,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = sentAt,
            OccurredOn = Day,
            TelegramChatId = 1,
            TelegramMessageId = Interlocked.Increment(ref nextMessageId),
            CreatedAt = sentAt,
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(Ct);
        return transaction.Id;
    }

    static CategorizedLineItem Line(decimal amount, CurrencyCode currency, Guid? categoryId = null) =>
        new("line", new Money(amount, currency), categoryId ?? CoffeeId, null);

    static CategorizationOutcome Spending(Guid walletId, params CategorizedLineItem[] lines) =>
        new(lines, Day, TransactionKind: TransactionKind.Expense, WalletId: walletId);

    static CategorizationOutcome Said(
        CategorizationOutcome outcome, decimal amount, CurrencyCode currency, decimal? fee = null, bool feeIncluded = false) =>
        outcome with { Charged = new StatedCharge(new Money(amount, currency), fee, feeIncluded) };

    static CategorizationOutcome AsCorrection(CategorizationOutcome outcome) =>
        outcome with { Kind = JobKind.Correct, Instruction = "correction" };

    static Task ApplyAsync(LedgerDbContext db, Guid transactionId, CategorizationOutcome outcome) =>
        new EfCategorizationStore(db, Clock).ApplyAsync(transactionId, outcome, Ct);

    static async Task<IReadOnlyList<ChargeRow>> ChargesOfAsync(LedgerDbContext db, Guid transactionId)
    {
        var charges = await db.Charges.AsNoTracking().Where(c => c.TransactionId == transactionId).ToListAsync(Ct);
        return [.. charges
            .OrderBy(c => c.Currency.Value, StringComparer.Ordinal)
            .Select(c => new ChargeRow(
                c.Currency.Value, c.ChargedAmount, c.FeeAmount, c.RateUsed, c.FeePercent, c.FeeFixed, c.FeeMinimum, c.Source))];
    }

    static async Task<IReadOnlyList<LineItem>> FeeLinesOfAsync(LedgerDbContext db, Guid transactionId) =>
        await db.LineItems.AsNoTracking()
            .Where(line => line.TransactionId == transactionId && line.Role == EntryRole.Fee)
            .OrderBy(line => line.Ordinal)
            .ToListAsync(Ct);

    static async Task<IReadOnlyList<(Guid WalletId, Money Amount, EntryRole Role)>> EntriesOfAsync(LedgerDbContext db, Guid transactionId)
    {
        var entries = await db.Entries.AsNoTracking().Where(entry => entry.TransactionId == transactionId).ToListAsync(Ct);
        return [.. entries
            .OrderBy(entry => entry.Amount.Currency.Value, StringComparer.Ordinal)
            .ThenBy(entry => entry.Role)
            .Select(entry => (entry.WalletId, entry.Amount, entry.Role))];
    }

    // Read through the view the echo and the dashboard read. Null: no balance row in that currency.
    static async Task<decimal?> BalanceAsync(LedgerDbContext db, Guid walletId, CurrencyCode currency)
    {
        var rows = await db.Database.SqlQuery<decimal>(
            $"""SELECT balance AS "Value" FROM wallet_balances WHERE wallet_id = {walletId} AND currency = {currency.Value}""")
            .ToListAsync(Ct);
        return rows.Count == 0 ? null : rows.Single();
    }

    [Fact]
    public async Task Wallet_terms_price_a_foreign_line_and_its_fee_becomes_a_fees_and_charges_line()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);

        await ApplyAsync(db, id, Spending(kaspi, Line(30m, CurrencyCode.Usd), Line(1000m, CurrencyCode.Kzt, GroceriesId)));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            new ChargeRow("USD", 15_600m, 156m, 520m, 1m, null, null, ChargeSource.WalletTerms));
        var fee = (await FeeLinesOfAsync(db, id)).Should().ContainSingle().Subject;
        fee.Description.Should().Be("Fee · USD purchase");
        fee.Amount.Should().Be(new Money(156m, CurrencyCode.Kzt), "a fee line is in its wallet's currency");
        fee.CategoryId.Should().Be(FeesChargesId);
        fee.CategorizedBy.Should().Be(CategorizationAuthority.Rule, "C# wrote it, never the model");
        fee.MerchantId.Should().BeNull();
        fee.Ordinal.Should().Be(3, "after every principal line");
    }

    [Fact]
    public async Task A_charges_fee_line_comes_after_the_highest_ordinal_a_line_names()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);

        // Lines that name their own ordinals (a receipt's own order, R-2) leave the running ordinal at 1.
        await ApplyAsync(db, id, Spending(
            kaspi,
            new CategorizedLineItem("line", new Money(30m, CurrencyCode.Usd), CoffeeId, null, Ordinal: 5),
            new CategorizedLineItem("line", new Money(1000m, CurrencyCode.Kzt), GroceriesId, null, Ordinal: 2)));

        db.ChangeTracker.Clear();
        (await FeeLinesOfAsync(db, id)).Select(line => line.Ordinal).Should().Equal(6);
    }

    [Fact]
    public async Task A_stated_charge_replaces_the_terms_and_survives_a_category_only_correction()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);
        await ApplyAsync(db, id, Spending(kaspi, Line(30m, CurrencyCode.Usd)));

        // "списали 15400" in reply.
        await ApplyAsync(db, id, AsCorrection(Said(Spending(kaspi, Line(30m, CurrencyCode.Usd)), 15_400m, CurrencyCode.Kzt)));

        db.ChangeTracker.Clear();
        var stated = new ChargeRow("USD", 15_400m, 154m, 513.333333333333m, 1m, null, null, ChargeSource.Stated);
        (await ChargesOfAsync(db, id)).Should().Equal(stated);

        // "это была еда, не кофе": the correction says nothing about the charge or the wallet.
        await ApplyAsync(db, id, AsCorrection(Spending(kaspi, Line(30m, CurrencyCode.Usd, GroceriesId))));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(stated);
        (await FeeLinesOfAsync(db, id)).Select(line => line.Amount).Should().Equal(new Money(154m, CurrencyCode.Kzt));
    }

    [Fact]
    public async Task A_kept_stated_charge_keeps_the_fee_that_was_said()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);

        // "списали 15400, комиссия 150": the wallet's 1 % would have made it 154.
        await ApplyAsync(db, id, Said(Spending(kaspi, Line(30m, CurrencyCode.Usd)), 15_400m, CurrencyCode.Kzt, fee: 150m));
        await ApplyAsync(db, id, AsCorrection(Spending(kaspi, Line(30m, CurrencyCode.Usd, GroceriesId))));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            [new ChargeRow("USD", 15_400m, 150m, 513.333333333333m, 1m, null, null, ChargeSource.Stated)],
            "a stated fee is stored, never computed again from the terms");
        (await FeeLinesOfAsync(db, id)).Select(line => line.Amount).Should().Equal(new Money(150m, CurrencyCode.Kzt));
    }

    [Fact]
    public async Task A_said_fee_is_taken_out_of_the_charge_only_when_the_charge_includes_it()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var included = await CaptureAsync(db);
        var separate = await CaptureAsync(db);

        // "списали 15556 включая комиссию 156" and "списали 15400, комиссия 150".
        await ApplyAsync(db, included, Said(Spending(kaspi, Line(30m, CurrencyCode.Usd)), 15_556m, CurrencyCode.Kzt, fee: 156m, feeIncluded: true));
        await ApplyAsync(db, separate, Said(Spending(kaspi, Line(30m, CurrencyCode.Usd)), 15_400m, CurrencyCode.Kzt, fee: 150m));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, included)).Should().Equal(
            new ChargeRow("USD", 15_400m, 156m, 513.333333333333m, 1m, null, null, ChargeSource.Stated));
        (await ChargesOfAsync(db, separate)).Should().Equal(
            new ChargeRow("USD", 15_400m, 150m, 513.333333333333m, 1m, null, null, ChargeSource.Stated));
    }

    [Fact]
    public async Task A_stated_charge_on_a_wallet_without_terms_has_no_fee()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        var id = await CaptureAsync(db);

        await ApplyAsync(db, id, Said(Spending(kaspi, Line(30m, CurrencyCode.Usd)), 15_400m, CurrencyCode.Kzt));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            new ChargeRow("USD", 15_400m, 0m, 513.333333333333m, null, null, null, ChargeSource.Stated));
        (await FeeLinesOfAsync(db, id)).Should().BeEmpty("a charge that cost no fee has no fee line");
    }

    [Fact]
    public async Task A_stated_charge_is_ignored_when_a_spending_has_two_foreign_currencies()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        await SetTermsAsync(db, kaspi, CurrencyCode.Eur, 560m, feePercent: 1m);
        var id = await CaptureAsync(db);

        // "30 долларов и 20 евро с каспи, списали 26000": the figure cannot be split between the two.
        await ApplyAsync(db, id, Said(
            Spending(kaspi, Line(30m, CurrencyCode.Usd), Line(20m, CurrencyCode.Eur)), 26_000m, CurrencyCode.Kzt));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            new ChargeRow("EUR", 11_200m, 112m, 560m, 1m, null, null, ChargeSource.WalletTerms),
            new ChargeRow("USD", 15_600m, 156m, 520m, 1m, null, null, ChargeSource.WalletTerms));
        (await FeeLinesOfAsync(db, id)).Select(line => (line.Description, line.Amount)).Should().Equal(
            ("Fee · EUR purchase", new Money(112m, CurrencyCode.Kzt)),
            ("Fee · USD purchase", new Money(156m, CurrencyCode.Kzt)));
    }

    [Fact]
    public async Task A_said_charge_that_cannot_be_honoured_falls_to_the_wallet_terms()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var otherCurrency = await CaptureAsync(db);
        var nothingLeft = await CaptureAsync(db);

        // Amendment 14: a charge in a currency other than the wallet's prices nothing.
        await ApplyAsync(db, otherCurrency, Said(Spending(kaspi, Line(30m, CurrencyCode.Usd)), 27.50m, CurrencyCode.Eur));
        // "списали 150 включая комиссию 150": nothing positive is left to be the charge.
        await ApplyAsync(db, nothingLeft, Said(Spending(kaspi, Line(30m, CurrencyCode.Usd)), 150m, CurrencyCode.Kzt, fee: 150m, feeIncluded: true));

        db.ChangeTracker.Clear();
        var byTerms = new ChargeRow("USD", 15_600m, 156m, 520m, 1m, null, null, ChargeSource.WalletTerms);
        (await ChargesOfAsync(db, otherCurrency)).Should().Equal(byTerms);
        (await ChargesOfAsync(db, nothingLeft)).Should().Equal(byTerms);
    }

    [Fact]
    public async Task A_negative_said_fee_falls_to_the_wallet_terms()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);

        await ApplyAsync(db, id, Said(Spending(kaspi, Line(30m, CurrencyCode.Usd)), 15_400m, CurrencyCode.Kzt, fee: -150m));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            [new ChargeRow("USD", 15_600m, 156m, 520m, 1m, null, null, ChargeSource.WalletTerms)],
            "a negative fee cannot be a charge's fee, so the said charge is not honoured");
    }

    [Fact]
    public async Task A_receipt_categorization_never_prices_a_foreign_line()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var mainWallet = new Guid("00000000-0000-0000-0000-000000000001");
        await SetTermsAsync(db, mainWallet, CurrencyCode.Eur, 117m, feePercent: 1m);
        var id = await CaptureAsync(db);
        // What ReceiptCategorizationWorker hands ApplyAsync for a vision-read receipt in EUR on the RSD main wallet.
        var receipt = new CategorizationOutcome(
            [new CategorizedLineItem("Kafa", new Money(3.50m, CurrencyCode.Eur), CoffeeId, null, Ordinal: 1)],
            Day, JobKind.CategorizeReceipt, TransactionKind: TransactionKind.Expense, WalletId: mainWallet);

        await ApplyAsync(db, id, receipt);
        await ApplyAsync(db, id, receipt with { Instruction = "это был кофе" });

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().BeEmpty("T-1, amendment 27: a fiscal receipt is never a foreign-currency spending");
        (await FeeLinesOfAsync(db, id)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_said_charge_that_is_not_honoured_does_not_keep_an_older_stated_charge()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);
        await ApplyAsync(db, id, Said(Spending(kaspi, Line(30m, CurrencyCode.Usd)), 15_400m, CurrencyCode.Kzt));

        // 2b passes a charge said in EUR through unjudged (contract, review C-4): not honoured on a KZT wallet, yet said.
        await ApplyAsync(db, id, AsCorrection(Said(Spending(kaspi, Line(30m, CurrencyCode.Usd)), 27.50m, CurrencyCode.Eur)));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            new ChargeRow("USD", 15_600m, 156m, 520m, 1m, null, null, ChargeSource.WalletTerms));
    }

    [Fact]
    public async Task A_kept_stated_charge_derives_its_rate_from_the_new_sum()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);
        await ApplyAsync(db, id, Said(Spending(kaspi, Line(30m, CurrencyCode.Usd)), 15_400m, CurrencyCode.Kzt));

        await ApplyAsync(db, id, AsCorrection(Spending(kaspi, Line(35m, CurrencyCode.Usd))));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            new ChargeRow("USD", 15_400m, 154m, 440m, 1m, null, null, ChargeSource.Stated));
    }

    [Fact]
    public async Task A_correction_on_the_same_wallet_prices_from_the_records_own_terms_not_todays()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);
        await ApplyAsync(db, id, Spending(kaspi, Line(30m, CurrencyCode.Usd)));

        await db.WalletFxTerms
            .Where(terms => terms.WalletId == kaspi && terms.Currency == CurrencyCode.Usd)
            .ExecuteUpdateAsync(set => set.SetProperty(terms => terms.Rate, 530m).SetProperty(terms => terms.FeePercent, (decimal?)2m), Ct);

        await ApplyAsync(db, id, AsCorrection(Spending(kaspi, Line(30m, CurrencyCode.Usd, GroceriesId))));
        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            [new ChargeRow("USD", 15_600m, 156m, 520m, 1m, null, null, ChargeSource.WalletTerms)],
            "changing a wallet's terms never reprices a record, not even through a correction");

        await ApplyAsync(db, id, AsCorrection(Spending(kaspi, Line(40m, CurrencyCode.Usd, GroceriesId))));
        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            [new ChargeRow("USD", 20_800m, 208m, 520m, 1m, null, null, ChargeSource.WalletTerms)],
            "a new sum is priced by the record's own snapshot");

        var later = await CaptureAsync(db);
        await ApplyAsync(db, later, Spending(kaspi, Line(30m, CurrencyCode.Usd)));
        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, later)).Should().Equal(
            [new ChargeRow("USD", 15_900m, 318m, 530m, 2m, null, null, ChargeSource.WalletTerms)],
            "a first reading takes today's terms");
    }

    [Fact]
    public async Task An_edited_message_that_no_longer_says_a_charge_drops_the_stated_charge()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);
        await ApplyAsync(db, id, Said(Spending(kaspi, Line(30m, CurrencyCode.Usd)), 15_400m, CurrencyCode.Kzt));

        // The operator edited "30 долларов с каспи, списали 15400" down to "30 долларов с каспи".
        await ApplyAsync(db, id, Spending(kaspi, Line(30m, CurrencyCode.Usd)) with { Kind = JobKind.Reinterpret });

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            [new ChargeRow("USD", 15_600m, 156m, 520m, 1m, null, null, ChargeSource.WalletTerms)],
            "an edited message is the whole statement, so a charge it no longer says was not said");
    }

    [Fact]
    public async Task An_edited_message_is_priced_at_the_wallets_current_terms()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);
        await ApplyAsync(db, id, Spending(kaspi, Line(30m, CurrencyCode.Usd)));

        await db.WalletFxTerms
            .Where(terms => terms.WalletId == kaspi && terms.Currency == CurrencyCode.Usd)
            .ExecuteUpdateAsync(set => set.SetProperty(terms => terms.Rate, 530m).SetProperty(terms => terms.FeePercent, (decimal?)2m), Ct);

        await ApplyAsync(db, id, Spending(kaspi, Line(30m, CurrencyCode.Usd)) with { Kind = JobKind.Reinterpret });

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            [new ChargeRow("USD", 15_900m, 318m, 530m, 2m, null, null, ChargeSource.WalletTerms)],
            "an edited message is read again like a first reading");
    }

    [Fact]
    public async Task Moving_the_record_to_another_wallet_takes_that_wallets_current_terms()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        var halyk = await AddKztWalletAsync(db, "Halyk KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        await SetTermsAsync(db, halyk, CurrencyCode.Usd, 510m, feeFixed: 200m);
        var id = await CaptureAsync(db);
        await ApplyAsync(db, id, Spending(kaspi, Line(30m, CurrencyCode.Usd)));

        await ApplyAsync(db, id, AsCorrection(Spending(halyk, Line(30m, CurrencyCode.Usd))));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            new ChargeRow("USD", 15_300m, 200m, 510m, null, 200m, null, ChargeSource.WalletTerms));
    }

    [Fact]
    public async Task Moving_the_record_to_another_wallet_discards_a_stated_charge()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        var halyk = await AddKztWalletAsync(db, "Halyk KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        await SetTermsAsync(db, halyk, CurrencyCode.Usd, 510m, feeFixed: 200m);
        var id = await CaptureAsync(db);
        await ApplyAsync(db, id, Said(Spending(kaspi, Line(30m, CurrencyCode.Usd)), 15_400m, CurrencyCode.Kzt));

        await ApplyAsync(db, id, AsCorrection(Spending(halyk, Line(30m, CurrencyCode.Usd))));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            [new ChargeRow("USD", 15_300m, 200m, 510m, null, 200m, null, ChargeSource.WalletTerms)],
            "a figure stated in Kaspi's tenge means nothing for Halyk");
    }

    [Fact]
    public async Task A_charge_prices_the_lines_the_apply_leaves_in_place()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(), TransactionId = id, Description = "hand-corrected", Amount = new Money(10m, CurrencyCode.Usd),
            CategoryId = CoffeeId, CategorizedBy = CategorizationAuthority.Rule, Ordinal = 1, Role = EntryRole.Principal,
        });
        await db.SaveChangesAsync(Ct);

        await ApplyAsync(db, id, Spending(kaspi, Line(30m, CurrencyCode.Usd)));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            [new ChargeRow("USD", 20_800m, 208m, 520m, 1m, null, null, ChargeSource.WalletTerms)],
            "the Rule line survives the model's delete, so 40 USD is what the wallet paid for");
    }

    [Fact]
    public async Task Without_terms_or_a_stated_charge_there_is_no_charge()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        var id = await CaptureAsync(db);

        await ApplyAsync(db, id, Spending(kaspi, Line(30m, CurrencyCode.Usd)));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().BeEmpty();
        (await FeeLinesOfAsync(db, id)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_charge_that_rounds_to_zero_is_not_a_charge()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 0.4m);
        var id = await CaptureAsync(db);

        await ApplyAsync(db, id, Spending(kaspi, Line(0.01m, CurrencyCode.Usd)));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().BeEmpty("0.01 × 0.4 = 0.004 rounds to 0.00, which is no charge");
    }

    [Fact]
    public async Task A_record_that_stops_being_an_expense_loses_its_charges_and_their_fee_lines()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);
        await ApplyAsync(db, id, Spending(kaspi, Line(30m, CurrencyCode.Usd)));

        await ApplyAsync(db, id, AsCorrection(
            new CategorizationOutcome([Line(30m, CurrencyCode.Usd)], Day, TransactionKind: TransactionKind.Income, WalletId: kaspi)));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().BeEmpty();
        (await FeeLinesOfAsync(db, id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Applying_the_same_outcome_twice_leaves_one_charge_and_one_fee_line()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);

        await ApplyAsync(db, id, Spending(kaspi, Line(30m, CurrencyCode.Usd)));
        await ApplyAsync(db, id, Spending(kaspi, Line(30m, CurrencyCode.Usd)));

        db.ChangeTracker.Clear();
        (await ChargesOfAsync(db, id)).Should().Equal(
            new ChargeRow("USD", 15_600m, 156m, 520m, 1m, null, null, ChargeSource.WalletTerms));
        (await FeeLinesOfAsync(db, id)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_charged_spending_debits_its_wallet_the_charge_plus_the_fee()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);

        await ApplyAsync(db, id, Spending(kaspi, Line(30m, CurrencyCode.Usd)));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, id)).Should().Equal(
            (kaspi, new Money(-15_600m, CurrencyCode.Kzt), EntryRole.Principal),
            (kaspi, new Money(-156m, CurrencyCode.Kzt), EntryRole.Fee));
        (await BalanceAsync(db, kaspi, CurrencyCode.Kzt)).Should().Be(-15_756.00m, "acceptance 4: charge 15600.00 + fee 156.00");
        (await BalanceAsync(db, kaspi, CurrencyCode.Usd)).Should().BeNull("a charged currency leaves no foreign balance behind");
    }

    [Fact]
    public async Task A_stated_charge_debits_the_stated_figure_and_its_fee()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);
        await ApplyAsync(db, id, Spending(kaspi, Line(30m, CurrencyCode.Usd)));

        await ApplyAsync(db, id, AsCorrection(Said(Spending(kaspi, Line(30m, CurrencyCode.Usd)), 15_400m, CurrencyCode.Kzt)));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, id)).Should().Equal(
            (kaspi, new Money(-15_400m, CurrencyCode.Kzt), EntryRole.Principal),
            (kaspi, new Money(-154m, CurrencyCode.Kzt), EntryRole.Fee));
        (await BalanceAsync(db, kaspi, CurrencyCode.Kzt)).Should().Be(-15_554.00m);
    }

    [Fact]
    public async Task Wallet_currency_lines_and_a_charged_foreign_line_share_one_principal_entry()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = await AddKztWalletAsync(db, "Kaspi KZT");
        await SetTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, feePercent: 1m);
        var id = await CaptureAsync(db);

        await ApplyAsync(db, id, Spending(kaspi, Line(1000m, CurrencyCode.Kzt, GroceriesId), Line(30m, CurrencyCode.Usd)));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, id)).Should().Equal(
            (kaspi, new Money(-16_600m, CurrencyCode.Kzt), EntryRole.Principal),
            (kaspi, new Money(-156m, CurrencyCode.Kzt), EntryRole.Fee));
    }

    [Fact]
    public async Task A_foreign_line_no_charge_prices_posts_in_its_own_currency()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var noTerms = await AddKztWalletAsync(db, "Kaspi KZT");
        var tinyRate = await AddKztWalletAsync(db, "Halyk KZT");
        await SetTermsAsync(db, tinyRate, CurrencyCode.Usd, 0.4m);
        var unpriced = await CaptureAsync(db);
        var roundedAway = await CaptureAsync(db);

        await ApplyAsync(db, unpriced, Spending(noTerms, Line(30m, CurrencyCode.Usd)));
        await ApplyAsync(db, roundedAway, Spending(tinyRate, Line(0.01m, CurrencyCode.Usd)));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, unpriced)).Should().Equal((noTerms, new Money(-30m, CurrencyCode.Usd), EntryRole.Principal));
        (await EntriesOfAsync(db, roundedAway)).Should().Equal((tinyRate, new Money(-0.01m, CurrencyCode.Usd), EntryRole.Principal));
        (await BalanceAsync(db, noTerms, CurrencyCode.Usd)).Should().Be(-30m);
        (await BalanceAsync(db, noTerms, CurrencyCode.Kzt)).Should().BeNull("M10: nothing is converted without a charge");
        (await BalanceAsync(db, tinyRate, CurrencyCode.Usd)).Should().Be(-0.01m);
        (await BalanceAsync(db, tinyRate, CurrencyCode.Kzt)).Should().BeNull("a charge that rounds to zero converts nothing either");
    }
}
