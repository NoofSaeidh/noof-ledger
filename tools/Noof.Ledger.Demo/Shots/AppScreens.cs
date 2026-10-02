using Microsoft.Playwright;
using Noof.Ledger.Application.Diagnostics;

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
        new("dashboard-transfers", "Dashboard: recent transfers", "/?view=transfers", "#recent-view-transfers[aria-current='page']"),
        new("wallets", "Wallets", "/wallets", "#create-wallet", Prepare: page => FillAndLeaveAsync(page, "#new-wallet-date", $"{MockData.Now:yyyy-MM-dd}")),
        new("transactions", "Transactions", "/transactions", "#transactions-grid"),
        new("trace", "Transaction trace", $"/transactions/{MockData.TracedTransactionId}/trace", "#trace-timeline"),
        new("trace-failed", "A failed transaction's trace", $"/transactions/{MockData.FailedTransactionId}/trace", "#trace-timeline"),
        new("trace-exchange", "An exchange's trace", $"/transactions/{MockData.ExchangeTransactionId}/trace", "#trace-transfer"),
        new("trace-foreign-spending", "A foreign-currency spending's trace",
            $"/transactions/{MockData.ForeignSpendingTransactionId}/trace", "#trace-charges"),
        new("trace-receipt", "A receipt's trace", $"/transactions/{MockData.ReceiptTransactionId}/trace", "#trace-receipt"),
        new("trace-receipt-vision", "A receipt read from the photo", $"/transactions/{MockData.VisionReceiptTransactionId}/trace", "#trace-receipt"),
        new("trace-receipt-check", "A receipt waiting for Record anyway",
            $"/transactions/{MockData.UnconfirmedReceiptTransactionId}/trace", "#trace-receipt-awaiting-confirmation"),
        new("diagnostics", "Diagnostics", "/diagnostics", "#diagnostics-checks", Prepare: HideFreeDiskSpaceAsync),
        new("logs", "Logs", "/diagnostics/logs", "#logs-grid", Prepare: FilterLogsToTheMockWindowAsync),
        new("log-settings", "Log settings", "/diagnostics/logs/settings", "#retention-verbose"),
        new("secrets", "Secrets", "/settings/secrets", "#status-anthropic-api-key"),
    ];

    static async Task FilterLogsToTheMockWindowAsync(IPage page)
    {
        await FillAndLeaveAsync(page, "#logs-filter-from", $"{MockData.LogWindowStart:yyyy-MM-dd}T00:00");
        await FillAndLeaveAsync(page, "#logs-filter-to", $"{MockData.LogWindowEnd:yyyy-MM-dd}T00:00");
        await WaitForOnlyTheMockLogRowsAsync(page);
    }

    // The grid reloads over the circuit, which network idle does not wait for. Until it has, the page
    // may show the host's own startup rows, and those name this machine's log directory.
    static async Task WaitForOnlyTheMockLogRowsAsync(IPage page)
    {
        var expected = MockData.LogRows.Count(row => row.Level >= LogSeverity.Information);
        try
        {
            await page.WaitForFunctionAsync(
                """
                ([expected, from, to]) => {
                    const loggedAt = [...document.querySelectorAll('#logs-grid tbody tr')]
                        .map(row => row.cells[0]?.textContent.trim() ?? '')
                        .filter(text => text !== '');
                    return loggedAt.length === expected && loggedAt.every(text => text >= from && text < to);
                }
                """,
                new object[] { expected, $"{MockData.LogWindowStart:yyyy-MM-dd}", $"{MockData.LogWindowEnd:yyyy-MM-dd}" },
                new PageWaitForFunctionOptions { Timeout = 15_000 });
        }
        catch (TimeoutException)
        {
            var shown = await page.EvaluateAsync<string[]>(
                "() => [...document.querySelectorAll('#logs-grid tbody tr')].map(row => row.cells[0]?.textContent.trim() ?? '').filter(text => text !== '')");
            throw new InvalidOperationException(
                $"The Logs page never showed just the {expected} mock rows between {MockData.LogWindowStart:yyyy-MM-dd} "
                + $"and {MockData.LogWindowEnd:yyyy-MM-dd} (it shows rows logged at: {string.Join(", ", shown)}); its picture was not taken.");
        }
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
