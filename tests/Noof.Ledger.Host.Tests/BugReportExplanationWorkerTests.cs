using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Diagnostics.Integrity;
using Noof.Ledger.Host.Workers;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Host.Tests;

public class BugReportExplanationWorkerTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset TakenAt = new(2026, 10, 2, 11, 59, 0, TimeSpan.Zero);
    static readonly IReadOnlyList<IntegrityFinding> NoFindings = [];
    static readonly IReadOnlyList<BugReportDelivery> NoDeliveries = [];
    static readonly ExplanationRequest Request = new("Checks:\n- synthetic");
    static readonly Explanation Answer = new("Reply to the echo with the amount.", LooksLikeBug: false);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Harness
    {
        public IBugReportStore Store { get; } = Substitute.For<IBugReportStore>();
        public IModelProvider ModelProvider { get; } = Substitute.For<IModelProvider>();
        public IFindingExplainer Explainer { get; } = Substitute.For<IFindingExplainer>();
        public IChatNotifier ChatNotifier { get; } = Substitute.For<IChatNotifier>();
        public IFindingText FindingText { get; } = Substitute.For<IFindingText>();
        public FakeTimeProvider Clock { get; } = new(Now);
        public CapturingLogger<BugReportExplanationWorker> Logger { get; } = new();

        public Harness()
        {
            ModelProvider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(true);
            Store.NextDueAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns((BugReportToExplain?)null);
            Store.PendingDeliveriesAsync(Arg.Any<CancellationToken>()).Returns(NoDeliveries);
            FindingText.ForReport(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<IntegrityFinding>?>(), Arg.Any<DateTimeOffset>())
                .Returns(Request);
            Explainer.ExplainAsync(Request, Arg.Any<CancellationToken>()).Returns(Answer);
        }

        public BugReportExplanationWorker Worker(IDatabaseGate? gate = null) => new(
            ScopeFactory(), Clock, new CategorizationWorkerOptions(), FindingText, gate ?? ReadyGate(),
            new OperationTimer(Clock, new SlowOperationOptions()), Logger);

        IServiceScopeFactory ScopeFactory()
        {
            var provider = Substitute.For<IServiceProvider>();
            provider.GetService(typeof(IBugReportStore)).Returns(Store);
            provider.GetService(typeof(IModelProvider)).Returns(ModelProvider);
            provider.GetService(typeof(IFindingExplainer)).Returns(Explainer);
            provider.GetService(typeof(IChatNotifier)).Returns(ChatNotifier);
            var scope = Substitute.For<IServiceScope>();
            scope.ServiceProvider.Returns(provider);
            var factory = Substitute.For<IServiceScopeFactory>();
            factory.CreateScope().Returns(scope);
            return factory;
        }
    }

    static IDatabaseGate ReadyGate()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return gate;
    }

    static Guid ReportId(int number) => new($"7a1c0000-0000-4000-8000-{number:D12}");

    static BugReportSnapshot Snapshot(string? collectionFailures = null) =>
        new(TakenAt, "Record: Transfer · Failed", collectionFailures is null ? NoFindings : null, collectionFailures);

    static BugReportToExplain Due(int number = 7, int attempts = 0, BugReportSnapshot? snapshot = null) =>
        new(ReportId(number), number, attempts, "the amount is wrong", TransactionId: null, snapshot);

    static int NextDueCalls(Harness harness) =>
        harness.Store.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IBugReportStore.NextDueAsync));

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 100 && !condition(); waited++)
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
    }

    [Fact]
    public async Task Nothing_due_is_an_idle_tick_that_calls_no_model()
    {
        var harness = new Harness();

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(BugReportTickResult.Idle);
        await harness.Explainer.DidNotReceiveWithAnyArgs().ExplainAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unconfigured_model_takes_no_report_and_spends_no_attempt()
    {
        var harness = new Harness();
        harness.ModelProvider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(false);
        harness.Store.NextDueAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Due());

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(BugReportTickResult.Idle);
        await harness.Store.DidNotReceiveWithAnyArgs().NextDueAsync(default, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().RecordFailedAttemptAsync(default, default, default, Arg.Any<CancellationToken>());
        await harness.Explainer.DidNotReceiveWithAnyArgs().ExplainAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_due_report_is_snapshotted_explained_and_stored_one_per_tick()
    {
        var harness = new Harness();
        var snapshot = Snapshot();
        harness.Store.NextDueAsync(Now, Arg.Any<CancellationToken>()).Returns(Due());
        harness.Store.TakeSnapshotAsync(ReportId(7), Arg.Any<CancellationToken>()).Returns(snapshot);

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(BugReportTickResult.Processed);
        await harness.Store.Received(1).NextDueAsync(Now, Arg.Any<CancellationToken>());
        await harness.Store.Received(1).TakeSnapshotAsync(ReportId(7), Arg.Any<CancellationToken>());
        harness.FindingText.Received(1).ForReport("the amount is wrong", "Record: Transfer · Failed", NoFindings, TakenAt);
        await harness.Explainer.Received(1).ExplainAsync(Request, Arg.Any<CancellationToken>());
        await harness.Store.Received(1).CompleteExplanationAsync(ReportId(7), Answer, Arg.Any<CancellationToken>());
        var explained = harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 1902).Subject;
        explained.Level.Should().Be(LogLevel.Information);
        explained.Properties["Number"].Should().Be(7);
        explained.Properties["LooksLikeBug"].Should().Be(false);
        harness.Logger.Entries.Should().Contain(entry => entry.Properties.GetValueOrDefault("Operation") as string == "job.explainBugReport");
        harness.Logger.Entries.Should().NotContain(entry => entry.EventId.Id == 1908);
    }

    [Fact]
    public async Task A_report_snapshotted_on_an_earlier_attempt_is_explained_from_that_snapshot_without_logging_it_again()
    {
        var harness = new Harness();
        harness.Store.NextDueAsync(Now, Arg.Any<CancellationToken>())
            .Returns(Due(attempts: 1, snapshot: Snapshot("findings: check failed (TimeoutException)")));

        await harness.Worker().RunTickAsync(Ct);

        await harness.Store.DidNotReceiveWithAnyArgs().TakeSnapshotAsync(default, Arg.Any<CancellationToken>());
        harness.FindingText.Received(1).ForReport("the amount is wrong", "Record: Transfer · Failed", null, TakenAt);
        await harness.Store.Received(1).CompleteExplanationAsync(ReportId(7), Answer, Arg.Any<CancellationToken>());
        harness.Logger.Entries.Should().NotContain(entry => entry.EventId.Id == 1908, "1908 belongs to the tick that took the snapshot");
    }

    [Fact]
    public async Task A_snapshot_with_parts_missing_is_logged_and_explained_without_them()
    {
        var harness = new Harness();
        harness.Store.NextDueAsync(Now, Arg.Any<CancellationToken>()).Returns(Due());
        harness.Store.TakeSnapshotAsync(ReportId(7), Arg.Any<CancellationToken>())
            .Returns(Snapshot("findings: check failed (TimeoutException)"));

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(BugReportTickResult.Processed);
        var incomplete = harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 1908).Subject;
        incomplete.Level.Should().Be(LogLevel.Warning);
        incomplete.Properties["Number"].Should().Be(7);
        harness.FindingText.Received(1).ForReport("the amount is wrong", "Record: Transfer · Failed", null, TakenAt);
        await harness.Store.Received(1).CompleteExplanationAsync(ReportId(7), Answer, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 60)]
    public async Task A_failed_explanation_spends_one_attempt_and_waits_the_backoff(int attemptsBefore, int waitSeconds)
    {
        var harness = new Harness();
        harness.Store.NextDueAsync(Now, Arg.Any<CancellationToken>()).Returns(Due(attempts: attemptsBefore, snapshot: Snapshot()));
        harness.Explainer.ExplainAsync(Request, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "overloaded"));
        harness.Store.RecordFailedAttemptAsync(ReportId(7), Arg.Any<DateTimeOffset>(), 3, Arg.Any<CancellationToken>())
            .Returns(BugExplanationState.Pending);

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(BugReportTickResult.Processed);
        await harness.Store.Received(1).RecordFailedAttemptAsync(
            ReportId(7), Now.AddSeconds(waitSeconds), BugReportExplanationWorker.MaxAttempts, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().CompleteExplanationAsync(default, default!, Arg.Any<CancellationToken>());
        var failed = harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 1903).Subject;
        failed.Level.Should().Be(LogLevel.Warning);
        failed.Properties["Number"].Should().Be(7);
        failed.Properties["Attempt"].Should().Be(attemptsBefore + 1);
        failed.Properties["MaxAttempts"].Should().Be(3);
        failed.Properties["FailureType"].Should().Be("Transient");
        failed.Exception.Should().BeNull("the model's failure text never reaches a log line");
    }

    [Fact]
    public async Task The_backoff_is_measured_from_the_ticks_start_not_from_the_end_of_a_slow_model_call()
    {
        var harness = new Harness();
        harness.Store.NextDueAsync(Now, Arg.Any<CancellationToken>()).Returns(Due(snapshot: Snapshot()));
        harness.Explainer.ExplainAsync(Request, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            harness.Clock.Advance(TimeSpan.FromSeconds(90));
            return Task.FromException<Explanation>(new ModelCallException(ModelFailureKind.Transient, "timed out"));
        });
        harness.Store.RecordFailedAttemptAsync(ReportId(7), Arg.Any<DateTimeOffset>(), 3, Arg.Any<CancellationToken>())
            .Returns(BugExplanationState.Pending);

        await harness.Worker().RunTickAsync(Ct);

        await harness.Store.Received(1).RecordFailedAttemptAsync(
            ReportId(7), Now.AddSeconds(30), BugReportExplanationWorker.MaxAttempts, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_database_fault_after_the_model_answered_is_a_failed_tick_not_an_attempt()
    {
        var harness = new Harness();
        harness.Store.NextDueAsync(Now, Arg.Any<CancellationToken>()).Returns(Due(snapshot: Snapshot()));
        harness.Store.CompleteExplanationAsync(ReportId(7), Answer, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unreachable"));

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(BugReportTickResult.Failed);
        await harness.Store.DidNotReceiveWithAnyArgs().RecordFailedAttemptAsync(default, default, default, Arg.Any<CancellationToken>());
        harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 1901);
        harness.Logger.Entries.Should().NotContain(entry => entry.EventId.Id == 1903 || entry.EventId.Id == 1904);
    }

    [Fact]
    public async Task Any_exception_from_the_explainer_spends_an_attempt_and_is_named_by_its_type()
    {
        var harness = new Harness();
        harness.Store.NextDueAsync(Now, Arg.Any<CancellationToken>()).Returns(Due(snapshot: Snapshot()));
        harness.Explainer.ExplainAsync(Request, Arg.Any<CancellationToken>()).ThrowsAsync(new JsonException("synthetic"));
        harness.Store.RecordFailedAttemptAsync(ReportId(7), Arg.Any<DateTimeOffset>(), 3, Arg.Any<CancellationToken>())
            .Returns(BugExplanationState.Pending);

        await harness.Worker().RunTickAsync(Ct);

        harness.Logger.Entries.Single(entry => entry.EventId.Id == 1903).Properties["FailureType"].Should().Be("JsonException");
    }

    [Fact]
    public async Task The_third_failure_leaves_the_report_failed_and_says_so()
    {
        var harness = new Harness();
        harness.Store.NextDueAsync(Now, Arg.Any<CancellationToken>()).Returns(Due(attempts: 2, snapshot: Snapshot()));
        harness.Explainer.ExplainAsync(Request, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "bad request"));
        harness.Store.RecordFailedAttemptAsync(ReportId(7), Arg.Any<DateTimeOffset>(), 3, Arg.Any<CancellationToken>())
            .Returns(BugExplanationState.Failed);

        await harness.Worker().RunTickAsync(Ct);

        var gaveUp = harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 1904).Subject;
        gaveUp.Properties["Number"].Should().Be(7);
        gaveUp.Properties["MaxAttempts"].Should().Be(3);
        harness.Logger.Entries.Should().NotContain(entry => entry.EventId.Id == 1903);
    }

    // Spec P-21: a revoked key, no credit or a revoked permission fails every report alike, so it
    // spends no attempt; the worker pauses as the categorisation workers do and explains the report once the pause ends.
    [Fact]
    public async Task An_account_refusal_spends_no_attempt_and_pauses_explaining()
    {
        var harness = new Harness();
        harness.Store.NextDueAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Due(snapshot: Snapshot()));
        harness.Explainer.ExplainAsync(Request, Arg.Any<CancellationToken>()).Returns(
            Task.FromException<Explanation>(new ModelCallException(ModelFailureKind.Terminal, "invalid x-api-key").AsAccountLevel()),
            Task.FromResult(Answer));
        var worker = harness.Worker();

        var refused = await worker.RunTickAsync(Ct);
        var paused = await worker.RunTickAsync(Ct);
        harness.Clock.Advance(new CategorizationWorkerOptions().AccountCooldown);
        var resumed = await worker.RunTickAsync(Ct);

        (refused, paused, resumed).Should().Be(
            (BugReportTickResult.Processed, BugReportTickResult.Idle, BugReportTickResult.Processed));
        NextDueCalls(harness).Should().Be(2, "the paused tick takes no report");
        await harness.Store.DidNotReceiveWithAnyArgs().RecordFailedAttemptAsync(default, default, default, Arg.Any<CancellationToken>());
        await harness.Store.Received(1).CompleteExplanationAsync(ReportId(7), Answer, Arg.Any<CancellationToken>());
        var refusal = harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 1909).Subject;
        refusal.Level.Should().Be(LogLevel.Warning);
        refusal.Properties["Number"].Should().Be(7);
        refusal.Exception.Should().BeNull("the model's failure text never reaches a log line");
        harness.Logger.Entries.Should().NotContain(entry => entry.EventId.Id == 1903 || entry.EventId.Id == 1904);
    }

    [Fact]
    public async Task A_shutdown_during_the_model_call_is_not_counted_as_an_attempt()
    {
        var harness = new Harness();
        harness.Store.NextDueAsync(Now, Arg.Any<CancellationToken>()).Returns(Due(snapshot: Snapshot()));
        harness.Explainer.ExplainAsync(Request, Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());

        var act = () => harness.Worker().RunTickAsync(Ct);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await harness.Store.DidNotReceiveWithAnyArgs().RecordFailedAttemptAsync(default, default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_tick_that_throws_is_logged_and_reported_as_failed()
    {
        var harness = new Harness();
        harness.Store.NextDueAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unreachable"));

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(BugReportTickResult.Failed);
        var tickFailed = harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 1901).Subject;
        tickFailed.Level.Should().Be(LogLevel.Error);
        tickFailed.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task The_loop_waits_for_the_database_gate_before_its_first_tick()
    {
        var harness = new Harness();
        var ready = new TaskCompletionSource();
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(ready.Task);
        var worker = harness.Worker(gate);

        await worker.StartAsync(Ct);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
            harness.ModelProvider.ReceivedCalls().Should().BeEmpty("nothing may touch the database before it is ready");
            harness.Store.ReceivedCalls().Should().BeEmpty("nothing may touch the database before it is ready");

            ready.SetResult();
            await WaitUntilAsync(() => NextDueCalls(harness) > 0);
        }
        finally
        {
            await worker.StopAsync(Ct);
        }

        NextDueCalls(harness).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task An_idle_tick_waits_the_poll_interval_before_the_next_one()
    {
        var harness = new Harness();
        var worker = harness.Worker();

        await worker.StartAsync(Ct);
        try
        {
            await WaitUntilAsync(() => NextDueCalls(harness) == 1);
            await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
            harness.Clock.Advance(BugReportExplanationWorker.PollInterval - TimeSpan.FromSeconds(1));
            await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
            NextDueCalls(harness).Should().Be(1);

            harness.Clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => NextDueCalls(harness) == 2);
        }
        finally
        {
            await worker.StopAsync(Ct);
        }

        NextDueCalls(harness).Should().Be(2);
    }

    [Fact]
    public async Task A_processed_tick_is_followed_at_once_by_the_next()
    {
        var harness = new Harness();
        harness.Store.NextDueAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Due(snapshot: Snapshot()), (BugReportToExplain?)null);
        var worker = harness.Worker();

        await worker.StartAsync(Ct);
        try
        {
            await WaitUntilAsync(() => NextDueCalls(harness) >= 2);
        }
        finally
        {
            await worker.StopAsync(Ct);
        }

        NextDueCalls(harness).Should().BeGreaterThanOrEqualTo(2, "the clock never moved, so only a processed tick's zero delay explains a second call");
    }

    static BugReportDelivery Delivery(int number, BugExplanationState state, bool? looksLikeBug) =>
        new(ReportId(number), number, 111L, 50 + number, state,
            state == BugExplanationState.Done ? "Reply to the echo with the amount." : null,
            looksLikeBug, Linked: true, FindingsCount: 1);

    [Fact]
    public async Task Explained_and_failed_reports_are_delivered_as_replies_to_their_bug_messages()
    {
        var harness = new Harness();
        IReadOnlyList<BugReportDelivery> pending =
        [
            Delivery(5, BugExplanationState.Done, looksLikeBug: false),
            Delivery(6, BugExplanationState.Done, looksLikeBug: true),
            Delivery(8, BugExplanationState.Failed, looksLikeBug: null),
        ];
        harness.Store.PendingDeliveriesAsync(Arg.Any<CancellationToken>()).Returns(pending);
        harness.ChatNotifier.ReplyToBugReportAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(900, 901, 902);

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(BugReportTickResult.Processed);
        const string Explained = "Reply to the echo with the amount.\n\n1 finding on this record";
        await harness.ChatNotifier.Received(1).ReplyToBugReportAsync(111L, 55, Explained, 5, Arg.Any<CancellationToken>());
        await harness.ChatNotifier.Received(1).ReplyToBugReportAsync(111L, 56, Explained, null, Arg.Any<CancellationToken>());
        await harness.ChatNotifier.Received(1).ReplyToBugReportAsync(
            111L, 58, "Couldn't explain it — the report is saved.", null, Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkDeliveredAsync(ReportId(5), 900, Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkDeliveredAsync(ReportId(6), 901, Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkDeliveredAsync(ReportId(8), 902, Arg.Any<CancellationToken>());
        harness.Logger.Entries.Where(entry => entry.EventId.Id == 1907).Select(entry => entry.Properties["Number"])
            .Should().Equal(5, 6, 8);
        await harness.Explainer.DidNotReceiveWithAnyArgs().ExplainAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Replies_go_out_even_while_the_model_is_unconfigured()
    {
        var harness = new Harness();
        harness.ModelProvider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(false);
        IReadOnlyList<BugReportDelivery> pending = [Delivery(8, BugExplanationState.Failed, looksLikeBug: null)];
        harness.Store.PendingDeliveriesAsync(Arg.Any<CancellationToken>()).Returns(pending);
        harness.ChatNotifier.ReplyToBugReportAsync(111L, 58, Arg.Any<string>(), null, Arg.Any<CancellationToken>()).Returns(902);

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(BugReportTickResult.Processed);
        await harness.Store.Received(1).MarkDeliveredAsync(ReportId(8), 902, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reply_that_can_never_be_sent_spends_no_attempt_never_reruns_the_model_and_warns_once()
    {
        var harness = new Harness();
        var stuck = Delivery(5, BugExplanationState.Done, looksLikeBug: false);
        IReadOnlyList<BugReportDelivery> firstTick = [stuck, Delivery(6, BugExplanationState.Done, looksLikeBug: true)];
        IReadOnlyList<BugReportDelivery> secondTick = [stuck, Delivery(9, BugExplanationState.Done, looksLikeBug: false)];
        IReadOnlyList<BugReportDelivery> thirdTick = [stuck];
        harness.Store.NextDueAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Due(number: 9, snapshot: Snapshot()), (BugReportToExplain?)null);
        harness.Store.PendingDeliveriesAsync(Arg.Any<CancellationToken>()).Returns(firstTick, secondTick, thirdTick);
        harness.ChatNotifier.ReplyToBugReportAsync(111L, Arg.Is<int>(id => id != 55), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(900);
        harness.ChatNotifier.ReplyToBugReportAsync(111L, 55, Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("The Telegram client is not ready yet: no bot token has been saved."));
        var worker = harness.Worker();

        var results = new List<BugReportTickResult>();
        for (var tick = 0; tick < 3; tick++)
            results.Add(await worker.RunTickAsync(Ct));

        results.Should().Equal(BugReportTickResult.Processed, BugReportTickResult.Processed, BugReportTickResult.Idle);
        await harness.Explainer.Received(1).ExplainAsync(Request, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().RecordFailedAttemptAsync(default, default, default, Arg.Any<CancellationToken>());
        await harness.ChatNotifier.Received(3).ReplyToBugReportAsync(111L, 55, Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkDeliveredAsync(ReportId(6), 900, Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkDeliveredAsync(ReportId(9), 900, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceive().MarkDeliveredAsync(ReportId(5), Arg.Any<int>(), Arg.Any<CancellationToken>());

        var warned = harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 1905).Subject;
        warned.Level.Should().Be(LogLevel.Warning);
        warned.Properties["Number"].Should().Be(5);
        warned.Properties["FailureType"].Should().Be("InvalidOperationException");
        warned.Exception.Should().BeNull();
        harness.Logger.Entries.Where(entry => entry.EventId.Id == 1906)
            .Should().HaveCount(2).And.OnlyContain(entry => entry.Level == LogLevel.Debug && (int)entry.Properties["Number"] == 5);
    }

    [Fact]
    public async Task A_database_fault_listing_the_replies_owed_is_a_failed_tick_not_an_attempt()
    {
        var harness = new Harness();
        harness.Store.NextDueAsync(Now, Arg.Any<CancellationToken>()).Returns(Due(snapshot: Snapshot()));
        harness.Store.PendingDeliveriesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unreachable"));

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(BugReportTickResult.Failed);
        harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 1901)
            .Which.Exception.Should().BeOfType<InvalidOperationException>();
        await harness.Store.DidNotReceiveWithAnyArgs().RecordFailedAttemptAsync(default, default, default, Arg.Any<CancellationToken>());
    }

    // A send that succeeded is not an undeliverable reply: storing its id failing is the database's fault, so it is the
    // tick's 1901, and the next tick sends again (a duplicate the spec accepts).
    [Fact]
    public async Task A_database_fault_storing_a_sent_reply_is_a_failed_tick_not_an_unsent_reply()
    {
        var harness = new Harness();
        IReadOnlyList<BugReportDelivery> pending = [Delivery(5, BugExplanationState.Done, looksLikeBug: false)];
        harness.Store.PendingDeliveriesAsync(Arg.Any<CancellationToken>()).Returns(pending);
        harness.ChatNotifier.ReplyToBugReportAsync(111L, 55, Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(900);
        harness.Store.MarkDeliveredAsync(ReportId(5), 900, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unreachable"));

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(BugReportTickResult.Failed);
        harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 1901)
            .Which.Exception.Should().BeOfType<InvalidOperationException>();
        harness.Logger.Entries.Should().NotContain(entry => entry.EventId.Id >= 1905 && entry.EventId.Id <= 1907);
        await harness.Store.DidNotReceiveWithAnyArgs().RecordFailedAttemptAsync(default, default, default, Arg.Any<CancellationToken>());
    }
}
