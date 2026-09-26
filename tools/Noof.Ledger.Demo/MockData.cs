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
    public static readonly DateOnly LogWindowStart = MonthStart;
    public static readonly DateOnly LogWindowEnd = MonthStart.AddDays(20);
    public static readonly Guid TracedTransactionId = Id(900);
    public static readonly Guid FailedTransactionId = Id(901);

    public static IReadOnlyList<MockWallet> Wallets { get; } =
    [
        new("Wise", CurrencyCode.Eur, 3000.00m, ["wise"], Default: true),
        new("Raiffeisen", CurrencyCode.Rsd, 180000.00m, ["raif"], Default: true),
        new("Cash", CurrencyCode.Usd, 600.00m, [], Default: true),
        new("Tinkoff", CurrencyCode.Rub, 50000.00m, [], Default: true),
        new("Kaspi", CurrencyCode.Kzt, 200000.00m, [], Default: true),
        new("Old Revolut", CurrencyCode.Eur, 900.00m, [], Default: false, Archived: true),
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
        Expense("Wise", "dinner at Walter 42 eur", Day(12), [new("Dinner", 42.00m, "restaurants", "Walter")]),
        Expense("Tinkoff", "groceries 1450 rub", Day(13), [new("Groceries", 1450m, "groceries", null)]),
        Expense("Raiffeisen", "pharmacy 1200 rsd", Day(14), [new("Pharmacy", 1200m, "health", null)]),
        Statement("Wise", "wise balance 5700", Day(15), stated: 5700.00m, computedBefore: 5720.30m),
        Expense("Kaspi", "groceries 8500 kzt", Day(16), [new("Groceries", 8500m, "groceries", null)]),
        Expense("Wise", "Lidl groceries 27.80, wine 12.50 eur", Day(18),
            [new("Groceries", 27.80m, "groceries", "Lidl"), new("Wine", 12.50m, "groceries", "Lidl")],
            TracedTransactionId),
        Failed("Raiffeisen", "#@%& ???", Day(19), FailedTransactionId),
    ];

    public static Guid Id(int number) => Guid.Parse($"7a1c0000-0000-4000-8000-{number:D12}");

    public static DateTimeOffset At(int day, int hour, int minute) =>
        new(MonthStart.Year, MonthStart.Month, day, hour, minute, 0, TimeSpan.Zero);

    static DateOnly Day(int day) => MonthStart.AddDays(day - 1);

    static MockRecord Expense(string wallet, string raw, DateOnly day, IReadOnlyList<MockLine> lines, Guid? id = null) =>
        new(wallet, TransactionKind.Expense, TransactionStatus.Completed, raw, day, lines, id);

    static MockRecord Income(string wallet, string raw, DateOnly day, MockLine line) =>
        new(wallet, TransactionKind.Income, TransactionStatus.Completed, raw, day, [line]);

    static MockRecord Statement(string wallet, string raw, DateOnly day, decimal stated, decimal computedBefore) =>
        new(wallet, TransactionKind.BalanceCheck, TransactionStatus.Completed, raw, day, [], Stated: stated, ComputedBefore: computedBefore);

    static MockRecord Failed(string wallet, string raw, DateOnly day, Guid id) =>
        new(wallet, TransactionKind.Expense, TransactionStatus.Failed, raw, day, [], id);
}

internal sealed record MockWallet(string Name, CurrencyCode Currency, decimal Opening, IReadOnlyList<string> Aliases, bool Default, bool Archived = false);

internal sealed record MockLine(string Description, decimal Amount, string CategorySlug, string? Merchant);

internal sealed record MockRecord(
    string Wallet,
    TransactionKind Kind,
    TransactionStatus Status,
    string RawText,
    DateOnly Day,
    IReadOnlyList<MockLine> Lines,
    Guid? Id = null,
    decimal? Stated = null,
    decimal? ComputedBefore = null);
