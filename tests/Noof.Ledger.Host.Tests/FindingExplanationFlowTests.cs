using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Web.Components.Integrity;

namespace Noof.Ledger.Host.Tests;

// The integrity page's Explain → Create / Dismiss, below the browser: the E2E host cannot take a fake explainer and no
// test-only configuration key may exist, so this is where Create is driven (spec P-18).
public sealed class FindingExplanationFlowTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);
    static readonly Guid RecordId = new("7a1c0000-0000-4000-8000-000000000910");
    static readonly Guid WalletId = new("7a1c0000-0000-4000-8000-000000000077");

    static readonly IntegrityFinding Finding = new(
        IntegrityCheck.NotApplied, IntegrityGroup.WaitingOnYou, RecordId, null, null,
        [new TextFact("Waiting for", "A reply to the echo")]);

    static readonly ExplanationRequest Request = new("Checks:\n- Not applied: something has waited on the operator.");

    readonly IFindingExplainer explainer = Substitute.For<IFindingExplainer>();
    readonly IBugReportStore store = Substitute.For<IBugReportStore>();
    readonly IFindingText findingText = Substitute.For<IFindingText>();

    public FindingExplanationFlowTests() => findingText.ForFinding(Finding, Now).Returns(Request);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    FindingExplanationFlow Flow() => new(explainer, store, findingText, new FakeTimeProvider(Now));

    void Answers(Explanation explanation) =>
        explainer.ExplainAsync(Request, Arg.Any<CancellationToken>()).Returns(explanation);

    [Fact]
    public async Task Explain_asks_about_the_one_finding_as_of_now_and_shows_the_answer_without_an_offer()
    {
        var answer = new Explanation("The bot asked for the received amount. Reply to the echo with it.", false);
        Answers(answer);
        var flow = Flow();

        await flow.ExplainAsync(3, Finding, Ct);

        await explainer.Received(1).ExplainAsync(Request, Arg.Any<CancellationToken>());
        flow.StateOf(3).Should().Be(new FindingExplanationState(Explanation: answer));
        flow.StateOf(3).OffersReport.Should().BeFalse();
        flow.StateOf(0).Should().Be(FindingExplanationState.Idle, "only the finding whose button was pressed changes");
    }

    [Fact]
    public async Task An_answer_that_looks_like_a_bug_offers_a_report()
    {
        Answers(new Explanation("The entries were written wrong. This is a bug — file it.", true));
        var flow = Flow();

        await flow.ExplainAsync(0, Finding, Ct);

        flow.StateOf(0).OffersReport.Should().BeTrue();
        FindingExplanationFlow.LooksLikeBugOffer.Should().Be("Looks like a bug in the app. Create a bug report?");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Any_failure_shows_the_fixed_text_never_the_exceptions_message(bool modelFailure)
    {
        Exception failure = modelFailure
            ? new ModelCallException(ModelFailureKind.Terminal, "No Anthropic API key is set.")
            : new InvalidOperationException("Something the explainer did not expect.");
        explainer.ExplainAsync(Request, Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        var flow = Flow();

        await flow.ExplainAsync(0, Finding, Ct);

        flow.StateOf(0).Should().Be(new FindingExplanationState(ExplainFailed: true));
        FindingExplanationFlow.CouldNotExplain.Should().Be("Couldn't explain this right now");
    }

    [Fact]
    public async Task Create_files_the_finding_with_the_answer_shown_and_links_the_number_it_got()
    {
        var answer = new Explanation("The entries were written wrong. This is a bug — file it.", true);
        Answers(answer);
        store.CreateFromDashboardAsync(Finding, answer, Arg.Any<CancellationToken>()).Returns(12);
        var flow = Flow();
        await flow.ExplainAsync(0, Finding, Ct);

        await flow.CreateAsync(0, Finding, Ct);

        await store.Received(1).CreateFromDashboardAsync(Finding, answer, Arg.Any<CancellationToken>());
        flow.StateOf(0).Should().Be(new FindingExplanationState(Explanation: answer, CreatedNumber: 12));
        flow.StateOf(0).OffersReport.Should().BeFalse("a report is filed once");
        FindingExplanationFlow.CreatedText(12).Should().Be("Bug report #12 created.");
    }

    [Fact]
    public async Task Create_does_nothing_unless_the_answer_shown_offers_it()
    {
        Answers(new Explanation("Reply to the echo with the amount.", false));
        var flow = Flow();

        await flow.CreateAsync(0, Finding, Ct);
        await flow.ExplainAsync(0, Finding, Ct);
        await flow.CreateAsync(0, Finding, Ct);

        await store.DidNotReceiveWithAnyArgs().CreateFromDashboardAsync(default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_create_says_so_and_keeps_the_offer_to_try_again()
    {
        var answer = new Explanation("This is a bug — file it.", true);
        Answers(answer);
        store.CreateFromDashboardAsync(Finding, answer, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("The database went away."));
        var flow = Flow();
        await flow.ExplainAsync(0, Finding, Ct);

        await flow.CreateAsync(0, Finding, Ct);

        flow.StateOf(0).Should().Be(new FindingExplanationState(Explanation: answer, CreateFailed: true));
        flow.StateOf(0).OffersReport.Should().BeTrue();
        FindingExplanationFlow.CouldNotCreate.Should().Be("Couldn't create the bug report.");
    }

    [Fact]
    public async Task Dismiss_hides_the_offer_keeps_the_answer_and_stops_Create()
    {
        var answer = new Explanation("This is a bug — file it.", true);
        Answers(answer);
        var flow = Flow();
        await flow.ExplainAsync(0, Finding, Ct);

        flow.Dismiss(0);
        await flow.CreateAsync(0, Finding, Ct);

        flow.StateOf(0).Should().Be(new FindingExplanationState(Explanation: answer, Dismissed: true));
        flow.StateOf(0).OffersReport.Should().BeFalse();
        await store.DidNotReceiveWithAnyArgs().CreateFromDashboardAsync(default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task One_action_runs_at_a_time_because_the_circuit_has_one_database_context()
    {
        var first = new TaskCompletionSource<Explanation>();
        explainer.ExplainAsync(Request, Arg.Any<CancellationToken>())
            .Returns(first.Task, Task.FromResult(new Explanation("Second.", false)));
        var flow = Flow();

        var explainingFirst = flow.ExplainAsync(0, Finding, Ct);
        var explainingSecond = flow.ExplainAsync(1, Finding, Ct);

        flow.Busy.Should().BeTrue();
        flow.StateOf(1).Explaining.Should().BeTrue("the second press shows it is waiting its turn");
        await explainer.Received(1).ExplainAsync(Arg.Any<ExplanationRequest>(), Arg.Any<CancellationToken>());

        first.SetResult(new Explanation("First.", false));
        await Task.WhenAll(explainingFirst, explainingSecond);

        await explainer.Received(2).ExplainAsync(Arg.Any<ExplanationRequest>(), Arg.Any<CancellationToken>());
        flow.Busy.Should().BeFalse();
    }

    // A second click can reach the circuit before the disabled button does. Queued behind the first, it would call the
    // model again - or, for Create, file a second report.
    [Fact]
    public async Task Explain_pressed_twice_calls_the_model_once()
    {
        var first = new TaskCompletionSource<Explanation>();
        explainer.ExplainAsync(Request, Arg.Any<CancellationToken>()).Returns(first.Task);
        var flow = Flow();

        var pressed = flow.ExplainAsync(0, Finding, Ct);
        var pressedAgain = flow.ExplainAsync(0, Finding, Ct);
        first.SetResult(new Explanation("Once.", false));
        await Task.WhenAll(pressed, pressedAgain);

        await explainer.Received(1).ExplainAsync(Arg.Any<ExplanationRequest>(), Arg.Any<CancellationToken>());
        flow.StateOf(0).Should().Be(new FindingExplanationState(Explanation: new Explanation("Once.", false)));
    }

    [Fact]
    public async Task Create_pressed_twice_files_one_report()
    {
        var answer = new Explanation("This is a bug — file it.", true);
        Answers(answer);
        var filing = new TaskCompletionSource<int>();
        store.CreateFromDashboardAsync(Finding, answer, Arg.Any<CancellationToken>()).Returns(filing.Task);
        var flow = Flow();
        await flow.ExplainAsync(0, Finding, Ct);

        var pressed = flow.CreateAsync(0, Finding, Ct);
        var pressedAgain = flow.CreateAsync(0, Finding, Ct);
        filing.SetResult(12);
        await Task.WhenAll(pressed, pressedAgain);

        await store.Received(1).CreateFromDashboardAsync(Finding, answer, Arg.Any<CancellationToken>());
        flow.StateOf(0).CreatedNumber.Should().Be(12);
    }

    [Fact]
    public async Task Leaving_the_page_mid_explanation_puts_the_finding_back_as_it_was()
    {
        explainer.ExplainAsync(Request, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return new Explanation("Never shown.", false);
        });
        var flow = Flow();
        using var leaving = new CancellationTokenSource();

        var explaining = flow.ExplainAsync(0, Finding, leaving.Token);
        flow.StateOf(0).Explaining.Should().BeTrue();
        await leaving.CancelAsync();
        await explaining;

        flow.StateOf(0).Should().Be(FindingExplanationState.Idle);
        flow.Busy.Should().BeFalse();
    }

    [Fact]
    public async Task A_model_call_that_fails_after_the_page_has_gone_lets_nothing_escape()
    {
        var modelCall = new TaskCompletionSource<Explanation>();
        explainer.ExplainAsync(Request, Arg.Any<CancellationToken>()).Returns(modelCall.Task);
        var flow = Flow();
        using var leaving = new CancellationTokenSource();

        var explaining = flow.ExplainAsync(0, Finding, leaving.Token);
        await leaving.CancelAsync();
        modelCall.SetException(new ModelCallException(ModelFailureKind.Transient, "The connection dropped."));
        await explaining;

        flow.StateOf(0).Should().Be(FindingExplanationState.Idle);
        flow.Busy.Should().BeFalse();
    }

    [Fact]
    public void A_finding_links_to_its_records_trace_else_to_its_wallet_else_nowhere()
    {
        FindingExplanationFlow.LinkFor(Finding)
            .Should().Be(new FindingLink($"/transactions/{RecordId}/trace", "Trace"));
        FindingExplanationFlow.LinkFor(Finding with { TransactionId = null, WalletId = WalletId })
            .Should().Be(new FindingLink($"/wallets#wallet-{WalletId}", "Wallet"));
        FindingExplanationFlow.LinkFor(Finding with { TransactionId = null }).Should().BeNull();
    }
}
