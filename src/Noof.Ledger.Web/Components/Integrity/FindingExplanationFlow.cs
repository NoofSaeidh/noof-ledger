using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Web.Components.Shared;

namespace Noof.Ledger.Web.Components.Integrity;

internal sealed record FindingLink(string Href, string Label);

internal sealed record FindingExplanationState(
    bool Explaining = false,
    Explanation? Explanation = null,
    bool ExplainFailed = false,
    bool Dismissed = false,
    bool Creating = false,
    int? CreatedNumber = null,
    bool CreateFailed = false)
{
    public static FindingExplanationState Idle { get; } = new();

    // The model's LooksLikeBug only shows or hides this offer; nothing it says changes a finding (IR-6).
    public bool OffersReport => Explanation is { LooksLikeBug: true } && !Dismissed && CreatedNumber is null;
}

// The integrity page's Explain -> Create / Dismiss, out of the component so Host.Tests can drive it (spec P-18).
internal sealed class FindingExplanationFlow(
    IFindingExplainer explainer, IBugReportStore store, IFindingText findingText, TimeProvider timeProvider)
{
    public const string CouldNotExplain = "Couldn't explain this right now";
    public const string LooksLikeBugOffer = "Looks like a bug in the app. Create a bug report?";
    public const string CouldNotCreate = "Couldn't create the bug report.";

    readonly OneAtATime operations = new();
    readonly Dictionary<int, FindingExplanationState> states = [];

    public bool Busy => operations.Busy;

    public static string CreatedText(int number) => $"Bug report #{number} created.";

    // Every 8a check sets TransactionId; the wallet anchor is the fallback no 8a finding reaches.
    public static FindingLink? LinkFor(IntegrityFinding finding) => finding switch
    {
        { TransactionId: { } transactionId } => new($"/transactions/{transactionId}/trace", "Trace"),
        { WalletId: { } walletId } => new($"/wallets#wallet-{walletId}", "Wallet"),
        _ => null,
    };

    public FindingExplanationState StateOf(int index) => states.GetValueOrDefault(index, FindingExplanationState.Idle);

    public Task<bool> RunExclusiveAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken) =>
        operations.RunAsync(operation, cancellationToken);

    public async Task ExplainAsync(int index, IntegrityFinding finding, CancellationToken cancellationToken)
    {
        var before = StateOf(index);
        // A second click can reach the circuit before the disabled button does; queued, it would call the model again.
        if (before.Explaining || before.Creating)
            return;

        states[index] = new(Explaining: true);

        var finished = await operations.RunAsync(async token =>
        {
            try
            {
                var request = findingText.ForFinding(finding, timeProvider.GetUtcNow());
                states[index] = new(Explanation: await explainer.ExplainAsync(request, token));
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                states[index] = new(ExplainFailed: true);
            }
        }, cancellationToken);

        if (!finished)
            states[index] = before;
    }

    public async Task CreateAsync(int index, IntegrityFinding finding, CancellationToken cancellationToken)
    {
        var before = StateOf(index);
        // Creating: a second click already queued behind the first would file a second report.
        if (before.Creating || !before.OffersReport || before.Explanation is not { } explanation)
            return;

        states[index] = before with { Creating = true, CreateFailed = false };

        var finished = await operations.RunAsync(async token =>
        {
            try
            {
                // No reply address: the page shows the number itself (R-2).
                var report = new NewBugReport(
                    BugReportSource.Dashboard, ReplyTo: null, Text: null, finding.TransactionId, finding, explanation);
                var saved = await store.FileAsync(report, token);
                states[index] = before with { CreatedNumber = saved.Number, CreateFailed = false };
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                states[index] = before with { CreateFailed = true };
            }
        }, cancellationToken);

        if (!finished)
            states[index] = before;
    }

    public void Dismiss(int index)
    {
        var state = StateOf(index);
        if (state.OffersReport)
            states[index] = state with { Dismissed = true };
    }
}
