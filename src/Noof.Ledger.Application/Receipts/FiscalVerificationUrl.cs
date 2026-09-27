namespace Noof.Ledger.Application.Receipts;

// The one source of the Tax Administration's fiscal verification URL - ReceiptLinkDetector
// (Noof.Ledger.Telegram) matches Prefix as a literal substring, and FiscalQrDecoder
// (Noof.Ledger.Receipts) refuses any URL whose host or path does not match Host/PathPrefix, so a QR
// pointing elsewhere is never treated as this Tax Administration's own receipt.
public sealed class FiscalVerificationUrl
{
    static readonly char[] Terminators = [' ', '\t', '\r', '\n'];

    public string Prefix { get; }

    public string Host { get; }

    public string PathPrefix { get; }

    public FiscalVerificationUrl(FiscalVerificationUrlOptions options)
    {
        var prefix = options.VerificationUrlPrefix;
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                $"{FiscalVerificationUrlOptions.ConfigurationSection}:{nameof(FiscalVerificationUrlOptions.VerificationUrlPrefix)} "
                + $"'{prefix}' must be an absolute https URL.");

        Prefix = prefix;
        Host = uri.Host;
        PathPrefix = uri.AbsolutePath;
    }

    // The one place that finds a verification link inside free text - ReceiptLinkDetector
    // (Noof.Ledger.Telegram) and StripUrl below both go through this, so there is exactly one copy of
    // the "match Prefix, stop at whitespace" pattern.
    public bool TryFind(string text, out string url)
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

    // Item A (Copilot, Phase 6 review): a text-link capture's RawText carries the whole verification
    // URL, including the vl payload - that must never reach a model prompt. Strips every detected URL
    // (a message can carry more than one - important finding, fix round 2), collapsing whatever
    // whitespace they leave behind, and returns null when nothing but the URL(s) remain (so an empty
    // caption is never sent as an empty string).
    public string? StripUrl(string? text)
    {
        if (text is null)
            return null;

        var remaining = text;
        while (TryFind(remaining, out var url))
            remaining = remaining.Remove(remaining.IndexOf(url, StringComparison.Ordinal), url.Length);

        var collapsed = string.Join(' ', remaining.Split(Terminators, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed;
    }
}
