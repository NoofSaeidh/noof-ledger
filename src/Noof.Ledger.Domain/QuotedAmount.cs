using System.Globalization;
using System.Text;

namespace Noof.Ledger.Domain;

public static class QuotedAmount
{
    // Telegram's iOS/Android clients sometimes auto-group a typed number with a thin space
    // (U+2009) or a non-breaking space (U+00A0 / U+202F) instead of a comma or dot. These are
    // always grouping marks, never a decimal separator, so they are stripped unconditionally.
    // U+0020 (an ordinary space) joins them for the same reason: it is how people actually type
    // a grouped number in Russian ("1 500"), and it is no less a grouping mark than the other
    // three -- omitting it let a message like "такси 1 500 рсд" resolve to the wrong figure
    // (see QuotedAmountTests.A_number_grouped_with_an_ordinary_space_resolves). Written as
    // explicit \u escapes, not literal glyphs: these four characters are visually
    // indistinguishable from a regular space (and from each other) in most editors and through
    // any copy-paste via markdown/JSON -- which is exactly how this array first shipped as three
    // copies of U+0020, silently defeating the handling this comment describes. Verified by
    // compiling and running this exact file: with the escapes below, a real
    // U+2009/U+00A0/U+202F/U+0020 grouped quote resolves correctly; with four literal spaces
    // it does not.
    static readonly char[] SpaceGroupers = ['\u2009', '\u00A0', '\u202F', '\u0020'];

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

        // Verbatim AND boundary check against the quote exactly as given -- before any
        // normalisation. This is the entire safety property this type exists to enforce; see the
        // test file's trap. rawText.Contains alone blocks a figure the model invented, but not one
        // it mis-bounded: "500" is a true substring of "1500", so a naive Contains would let the
        // model claim a quote of "500" against raw text that actually says 1500. A boundary is
        // therefore required on both sides of a matching occurrence: a digit obviously continues
        // the same number, and so do '.' and ',' (the two characters this parser itself treats as
        // separators -- a quote flanked by either might really be a longer number with the digits
        // on the other side of that separator left out) and '-' (a quote flanked by it might really
        // be one segment of a hyphenated date, e.g. "2026" in "2026-09-21"). Any other character,
        // including whitespace and the start/end of the string, is a genuine boundary. Because a
        // quote can legitimately occur more than once (see
        // A_quote_repeated_in_the_raw_text_still_resolves), this accepts if ANY occurrence has valid
        // boundaries on both sides, not only the first.
        if (string.IsNullOrEmpty(rawText) || !OccursAsWholeNumber(rawText, amountQuote))
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

        // A single well-formed number has at most one decimal point and, per the separator rule
        // below, at most one grouping mark -- this parser only ever treats ONE occurrence of '.'
        // and ONE occurrence of ',' as meaningful (whichever is rightmost becomes the decimal
        // point when both are present). Two dots with no comma, as in "1.2.3", has no unambiguous
        // reading: the old code silently kept only the last dot as decimal and discarded the
        // first, inventing "12.3" out of a quote that was never a single number to begin with.
        if (candidate.Count(c => c == '.') > 1 || candidate.Count(c => c == ',') > 1)
        {
            failure = $"Amount quote \"{quote}\" is not a single well-formed number.";
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

    static bool IsNumberBoundaryChar(char c) => char.IsAsciiDigit(c) || c is '.' or ',' or '-';

    static bool IsGroupingSpace(char c) => Array.IndexOf(SpaceGroupers, c) >= 0;

    static bool OccursAsWholeNumber(string rawText, string quote)
    {
        var searchFrom = 0;
        while (true)
        {
            var index = rawText.IndexOf(quote, searchFrom, StringComparison.Ordinal);
            if (index < 0)
                return false;

            if (HasBoundaryBefore(rawText, index) && HasBoundaryAfter(rawText, index, quote.Length))
                return true;

            searchFrom = index + 1;
        }
    }

    // A grouping space (see SpaceGroupers) is whitespace to the eye but not a genuine break in
    // the number: "500" inside "1 500" sits right after one. So a grouping space is not itself
    // accepted as a boundary -- we look past it to the character beyond, and only that one
    // decides. A digit there means the quote is a fragment of a longer grouped number; anything
    // else (including the start of the string, or another grouping space) is a real boundary.
    static bool HasBoundaryBefore(string rawText, int index)
    {
        if (index == 0)
            return true;

        var adjacent = rawText[index - 1];
        if (IsNumberBoundaryChar(adjacent))
            return false;

        if (!IsGroupingSpace(adjacent))
            return true;

        return index < 2 || !char.IsAsciiDigit(rawText[index - 2]);
    }

    static bool HasBoundaryAfter(string rawText, int index, int quoteLength)
    {
        var afterIndex = index + quoteLength;
        if (afterIndex == rawText.Length)
            return true;

        var adjacent = rawText[afterIndex];
        if (IsNumberBoundaryChar(adjacent))
            return false;

        if (!IsGroupingSpace(adjacent))
            return true;

        return afterIndex + 1 >= rawText.Length || !char.IsAsciiDigit(rawText[afterIndex + 1]);
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
