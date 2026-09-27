using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.Receipts.Suf;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Receipts.Tests.Suf;

public class SufReceiptClientTests
{
    static readonly FiscalQrPayload Payload = new(
        VerificationUrl: "https://suf.purs.gov.rs/v/?vl=synthetic",
        Total: 329.90m,
        IssuedAt: new DateTimeOffset(2026, 9, 25, 12, 30, 0, TimeSpan.Zero),
        RequestedBy: "REQ12345",
        SignedBy: "SIG54321",
        Kind: ReceiptKind.Sale,
        TotalCounter: 42,
        TransactionTypeCounter: 7);

    const string Journal = """
        ============ ФИСКАЛНИ РАЧУН ============
        123456789
        ТЕСТ ДОО
        001-Продавница Центар
        Кнез Михаилова 1
        Београд
        ========================================
        Назив   Цена         Кол.      Укупно
        Хлеб/kom(Ђ)
              120,00               2       240,00
        Млеко(Е)
               89,90               1        89,90
        ----------------------------------------
        Укупан износ: 329,90
        Платна картица: 329,90
        ========================================
        Ђ  ОПШТА  20                    40,00
        ----------------------------------------
        Укупан износ пореза: 40,00
        ========================================
        ПФР време: 25.09.2026. 12:30:00
        ПФР број рачуна: ЈИД123-АБВ456-78
        Бројач рачуна: 123/456ПП
        ========================================
        data:image/gif;base64,AAAA
        ======== КРАЈ ФИСКАЛНОГ РАЧУНА =========
        """;

    static string JsonBody(string journal, string sdcTime = "2026-09-25T12:30:00") =>
        $$"""
        {
            "invoiceRequest": { "businessName": "Test DOO", "taxId": "123456789", "address": "Knez Mihailova 1", "locationName": "001-Center" },
            "invoiceResult": { "invoiceNumber": "JID123-ABC456-78", "totalAmount": 329.90, "sdcTime": "{{sdcTime}}" },
            "journal": {{System.Text.Json.JsonSerializer.Serialize(journal)}}
        }
        """;

