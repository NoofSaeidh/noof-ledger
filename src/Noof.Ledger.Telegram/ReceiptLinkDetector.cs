namespace Noof.Ledger.Telegram;

// Finds a fiscal QR verification link anywhere in a text message, so a receipt link pasted with a
// caption ("lunch https://suf.purs.gov.rs/v/?vl=...") is still recognised. Decoding the link itself
// is IFiscalQrDecoder's job (Noof.Ledger.Receipts); this only locates the substring.
internal static class ReceiptLinkDetector
{
    const string Prefix = "https://suf.purs.gov.rs/v/?vl=";
    static readonly char[] Terminators = [' ', '\t', '\r', '\n'];

    public static bool TryFind(string text, out string url)
    {
        var start = text.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            url = "";
            return false;
        }

        var end = text.IndexOfAny(Terminators, start);
        url = end < 0 ? text[start..] : text[start..end];
        return true;
    }
}
