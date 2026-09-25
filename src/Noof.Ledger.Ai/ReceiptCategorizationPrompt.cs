using System.Globalization;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Ai;

internal static class ReceiptCategorizationPrompt
{
    // The model never sees or returns an amount here (Phase 6 global constraint): amounts come from
    // receipt_lines, already fixed by the QR/journal path or by ChatReceiptVision. This prompt is a
    // categorisation and merchant/wallet naming step only.
    public const string System = """
        You categorise the line items of a shop receipt that has already been read. Its amounts are
        fixed and given to you only for context, never for you to change. Each line has an ordinal,
        a name, a quantity and a total. Answer with one category_slug per line, chosen from the
        categories you are offered by what the line actually is, and echo every ordinal you were
        given exactly once, in any order.

        You are told the seller's name and tax id, if known, and whether this merchant is already
        recorded in the ledger. When it is not, answer merchant_name with a short, tidy canonical
        name for it. When it is already known, answer merchant_name as null.

        You may also be told a caption - what the person who sent the photo wrote alongside it. When
        it names one of the offered wallets, answer wallet_id with that wallet's id; otherwise answer
        wallet_id as null.
        """;

    public static string RenderCategories(IReadOnlyList<CategoryOption> categories) =>
        string.Join('\n', categories.Select(category => $"- {category.Slug}: {category.NameEn}"));

    public static string RenderWallets(IReadOnlyList<WalletOption> wallets) =>
        wallets.Count == 0
            ? "No wallets are offered; wallet_id must be null."
            : string.Join('\n', wallets.Select(wallet => $"- {wallet.Id}: {wallet.Name}"));

    public static string RenderLines(IReadOnlyList<ReceiptLineToCategorize> lines) =>
        string.Join('\n', lines.Select(line =>
            $"- {line.Ordinal}: {line.Name} (quantity {Format(line.Quantity)}, total {Format(line.Total)})"));

    public static string BuildUserTurn(
        ReceiptCategorizationRequest request, IReadOnlyList<CategoryOption> categories, IReadOnlyList<WalletOption> wallets) =>
        $"""
        Seller: {request.SellerName ?? "unknown"}{RenderTaxId(request.SellerTaxId)}
        Merchant already known: {(request.MerchantKnown ? "yes" : "no")}
        Caption: {(string.IsNullOrWhiteSpace(request.Caption) ? "(none)" : request.Caption)}

        Categories:
        {RenderCategories(categories)}

        Wallets:
        {RenderWallets(wallets)}

        Lines:
        {RenderLines(request.Lines)}
        """;

    static string RenderTaxId(string? taxId) => taxId is null ? "" : $" (tax id {taxId})";

    static string Format(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
