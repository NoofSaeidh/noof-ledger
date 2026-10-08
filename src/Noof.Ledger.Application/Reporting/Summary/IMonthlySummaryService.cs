using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting.Summary;

public readonly record struct SummaryScope(Guid? WalletId)
{
    public static SummaryScope AllWallets { get; } = new(null);

    public bool IsAllWallets => WalletId is null;
}

// LastDay is today for the current month (Finished false), else the month's last day.
public sealed record SummaryPeriod(DateOnly FirstDay, DateOnly LastDay, bool Finished);

// Previous: the previous month's window, null when it is before the scope's history start (A-5); Average: the mean of
// AverageMonths windows (null when 0). Spec A-6 decides how a change is shown; the record holds only figures.
public sealed record SummaryAmount(decimal Amount, decimal? Previous, decimal? Average);

public enum HighlightBase
{
    Unknown = 0,
    Average = 1,
    PreviousMonth = 2,
}

public sealed record SummaryHighlight(string CategoryName, decimal Amount, decimal Change, HighlightBase Against);

public sealed record SummaryCategory(string CategoryName, SummaryAmount Amount);

public sealed record SummaryMerchant(string MerchantName, decimal Amount, int Records);

public sealed record SummaryRecord(Guid TransactionId, DateOnly OccurredOn, string Label, decimal Amount, bool Approximate);

public sealed record SummaryWalletLine(Guid WalletId, string WalletName, CurrencyCode Currency, decimal Spent, decimal Received, bool Approximate);

public sealed record SummaryNotConverted(CurrencyCode Currency, decimal Spent, decimal Received);

public sealed record SummaryWalletOption(Guid WalletId, string WalletName, CurrencyCode Currency, bool Archived);

public sealed record MonthlySummary(
    SummaryPeriod Period,
    SummaryScope Scope,
    CurrencyCode Currency,           // the chosen currency, or the wallet's in a wallet scope
    string? WalletName,              // null for all wallets
    SummaryAmount Spent,
    SummaryAmount Received,
    SummaryAmount Net,
    decimal? MovedOut,               // wallet scope only
    decimal? MovedIn,                // wallet scope only
    int AverageMonths,
    IReadOnlyList<SummaryHighlight> Highlights,     // at most 3
    IReadOnlyList<SummaryCategory> Categories,      // by Amount descending, then name
    IReadOnlyList<SummaryMerchant> TopMerchants,    // at most 5
    IReadOnlyList<SummaryRecord> LargestRecords,    // at most 5
    IReadOnlyList<SummaryWalletLine> Wallets,       // all-wallets scope only, else empty
    IReadOnlyList<SummaryNotConverted> NotConverted,
    IReadOnlyList<SummaryWalletOption> WalletOptions, // spec P-10; the same in every scope
    bool AnyApproximate,
    DateOnly? OldestRateDate,
    DateOnly? NewestRateDate,
    bool HasAnyRecords,              // anything counted in the month itself
    bool NoRates);                   // all-wallets scope, something needed converting and nothing could be: the view is the Not converted block only

public interface IMonthlySummaryService
{
    // The first day of the current month in the configured zone (TimeZoneInfo singleton + TimeProvider).
    DateOnly CurrentMonth();

    // firstDayOfMonth after CurrentMonth() throws ArgumentOutOfRangeException; currency outside ReportingCurrencies.All
    // likewise (ignored, and may be anything, in a wallet scope). An id absent from SummaryRows.Wallets throws
    // KeyNotFoundException; a real wallet with nothing in the months read gives zeros.
    Task<MonthlySummary> BuildAsync(DateOnly firstDayOfMonth, SummaryScope scope, CurrencyCode currency, CancellationToken cancellationToken);
}
