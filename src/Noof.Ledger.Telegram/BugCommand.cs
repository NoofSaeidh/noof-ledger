using System.Text.RegularExpressions;

namespace Noof.Ledger.Telegram;

// Its own matcher, not IsHealthCommand's: /bug takes text after it and a bot name before it, and must still leave
// "/bugfix 500" and "/bugs" to the capture path as spending. It runs before the owner gate, so it matches the command
// alone and takes the rest as it is: a stranger's text never meets a backtracking body pattern.
internal static class BugCommand
{
    static readonly Regex Command = new(@"^\s*/bug(?:@[A-Za-z0-9_]+)?(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public static bool TryParse(string text, out string? reportText)
    {
        reportText = null;
        Match match;
        try
        {
            match = Command.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        if (!match.Success)
            return false;

        var words = text[match.Length..].Trim();
        reportText = words.Length == 0 ? null : words;
        return true;
    }
}
