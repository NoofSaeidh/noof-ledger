using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Demo;

internal static class MockData
{
    public const string TimeZoneId = "Europe/Belgrade";
    public const string Username = "demo";
    public const string Password = "demo";
    public const long TelegramChatId = 555_000_001;
    public const string FakeKey = "demo-not-a-real-key";
    public const string AnthropicKeySecret = "anthropic-api-key";
    public const string GroqKeySecret = "groq-api-key";

    // The dashboard's "This month" follows the real calendar, so the mock month is the current one: the
    // pictures change at most once a month, when the dates roll over, instead of the charts going empty.
    static readonly DateOnly MonthStart = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    public static readonly DateTimeOffset Now = At(20, 18, 0);
    public static readonly Guid UserId = Guid.Parse("7a1c0000-0000-4000-8000-0000000000aa");
    public static readonly DateOnly OpeningDate = MonthStart;

    // The host logs its own startup rows at the real now, which is never before the month's first
    // midnight: a window that closes there shows only these rows, whichever day the pictures are taken.
    public static readonly DateOnly LogWindowStart = MonthStart.AddDays(-3);
    public static readonly DateOnly LogWindowEnd = MonthStart;

    // The defaults keep Debug rows for a day, and the host prunes a minute after it starts - the mock
    // rows, up to a month old, would vanish halfway through a screenshot run.
    public static readonly LogRetentionDays LogRetention = new(Verbose: 60, Debug: 60, Information: 60, Warning: 90, Error: 90, Fatal: 90);

    public static readonly Guid TracedTransactionId = Id(900);
    public static readonly Guid FailedTransactionId = Id(901);
    public static readonly Guid ReceiptTransactionId = Id(902);
    public static readonly Guid UnconfirmedReceiptTransactionId = Id(903);
    public static readonly Guid VisionReceiptTransactionId = Id(904);
    public static readonly Guid WithdrawalTransactionId = Id(905);
    public static readonly Guid ExchangeTransactionId = Id(906);
    public static readonly Guid ForeignSpendingTransactionId = Id(907);
    public static readonly Guid TransferTransactionId = Id(908);

    public static IReadOnlyList<MockLogRow> LogRows { get; } =
    [
        new(BeforeTheMonth(3, 9, 0), LogSeverity.Debug, "Noof.Ledger.Telegram.TelegramPollingService", "Polled Telegram: 1 update"),
        new(BeforeTheMonth(3, 9, 5), LogSeverity.Information, "Noof.Ledger.Host.Workers.BackupWorker",
            $"Backup finished: noof_ledger-{BeforeTheMonth(3, 9, 5):yyyyMMdd}.dump"),
        new(BeforeTheMonth(2, 14, 30), LogSeverity.Warning, "Noof.Ledger.Ai", "Model call took 31.2 s, over its 30 s threshold"),
        new(BeforeTheMonth(2, 16, 10), LogSeverity.Warning, "Noof.Ledger.Host.Workers.ExtractReceiptWorker",
            "ReceiptFetchFailed: the Tax Administration site did not answer (status 503)"),
        new(BeforeTheMonth(1, 8, 15), LogSeverity.Error, "Noof.Ledger.Ai", "Categorisation call failed",
            "System.TimeoutException: The operation timed out after 00:00:30."),
    ];

    public static IReadOnlyList<MockWallet> Wallets { get; } =
    [
        new("Wise", CurrencyCode.Eur, 3000.00m, ["wise"], Default: true),
        new("Raiffeisen", CurrencyCode.Rsd, 180000.00m, ["raif"], Default: true, PaymentDefault: PaymentMethod.Card,
            Terms: [new(CurrencyCode.Eur, 117.35m, FeePercent: 0.5m, FeeFixed: null, FeeMinimum: 100.00m)]),
        new("Cash", CurrencyCode.Usd, 600.00m, [], Default: true, PaymentDefault: PaymentMethod.Cash),
        new("Tinkoff", CurrencyCode.Rub, 50000.00m, [], Default: true),
        new("Kaspi", CurrencyCode.Kzt, 200000.00m, [], Default: true,
            Terms: [new(CurrencyCode.Usd, 520m, FeePercent: 1m, FeeFixed: null, FeeMinimum: null)]),
        new("Old Revolut", CurrencyCode.Eur, 900.00m, [], Default: false, Archived: true),
        new("Cash RSD", CurrencyCode.Rsd, 15000.00m, [], Default: false, PaymentDefault: PaymentMethod.Cash),
        new("Cash EUR", CurrencyCode.Eur, 250.00m, [], Default: false, PaymentDefault: PaymentMethod.Cash),
    ];

