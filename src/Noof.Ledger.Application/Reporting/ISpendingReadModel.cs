using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting;

public sealed record RecentLineItem(
    string Description, Money Amount, string? CategoryName, string? MerchantName, EntryRole Role = EntryRole.Principal);

public enum RecentView
{
    SpendingAndIncome = 0,
    Transfers = 1,
    All = 2,
}

// From and To are what each wallet moved, the fee inside its leg's amount (T-12). Rate is the stated rate when the
// operator said one, otherwise the rate of the two principals, and null between two wallets of one currency.
public sealed record TransferLine(
    string FromWalletName, Money From, string ToWalletName, Money To, Money? Fee, TransferLeg? FeeLeg, ExchangeRate? Rate);

public sealed record MonthTransfer(Guid Id, DateOnly OccurredOn, TransferLine Transfer);

// LocalTime is null when the purchase was dated to a day other than the one the message was sent on:
// only the day is known, and the dashboard does not invent a time for it (D3).
public sealed record RecentTransaction(
    Guid Id,
    DateOnly OccurredOn,
    TimeOnly? LocalTime,
    string RawText,
    TransactionStatus Status,
    string WalletName,
    IReadOnlyList<RecentLineItem> Items,
    TransactionKind Kind = TransactionKind.Expense,
    TransferLine? Transfer = null);

public sealed record MonthTotal(string CategoryName, CurrencyCode Currency, decimal Amount);

// Totals is what was spent, Received what came in as income, each by category and currency and never converted.
public sealed record MonthSummary(DateOnly FirstDay, IReadOnlyList<MonthTotal> Totals, IReadOnlyList<MonthTotal>? Received = null);

public interface ISpendingReadModel
{
    // SpendingAndIncome is every kind but Transfer, records not yet read included; Transfers is transfers only (T-9).
    Task<IReadOnlyList<RecentTransaction>> RecentAsync(int limit, RecentView view, CancellationToken cancellationToken);

    // "This month" is decided by the read model, not the page, so there is exactly one definition of
    // it. Rows are bucketed by occurred_on, the local day stamped per row at capture. Totals are the Principal lines of
    // expenses plus the Fee lines of expenses and transfers; Received the Principal lines of incomes.
    Task<MonthSummary> ThisMonthAsync(CancellationToken cancellationToken);

    // Newest first, cancelled ones left out.
    Task<IReadOnlyList<MonthTransfer>> TransfersThisMonthAsync(CancellationToken cancellationToken);
}
