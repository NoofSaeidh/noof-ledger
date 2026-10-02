using System.Text.RegularExpressions;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Receipts;

// An exchange-office slip as read_receipt read it (spec §3), each field null when unread: evidence the operator
// corrects, never the source of the transaction's money (T-8). Amounts are the customer's side - what was given and
// what was received - and Rate is dinars per one foreign unit, as the slip prints it. Assess decides whether the slip
// is recorded, held for "Record anyway", or incomplete.
public sealed record ExtractedExchange(
    decimal? GivenAmount, string? GivenCurrency, decimal? ReceivedAmount, string? ReceivedCurrency,
    decimal? Rate, decimal? CommissionAmount, string? CommissionCurrency, string? SlipNumber)
{
    // One para, plus what a rate printed to four decimals can be off by on the foreign amount (spec §3).
    const decimal OnePara = 0.01m;
    const decimal PrintedRateError = 0.00005m;

    static readonly Regex NineDigitPib = new(@"^\d{9}$", RegexOptions.Compiled);

    public SlipAssessment Assess(string? sellerTaxId, bool taxIdMalformed)
    {
        List<SlipMissing> missing = [];
        if (GivenAmount is not > 0m)
            missing.Add(SlipMissing.GivenAmount);
        if (SupportedCurrency(GivenCurrency) is null)
            missing.Add(SlipMissing.GivenCurrency);
        if (ReceivedAmount is not > 0m && !ReceivedIsComputable())
            missing.Add(SlipMissing.ReceivedAmount);
        if (SupportedCurrency(ReceivedCurrency) is null)
            missing.Add(SlipMissing.ReceivedCurrency);

        List<SlipProblem> problems = [];
        if (AmountsDisagree())
            problems.Add(SlipProblem.AmountsDisagree);
        if (taxIdMalformed || sellerTaxId is null || !NineDigitPib.IsMatch(sellerTaxId.Trim()))
            problems.Add(SlipProblem.TaxIdUnreadable);
        if (string.IsNullOrWhiteSpace(SlipNumber))
            problems.Add(SlipProblem.SlipNumberUnreadable);

        var disposition = missing.Count > 0 ? SlipDisposition.Incomplete
            : problems.Count > 0 ? SlipDisposition.Hold
            : SlipDisposition.Record;

        return new SlipAssessment(disposition, problems, missing);
    }

    // A Serbian slip prints dinars per one foreign unit, so the rate relates the two sides only when exactly
    // one of them is dinars.
    public ExchangeRate? PrintedRate()
    {
        if (Rate is not { } rate || rate <= 0m
            || SupportedCurrency(GivenCurrency) is not { } given || SupportedCurrency(ReceivedCurrency) is not { } received)
            return null;

        return given == CurrencyCode.Rsd && received != CurrencyCode.Rsd ? new ExchangeRate(received, rate, CurrencyCode.Rsd)
            : received == CurrencyCode.Rsd && given != CurrencyCode.Rsd ? new ExchangeRate(given, rate, CurrencyCode.Rsd)
            : null;
    }

    public static CurrencyCode? SupportedCurrency(string? code) =>
        CurrencyCode.Supported
            .Where(currency => string.Equals(currency.Value, code?.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(currency => (CurrencyCode?)currency)
            .FirstOrDefault();

    // A commission printed with no currency is read as dinars - the currency a Serbian slip prints it in, the same
    // deliberate default ChatReceiptVision keeps for a receipt with no printed currency. Null when it names a
    // currency the ledger does not hold.
    public CurrencyCode? CommissionCurrencyOrDinars() =>
        string.IsNullOrWhiteSpace(CommissionCurrency) ? CurrencyCode.Rsd : SupportedCurrency(CommissionCurrency);

    bool ReceivedIsComputable() => GivenAmount is > 0m && PrintedRate() is not null;

    // From the customer's side: everything handed over equals everything handed back plus the commission, all in
    // dinars at the printed rate. Vision reports the amount actually paid out or in, so a printed commission is
    // already inside its side; subtracting it once here is what balances the two, not a double count.
    bool AmountsDisagree()
    {
        if (GivenAmount is not { } given || given <= 0m || ReceivedAmount is not { } received || received <= 0m
            || PrintedRate() is not { } printed)
            return false;

        var rate = printed.QuoteAmount;
        var givenIsForeign = SupportedCurrency(GivenCurrency) == printed.Base;
        var foreignAmount = givenIsForeign ? given : received;
        var givenInDinars = givenIsForeign ? given * rate : given;
        var receivedInDinars = givenIsForeign ? received : received * rate;
        var unexplained = givenInDinars - receivedInDinars - CommissionInDinars(printed.Base, rate);

        return Math.Abs(unexplained) > OnePara + foreignAmount * PrintedRateError;
    }

    // A commission in a third currency (neither side's) cannot be allowed for, so it counts as nothing and the
    // check holds the slip instead.
    decimal CommissionInDinars(CurrencyCode foreign, decimal rate)
    {
        if (CommissionAmount is not { } commission || commission <= 0m)
            return 0m;

        var currency = CommissionCurrencyOrDinars();
        return currency == CurrencyCode.Rsd ? commission
            : currency == foreign ? commission * rate
            : 0m;
    }
}
