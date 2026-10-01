using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Persistence.Tests;

// M12: sums are numeric(19,4) in SQL and decimal in C#, and everything shown is
// InvariantCulture-formatted regardless of the ambient thread culture. A missing explicit culture
// argument on a ToString/decimal.Parse call is exactly the class of bug this catches - it would
// pass silently under en-US (the default CI/dev locale) and only misbehave under a comma-decimal
// culture, which is why the assertions below run wrapped in CultureScope rather than trusting the
// test host's own locale to already be "wrong" by luck.
[Collection("postgres")]
public class MoneyExactnessTests(PostgresFixture fixture)
{
    static readonly Guid EurWalletId = Guid.NewGuid();
    static readonly Guid RsdWalletId = Guid.NewGuid();
    static readonly Guid UsdWalletId = Guid.NewGuid();
    static readonly Guid RubWalletId = Guid.NewGuid();
    static readonly Guid KztWalletId = Guid.NewGuid();
    static readonly Guid RaiffeisenId = Guid.NewGuid();
    static readonly Guid CashRsdId = Guid.NewGuid();
    static readonly Guid KaspiId = Guid.NewGuid();
    static readonly Guid GroceriesId = new("00000000-0000-0000-0001-000000000001");
    static int nextMessageId;

    const string TimeZone = "Europe/Belgrade";
    static readonly DateOnly Day1 = new(2026, 9, 1);
    static readonly DateOnly Day2 = new(2026, 9, 2);
    static readonly DateTimeOffset At1 = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset At2 = new(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);

    static async Task SeedAsync(LedgerDbContext db)
    {
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        db.Wallets.AddRange(
            Wallet(EurWalletId, "Wise EUR", CurrencyCode.Eur),
            Wallet(RsdWalletId, "Raiffeisen RSD", CurrencyCode.Rsd),
            Wallet(UsdWalletId, "Payoneer USD", CurrencyCode.Usd),
            Wallet(RubWalletId, "Cash RUB", CurrencyCode.Rub),
            Wallet(KztWalletId, "Halyk KZT", CurrencyCode.Kzt));

        // EUR: the canonical 0.1 + 0.2 case. Decimal keeps this exact; double would not.
        Opening(db, EurWalletId, CurrencyCode.Eur, 1000.00m, Day1, At1);
        Expense(db, EurWalletId, CurrencyCode.Eur, 0.10m, Day2, At2);
        Expense(db, EurWalletId, CurrencyCode.Eur, 0.20m, Day2, At2);

        // RSD: the spec's own worked example (M11) - opening, then a statement that adjusts +200.
        Opening(db, RsdWalletId, CurrencyCode.Rsd, 44_800.00m, Day1, At1);
        Statement(db, RsdWalletId, CurrencyCode.Rsd, 45_000.00m, 44_800.00m, Day2, At2);

        // USD: a 4-decimal input. The view keeps all four; only display truncates to two.
        Opening(db, UsdWalletId, CurrencyCode.Usd, 500.0001m, Day1, At1);
        Expense(db, UsdWalletId, CurrencyCode.Usd, 0.0001m, Day2, At2);

        // RUB: three expenses that must sum to exactly 100.00, not 99.99999999999999.
        Opening(db, RubWalletId, CurrencyCode.Rub, 0.00m, Day1, At1);
        Expense(db, RubWalletId, CurrencyCode.Rub, 33.33m, Day2, At2);
        Expense(db, RubWalletId, CurrencyCode.Rub, 33.33m, Day2, At2);
        Expense(db, RubWalletId, CurrencyCode.Rub, 33.34m, Day2, At2);

        // KZT: a large value (M12 explicitly names "large KZT values").
        Opening(db, KztWalletId, CurrencyCode.Kzt, 50_000_000.00m, Day1, At1);
        Income(db, KztWalletId, CurrencyCode.Kzt, 1_234_567.89m, Day2, At2);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    static Wallet Wallet(Guid id, string name, CurrencyCode currency) => new()
    {
        Id = id, Name = name, Currency = currency, Aliases = [], IsDefaultForCurrency = false,
        Archived = false, CreatedAt = At1,
    };

    static void Opening(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal amount, DateOnly occurredOn, DateTimeOffset occurredAt) =>
        AddCheckpoint(db, walletId, currency, amount, computedBefore: 0m, occurredOn, occurredAt);

    static void Statement(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal stated, decimal computedBefore, DateOnly occurredOn, DateTimeOffset occurredAt) =>
        AddCheckpoint(db, walletId, currency, stated, computedBefore, occurredOn, occurredAt);

    static void AddCheckpoint(
        LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal amount, decimal computedBefore, DateOnly occurredOn, DateTimeOffset occurredAt)
    {
        var transactionId = Guid.NewGuid();
        db.Transactions.Add(new Transaction
        {
            Id = transactionId, WalletId = walletId, Kind = TransactionKind.BalanceCheck,
            CaptureKind = CaptureKind.Manual, RawText = "Balance statement", Status = TransactionStatus.Completed,
            TimeZoneId = TimeZone, OccurredAt = occurredAt, OccurredOn = occurredOn,
            TelegramChatId = null, TelegramMessageId = null, CreatedAt = occurredAt,
        });
        db.BalanceChecks.Add(new BalanceCheck
        {
            TransactionId = transactionId, WalletId = walletId, Stated = new Money(amount, currency), ComputedBefore = computedBefore,
        });
    }

    static void Expense(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal amount, DateOnly occurredOn, DateTimeOffset occurredAt) =>
        AddEntry(db, walletId, currency, -amount, TransactionKind.Expense, occurredOn, occurredAt);

    static void Income(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal amount, DateOnly occurredOn, DateTimeOffset occurredAt) =>
        AddEntry(db, walletId, currency, amount, TransactionKind.Income, occurredOn, occurredAt);

    static void AddEntry(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal signedAmount, TransactionKind kind, DateOnly occurredOn, DateTimeOffset occurredAt)
    {
        var transactionId = Guid.NewGuid();
        db.Transactions.Add(new Transaction
        {
            Id = transactionId, WalletId = walletId, Kind = kind, CaptureKind = CaptureKind.Manual,
            RawText = kind == TransactionKind.Expense ? "seeded expense" : "seeded income", Status = TransactionStatus.Completed,
            TimeZoneId = TimeZone, OccurredAt = occurredAt, OccurredOn = occurredOn,
            TelegramChatId = null, TelegramMessageId = null, CreatedAt = occurredAt,
        });
        db.Entries.Add(new Entry
        {
            Id = Guid.NewGuid(), TransactionId = transactionId, WalletId = walletId,
            Amount = new Money(signedAmount, currency), Role = EntryRole.Principal,
        });
    }

    static async Task<Guid> CaptureAsync(LedgerDbContext db, string rawText)
    {
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(), RawText = rawText, Status = TransactionStatus.Captured,
            TimeZoneId = TimeZone, OccurredAt = At2, OccurredOn = Day2,
            TelegramChatId = 1, TelegramMessageId = Interlocked.Increment(ref nextMessageId), CreatedAt = At2,
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return transaction.Id;
    }

    static (IBalanceReadModel Balances, ServiceProvider Provider) BuildBalanceReadModel(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ledger"] = connectionString })
            .Build();
        services.AddNoofPersistence(configuration, maxJobAttempts: 8);

        // Ownership stays with the caller (review finding 8: the provider was never disposed
        // before). Resolved straight from the root provider - IBalanceReadModel becomes part of
        // the root's own implicit scope, so disposing the returned ServiceProvider once the
        // test's assertions are done with it disposes the scoped DbContext underneath it too.
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IBalanceReadModel>(), provider);
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("sr-Latn-RS")]
    public async Task Balances_are_exact_in_all_five_currencies_under_a_non_invariant_culture(string cultureName)
    {
        using var culture = new CultureScope(cultureName);
        var connectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();
        await using (var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options))
            await SeedAsync(db);

