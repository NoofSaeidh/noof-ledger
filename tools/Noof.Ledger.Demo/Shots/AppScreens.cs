using Microsoft.Playwright;

namespace Noof.Ledger.Demo.Shots;

internal sealed record Viewport(string Name, int Width, int Height, float Scale);

internal sealed record AppScreen(
    string Name, string Title, string Path, string ReadyMarker, bool SignedIn = true, Func<IPage, Task>? Prepare = null);

internal static class AppScreens
{
    public static readonly Viewport Phone = new("phone", 390, 844, 2);
    public static readonly Viewport Desktop = new("desktop", 1440, 900, 1);

    // The one value on these pages that follows the host's real clock: each health check's "Checked at".
    public const string HideLiveValues = "#diagnostics-checks tbody td:nth-child(4) { visibility: hidden; }";

    public static IReadOnlyList<AppScreen> All { get; } =
    [
        new("login", "Sign in", "/", "input[name='username']", SignedIn: false),
        new("dashboard", "Dashboard", "/", "#balances"),
        new("wallets", "Wallets", "/wallets", "#create-wallet", Prepare: page => FillAndLeaveAsync(page, "#new-wallet-date", $"{MockData.Now:yyyy-MM-dd}")),
        new("transactions", "Transactions", "/transactions", "#transactions-grid"),
        new("trace", "Transaction trace", $"/transactions/{MockData.TracedTransactionId}/trace", "#trace-timeline"),
        new("trace-failed", "A failed transaction's trace", $"/transactions/{MockData.FailedTransactionId}/trace", "#trace-timeline"),
        new("diagnostics", "Diagnostics", "/diagnostics", "#diagnostics-checks", Prepare: HideFreeDiskSpaceAsync),
        new("logs", "Logs", "/diagnostics/logs", "#logs-grid", Prepare: FilterLogsToTheMockMonthAsync),
        new("log-settings", "Log settings", "/diagnostics/logs/settings", "#retention-verbose"),
        new("secrets", "Secrets", "/settings/secrets", "#status-anthropic-api-key"),
    ];

    static async Task FilterLogsToTheMockMonthAsync(IPage page)
    {
        await FillAndLeaveAsync(page, "#logs-filter-from", $"{MockData.LogWindowStart:yyyy-MM-dd}T00:00");
        await FillAndLeaveAsync(page, "#logs-filter-to", $"{MockData.LogWindowEnd:yyyy-MM-dd}T00:00");
    }

    // A focused field would be photographed with its focus ring and selection.
    static async Task FillAndLeaveAsync(IPage page, string selector, string value)
    {
        await page.FillAsync(selector, value);
        await page.Locator(selector).BlurAsync();
    }

    // Free disk space is the machine's live state, not the app's, and differs on every run.
    static Task HideFreeDiskSpaceAsync(IPage page) => page.EvaluateAsync(
        """
        () => document.querySelectorAll('#diagnostics-checks tbody tr').forEach(row => {
            if (row.cells[0]?.textContent.trim() === 'Disk') row.cells[2].style.visibility = 'hidden';
        })
        """);
}
