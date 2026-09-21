using System.Globalization;
using System.Text;

namespace Noof.Ledger.Domain;

public static class QuotedAmount
{
    // Telegram's iOS/Android clients sometimes auto-group a typed number with a thin space
    // (U+2009) or a non-breaking space (U+00A0 / U+202F) instead of a comma or dot. These are
    // always grouping marks, never a decimal separator, so they are stripped unconditionally.
    // Written as explicit \u escapes, not literal glyphs: these three characters are visually
    // indistinguishable from a regular space (and from each other) in most editors and through
    // any copy-paste via markdown/JSON -- which is exactly how this array first shipped as three
    // copies of U+0020, silently defeating the handling this comment describes. Verified by
    // compiling and running this exact file: with the escapes below, a real U+2009/U+00A0
    // grouped quote resolves correctly; with three literal spaces it does not.
    static readonly char[] SpaceGroupers = [' ', ' ', ' '];

    static readonly CurrencyCode[] KnownCurrencies =
    [
        CurrencyCode.Eur, CurrencyCode.Rsd, CurrencyCode.Usd, CurrencyCode.Rub, CurrencyCode.Kzt,
    ];

    public static bool TryResolve(
        string rawText,
        string? amountQuote,
        string? currencyCode,
        out Money money,
        out string failure)
    {
        money = default;

        if (string.IsNullOrWhiteSpace(amountQuote))
        {
            failure = "Amount quote is empty or whitespace.";
            return false;
        }

        // Verbatim check against the quote exactly as given -- before any normalisation. This
        // is the entire safety property this type exists to enforce; see the test file's trap.
        if (string.IsNullOrEmpty(rawText) || !rawText.Contains(amountQuote, StringComparison.Ordinal))
        {
            failure = $"Quote \"{amountQuote}\" does not occur verbatim in the raw text.";
            return false;
        }

        if (!TryParseAmount(amountQuote, out var amount, out failure))
            return false;

        if (!TryParseCurrency(currencyCode, out var currency, out failure))
            return false;

        money = new Money(amount, currency);
        failure = string.Empty;
        return true;
    }

    static bool TryParseAmount(string quote, out decimal amount, out string failure)
    {
        amount = default;

        var candidate = quote.Trim();
        foreach (var grouper in SpaceGroupers)
            candidate = candidate.Replace(grouper.ToString(), string.Empty);

        // Safe: TryResolve already rejected a null/whitespace-only quote, and grouping spaces
        // are themselves whitespace, so at least one non-whitespace character survives here.
        if (candidate[0] == '-')
        {
            failure = $"Amount quote \"{quote}\" is negative.";
            return false;
        }

        if (!candidate.All(c => char.IsAsciiDigit(c) || c is '.' or ','))
        {
            failure = $"Amount quote \"{quote}\" is not a plain number.";
            return false;
        }

        var lastDot = candidate.LastIndexOf('.');
        var lastComma = candidate.LastIndexOf(',');

        // The separator rule, pinned (see "Design decisions locked by this task"): with both
        // '.' and ',' present, the rightmost is the decimal point. With only one kind present,
        // exactly 3 digits after its last occurrence means thousands group, not decimal point.
        char? decimalSeparator = (lastDot, lastComma) switch
        {
            ( >= 0, >= 0) => lastDot > lastComma ? '.' : ',',
            ( >= 0, -1) => candidate.Length - lastDot - 1 == 3 ? null : '.',
            (-1, >= 0) => candidate.Length - lastComma - 1 == 3 ? null : ',',
            _ => null,
        };

        var normalized = new StringBuilder(candidate.Length);
        for (var i = 0; i < candidate.Length; i++)
        {
            var c = candidate[i];
            if (c is '.' or ',')
            {
                var isChosenDecimalPoint = c == decimalSeparator && i == (c == '.' ? lastDot : lastComma);
                if (isChosenDecimalPoint)
                    normalized.Append('.');

                continue;
            }

            normalized.Append(c);
        }

        // NumberStyles.AllowDecimalPoint with InvariantCulture, never a culture-sensitive parse
        // (this codebase has a dedicated culture-regression gate for exactly this failure mode
        // -- see CurrencyCodeTests.Orders_ordinally_regardless_of_culture from Phase 0).
        if (!decimal.TryParse(normalized.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
        {
            failure = $"Amount quote \"{quote}\" does not parse as a number.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    static bool TryParseCurrency(string? code, out CurrencyCode currency, out string failure)
    {
        currency = default;

        if (string.IsNullOrEmpty(code) || code.Length != 3 || !code.All(char.IsAsciiLetter))
        {
            failure = $"'{code}' is not a three-letter currency code.";
            return false;
        }

        var candidate = new CurrencyCode(code);
        if (!KnownCurrencies.Contains(candidate))
        {
            failure = $"'{code}' is not a known currency.";
            return false;
        }

        currency = candidate;
        failure = string.Empty;
        return true;
    }
}
