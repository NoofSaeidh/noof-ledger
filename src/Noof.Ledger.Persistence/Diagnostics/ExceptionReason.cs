using System.Text.Json;
using System.Text.RegularExpressions;

namespace Noof.Ledger.Persistence.Diagnostics;

// The exception text stored in app_log.exception is already secret-redacted before it reaches any
// sink, so parsing it further here can never surface anything that was not already safe to show.
internal static partial class ExceptionReason
{
    public static string? Extract(string? exceptionText)
    {
        if (string.IsNullOrWhiteSpace(exceptionText))
            return null;

        return TryExtractApiErrorMessage(exceptionText) ?? FirstLine(exceptionText);
    }

    static string? TryExtractApiErrorMessage(string text)
    {
        var match = ApiErrorMessagePattern().Match(text);
        if (!match.Success)
            return null;

        try
        {
            return JsonSerializer.Deserialize<string>($"\"{match.Groups["message"].Value}\"");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string FirstLine(string text) => text.Split('\n', 2)[0].TrimEnd('\r').Trim();

    // Matches a JSON error body of the shape a model provider's API returns, embedded anywhere in
    // the exception's own text, e.g.
    // {"type":"error","error":{"type":"invalid_request_error","message":"tools.1.custom: ..."},"request_id":"..."}.
    [GeneratedRegex(""""
        "error"\s*:\s*\{.*?"message"\s*:\s*"(?<message>(?:[^"\\]|\\.)*)"
        """", RegexOptions.Singleline)]
    private static partial Regex ApiErrorMessagePattern();
}
