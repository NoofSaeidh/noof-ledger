using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Domain;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Fx.Tests;

public class OpenErApiRateSourceTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    // 2027-04-01 00:00:00 UTC.
    const long EndOfLifeUnix = 1_806_537_600;

    const int NotFetchedId = 2201;
    const int RejectedId = 2202;
    const int EndOfLifeId = 2203;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static OpenErApiRateSource SourceFor(
        HttpMessageHandler handler, CapturingLogger<OpenErApiRateSource>? logger = null,
        OpenErApiEndOfLifeNotice? notice = null, TimeSpan? timeout = null)
    {
        var clock = new FakeTimeProvider(Now);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://open.er-api.com/") };
        if (timeout is { } shorter)
            httpClient.Timeout = shorter;

        return new OpenErApiRateSource(
            httpClient, clock, notice ?? new OpenErApiEndOfLifeNotice(),
            new OperationTimer(clock, new SlowOperationOptions()), logger ?? new CapturingLogger<OpenErApiRateSource>());
    }

    static StubHttpMessageHandler Ok(string body) => StubHttpMessageHandler.Returning(HttpStatusCode.OK, body);

    static CapturedLogEntry SingleWarning(CapturingLogger<OpenErApiRateSource> logger, int eventId)
    {
        var entry = logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == eventId).Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeNull("an exception's message is never logged, only a fixed phrase");
        logger.Entries.Where(other => other.Level >= LogLevel.Warning).Should().ContainSingle("one failure is one Warning");
        return entry;
    }

    [Fact]
    public async Task Asks_open_er_api_for_the_latest_EUR_rates()
    {
        var handler = Ok(OpenErApiPayloads.Latest());

        await SourceFor(handler).FetchLatestAsync(Ct);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri.Should().Be(new Uri("https://open.er-api.com/v6/latest/EUR"));
    }

    [Fact]
    public async Task A_valid_answer_is_returned_as_a_snapshot_with_no_warning()
    {
        var logger = new CapturingLogger<OpenErApiRateSource>();

        var snapshot = (await SourceFor(Ok(OpenErApiPayloads.Latest()), logger).FetchLatestAsync(Ct))
            .Should().BeOfType<FxRateSnapshot>().Subject;

        snapshot.AsOfDate.Should().Be(new DateOnly(2026, 10, 8));
        snapshot.Source.Should().Be(FxSources.OpenErApi);
        snapshot.UnitsPerEur.Should().HaveCount(4);
        snapshot.UnitsPerEur[CurrencyCode.Rsd].Should().Be(117.1532m);
        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_fetch_is_timed_as_fx_fetchRates()
    {
        var logger = new CapturingLogger<OpenErApiRateSource>();

        await SourceFor(Ok(OpenErApiPayloads.Latest()), logger).FetchLatestAsync(Ct);

        logger.Entries.Should().ContainSingle(entry => entry.Properties.GetValueOrDefault("Operation") as string == "fx.fetchRates");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "status 429")]
    [InlineData(HttpStatusCode.InternalServerError, "status 500")]
    [InlineData(HttpStatusCode.NotFound, "status 404")]
    public async Task An_error_status_returns_nothing_and_warns_with_the_status(HttpStatusCode status, string reason)
    {
        var logger = new CapturingLogger<OpenErApiRateSource>();

        var snapshot = await SourceFor(StubHttpMessageHandler.Returning(status, OpenErApiPayloads.Latest()), logger)
            .FetchLatestAsync(Ct);

        snapshot.Should().BeNull();
        SingleWarning(logger, NotFetchedId).Properties["Reason"].Should().Be(reason);
    }

    [Fact]
    public async Task A_timeout_returns_nothing_and_warns()
    {
        var logger = new CapturingLogger<OpenErApiRateSource>();

        var snapshot = await SourceFor(StubHttpMessageHandler.NeverResponding(), logger, timeout: TimeSpan.FromMilliseconds(50))
            .FetchLatestAsync(Ct);

        snapshot.Should().BeNull();
        SingleWarning(logger, NotFetchedId).Properties["Reason"].Should().Be("the request timed out");
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "the host name could not be resolved")]
    [InlineData(HttpRequestError.ConnectionError, "the connection failed")]
    [InlineData(HttpRequestError.SecureConnectionError, "a secure connection could not be established")]
    [InlineData(HttpRequestError.Unknown, "the request failed")]
    public async Task A_connection_failure_returns_nothing_and_warns_with_a_fixed_phrase(HttpRequestError error, string reason)
    {
        var logger = new CapturingLogger<OpenErApiRateSource>();
        var failure = new HttpRequestException(error, "socket text that must never reach a log line");

        var snapshot = await SourceFor(StubHttpMessageHandler.Throwing(failure), logger).FetchLatestAsync(Ct);

        snapshot.Should().BeNull();
        SingleWarning(logger, NotFetchedId).Properties["Reason"].Should().Be(reason);
    }

    [Theory]
    [InlineData("not json at all {{{", "the response was not valid JSON")]
    [InlineData("""{"result":"success","base_code":"EUR","rates":[]}""", "the response was not valid JSON")]
    [InlineData("null", "the response was empty")]
    public async Task A_body_that_is_not_a_payload_returns_nothing_and_warns(string body, string reason)
    {
        var logger = new CapturingLogger<OpenErApiRateSource>();

        var snapshot = await SourceFor(Ok(body), logger).FetchLatestAsync(Ct);

        snapshot.Should().BeNull();
        SingleWarning(logger, RejectedId).Properties["Reason"].Should().Be(reason);
    }

    [Theory]
    [InlineData("\"117.1532\"")]
    [InlineData("1e40")]
    public async Task A_rate_no_decimal_reads_is_invalid_JSON(string rsdToken)
    {
        var logger = new CapturingLogger<OpenErApiRateSource>();
        var body = OpenErApiPayloads.Latest(rates: OpenErApiPayloads.RatesWith("RSD", rsdToken));

        var snapshot = await SourceFor(Ok(body), logger).FetchLatestAsync(Ct);

        snapshot.Should().BeNull();
        SingleWarning(logger, RejectedId).Properties["Reason"].Should().Be("the response was not valid JSON");
    }

    [Fact]
    public async Task A_payload_that_fails_validation_returns_nothing_and_warns_with_its_reason()
    {
        var logger = new CapturingLogger<OpenErApiRateSource>();

        var snapshot = await SourceFor(Ok(OpenErApiPayloads.Latest(baseCode: "USD")), logger).FetchLatestAsync(Ct);

        snapshot.Should().BeNull();
        SingleWarning(logger, RejectedId).Properties["Reason"].Should().Be("the base was not EUR");
    }

    [Fact]
    public async Task An_announced_end_of_life_warns_once_per_notice_and_the_rates_still_come_back()
    {
        var logger = new CapturingLogger<OpenErApiRateSource>();
        var notice = new OpenErApiEndOfLifeNotice();
        var body = OpenErApiPayloads.Latest(endOfLifeUnix: EndOfLifeUnix);

        var first = await SourceFor(Ok(body), logger, notice).FetchLatestAsync(Ct);
        var second = await SourceFor(Ok(body), logger, notice).FetchLatestAsync(Ct);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        SingleWarning(logger, EndOfLifeId).Properties["EndOfLifeUnix"].Should().Be(EndOfLifeUnix);
    }

    [Fact]
    public async Task An_end_of_life_announced_with_an_invalid_payload_still_warns()
    {
        var logger = new CapturingLogger<OpenErApiRateSource>();

        await SourceFor(Ok(OpenErApiPayloads.Latest(baseCode: "USD", endOfLifeUnix: EndOfLifeUnix)), logger).FetchLatestAsync(Ct);

        logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == EndOfLifeId);
        logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == RejectedId);
    }

    [Fact]
    public async Task The_callers_own_cancellation_propagates()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => SourceFor(StubHttpMessageHandler.NeverResponding()).FetchLatestAsync(cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
