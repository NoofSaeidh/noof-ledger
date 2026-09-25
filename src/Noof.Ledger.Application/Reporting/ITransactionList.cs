using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting;

public sealed record TransactionListFilter(
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? WalletId = null,
    TransactionKind? Kind = null,
    TransactionStatus? Status = null,
    string? Text = null);

public sealed record TransactionListRow(
    Guid Id,
    DateOnly OccurredOn,
    TimeOnly? OccurredAt,
    string WalletName,
    TransactionKind Kind,
    TransactionStatus Status,
    string? RawText,
    IReadOnlyList<Money> Amounts,
    IReadOnlyList<string> Categories);

public sealed record TransactionListPage(IReadOnlyList<TransactionListRow> Rows, int TotalCount);

public interface ITransactionList
{
    Task<TransactionListPage> QueryAsync(TransactionListFilter filter, int pageIndex, int pageSize,
        CancellationToken cancellationToken);
}
