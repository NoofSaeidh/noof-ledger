using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting.Summary;

public enum SummaryLineKind
{
    Unknown = 0,
    Spent = 1,     // an expense's Principal line, or any Fee line (expense or transfer)
    Received = 2,  // an income's Principal line that is not a refund
    Refund = 3,    // a Principal line of an Income record whose receipt has receipt_kind = Refund (spec SS-19)
}

// WalletId is the line's wallet: the record's, except a transfer's Fee line, which is its fee_leg's wallet.
// ChargedAmount is the charges row of (record, Currency) for an expense's Principal line, in WalletCurrency; else null.
// MerchantName is the record's merchant per spec P-7, the same on every line of the record.
public sealed record SummaryLineRow(
    Guid TransactionId,
    DateOnly OccurredOn,
    Guid WalletId,
    CurrencyCode WalletCurrency,
    SummaryLineKind Kind,
    EntryRole Role,
    int Ordinal,
    string CategoryName,
    string? MerchantName,
    string Description,
    decimal Amount,
    CurrencyCode Currency,
    decimal? ChargedAmount);

// From and To as stored: what each wallet moved, the fee inside its leg (T-12).
public sealed record SummaryTransferRow(
    Guid TransactionId,
    DateOnly OccurredOn,
    Guid FromWalletId,
    Money From,
    Guid ToWalletId,
    Money To,
    TransferLeg? FeeLeg,
    decimal? Fee,
    string? VenueName);

// HistoryStart: the first day of the first month, over the whole history, with a counted line on this wallet.
public sealed record SummaryWallet(Guid Id, string Name, CurrencyCode Currency, bool Archived, DateOnly? HistoryStart);

public sealed record SummaryRows(
    IReadOnlyList<SummaryWallet> Wallets,
    IReadOnlyList<SummaryLineRow> Lines,
    IReadOnlyList<SummaryTransferRow> Transfers);

public interface ISummaryRowsReader
{
    // Lines and transfers with occurred_on in [from, toExclusive), status <> Cancelled. Wallets: every wallet, archived
    // ones included, each with its whole-history start — the all-wallets start is their minimum (spec A-5).
    Task<SummaryRows> ReadAsync(DateOnly from, DateOnly toExclusive, CancellationToken cancellationToken);

    // Spec P-3: a Pending or Claimed categorization job, of any kind, on a non-cancelled record with
    // occurred_on >= since.
    Task<bool> HasUnsettledWorkAsync(DateOnly since, CancellationToken cancellationToken);
}
