using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting;

public sealed record RecentLineItem(string Description, Money Amount, string? CategoryName, string? MerchantName);

// LocalTime is null when the purchase was dated to a day other than the one the message was sent on:
// only the day is known, and the dashboard does not invent a time for it (D3).
public sealed record RecentTransaction(
    Guid Id,
    DateOnly OccurredOn,
    TimeOnly? LocalTime,
    string RawText,
    TransactionStatus Status,
    string WalletName,
    IReadOnlyList<RecentLineItem> Items);

public sealed record MonthTotal(string CategoryName, CurrencyCode Currency, decimal Amount);

public sealed record MonthSummary(DateOnly FirstDay, IReadOnlyList<MonthTotal> Totals);

public interface ISpendingReadModel
{
    Task<IReadOnlyList<RecentTransaction>> RecentAsync(int limit, CancellationToken cancellationToken);

    // "This month" is decided by the read model, not the page, so there is exactly one definition of
    // it. Rows are bucketed by occurred_on, the local day stamped per row at capture.
    Task<MonthSummary> ThisMonthAsync(CancellationToken cancellationToken);
}
