using System.Globalization;
using System.Text.RegularExpressions;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Receipts.Journal;

// Section shape (delimiters, header layout, item/payment/fiscalization key-value lines) verified
// against turanjanin/serbian-fiscal-receipts-parser's Parser.php (MIT):
// https://github.com/turanjanin/serbian-fiscal-receipts-parser
internal static partial class FiscalJournalParser
{
    static readonly (string Key, PaymentMethod Method)[] PaymentKeys =
    [
        ("Платна картица", PaymentMethod.Card), ("Platna kartica", PaymentMethod.Card),
        ("Готовина", PaymentMethod.Cash), ("Gotovina", PaymentMethod.Cash),
        ("Пренос на рачун", PaymentMethod.Transfer), ("Prenos na račun", PaymentMethod.Transfer),
        ("Ваучер", PaymentMethod.Voucher), ("Vaučer", PaymentMethod.Voucher),
        ("Друго безготовинско", PaymentMethod.Other), ("Drugo bezgotovinsko", PaymentMethod.Other),
    ];

    public static ParsedJournal? Parse(string journal)
    {
        var allLines = journal.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (allLines.Length < 3)
            return null;

        var content = allLines[1..^1];
        var sections = SplitBy(content, IsDelimiterOf('='));
        if (sections.Count < 3)
            return null;

        var header = ParseHeader(sections[0]);

        var itemsAndPayment = SplitBy(sections[1], IsDelimiterOf('-'));
        if (itemsAndPayment.Count < 2)
            return null;

        var lines = ParseItemLines(itemsAndPayment[0]);
        if (lines.Count == 0)
            return null;

        var payment = ExtractKeyValues(itemsAndPayment[1]);

        var fiscalSection = sections.Skip(2)
            .FirstOrDefault(section => section.Any(line => line.Contains("ПФР", StringComparison.Ordinal) || line.Contains("PFR", StringComparison.Ordinal)));
        if (fiscalSection is null)
            return null;

        var fiscal = ExtractKeyValues(fiscalSection);
        var issuedAt = ParseBelgradeTime(fiscal.GetValueOrDefault("ПФР време") ?? fiscal.GetValueOrDefault("PFR vreme"));
        var fiscalNumber = fiscal.GetValueOrDefault("ПФР број рачуна") ?? fiscal.GetValueOrDefault("PFR broj računa");

        return new ParsedJournal(
            SellerTaxId: header.Tin,
            SellerName: header.Name,
            SellerAddress: header.Address,
            LocationName: header.Location,
            FiscalNumber: fiscalNumber,
            IssuedAt: issuedAt,
            Total: ParseTotal(payment),
            PaymentMethod: ParsePaymentMethod(payment),
            Lines: lines);
    }

    static (string? Tin, string? Name, string? Location, string? Address, string? City) ParseHeader(string[] headerLines)
    {
        if (headerLines.Length == 0)
            return (null, null, null, null, null);

        var tin = headerLines[0].Trim();
        var nameLines = new List<string>();
        string? location = null;
        var i = 1;

        for (; i < headerLines.Length; i++)
        {
            var line = headerLines[i].Trim();
            var match = LocationLine().Match(line);
            if (match.Success)
            {
                location = match.Groups["name"].Value.Trim();
                i++;
                break;
            }

            if (line.Length > 0)
                nameLines.Add(line);
        }

        var address = i < headerLines.Length ? headerLines[i++].Trim() : null;
        var city = i < headerLines.Length ? headerLines[i++].Trim() : null;

        return (tin, string.Join(' ', nameLines), location, address, city);
    }

