using System.Text.RegularExpressions;

namespace Noof.Ledger.Telegram;

// Its own matcher, not IsHealthCommand's: /bug takes text after it and a bot name before it, and must still leave
// "/bugfix 500" and "/bugs" to the capture path as spending.
internal static class BugCommand
{
    static readonly Regex Pattern = new(@"^\s*/bug(?:@[A-Za-z0-9_]+)?(?:\s+(?<text>.*?))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParse(string text, out string? reportText)
    {
        reportText = null;
        var match = Pattern.Match(text);
        if (!match.Success)
            return false;

        var words = match.Groups["text"].Value.Trim();
        reportText = words.Length == 0 ? null : words;
        return true;
    }
}
