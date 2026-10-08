using Noof.Ledger.Application.Reporting.Summary;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Tests.Summary;

internal sealed record TestLine(string Category, string Description, decimal Amount, CurrencyCode Currency, EntryRole Role);

// Rows in the shape the summary's rows reader returns them, so a calculator test states its money in a few lines. A
// record's lines get ordinals in the order given; a transfer with a fee gets its Fee line on the fee leg's wallet.
internal sealed class SummaryRowsBuilder
{
    public static readonly Guid WiseId = new("5a000000-0000-4000-8000-000000000001");
    public static readonly Guid CashRsdId = new("5a000000-0000-4000-8000-000000000002");
    public static readonly Guid KaspiId = new("5a000000-0000-4000-8000-000000000003");
    public static readonly Guid OldRevolutId = new("5a000000-0000-4000-8000-000000000004");
    public static readonly Guid MainId = new("5a000000-0000-4000-8000-000000000005");
    public static readonly Guid DormantId = new("5a000000-0000-4000-8000-000000000006");

    readonly List<SummaryWallet> wallets = [];
    readonly List<SummaryLineRow> lines = [];
    readonly List<SummaryTransferRow> transfers = [];
    int records;

    public static TestLine Line(string category, decimal amount, CurrencyCode? currency = null, string? description = null) =>
        new(category, description ?? category, amount, currency ?? CurrencyCode.Eur, EntryRole.Principal);

    public static TestLine Fee(decimal amount, CurrencyCode currency) =>
        new("Fees & Charges", "Fee", amount, currency, EntryRole.Fee);

    public SummaryRowsBuilder Wallet(Guid id, string name, CurrencyCode currency, DateOnly? historyStart, bool archived = false)
    {
        wallets.Add(new SummaryWallet(id, name, currency, archived, historyStart));
        return this;
    }

    public Guid Expense(Guid walletId, DateOnly day, string? merchant, params TestLine[] items) =>
        Record(walletId, day, merchant, SummaryLineKind.Spent, charged: null, items);

    // The charge prices every Principal line in a currency other than the wallet's, as a charges row does.
    public Guid ChargedExpense(Guid walletId, DateOnly day, string? merchant, decimal charged, params TestLine[] items) =>
        Record(walletId, day, merchant, SummaryLineKind.Spent, charged, items);

    public Guid Income(Guid walletId, DateOnly day, string? merchant, params TestLine[] items) =>
        Record(walletId, day, merchant, SummaryLineKind.Received, charged: null, items);

    public Guid Refund(Guid walletId, DateOnly day, string? merchant, params TestLine[] items) =>
        Record(walletId, day, merchant, SummaryLineKind.Refund, charged: null, items);

    public Guid Transfer(
        DateOnly day, Guid fromId, decimal from, Guid toId, decimal to,
        TransferLeg? feeLeg = null, decimal? fee = null, string? venue = null)
    {
        var id = NextRecordId();
        transfers.Add(new SummaryTransferRow(
            id, day, fromId, new Money(from, CurrencyOf(fromId)), toId, new Money(to, CurrencyOf(toId)), feeLeg, fee, venue));

        if (feeLeg is { } leg && fee is { } amount)
        {
            var feeWallet = leg == TransferLeg.From ? fromId : toId;
            lines.Add(new SummaryLineRow(
                id, day, feeWallet, CurrencyOf(feeWallet), SummaryLineKind.Spent, EntryRole.Fee, 0, "Fees & Charges",
                venue, "Fee", amount, CurrencyOf(feeWallet), null));
        }

        return id;
    }

    public SummaryRows Build() => new([.. wallets], [.. lines], [.. transfers]);

    Guid Record(Guid walletId, DateOnly day, string? merchant, SummaryLineKind kind, decimal? charged, TestLine[] items)
    {
        var id = NextRecordId();
        var walletCurrency = CurrencyOf(walletId);
        for (var ordinal = 0; ordinal < items.Length; ordinal++)
        {
            var item = items[ordinal];
            var priced = charged is not null && item.Role == EntryRole.Principal && item.Currency != walletCurrency;
            lines.Add(new SummaryLineRow(
                id, day, walletId, walletCurrency, kind, item.Role, ordinal, item.Category, merchant, item.Description,
                item.Amount, item.Currency, priced ? charged : null));
        }

        return id;
    }

    CurrencyCode CurrencyOf(Guid walletId) => wallets.Single(wallet => wallet.Id == walletId).Currency;

    Guid NextRecordId() => new($"7e000000-0000-4000-8000-{++records:D12}");
}