    public static IReadOnlyList<MockRecord> Records { get; } =
    [
        Expense("Wise", "Maxi groceries 34.50 eur", Day(2), [new("Groceries", 34.50m, "groceries", "Maxi")]),
        Expense("Kaspi", "haircut 6000 kzt", Day(2), [new("Haircut", 6000m, "personal-care", null)]),
        Expense("Raiffeisen", "Maxi 3450 rsd", Day(3), [new("Groceries", 3450m, "groceries", "Maxi")]),
        Expense("Cash", "coffee 4.50 usd", Day(4), [new("Coffee", 4.50m, "coffee", null)]),
        Expense("Wise", "coffee 3.20 eur", Day(5), [new("Coffee", 3.20m, "coffee", null)]),
        Expense("Tinkoff", "netflix 599 rub", Day(7), [new("Netflix", 599m, "subscriptions", null)]),
        Expense("Raiffeisen", "taxi 850 rsd", Day(8), [new("Taxi", 850m, "transport", null)]),
        Expense("Cash", "internet 45 usd", Day(9), [new("Internet", 45.00m, "utilities", null)]),
        Income("Wise", "salary 2800 eur", Day(10), new("Salary", 2800.00m, "salary", null)),
        Income("Cash", "+450 usd freelance", Day(11), new("Freelance", 450.00m, "other-income", null)),
        Receipt(TransactionStatus.Completed, Day(12), VisionReceiptTransactionId, new(
            ReceiptSource.Vision, "Apoteka Zdravlje", null, "Bulevar Demo 12, Beograd", "444555666", "DEMO0002-DEMO0002-2048",
            At(12, 10, 5), 1039.00m, PaymentMethod.Card, QrTotal: 1039.00m,
            [
                new("Brufen 400mg 30 tbl", 1m, "kom", 389.00m, 389.00m, "health"),
                new("Vitamin C 1000mg", 1m, "kom", 650.00m, 650.00m, "health"),
            ])),
        Expense("Wise", "dinner at Walter 42 eur", Day(12), [new("Dinner", 42.00m, "restaurants", "Walter")]),
        Expense("Tinkoff", "groceries 1450 rub", Day(13), [new("Groceries", 1450m, "groceries", null)]),
        Expense("Raiffeisen", "pharmacy 1200 rsd", Day(14), [new("Pharmacy", 1200m, "health", null)]),
        Statement("Wise", "wise balance 5700", Day(15), stated: 5700.00m, computedBefore: 5720.30m),
        Expense("Kaspi", "groceries 8500 kzt", Day(16), [new("Groceries", 8500m, "groceries", null)]),
        Receipt(TransactionStatus.Completed, Day(17), ReceiptTransactionId, new(
            ReceiptSource.FiscalQr, "Maxi", "Dorćol", "Cara Dušana 1, Beograd", "111222333", "DEMO0001-DEMO0001-1024",
            At(17, 15, 42), 1364.94m, PaymentMethod.Card, QrTotal: 1364.94m,
            [
                new("Mleko 2,8% 1L", 2m, "kom", 159.99m, 319.98m, "groceries"),
                new("Hleb beli 500g", 1m, "kom", 89.99m, 89.99m, "groceries"),
                new("Jabuke", 1.25m, "kg", 139.99m, 174.99m, "groceries"),
                new("Kafa mlevena 200g", 1m, "kom", 429.99m, 429.99m, "groceries"),
                new("Šampon 400ml", 1m, "kom", 349.99m, 349.99m, "personal-care"),
            ])),
        Expense("Wise", "Lidl groceries 27.80, wine 12.50 eur", Day(18),
            [new("Groceries", 27.80m, "groceries", "Lidl"), new("Wine", 12.50m, "groceries", "Lidl")],
            TracedTransactionId),
        Failed("Raiffeisen", "#@%& ???", Day(19), FailedTransactionId),

        // Read from a photo with no fiscal QR, and one line was missed: it waits for Record anyway, so it
        // has no wallet and nothing categorised yet.
        Receipt(TransactionStatus.Captured, Day(19), UnconfirmedReceiptTransactionId, new(
            ReceiptSource.Vision, "Pekara Centar", null, null, null, null,
            At(19, 7, 20), 740.00m, PaymentMethod.Cash, QrTotal: null,
            [
                new("Burek sa sirom", 2m, "kom", 220.00m, 440.00m, null),
                new("Jogurt 1L", 1m, "kom", 150.00m, 150.00m, null),
                new("Kifla", 3m, "kom", 40.00m, 120.00m, null),
            ])),
    ];

    // Two legs each, so they do not fit MockRecord; MockDataWriter posts them through LedgerPostings, as the app does.
    // The plain transfer comes the day after Wise's statement, so that statement's computedBefore stays true.
    public static IReadOnlyList<MockTransfer> Transfers { get; } =
    [
        new(WithdrawalTransactionId, "withdrew 10000 rsd from raif, fee 150", Day(6),
            "Raiffeisen", 10150.00m, "Cash RSD", 10000.00m, Fee: 150.00m, FeeLeg: TransferLeg.From),
        new(ExchangeTransactionId, "exchanged 100 eur for 11700 rsd", Day(7),
            "Cash EUR", 100.00m, "Cash RSD", 11700.00m),
        new(TransferTransactionId, "moved 200 eur from wise to cash", Day(16),
            "Wise", 200.00m, "Cash EUR", 200.00m),
    ];

