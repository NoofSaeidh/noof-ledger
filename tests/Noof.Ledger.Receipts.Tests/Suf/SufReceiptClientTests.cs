using System.Net;
using AwesomeAssertions;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.Receipts.Suf;

namespace Noof.Ledger.Receipts.Tests.Suf;

public class SufReceiptClientTests
{
    static readonly FiscalQrPayload Payload = new(
        VerificationUrl: "https://suf.purs.gov.rs/v/?vl=synthetic",
        Total: 329.90m,
        IssuedAt: new DateTimeOffset(2026, 9, 25, 12, 30, 0, TimeSpan.Zero),
        RequestedBy: "REQ12345",
        SignedBy: "SIG54321",
        Kind: Noof.Ledger.Application.Receipts.ReceiptKind.Sale,
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

    static string JsonBody(string journal) =>
        $$"""
        {
            "invoiceRequest": { "businessName": "Test DOO", "taxId": "123456789", "address": "Knez Mihailova 1", "locationName": "001-Center" },
            "invoiceResult": { "invoiceNumber": "JID123-ABC456-78", "totalAmount": 329.90, "sdcTime": "2026-09-25T12:30:00" },
            "journal": {{System.Text.Json.JsonSerializer.Serialize(journal)}}
        }
        """;

    static SufReceiptClient ClientFor(HttpMessageHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task Fetches_and_parses_the_happy_path()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, JsonBody(Journal));
        var client = ClientFor(handler);

        var result = await client.FetchAsync(Payload, CancellationToken.None);

        result.Failure.Should().BeNull();
        result.Receipt.Should().NotBeNull();
        result.Receipt!.Source.Should().Be(Noof.Ledger.Application.Receipts.ReceiptSource.FiscalQr);
        result.Receipt.Currency.Should().Be(CurrencyCode.Rsd);
        result.Receipt.QrTotal.Should().Be(Payload.Total);
        result.Receipt.VerificationUrl.Should().Be(Payload.VerificationUrl);
        result.Receipt.SellerTaxId.Should().Be("123456789");
        result.Receipt.Total.Should().Be(329.90m);
        result.Receipt.Lines.Should().HaveCount(2);
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
        var client = new SufReceiptClient(httpClient);

        var result = await client.FetchAsync(Payload, CancellationToken.None);

        result.Receipt.Should().BeNull();
        result.Failure.Should().NotBeNull();
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
