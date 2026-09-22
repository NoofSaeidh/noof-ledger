using System.Globalization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Workers;

public static class CategorizationReply
{
    public sealed record ReplyLine(string Description, Money Amount, string CategoryName);

    public static string ComposeSuccess(string walletName, IReadOnlyList<ReplyLine> lines)
    {
        var itemLines = lines.Select(FormatLine);

        var totals = lines
            .GroupBy(line => line.Amount.Currency)
            .OrderBy(group => group.Key.Value, StringComparer.Ordinal)
            .Select(group => $"{FormatAmount(group.Sum(line => line.Amount.Amount))} {group.Key}");

        return $"Categorised — {walletName}\n{string.Join('\n', itemLines)}\n\nTotal: {string.Join(", ", totals)}";
    }

    public static string ComposeFailure(string walletName) =>
        $"I couldn't categorise this one for {walletName} automatically. It's saved, nothing is lost, " +
        "but you'll need to sort it out by hand for now.";

    // Distinct from ComposeFailure on purpose: the categoriser read the message and reached a
    // conclusion (a loan received, not a purchase, for example) - it did not fail. Wording this
    // like a failure would tell the operator to go fix something that isn't broken.
    public static string ComposeNothingToRecord(string walletName) =>
        $"Read this for {walletName} — nothing here looks like spending, so nothing was recorded.";

    static string FormatLine(ReplyLine line) =>
        $"• {line.Description} — {FormatAmount(line.Amount.Amount)} {line.Amount.Currency} ({line.CategoryName})";

    static string FormatAmount(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