    // Charged at Kaspi's USD terms, 520 and 1 %: 15 600.00 KZT and a 156.00 KZT fee.
    public static MockForeignSpending ForeignSpending { get; } = new(
        ForeignSpendingTransactionId, "Kaspi", "app store 30 usd", Day(13),
        new MockLine("App Store", 30.00m, "subscriptions", null), CurrencyCode.Usd,
        Charged: 15600.00m, Fee: 156.00m, Rate: 520m, FeePercent: 1m);

    public static Guid Id(int number) => Guid.Parse($"7a1c0000-0000-4000-8000-{number:D12}");

    public static DateTimeOffset At(int day, int hour, int minute) =>
        new(MonthStart.Year, MonthStart.Month, day, hour, minute, 0, TimeSpan.Zero);

    static DateOnly Day(int day) => MonthStart.AddDays(day - 1);

    static DateTimeOffset BeforeTheMonth(int days, int hour, int minute) =>
        new(MonthStart.AddDays(-days).ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero);

    static MockRecord Expense(string wallet, string raw, DateOnly day, IReadOnlyList<MockLine> lines, Guid? id = null) =>
        new(wallet, TransactionKind.Expense, TransactionStatus.Completed, raw, day, lines, id);

    static MockRecord Income(string wallet, string raw, DateOnly day, MockLine line) =>
        new(wallet, TransactionKind.Income, TransactionStatus.Completed, raw, day, [line]);

    static MockRecord Statement(string wallet, string raw, DateOnly day, decimal stated, decimal computedBefore) =>
        new(wallet, TransactionKind.BalanceCheck, TransactionStatus.Completed, raw, day, [], Stated: stated, ComputedBefore: computedBefore);

    static MockRecord Failed(string wallet, string raw, DateOnly day, Guid id) =>
        new(wallet, TransactionKind.Expense, TransactionStatus.Failed, raw, day, [], id);

    // A recorded receipt lands in the dinar wallet marked default for how it was paid, as the app would put it.
    static MockRecord Receipt(TransactionStatus status, DateOnly day, Guid id, MockReceipt receipt) =>
        new(status == TransactionStatus.Completed ? DinarWalletFor(receipt.Payment) : null,
            TransactionKind.Expense, status, RawText: null, day,
            status == TransactionStatus.Completed
                ? [.. receipt.Lines.Select(line => new MockLine(line.Name, line.Total, line.CategorySlug!, receipt.SellerName))]
                : [],
            id, Receipt: receipt);

    public static string DinarWalletFor(PaymentMethod payment) =>
        Wallets.Single(wallet => wallet.PaymentDefault == payment && wallet.Currency == CurrencyCode.Rsd).Name;
}

internal sealed record MockWallet(
    string Name, CurrencyCode Currency, decimal Opening, IReadOnlyList<string> Aliases, bool Default, bool Archived = false,
    PaymentMethod? PaymentDefault = null, IReadOnlyList<WalletTermsDetails>? Terms = null);

internal sealed record MockLine(string Description, decimal Amount, string CategorySlug, string? Merchant);

// Wallet is null only for a receipt still waiting for Record anyway: the app picks one when it categorises.
internal sealed record MockRecord(
    string? Wallet,
    TransactionKind Kind,
    TransactionStatus Status,
    string? RawText,
    DateOnly Day,
    IReadOnlyList<MockLine> Lines,
    Guid? Id = null,
    decimal? Stated = null,
    decimal? ComputedBefore = null,
    MockReceipt? Receipt = null);

// Each amount is all that moved in its wallet, the fee inside its leg (T-12).
internal sealed record MockTransfer(
    Guid Id, string RawText, DateOnly Day, string From, decimal FromAmount, string To, decimal ToAmount,
    decimal? Fee = null, TransferLeg? FeeLeg = null);

internal sealed record MockForeignSpending(
    Guid Id, string Wallet, string RawText, DateOnly Day, MockLine Line, CurrencyCode LineCurrency,
    decimal Charged, decimal Fee, decimal Rate, decimal FeePercent);

internal sealed record MockReceipt(
    ReceiptSource Source,
    string SellerName,
    string? LocationName,
    string? SellerAddress,
    string? SellerTaxId,
    string? FiscalNumber,
    DateTimeOffset IssuedAt,
    decimal Total,
    PaymentMethod Payment,
    decimal? QrTotal,
    IReadOnlyList<MockReceiptLine> Lines);

internal sealed record MockReceiptLine(string Name, decimal Quantity, string Unit, decimal UnitPrice, decimal Total, string? CategorySlug);

internal sealed record MockLogRow(DateTimeOffset At, LogSeverity Level, string Source, string Message, string? Exception = null);
