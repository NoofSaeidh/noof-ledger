using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;
using Noof.Ledger.Persistence.Diagnostics.Integrity;
using Noof.Ledger.Persistence.Editing;
using Noof.Ledger.Persistence.Wallets;

namespace Noof.Ledger.Persistence.Tests;

// Records written through the production write path and then broken by one raw statement, so an integrity test seeds
// exactly the fault it names on top of data the write path itself produced.
internal static class IntegritySeed
{
    public static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
    public static readonly Guid MainWalletId = new("00000000-0000-0000-0000-000000000001");
    public const string MainWalletName = "Main Wallet";
    public static readonly DateOnly Day = new(2026, 9, 10);
    static readonly Guid GroceriesId = new("00000000-0000-0000-0001-000000000001");
    static int nextMessageId = 70_000;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Guid> AddWalletAsync(LedgerDbContext db, string name, CurrencyCode currency)
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), Name = name, Currency = currency, CreatedAt = Clock.GetUtcNow() };
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(Ct);
        return wallet.Id;
    }

    public static async Task AddFxTermsAsync(
        LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal rate, decimal feePercent)
    {
        db.WalletFxTerms.Add(new WalletFxTerms { WalletId = walletId, Currency = currency, Rate = rate, FeePercent = feePercent });
        await db.SaveChangesAsync(Ct);
    }

    // A text capture as EfCaptureStore leaves it: no wallet and no job yet, sent at 10:00 UTC on the given day.
    public static async Task<Guid> CaptureAsync(LedgerDbContext db, DateOnly sentOn)
    {
        var sentAt = new DateTimeOffset(sentOn.Year, sentOn.Month, sentOn.Day, 10, 0, 0, TimeSpan.Zero);
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            RawText = "test capture",
            Status = TransactionStatus.Captured,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = sentAt,
            OccurredOn = sentOn,
            TelegramChatId = 1,
            TelegramMessageId = Interlocked.Increment(ref nextMessageId),
            CreatedAt = sentAt,
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(Ct);
        return transaction.Id;
    }

    public static async Task<Guid> RecordAsync(LedgerDbContext db, CategorizationOutcome outcome, DateOnly? sentOn = null)
    {
        var id = await CaptureAsync(db, sentOn ?? Day);
        await new EfCategorizationStore(db, Clock).ApplyAsync(id, outcome, Ct);
        return id;
    }

    public static CategorizedLineItem Line(decimal amount, CurrencyCode currency) =>
        new("line", new Money(amount, currency), GroceriesId, null);

    public static CategorizationOutcome Expense(Guid walletId, params CategorizedLineItem[] lines) =>
        new(lines, Day, TransactionKind: TransactionKind.Expense, WalletId: walletId);

    public static CategorizationOutcome Income(Guid walletId, params CategorizedLineItem[] lines) =>
        new(lines, Day, TransactionKind: TransactionKind.Income, WalletId: walletId);

    public static CategorizationOutcome Statement(Guid walletId, decimal amount, CurrencyCode currency) =>
        new([], Day, TransactionKind: TransactionKind.BalanceCheck, WalletId: walletId, StatedBalance: new Money(amount, currency));

    public static CategorizationOutcome TransferOutcome(TransferFacts facts) =>
        new([], Day, TransactionKind: TransactionKind.Transfer, Transfer: facts);

    public static Money Rsd(decimal amount) => new(amount, CurrencyCode.Rsd);

    public static Money Eur(decimal amount) => new(amount, CurrencyCode.Eur);

    // The context's tracked entities no longer describe the rows a raw statement changed.
    public static async Task BreakAsync(LedgerDbContext db, FormattableString sql)
    {
        await db.Database.ExecuteSqlAsync(sql, Ct);
        db.ChangeTracker.Clear();
    }

    public static Task AddRawLineAsync(LedgerDbContext db, Guid transactionId, EntryRole role, decimal amount, string currency) =>
        BreakAsync(db, $"""
            INSERT INTO line_items (id, transaction_id, description, category_id, categorized_by, ordinal, role, amount, currency)
            VALUES ({Guid.NewGuid()}, {transactionId}, 'seeded', {GroceriesId}, 2, 99, {(int)role}, {amount}, {currency})
            """);

    public static async Task MarkFailedAsync(LedgerDbContext db, Guid transactionId, RecordFailureReason reason)
    {
        await new EfCategorizationStore(db, Clock).MarkFailedAsync(transactionId, reason, Ct);
        db.ChangeTracker.Clear();
    }

    public static async Task CancelAsync(LedgerDbContext db, Guid transactionId)
    {
        db.ChangeTracker.Clear();
        (await new EfRecordEditor(db, Clock).CancelAsync(transactionId, Ct)).Should().BeTrue();
    }

    // Every kind the write path produces, an opening balance, and a cancelled, a failed and a captured record: none of
    // them is an I-1 or I-2 finding. The failed and the captured one are I-4's and I-3's, so no test runs this seed
    // through every check.
    public static async Task SeedEveryKindAsync(LedgerDbContext db)
    {
        await new EfWalletAdmin(db, Clock, TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade")).CreateAsync(
            new NewWallet("Raiffeisen RSD", CurrencyCode.Rsd, 45_000m, new DateOnly(2026, 9, 1), [], false), Ct);
        db.ChangeTracker.Clear();
        var wise = await AddWalletAsync(db, "Wise EUR", CurrencyCode.Eur);
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var cashEur = await AddWalletAsync(db, "Cash EUR", CurrencyCode.Eur);
        var kaspi = await AddWalletAsync(db, "Kaspi KZT", CurrencyCode.Kzt);
        await AddFxTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, 1m);

        await RecordAsync(db, Expense(MainWalletId, Line(250m, CurrencyCode.Rsd), Line(3.50m, CurrencyCode.Eur)));
        await RecordAsync(db, Income(wise, Line(2000m, CurrencyCode.Eur)));
        await RecordAsync(db, Statement(MainWalletId, 45000m, CurrencyCode.Rsd));
        await RecordAsync(db, TransferOutcome(new TransferFacts(MainWalletId, Rsd(10150m), cash, Rsd(10000m), Rsd(150m), TransferLeg.From, null)));
        await RecordAsync(db, TransferOutcome(new TransferFacts(
            cashEur, Eur(100m), cash, Rsd(11500m), Rsd(200m), TransferLeg.To, new ExchangeRate(CurrencyCode.Eur, 117m, CurrencyCode.Rsd))));
        await RecordAsync(db, TransferOutcome(new TransferFacts(cash, Rsd(2000m), MainWalletId, Rsd(2000m), null, null, null)));
        await RecordAsync(db, Expense(kaspi, Line(1000m, CurrencyCode.Kzt), Line(30m, CurrencyCode.Usd)));

        var cancelled = await RecordAsync(db, Expense(cash, Line(500m, CurrencyCode.Rsd)));
        await CancelAsync(db, cancelled);
        var failed = await CaptureAsync(db, Day);
        await MarkFailedAsync(db, failed, RecordFailureReason.MissingReceivedAmount);
        await CaptureAsync(db, Day);
    }

    // Runs the check over everything, then holds each of its queries to the scope: a reported record's scope sees
    // exactly that record's findings, and an unknown record's scope sees none.
    public static async Task<IReadOnlyList<IntegrityFinding>> FindCheckingScopeAsync(IIntegrityCheck check)
    {
        var all = await check.FindAsync(IntegrityScope.All, Ct);
        foreach (var transactionId in all.Select(finding => finding.TransactionId).OfType<Guid>().Distinct())
        {
            (await check.FindAsync(IntegrityScope.For(transactionId), Ct)).Select(Describe).Should().Equal(
                all.Where(finding => finding.TransactionId == transactionId).Select(Describe),
                "the scope of record {0} sees that record's findings and nothing else", transactionId);
        }

        (await check.FindAsync(IntegrityScope.For(Guid.NewGuid()), Ct)).Should().BeEmpty(
            "the scope of a record that does not exist sees nothing");
        return all;
    }

    // A finding's Facts is a list, which a record compares by reference.
    static string Describe(IntegrityFinding finding) =>
        $"{finding.Check} {finding.TransactionId} {finding.WalletId} {string.Join("; ", finding.Facts)}";

    public static void ShouldBeBug(
        this IntegrityFinding finding, IntegrityCheck check, Guid transactionId, Guid? walletId, params IntegrityFact[] facts)
    {
        (finding.Check, finding.Group, finding.TransactionId, finding.WalletId, finding.JobId)
            .Should().Be((check, IntegrityGroup.Bug, (Guid?)transactionId, walletId, (Guid?)null));
        finding.Facts.Should().Equal(facts);
    }
}