    static List<ExtractedReceiptLine> ParseItemLines(string[] lines)
    {
        var result = new List<ExtractedReceiptLine>();
        var nameBuffer = new List<string>();
        var ordinal = 0;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            // Real journals print a column-header row (Назив Цена Кол. Укупно / Naziv Cena Kol.
            // Ukupno) as the first line of the items section, between the `====` delimiter and the
            // first article - the upstream parser this was ported from skips it explicitly (I-1,
            // 2026-09-25 final review). Left in, it never matches AmountLine below, so it joins the
            // name buffer and gets glued onto line 1's name on every real receipt.
            if (ItemsHeaderRow().IsMatch(line))
                continue;

            var match = AmountLine().Match(line);
            if (!match.Success)
            {
                nameBuffer.Add(line);
                continue;
            }

            if (nameBuffer.Count == 0)
                continue;

            var (name, unit, taxLabel) = ParseItemName(string.Join(' ', nameBuffer));
            nameBuffer.Clear();
            ordinal++;

            result.Add(new ExtractedReceiptLine(
                Ordinal: ordinal,
                Name: name,
                Quantity: ParseAmount(match.Groups["qty"].Value),
                Unit: unit,
                UnitPrice: ParseAmount(match.Groups["price"].Value),
                Total: ParseAmount(match.Groups["total"].Value),
                TaxLabel: taxLabel));
        }

        return result;
    }

    static (string Name, string? Unit, string? TaxLabel) ParseItemName(string nameLine)
    {
        var match = ItemName().Match(nameLine);
        if (!match.Success)
            return (nameLine, null, null);

        var unit = match.Groups["unit"].Success ? match.Groups["unit"].Value : null;
        var tax = match.Groups["tax"].Success ? match.Groups["tax"].Value : null;
        return (match.Groups["name"].Value.Trim(), unit, tax);
    }

    static Dictionary<string, string> ExtractKeyValues(IEnumerable<string> lines)
    {
        var pairs = new Dictionary<string, string>();
        foreach (var line in lines)
        {
            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator < 0)
                continue;

            pairs[line[..separator].Trim()] = line[(separator + 2)..].Trim();
        }

        return pairs;
    }

    static decimal ParseTotal(Dictionary<string, string> payment)
    {
        var refund = payment.GetValueOrDefault("Укупна рефундација") ?? payment.GetValueOrDefault("Ukupna refundacija");
        if (refund is not null)
            return ParseAmount(refund);

        var sale = payment.GetValueOrDefault("Укупан износ") ?? payment.GetValueOrDefault("Ukupan iznos");
        return sale is not null ? ParseAmount(sale) : 0m;
    }

    static PaymentMethod? ParsePaymentMethod(Dictionary<string, string> payment)
    {
        var matched = new HashSet<PaymentMethod>();
        foreach (var (key, method) in PaymentKeys)
        {
            if (payment.TryGetValue(key, out var raw) && ParseAmount(raw) != 0)
                matched.Add(method);
        }

        return matched.Count switch
        {
            0 => null,
            1 => matched.First(),
            _ => PaymentMethod.Mixed,
        };
    }

    static DateTimeOffset? ParseBelgradeTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!DateTime.TryParseExact(raw.Trim(), "dd.MM.yyyy. HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;

        var belgrade = TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade");
        return new DateTimeOffset(local, belgrade.GetUtcOffset(local));
    }

    static decimal ParseAmount(string raw)
    {
        var value = raw.Trim();
        if (value.Contains(','))
            value = value.Replace(".", string.Empty).Replace(',', '.');

        return decimal.Parse(value, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    static Func<string, bool> IsDelimiterOf(char character) =>
        line => line.Trim() is { Length: >= 3 } trimmed && trimmed.All(c => c == character);

    static List<string[]> SplitBy(string[] lines, Func<string, bool> isDelimiter)
    {
        var sections = new List<List<string>>();
        var current = new List<string>();

        foreach (var line in lines)
        {
            if (isDelimiter(line))
            {
                sections.Add(current);
                current = [];
                continue;
            }

            current.Add(line);
        }

        sections.Add(current);
        return [.. sections.Select(section => section.ToArray())];
    }

    [GeneratedRegex(@"^(?<name>.+?)(?:/(?<unit>[^\s()]+))?\s*(?:\((?<tax>[^)]+)\))?$")]
    private static partial Regex ItemName();

    [GeneratedRegex(@"^(?<price>-?[\d.,]+)\s+(?<qty>-?[\d.,]+)\s+(?<total>-?[\d.,]+)$")]
    private static partial Regex AmountLine();

    [GeneratedRegex(@"^\d+-(?<name>.+)$")]
    private static partial Regex LocationLine();

    [GeneratedRegex(@"^(Назив|Naziv)\s+(Цена|Cena)\s+(Кол\.|Kol\.)\s+(Укупно|Ukupno)$")]
    private static partial Regex ItemsHeaderRow();
}