    static DateTimeOffset BelgradeTime(int year, int month, int day, int hour, int minute, int second)
    {
        var belgrade = TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade");
        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, belgrade.GetUtcOffset(local));
    }

    static SufReceiptClient ClientFor(HttpMessageHandler handler, IOperationTimer? timer = null, Microsoft.Extensions.Logging.ILogger<SufReceiptClient>? logger = null) =>
        new(new HttpClient(handler), timer ?? NoopTimer, logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SufReceiptClient>.Instance);

    static readonly IOperationTimer NoopTimer = new OperationTimer(TimeProvider.System, new SlowOperationOptions());

    [Fact]
    public async Task Fetches_and_parses_the_happy_path()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, JsonBody(Journal));
        var client = ClientFor(handler);

        var result = await client.FetchAsync(Payload, CancellationToken.None);

        result.Failure.Should().BeNull();
        result.Receipt.Should().NotBeNull();
        result.Receipt!.Source.Should().Be(ReceiptSource.FiscalQr);
        result.Receipt.Currency.Should().Be(CurrencyCode.Rsd);
        result.Receipt.QrTotal.Should().Be(Payload.Total);
        result.Receipt.VerificationUrl.Should().Be(Payload.VerificationUrl);
        result.Receipt.SellerTaxId.Should().Be("123456789");
        result.Receipt.Total.Should().Be(329.90m);
        result.Receipt.Lines.Should().HaveCount(2);
        result.Receipt.IssuedAt.Should().Be(BelgradeTime(2026, 9, 25, 12, 30, 0),
            "the journal's own ПФР време is unambiguous and preferred over sdcTime");
    }

    [Fact]
    public async Task Falls_back_to_sdcTime_parsed_as_belgrade_local_and_keeps_a_late_evening_receipts_date()
    {
        var journalWithNoPfrTime = Journal.Replace("ПФР време: 25.09.2026. 12:30:00", "", StringComparison.Ordinal);
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, JsonBody(journalWithNoPfrTime, sdcTime: "2026-09-25T23:30:00"));
        var client = ClientFor(handler);

        var result = await client.FetchAsync(Payload, CancellationToken.None);

        result.Receipt.Should().NotBeNull();
        result.Receipt!.IssuedAt.Should().Be(BelgradeTime(2026, 9, 25, 23, 30, 0),
            "sdcTime carries no offset and must be read as Belgrade local time, not UTC - a receipt " +
            "issued 23:30 local must not land on the next day");
    }

    [Fact]
    public async Task Sends_the_accept_header_and_a_descriptive_user_agent()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, JsonBody(Journal));
        var client = ClientFor(handler);

        await client.FetchAsync(Payload, CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.Accept.Should().ContainSingle(h => h.MediaType == "application/json");
        handler.LastRequest.Headers.UserAgent.ToString().Should().Contain("noof-ledger/").And.Contain("personal receipt lookup");
        handler.LastRequest.RequestUri.Should().Be(new Uri(Payload.VerificationUrl));
    }

    [Fact]
    public async Task Reports_a_failure_on_a_server_error()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.InternalServerError, "oops");
        var client = ClientFor(handler);

        var result = await client.FetchAsync(Payload, CancellationToken.None);

        result.Receipt.Should().BeNull();
        result.Failure.Should().NotBeNull();
        result.Failure!.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task Reports_a_failure_when_the_response_body_cannot_be_read_instead_of_throwing()
    {
        // M-9 (2026-09-25 final review): a body that fails mid-read (IOException, wrapped by
        // HttpContent as HttpRequestException) must never throw out of FetchAsync. With the default
        // HttpCompletionOption this client uses, HttpClient already buffers the body during SendAsync,
        // so this is caught by the SendAsync try's own HttpRequestException handler either way - the
        // ReadAsStringAsync/Deserialize try below now also catches it directly, in case that ever
        // changes (e.g. HttpCompletionOption.ResponseHeadersRead). This test guards the contract, not
        // one specific catch clause.
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenBodyContent() }));
        var client = ClientFor(handler);

        FiscalFetchResult? result = null;
        var act = async () => result = await client.FetchAsync(Payload, CancellationToken.None);

        await act.Should().NotThrowAsync();
        result!.Receipt.Should().BeNull();
        result.Failure.Should().NotBeNull();
    }

    sealed class BrokenBodyContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new IOException("connection reset while reading the response body");

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken) =>
            throw new IOException("connection reset while reading the response body");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task Reports_a_failure_on_garbage_json()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, "not json at all {{{");
        var client = ClientFor(handler);

        var result = await client.FetchAsync(Payload, CancellationToken.None);

        result.Receipt.Should().BeNull();
        result.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task Reports_a_failure_on_a_journal_with_no_items()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, JsonBody("no receipt structure here"));
        var client = ClientFor(handler);

        var result = await client.FetchAsync(Payload, CancellationToken.None);

        result.Receipt.Should().BeNull();
        result.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task Reports_a_failure_on_timeout_without_throwing()
    {
        var handler = StubHttpMessageHandler.NeverResponding();
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        var client = new SufReceiptClient(httpClient, NoopTimer, Microsoft.Extensions.Logging.Abstractions.NullLogger<SufReceiptClient>.Instance);

        var result = await client.FetchAsync(Payload, CancellationToken.None);

        result.Receipt.Should().BeNull();
        result.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task A_scripted_response_that_advances_the_clock_gives_one_receipt_fiscalFetch_timing()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var timer = new OperationTimer(clock, new SlowOperationOptions());
        var logger = new CapturingLogger<SufReceiptClient>();
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonBody(Journal)) });
        });
        var client = ClientFor(handler, timer, logger);

        await client.FetchAsync(Payload, CancellationToken.None);

        logger.Entries.Should().ContainSingle(entry => (string)entry.Properties["Operation"] == "receipt.fiscalFetch");
    }

    [Fact]
    public async Task Never_leaks_the_verification_url_into_the_failure_reason_on_a_connection_failure()
    {
        // Copilot review, PR #3 (item B): HttpRequestException.Message can embed the request URI,
        // which would put the full verification link (vl) into FiscalFetchFailure.Reason and from
        // there into the log line ExtractReceiptWorker writes - forbidden by CLAUDE.md's "a fiscal
        // receipt's verification URL is never logged".
        const string secretUrl = "https://suf.purs.gov.rs/v/?vl=SECRETPAYLOADFROMTHEQR1234567890";
        var payload = Payload with { VerificationUrl = secretUrl };
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException($"Connection failed while contacting {secretUrl}"));
        var client = ClientFor(handler);

        var result = await client.FetchAsync(payload, CancellationToken.None);

        result.Receipt.Should().BeNull();
        result.Failure.Should().NotBeNull();
        result.Failure!.Reason.Should().NotContain(secretUrl);
        result.Failure.Reason.Should().NotContain("SECRETPAYLOADFROMTHEQR1234567890");
    }

    [Fact]
    public async Task Reports_a_failure_instead_of_throwing_on_a_journal_missing_its_total_line()
    {
        // Copilot review, PR #3: a journal with valid item lines but no Укупан износ/Ukupan iznos
        // (or refund equivalent) must take the same malformed-journal path as an unparseable
        // amount, never silently report a total of 0.
        var journalWithNoTotal = Journal.Replace("Укупан износ: 329,90", "", StringComparison.Ordinal);
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, JsonBody(journalWithNoTotal));
        var client = ClientFor(handler);

        var result = await client.FetchAsync(Payload, CancellationToken.None);

        result.Receipt.Should().BeNull();
        result.Failure.Should().NotBeNull();
        result.Failure!.Reason.Should().Be("The Tax Administration's journal could not be parsed.",
            "a missing total line is a malformed journal, not a fetch that must be retried");
    }

    [Fact]
    public async Task Reports_a_failure_instead_of_throwing_on_a_malformed_journal_amount()
    {
        // Copilot review, PR #3 (item C): FiscalJournalParser.Parse's ParseAmount throws
        // FormatException on an unparseable amount. Left uncaught, ExtractReceiptWorker's generic
        // catch treats this as a transient retry instead of taking the QR-total -> vision fallback
        // every other fetch failure gets.
        var malformedJournal = Journal.Replace("120,00               2       240,00", "120,00               2       1,2,3,4", StringComparison.Ordinal);
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, JsonBody(malformedJournal));
        var client = ClientFor(handler);

        var result = await client.FetchAsync(Payload, CancellationToken.None);

        result.Receipt.Should().BeNull();
        result.Failure.Should().NotBeNull();
        result.Failure!.Reason.Should().Be("The Tax Administration's journal could not be parsed.",
            "a regression back into an unhandled FormatException, or a change to the fixed reason text, must fail this test rather than pass silently");
    }

    [Fact]
    public async Task Propagates_cancellation_from_the_callers_own_token()
    {
        var handler = StubHttpMessageHandler.NeverResponding();
        var client = ClientFor(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => client.FetchAsync(Payload, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
