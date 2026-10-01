using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting;

// WalletId matches a record's own wallet or either leg of a transfer.
public sealed record TransactionListFilter(
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? WalletId = null,
    TransactionKind? Kind = null,
    TransactionStatus? Status = null,
    string? Text = null,
    bool ReceiptsOnly = false,
    bool ExcludeTransfers = false);

// Amounts: an expense's purchases per currency, an income's entries, a statement's stated amount, a transfer's
// [From, To] as each wallet moved them (Transfer carries the rest).
public sealed record TransactionListRow(
    Guid Id,
    DateOnly OccurredOn,
    TimeOnly? OccurredAt,
    string WalletName,
    TransactionKind Kind,
    TransactionStatus Status,
    string? RawText,
    IReadOnlyList<Money> Amounts,
    IReadOnlyList<string> Categories,
    bool HasReceipt = false,
    string? ShopName = null,
    TransferLine? Transfer = null);

public sealed record TransactionListPage(IReadOnlyList<TransactionListRow> Rows, int TotalCount);

public interface ITransactionList
{
    Task<TransactionListPage> QueryAsync(TransactionListFilter filter, int pageIndex, int pageSize,
        CancellationToken cancellationToken);
}