        var (balances, provider) = BuildBalanceReadModel(connectionString);
        await using var _ = provider;

        (await balances.BalanceOfAsync(EurWalletId, TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo([new Money(999.70m, CurrencyCode.Eur)]);
        (await balances.BalanceOfAsync(RsdWalletId, TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo([new Money(45_000.00m, CurrencyCode.Rsd)]);
        (await balances.BalanceOfAsync(UsdWalletId, TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo([new Money(500.0000m, CurrencyCode.Usd)]);
        (await balances.BalanceOfAsync(RubWalletId, TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo([new Money(-100.00m, CurrencyCode.Rub)]);
        (await balances.BalanceOfAsync(KztWalletId, TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo([new Money(51_234_567.89m, CurrencyCode.Kzt)]);
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("sr-Latn-RS")]
    public async Task A_transfer_with_a_fee_and_a_charged_foreign_spending_are_exact_under_a_non_invariant_culture(string cultureName)
    {
        using var culture = new CultureScope(cultureName);
        var ct = TestContext.Current.CancellationToken;
        var connectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();

        await using (var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options))
        {
            await db.Database.MigrateAsync(ct);
            db.Wallets.AddRange(
                Wallet(RaiffeisenId, "Raiffeisen RSD", CurrencyCode.Rsd),
                Wallet(CashRsdId, "Cash RSD", CurrencyCode.Rsd),
                Wallet(KaspiId, "Kaspi KZT", CurrencyCode.Kzt));
            Opening(db, RaiffeisenId, CurrencyCode.Rsd, 50_000.00m, Day1, At1);
            Opening(db, KaspiId, CurrencyCode.Kzt, 100_000.00m, Day1, At1);
            db.WalletFxTerms.Add(new WalletFxTerms { WalletId = KaspiId, Currency = CurrencyCode.Usd, Rate = 519.87m, FeePercent = 1.5m });
            await db.SaveChangesAsync(ct);
            var store = new EfCategorizationStore(db, new FakeTimeProvider(At2));

            var withdrawal = await CaptureAsync(db, "снял 10000,25 с райфа, комиссия 150,25");
            await store.ApplyAsync(withdrawal, new CategorizationOutcome(
                [], Day2, TransactionKind: TransactionKind.Transfer, WalletId: RaiffeisenId,
                Transfer: new TransferFacts(
                    RaiffeisenId, new Money(10_150.50m, CurrencyCode.Rsd), CashRsdId, new Money(10_000.25m, CurrencyCode.Rsd),
                    new Money(150.25m, CurrencyCode.Rsd), TransferLeg.From, null)), ct);

            var dinner = await CaptureAsync(db, "ужин 30,10 долларов с каспи");
            await store.ApplyAsync(dinner, new CategorizationOutcome(
                [new CategorizedLineItem("ужин", new Money(30.10m, CurrencyCode.Usd), GroceriesId, null)], Day2,
                TransactionKind: TransactionKind.Expense, WalletId: KaspiId), ct);

            (await store.GetSubjectAsync(dinner, ct))!.Charges.Should().Equal([new ChargeView(
                CurrencyCode.Usd, 30.10m, new Money(15_648.09m, CurrencyCode.Kzt), new Money(234.72m, CurrencyCode.Kzt),
                519.87m, new FeeTerms(1.5m, null, null), ChargeSource.WalletTerms)]);

            var snapshot = await db.TransactionRevisions.AsNoTracking()
                .Where(revision => revision.TransactionId == dinner)
                .Select(revision => revision.Snapshot)
                .SingleAsync(ct);
            using var json = JsonDocument.Parse(snapshot);
            var charge = json.RootElement.GetProperty("charges").EnumerateArray().Single();
            // Read back invariant on purpose: "15648,0900" written under ru-RU would parse here as 156480900.
            decimal.Parse(charge.GetProperty("charged_amount").GetString()!, CultureInfo.InvariantCulture).Should().Be(15_648.09m);
            decimal.Parse(charge.GetProperty("fee_amount").GetString()!, CultureInfo.InvariantCulture).Should().Be(234.72m);
            decimal.Parse(charge.GetProperty("rate_used").GetString()!, CultureInfo.InvariantCulture).Should().Be(519.87m);
        }

        var (balances, provider) = BuildBalanceReadModel(connectionString);
        await using var _ = provider;

        (await balances.BalanceOfAsync(RaiffeisenId, ct)).Should().Equal([new Money(39_849.50m, CurrencyCode.Rsd)]);
        (await balances.BalanceOfAsync(CashRsdId, ct)).Should().Equal([new Money(10_000.25m, CurrencyCode.Rsd)]);
        (await balances.BalanceOfAsync(KaspiId, ct)).Should().Equal([new Money(84_117.19m, CurrencyCode.Kzt)]);
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("sr-Latn-RS")]
    public void The_expense_echo_renders_cents_exactly_regardless_of_culture(string cultureName)
    {
        using var culture = new CultureScope(cultureName);
        var echo = new RecordEcho();
        var subject = new CategorizationSubject(
            Guid.NewGuid(), "coffee 0.10 EUR", 111L, 42, "Wise EUR", TransactionStatus.Completed, Day2, Day2,
            [new RecordedLine("Coffee", new Money(0.10m, CurrencyCode.Eur), "coffee", "Coffee", null)],
            CaptureKind.Text, TransactionKind.Expense, CurrencyCode.Eur,
            [new Money(999.70m, CurrencyCode.Eur)]);

        var message = echo.Compose(subject);

        message.Text.Should().StartWith("Recorded — Wise EUR · balance 999.70 EUR\n");
        message.Text.Should().Contain("0.10 EUR");
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("sr-Latn-RS")]
    public void The_balance_statement_echo_renders_the_adjustment_exactly(string cultureName)
    {
        using var culture = new CultureScope(cultureName);
        var echo = new RecordEcho();
        var subject = new CategorizationSubject(
            Guid.NewGuid(), "на райфе 45000", 111L, 43, "Raiffeisen RSD", TransactionStatus.Completed, Day2, Day2,
            [], CaptureKind.Text, TransactionKind.BalanceCheck, CurrencyCode.Rsd, [new Money(45_000.00m, CurrencyCode.Rsd)],
            new BalanceStatement(new Money(45_000.00m, CurrencyCode.Rsd), 44_800.00m));

        var message = echo.Compose(subject);

        message.Text.Should().Be("Raiffeisen RSD: balance was 44800.00 RSD, you said 45000.00 RSD — adjusted +200.00 RSD");
    }
}
