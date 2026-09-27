namespace Noof.Ledger.Application.Receipts;

// The one source of the Tax Administration's fiscal verification URL - Noof.Ledger.Telegram matches
// Prefix as a literal substring, and FiscalQrDecoder (Noof.Ledger.Receipts) refuses any URL whose host
// or path does not match Host/PathPrefix, so a QR pointing elsewhere is never treated as this Tax
// Administration's own receipt. Internal: consumers across assemblies depend on IFiscalVerificationUrl,
// registered by AddNoofApplication.
internal sealed class FiscalVerificationUrl : IFiscalVerificationUrl
{
    static readonly char[] Terminators = [' ', '\t', '\r', '\n'];

    public string Prefix { get; }

    public string Host { get; }

    public string PathPrefix { get; }

    // TryFind matches this part (scheme+host) case-insensitively, the same way FiscalQrDecoder
    // compares uri.Host - a pasted link's host casing is not guaranteed. restAfterOrigin (the path and
    // the "vl=" query key) stays Ordinal, matching FiscalQrDecoder's own case-sensitive
    // AbsolutePath.StartsWith(PathPrefix); the vl value itself is base64 and is never compared here,
    // only copied verbatim from the source text.
    readonly string originPrefix;
    readonly string restAfterOrigin;

    public FiscalVerificationUrl(FiscalVerificationUrlOptions options)
    {
        var prefix = options.VerificationUrlPrefix;
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                $"{FiscalVerificationUrlOptions.ConfigurationSection}:{nameof(FiscalVerificationUrlOptions.VerificationUrlPrefix)} "
                + $"'{prefix}' must be an absolute https URL.");

        // FiscalQrDecoder admits a link with AbsolutePath.StartsWith(PathPrefix) - without the
        // trailing slash, "/v" would also admit "/verify/...", a different path that merely shares a
        // segment prefix. Failing fast here is simpler than normalising a value the operator can just
        // as easily write correctly in appsettings.json.
        if (!uri.AbsolutePath.EndsWith('/'))
            throw new InvalidOperationException(
                $"{FiscalVerificationUrlOptions.ConfigurationSection}:{nameof(FiscalVerificationUrlOptions.VerificationUrlPrefix)} "
                + $"'{prefix}' must end with a slash before the query string.");

        Prefix = prefix;
        Host = uri.Host;
        PathPrefix = uri.AbsolutePath;

        originPrefix = $"{uri.Scheme}://{uri.Host}";
        restAfterOrigin = prefix[originPrefix.Length..];
    }

    // The one place that finds a verification link inside free text - TelegramUpdateRouter and
    // CorrectionHandler (Noof.Ledger.Telegram) and StripUrl below both go through this, so there is
    // exactly one copy of the "match Prefix, stop at whitespace" pattern.
    public bool TryFind(string text, out string url)
    {
        var searchFrom = 0;
        while (true)
        {
            var originStart = text.IndexOf(originPrefix, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (originStart < 0)
            {
                url = "";
                return false;
            }

            var restStart = originStart + originPrefix.Length;
            if (restStart + restAfterOrigin.Length <= text.Length
                && string.CompareOrdinal(text, restStart, restAfterOrigin, 0, restAfterOrigin.Length) == 0)
            {
                var end = text.IndexOfAny(Terminators, originStart);
                url = end < 0 ? text[originStart..] : text[originStart..end];
                return true;
            }

            searchFrom = originStart + 1;
        }
    }

    // Item A (Copilot, Phase 6 review): a text-link capture's RawText carries the whole verification
    // URL, including the vl payload - that must never reach a model prompt. Strips every detected URL
    // (a message can carry more than one - important finding, fix round 2).
    //
    // IMPORTANT finding (Fable 5.1 review): this used to collapse ALL whitespace in the whole text -
    // Split(Terminators, RemoveEmptyEntries) + Join(' ') - even when no URL was found, flattening
    // every multi-line message onto one line before it ever reached the model. A message with no URL
    // now comes back unchanged; when a URL is removed, only the whitespace immediately touching that
    // span collapses (to a single space, or to nothing at the start/end of the text) - a newline
    // between two lines that never mentioned the URL is untouched. Returns null when nothing but the
    // URL(s) remain (so an empty caption is never sent as an empty string).
    public string? StripUrl(string? text)
    {
        if (text is null)
            return null;

        var remaining = text;
        while (TryFind(remaining, out var url))
        {
            var start = remaining.IndexOf(url, StringComparison.Ordinal);
            var end = start + url.Length;

            var trimmedStart = start;
            while (trimmedStart > 0 && Array.IndexOf(Terminators, remaining[trimmedStart - 1]) >= 0)
                trimmedStart--;

            var trimmedEnd = end;
            while (trimmedEnd < remaining.Length && Array.IndexOf(Terminators, remaining[trimmedEnd]) >= 0)
                trimmedEnd++;

            var joiner = trimmedStart > 0 && trimmedEnd < remaining.Length ? " " : "";
            remaining = remaining[..trimmedStart] + joiner + remaining[trimmedEnd..];
        }

        return remaining.Length == 0 ? null : remaining;
    }
}
