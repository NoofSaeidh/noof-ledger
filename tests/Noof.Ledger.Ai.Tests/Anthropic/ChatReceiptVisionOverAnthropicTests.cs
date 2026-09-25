using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Noof.Ledger.Ai.Anthropic;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Ai.Tests.Anthropic;

// ChatReceiptVision end to end over the real Anthropic pipeline - factory, translating client, the
// SDK's own adapter - asserted on the HTTP body the SDK actually sent, the same way
// ChatCategorizerOverAnthropicTests pins ChatCategorizer.
public class ChatReceiptVisionOverAnthropicTests
{
    static readonly byte[] TinyImage = [0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03, 0x04];

    static (ChatReceiptVision Vision, StubHttpMessageHandler Handler) Build()
    {
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var secretStore = new StubSecretStore(SecretState.Present, "sk-ant-test-key-do-not-log-me");
        var options = new AnthropicOptions { Model = "claude-haiku-4-5-20251001", MaxTokens = 2048, Timeout = TimeSpan.FromSeconds(90) };
        var clientFactory = new AnthropicChatClientFactory(secretStore, httpClient, options);
        return (new ChatReceiptVision(clientFactory), handler);
    }

    [Fact]
    public async Task Sends_the_image_as_a_base64_block_alongside_a_forced_strict_read_receipt_tool()
    {
        var (vision, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.ReadReceiptJsonAnswer);

        await vision.ReadAsync(TinyImage, "image/jpeg", qrTotal: null, TestContext.Current.CancellationToken);

        var sent = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        var content = sent.GetProperty("messages")[0].GetProperty("content");
        var imageBlock = content.EnumerateArray().Single(block => block.GetProperty("type").GetString() == "image");
        imageBlock.GetProperty("source").GetProperty("media_type").GetString().Should().Be("image/jpeg");
        imageBlock.GetProperty("source").GetProperty("data").GetString().Should().Be(Convert.ToBase64String(TinyImage));

        var tools = sent.GetProperty("tools");
        tools.GetArrayLength().Should().Be(1);
        tools[0].GetProperty("name").GetString().Should().Be("read_receipt");
        tools[0].GetProperty("strict").GetBoolean().Should().BeTrue();
        sent.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("tool");
        sent.GetProperty("tool_choice").GetProperty("name").GetString().Should().Be("read_receipt");

        var currencyEnum = tools[0].GetProperty("input_schema").GetProperty("properties")
            .GetProperty("currency").GetProperty("enum").EnumerateArray().Select(e => e.GetString());
        currencyEnum.Should().BeEquivalentTo(["EUR", "RSD", "USD", "RUB", "KZT"]);
    }

    [Fact]
    public async Task Payment_method_reaches_the_wire_as_an_anyOf_never_an_enum_beside_a_type_array()
    {
        var (vision, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.ReadReceiptJsonAnswer);

        await vision.ReadAsync(TinyImage, "image/jpeg", qrTotal: null, TestContext.Current.CancellationToken);

        var sent = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        var paymentMethod = sent.GetProperty("tools")[0].GetProperty("input_schema")
            .GetProperty("properties").GetProperty("payment_method");

        paymentMethod.TryGetProperty("type", out _).Should().BeFalse(
            "the nullable enum must reach the wire as an anyOf, never an enum beside a type array");
        JsonNode.DeepEquals(
                JsonNode.Parse(paymentMethod.GetProperty("anyOf").GetRawText()),
                JsonNode.Parse(
                    """[{ "type": "string", "enum": ["card", "cash", "transfer", "voucher", "other", "mixed"] }, { "type": "null" }]"""))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Maps_the_answer_to_an_ExtractedReceipt_with_exact_decimals_and_ordinals()
    {
        var (vision, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.ReadReceiptJsonAnswer);

        var receipt = await vision.ReadAsync(TinyImage, "image/jpeg", qrTotal: 845.50m, TestContext.Current.CancellationToken);

        receipt.Source.Should().Be(Application.Receipts.ReceiptSource.Vision);
        receipt.SellerName.Should().Be("Maxi");
        receipt.SellerTaxId.Should().Be("123456789");
        receipt.Currency.Should().Be(CurrencyCode.Rsd);
        receipt.Total.Should().Be(845.50m);
        receipt.QrTotal.Should().Be(845.50m);
        receipt.Kind.Should().Be(Application.Receipts.ReceiptKind.Sale);
        receipt.PaymentMethod.Should().Be(Application.Receipts.PaymentMethod.Card);
        receipt.IssuedAt.Should().NotBeNull();
        receipt.IssuedAt!.Value.Offset.Should().Be(TimeSpan.FromHours(2), "20 September is still Belgrade summer time (UTC+2)");

        receipt.Lines.Should().HaveCount(2);
        receipt.Lines[0].Ordinal.Should().Be(1);
        receipt.Lines[0].Name.Should().Be("Mleko");
        receipt.Lines[0].Total.Should().Be(120m);
        receipt.Lines[1].Ordinal.Should().Be(2);
        receipt.Lines[1].UnitPrice.Should().Be(90.25m);
        receipt.Lines[1].Total.Should().Be(180.50m);
    }

    [Fact]
    public async Task An_image_over_5_MB_is_rejected_before_any_network_call()
    {
        var (vision, handler) = Build();
        var oversized = new byte[(5 * 1024 * 1024) + 1];

        var act = () => vision.ReadAsync(oversized, "image/jpeg", null, TestContext.Current.CancellationToken);

        var thrown = (await act.Should().ThrowAsync<ModelCallException>()).Which;
        thrown.Kind.Should().Be(ModelFailureKind.Terminal);
        handler.Requests.Should().BeEmpty("an oversized image must never reach the network");
    }

    [Fact]
    public async Task An_image_exactly_at_the_5_MB_limit_is_accepted()
    {
        var (vision, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.ReadReceiptJsonAnswer);
        var exactlyAtLimit = new byte[5 * 1024 * 1024];

        await vision.ReadAsync(exactlyAtLimit, "image/jpeg", null, TestContext.Current.CancellationToken);

        handler.Requests.Should().ContainSingle();
    }
}
