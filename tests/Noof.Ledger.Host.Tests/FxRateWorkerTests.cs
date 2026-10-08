using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Domain;
using Noof.Ledger.Fx;
using Noof.Ledger.Host.Workers;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Host.Tests;

public class FxRateWorkerTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 8, 5, 0, 0, TimeSpan.Zero);
    static readonly DateOnly Today = new(2026, 10, 8);

    static readonly FxRateSnapshot Snapshot = OpenErApiPayloads.Snapshot(Today);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Harness
    {
        public IFxRateStore Store { get; } = Substitute.For<IFxRateStore>();
        public IFxRateSource Source { get; } = Substitute.For<IFxRateSource>();
        public FakeTimeProvider Clock { get; }
        public CapturingLogger<FxRateWorker> Logger { get; } = new();

        public Harness(DateTimeOffset? now = null)
        {
            Clock = new(now ?? Now);
            Store.NewestAsOfDateAsync(Arg.Any<CancellationToken>()).Returns((DateOnly?)null);
            Store.AppendAsync(Arg.Any<FxRateSnapshot>(), Arg.Any<CancellationToken>()).Returns(true);
            Source.FetchLatestAsync(Arg.Any<CancellationToken>()).Returns(Snapshot);
        }

        public FxRateWorker Worker(IDatabaseGate? gate = null) => new(ScopeFactory(), Clock, gate ?? ReadyGate(), Logger);

        IServiceScopeFactory ScopeFactory()
        {
            var provider = Substitute.For<IServiceProvider>();
            provider.GetService(typeof(IFxRateStore)).Returns(Store);
            provider.GetService(typeof(IFxRateSource)).Returns(Source);
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

    static int NewestCalls(Harness harness) =>
        harness.Store.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IFxRateStore.NewestAsOfDateAsync));

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 100 && !condition(); waited++)
            await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
    }

    [Fact]
    public async Task A_fresh_install_fetches_at_once_and_stores_the_snapshot()
    {
        var harness = new Harness();

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(FxRateTickResult.Stored);
        await harness.Store.Received(1).AppendAsync(Snapshot, Arg.Any<CancellationToken>());
        var stored = harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 2302).Subject;
        stored.Level.Should().Be(LogLevel.Information);
        stored.Properties["AsOfDate"].Should().Be(Today);
        stored.Properties["Currencies"].Should().Be(4);
    }

    [Theory]
    [InlineData(2026, 10, 8)]
    [InlineData(2026, 10, 9)]
    public async Task A_rate_as_of_UTC_today_or_later_is_fresh_and_nothing_is_fetched(int year, int month, int day)
    {
        var harness = new Harness();
        harness.Store.NewestAsOfDateAsync(Arg.Any<CancellationToken>()).Returns(new DateOnly(year, month, day));

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(FxRateTickResult.UpToDate);
        await harness.Source.DidNotReceive().FetchLatestAsync(Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().AppendAsync(default!, Arg.Any<CancellationToken>());
        harness.Logger.Entries.Should().BeEmpty("an up-to-date archive is the normal case, not news");
    }

    // Day D with D-1 stored: today's rates are published just after 00:00 UTC, so yesterday's are not enough.
    [Theory]
    [InlineData(2026, 10, 7)]
    [InlineData(2026, 10, 6)]
    public async Task A_rate_older_than_UTC_today_is_fetched_again(int year, int month, int day)
    {
        var harness = new Harness();
        harness.Store.NewestAsOfDateAsync(Arg.Any<CancellationToken>()).Returns(new DateOnly(year, month, day));

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(FxRateTickResult.Stored);
        await harness.Source.Received(1).FetchLatestAsync(Arg.Any<CancellationToken>());
        await harness.Store.Received(1).AppendAsync(Snapshot, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Today_is_the_UTC_calendars_not_the_local_one()
    {
        var harness = new Harness(new DateTimeOffset(2026, 10, 7, 23, 30, 0, TimeSpan.Zero));
        harness.Store.NewestAsOfDateAsync(Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 10, 7));

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(FxRateTickResult.UpToDate, "at 23:30 UTC on the 7th, UTC today is still the 7th, though Belgrade is on the 8th");
    }

    [Fact]
    public async Task A_source_that_fetched_nothing_stores_nothing_and_says_so_where_the_health_row_looks()
    {
        var harness = new Harness();
        harness.Source.FetchLatestAsync(Arg.Any<CancellationToken>()).Returns((FxRateSnapshot?)null);

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(FxRateTickResult.NothingFetched);
        await harness.Store.DidNotReceiveWithAnyArgs().AppendAsync(default!, Arg.Any<CancellationToken>());
        harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 2304)
            .Which.Level.Should().Be(LogLevel.Information);
        harness.Logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning,
            "the source has already warned with the reason");
    }

    [Fact]
    public async Task A_snapshot_that_was_already_stored_is_reported_and_is_no_error()
    {
        var harness = new Harness();
        harness.Store.AppendAsync(Snapshot, Arg.Any<CancellationToken>()).Returns(false);

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(FxRateTickResult.AlreadyStored);
        var already = harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 2303).Subject;
        already.Level.Should().Be(LogLevel.Information);
        already.Properties["AsOfDate"].Should().Be(Today);
    }

    [Fact]
    public async Task A_store_that_throws_is_logged_and_the_tick_reports_failed()
    {
        var harness = new Harness();
        harness.Store.NewestAsOfDateAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("database unreachable"));

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(FxRateTickResult.Failed);
        var failed = harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 2301).Subject;
        failed.Level.Should().Be(LogLevel.Error);
        failed.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task A_source_that_throws_after_all_is_caught_the_same_way()
    {
        var harness = new Harness();
        harness.Source.FetchLatestAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("unexpected"));

        var result = await harness.Worker().RunTickAsync(Ct);

        result.Should().Be(FxRateTickResult.Failed);
        await harness.Store.DidNotReceiveWithAnyArgs().AppendAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_shutdown_during_the_fetch_propagates_and_stores_nothing()
    {
        var harness = new Harness();
        harness.Source.FetchLatestAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());

        var act = () => harness.Worker().RunTickAsync(Ct);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await harness.Store.DidNotReceiveWithAnyArgs().AppendAsync(default!, Arg.Any<CancellationToken>());
        harness.Logger.Entries.Should().NotContain(entry => entry.EventId.Id == 2301);
    }

    public static TheoryData<string> RemoteFailures() =>
        ["HTTP 429", "HTTP 500", "a timeout", "base_code USD", "a zero RSD rate", "result error"];

    // Review Focus 3, through the real AddNoofFx registration: only the HTTP answer and the store are fake.
    [Theory]
    [MemberData(nameof(RemoteFailures))]
    public async Task A_remote_failure_through_the_real_rate_source_stores_nothing_and_throws_nothing(string failure)
    {
        var harness = new Harness();
        await using var provider = RealSourceProvider(harness, Respond(failure));
        var worker = new FxRateWorker(provider.GetRequiredService<IServiceScopeFactory>(), harness.Clock, ReadyGate(), harness.Logger);

        var result = await worker.RunTickAsync(Ct);

        result.Should().Be(FxRateTickResult.NothingFetched);
        await harness.Store.DidNotReceiveWithAnyArgs().AppendAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_real_rate_sources_snapshot_is_what_the_store_receives()
    {
        var harness = new Harness();
        await using var provider = RealSourceProvider(harness, _ => Answer(HttpStatusCode.OK, OpenErApiPayloads.Latest()));
        var worker = new FxRateWorker(provider.GetRequiredService<IServiceScopeFactory>(), harness.Clock, ReadyGate(), harness.Logger);

        var result = await worker.RunTickAsync(Ct);

        result.Should().Be(FxRateTickResult.Stored);
        await harness.Store.Received(1).AppendAsync(
            Arg.Is<FxRateSnapshot>(snapshot => snapshot.AsOfDate == Today
                && snapshot.Source == FxSources.OpenErApi
                && snapshot.UnitsPerEur.Count == 4
                && snapshot.UnitsPerEur[CurrencyCode.Rsd] == 117.1532m),
            Arg.Any<CancellationToken>());
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
            harness.Store.ReceivedCalls().Should().BeEmpty("nothing may touch the database before it is ready");
            harness.Source.ReceivedCalls().Should().BeEmpty();

            ready.SetResult();
            await WaitUntilAsync(() => NewestCalls(harness) > 0);
        }
        finally
        {
            await worker.StopAsync(Ct);
        }

        NewestCalls(harness).Should().Be(1);
    }

    [Fact]
    public async Task The_next_tick_comes_six_hours_after_the_last()
    {
        var harness = new Harness();
        harness.Store.NewestAsOfDateAsync(Arg.Any<CancellationToken>()).Returns(Today);
        var worker = harness.Worker();
        FxRateWorker.Interval.Should().Be(TimeSpan.FromHours(6));

        await worker.StartAsync(Ct);
        try
        {
            await WaitUntilAsync(() => NewestCalls(harness) == 1);
            await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
            harness.Clock.Advance(FxRateWorker.Interval - TimeSpan.FromSeconds(1));
            await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
            NewestCalls(harness).Should().Be(1);

            harness.Clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => NewestCalls(harness) == 2);
        }
        finally
        {
            await worker.StopAsync(Ct);
        }

        NewestCalls(harness).Should().Be(2);
    }

    [Fact]
    public async Task A_failing_tick_never_ends_the_loop()
    {
        var harness = new Harness();
        harness.Store.NewestAsOfDateAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("database down"));
        var worker = harness.Worker();

        await worker.StartAsync(Ct);
        try
        {
            await WaitUntilAsync(() => NewestCalls(harness) == 1);
            await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
            harness.Clock.Advance(FxRateWorker.Interval);
            await WaitUntilAsync(() => NewestCalls(harness) == 2);
        }
        finally
        {
            await worker.StopAsync(Ct);
        }

        NewestCalls(harness).Should().Be(2);
        harness.Logger.Entries.Count(entry => entry.EventId.Id == 2301).Should().Be(2);
    }

    static ServiceProvider RealSourceProvider(Harness harness, Func<CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(harness.Clock);
        services.AddSingleton<IOperationTimer>(new OperationTimer(harness.Clock, new SlowOperationOptions()));
        services.AddNoofFx();
        // A later configuration of the same named client runs after AddNoofFx's own, so this timeout wins.
        services.AddHttpClient("open-er-api", client => client.Timeout = TimeSpan.FromMilliseconds(50))
            .ConfigurePrimaryHttpMessageHandler(() => new CannedHandler(respond));
        services.AddScoped(_ => harness.Store);
        return services.BuildServiceProvider();
    }

    static Func<CancellationToken, Task<HttpResponseMessage>> Respond(string failure) => failure switch
    {
        "HTTP 429" => _ => Answer(HttpStatusCode.TooManyRequests, OpenErApiPayloads.Latest()),
        "HTTP 500" => _ => Answer(HttpStatusCode.InternalServerError, "oops"),
        "a timeout" => async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        },
        "base_code USD" => _ => Answer(HttpStatusCode.OK, OpenErApiPayloads.Latest(baseCode: "USD")),
        "a zero RSD rate" => _ => Answer(HttpStatusCode.OK, OpenErApiPayloads.Latest(rates: OpenErApiPayloads.RatesWith("RSD", "0"))),
        "result error" => _ => Answer(HttpStatusCode.OK, """{"result":"error","error-type":"unsupported-code"}"""),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null),
    };

    static Task<HttpResponseMessage> Answer(HttpStatusCode status, string body) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

    sealed class CannedHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(cancellationToken);
    }
}
