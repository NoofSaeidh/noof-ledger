namespace Noof.Ledger.Domain;

// The single definition (M-5, Phase 6 final review): Application.Receipts.ReceiptContracts's
// contract records (ExtractedReceipt, ReceiptView, ...) use this type directly rather than keeping
// a lockstep mirror - Domain has zero NuGet references, but Application already depends on Domain,
// so there was never a reason for Application to own a second copy.
public enum ReceiptSource
{
    FiscalQr = 0,
    Vision = 1,
}
