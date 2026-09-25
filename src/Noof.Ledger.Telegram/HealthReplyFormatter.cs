using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Telegram;

internal static class HealthReplyFormatter
{
    public static string Format(SystemHealthReport report)
    {
        var failing = report.Items.Count(item => item.Level == HealthLevel.Failing);
        var warning = report.Items.Count(item => item.Level == HealthLevel.Warning);

        var header = failing switch
        {
            > 0 => $"Health: {failing} failing",
            _ when warning > 0 => $"Health: {warning} warning{(warning == 1 ? "" : "s")}",
            _ => "Health: all good",
        };

        return string.Join('\n', [header, .. report.Items.Select(FormatLine)]);
    }

    static string FormatLine(HealthItem item)
    {
        var icon = item.Level switch
        {
            HealthLevel.Ok => "✅",
            HealthLevel.Warning => "⚠️",
            _ => "❌",
        };

        return $"{icon} {item.Name} — {item.Summary}";
    }
}
