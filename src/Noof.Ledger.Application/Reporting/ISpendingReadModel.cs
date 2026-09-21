using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting;

public sealed record RecentLineItem(string Description, Money Amount, string? CategoryName, string? MerchantName);

public sealed record RecentTransaction(
    Guid Id,
    DateTimeOffset OccurredAt,
    string TimeZoneId,
    string RawText,
    TransactionStatus Status,
    string WalletName,
    IReadOnlyList<RecentLineItem> Items);

public sealed record MonthTotal(string CategoryName, CurrencyCode Currency, decimal Amount);

public sealed record MonthSummary(DateOnly FirstDay, IReadOnlyList<MonthTotal> Totals);

public interface ISpendingReadModel
{
    Task<IReadOnlyList<RecentTransaction>> RecentAsync(int limit, CancellationToken cancellationToken);

    // "This month" is decided by the read model, not the page, so there is exactly one definition
    // of it. Each row is bucketed by ITS OWN stored time zone (decision P1-3).
    Task<MonthSummary> ThisMonthAsync(CancellationToken cancellationToken);
}
