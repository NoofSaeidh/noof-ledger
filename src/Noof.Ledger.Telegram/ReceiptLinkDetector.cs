using Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Telegram;

// Finds a fiscal QR verification link anywhere in a text message, so a receipt link pasted with a
// caption ("lunch https://suf.purs.gov.rs/v/?vl=...") is still recognised. Decoding the link itself
// is IFiscalQrDecoder's job (Noof.Ledger.Receipts); the match itself is FiscalVerificationUrl.TryFind
// (Noof.Ledger.Application), so this and the model-prompt caption stripping share one pattern.
internal sealed class ReceiptLinkDetector(IFiscalVerificationUrl verificationUrl)
{
    public bool TryFind(string text, out string url) => verificationUrl.TryFind(text, out url);
}
