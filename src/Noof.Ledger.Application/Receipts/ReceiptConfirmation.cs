using System.Globalization;

namespace Noof.Ledger.Application.Receipts;

// Shared between ExtractReceiptWorker's own replay of a still-unconfirmed job (C-1) and
// RecordActionHandler's Cancel/Restore of a receipt that was saved without a CategorizeReceipt job
// (2026-09-27) - both need to turn a stored ReceiptView back into the same
// IRecordEcho.ComposeReceiptNeedsConfirmation the operator saw the first time. A malformed printed tax
// id cannot be recovered here - ChatReceiptVision only ever stores a well-formed one - so a replay or a
// Restore whose only original reason was the tax id shows the mismatch problem alone, which may be an
// empty list (docs/OPEN-QUESTIONS.md P6-2).
public static class ReceiptConfirmation
{
    public static ExtractedReceipt ToExtractedReceipt(ReceiptView view) => new(
        view.Source, view.VerificationUrl, view.SellerTaxId, view.SellerName, view.SellerAddress, view.LocationName,
        view.FiscalNumber, view.IssuedAt, view.Total, view.Currency, view.Kind, view.PaymentMethod, view.QrTotal,
        [.. view.Lines.Select(line => new ExtractedReceiptLine(line.Ordinal, line.Name, line.Quantity, line.Unit, line.UnitPrice, line.Total, line.TaxLabel))]);

    public static bool HasMismatch(ExtractedReceipt extracted) =>
        Math.Abs(extracted.Lines.Sum(line => line.Total) - (extracted.QrTotal ?? extracted.Total)) > 0.01m;

    public static List<string> BuildProblems(ExtractedReceipt extracted, bool mismatch, bool taxIdMalformed = false)
    {
        List<string> problems = [];

        if (mismatch)
        {
            var sum = extracted.Lines.Sum(line => line.Total).ToString("0.00", CultureInfo.InvariantCulture);
            var total = (extracted.QrTotal ?? extracted.Total).ToString("0.00", CultureInfo.InvariantCulture);
            problems.Add($"Lines add up to {sum} {extracted.Currency}, the receipt says {total} {extracted.Currency}");
        }

        if (taxIdMalformed)
            problems.Add("The printed tax id does not look like a valid PIB (9 digits)");

        return problems;
    }
}
