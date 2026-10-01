using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Ai.Tests;

// ChatReceiptVision with no provider underneath it at all. What reaches the wire is pinned by
// ChatReceiptVisionOverAnthropicTests; this pins what the provider-neutral layer itself says.
public class ChatReceiptVisionTests
{
    static readonly IOperationTimer NoopTimer = new OperationTimer(TimeProvider.System, new SlowOperationOptions());

    static readonly byte[] TinyImage = [1, 2, 3, 4];

    static FunctionCallContent Answer() => new(
        "call_1", "read_receipt",
        new Dictionary<string, object?>
        {
            ["readable"] = true,
            ["unreadable_reason"] = null,
            ["seller_name"] = "Maxi",
            ["seller_tax_id"] = "123456789",
            ["fiscal_number"] = null,
            ["issued_at"] = null,
            ["currency"] = "RSD",
            ["total"] = 100,
            ["payment_method"] = null,
            ["kind"] = "sale",
            ["lines"] = new[]
            {
                new Dictionary<string, object?> { ["name"] = "Bread", ["quantity"] = 1, ["unit_price"] = 100, ["total"] = 100 },
            },
        });

    [Fact]
    public async Task The_tool_it_offers_is_strict_in_provider_neutral_terms()
    {
        var provider = new ScriptedChatClient().Answer(Answer());
        var vision = new ChatReceiptVision(new FixedChatClientFactory(provider), NoopTimer, NullLogger<ChatReceiptVision>.Instance);

        await vision.ReadAsync(TinyImage, "image/jpeg", null, TestContext.Current.CancellationToken);

        var offered = provider.Requests.SelectMany(request => request.Options!.Tools!).ToList();
        offered.Should().ContainSingle().Which.Name.Should().Be("read_receipt");
        offered.Should().OnlyContain(tool => tool.IsStrict());
        offered.Should().OnlyContain(tool => !tool.AdditionalProperties.ContainsKey("Strict"),
            "\"Strict\" is what one provider's adapter reads; saying it here would tie this layer to that provider");
    }

    [Fact]
    public async Task The_tool_describes_a_shop_receipt_or_an_exchange_office_slip()
    {
        var provider = new ScriptedChatClient().Answer(Answer());
        var vision = new ChatReceiptVision(new FixedChatClientFactory(provider), NoopTimer, NullLogger<ChatReceiptVision>.Instance);

        await vision.ReadAsync(TinyImage, "image/jpeg", null, TestContext.Current.CancellationToken);

        provider.Requests.Single().Options!.Tools!.Single().Description
            .Should().Be("Record what a photographed shop receipt or exchange-office slip prints.");
    }

    [Fact]
    public async Task A_non_ISO_issued_at_is_read_as_a_missing_date_instead_of_throwing()
    {
        var answer = new FunctionCallContent(
            "call_1", "read_receipt",
            new Dictionary<string, object?>
            {
                ["readable"] = true,
                ["unreadable_reason"] = null,
                ["seller_name"] = "Maxi",
                ["seller_tax_id"] = "123456789",
                ["fiscal_number"] = null,
                ["issued_at"] = "25.09.2026 12:30",
                ["currency"] = "RSD",
                ["total"] = 100,
                ["payment_method"] = null,
                ["kind"] = "sale",
                ["lines"] = new[]
                {
                    new Dictionary<string, object?> { ["name"] = "Bread", ["quantity"] = 1, ["unit_price"] = 100, ["total"] = 100 },
                },
            });
        var provider = new ScriptedChatClient().Answer(answer);
        var vision = new ChatReceiptVision(new FixedChatClientFactory(provider), NoopTimer, NullLogger<ChatReceiptVision>.Instance);

        var result = await vision.ReadAsync(TinyImage, "image/jpeg", null, TestContext.Current.CancellationToken);

        result.Unreadable.Should().BeNull();
        result.Receipt!.IssuedAt.Should().BeNull("a date the model read but could not phrase in ISO form must not fault the extraction");
    }

    [Fact]
    public async Task Readable_false_is_reported_as_unreadable_and_nothing_is_extracted()
    {
        var answer = new FunctionCallContent(
            "call_1", "read_receipt",
            new Dictionary<string, object?>
            {
                ["readable"] = false,
                ["unreadable_reason"] = "blurry",
                ["seller_name"] = null,
                ["seller_tax_id"] = null,
                ["fiscal_number"] = null,
                ["issued_at"] = null,
                ["currency"] = "RSD",
                ["total"] = null,
                ["payment_method"] = null,
                ["kind"] = "sale",
                ["lines"] = Array.Empty<object>(),
            });
        var provider = new ScriptedChatClient().Answer(answer);
        var vision = new ChatReceiptVision(new FixedChatClientFactory(provider), NoopTimer, NullLogger<ChatReceiptVision>.Instance);

        var result = await vision.ReadAsync(TinyImage, "image/jpeg", null, TestContext.Current.CancellationToken);

        result.Receipt.Should().BeNull("an unreadable photo must never produce a half-built receipt");
        result.Unreadable.Should().Be(ReceiptUnreadableReason.Blurry);
    }

    [Fact]
    public async Task Readable_true_with_a_null_total_is_still_reported_as_unreadable()
    {
        var answer = new FunctionCallContent(
            "call_1", "read_receipt",
            new Dictionary<string, object?>
            {
                ["readable"] = true,
                ["unreadable_reason"] = null,
                ["seller_name"] = "Maxi",
                ["seller_tax_id"] = null,
                ["fiscal_number"] = null,
                ["issued_at"] = null,
                ["currency"] = "RSD",
                ["total"] = null,
                ["payment_method"] = null,
                ["kind"] = "sale",
                ["lines"] = Array.Empty<object>(),
            });
        var provider = new ScriptedChatClient().Answer(answer);
        var vision = new ChatReceiptVision(new FixedChatClientFactory(provider), NoopTimer, NullLogger<ChatReceiptVision>.Instance);

        var result = await vision.ReadAsync(TinyImage, "image/jpeg", null, TestContext.Current.CancellationToken);

        result.Receipt.Should().BeNull("readable but no total is a contradiction, not a receipt to trust");
        result.Unreadable.Should().Be(ReceiptUnreadableReason.Other, "the model gave no reason of its own for this contradiction");
    }

    [Fact]
    public async Task Readable_true_with_zero_lines_is_still_reported_as_unreadable()
    {
        var answer = new FunctionCallContent(
            "call_1", "read_receipt",
            new Dictionary<string, object?>
            {
                ["readable"] = true,
                ["unreadable_reason"] = null,
                ["seller_name"] = "Maxi",
                ["seller_tax_id"] = null,
                ["fiscal_number"] = null,
                ["issued_at"] = null,
                ["currency"] = "RSD",
                ["total"] = 100,
                ["payment_method"] = null,
                ["kind"] = "sale",
                ["lines"] = Array.Empty<object>(),
            });
        var provider = new ScriptedChatClient().Answer(answer);
        var vision = new ChatReceiptVision(new FixedChatClientFactory(provider), NoopTimer, NullLogger<ChatReceiptVision>.Instance);

        var result = await vision.ReadAsync(TinyImage, "image/jpeg", null, TestContext.Current.CancellationToken);

        result.Receipt.Should().BeNull();
        result.Unreadable.Should().Be(ReceiptUnreadableReason.Other);
    }

    // Copilot finding, PR #3: the prompt tells the model to leave currency/kind null when it cannot
    // read them - the schema must accept that, and the mapping must not invent a value the model
    // never reported. RSD is the one deliberate default this prompt keeps (Serbian receipts); kind has
    // no safe default, since a guessed Sale on an actual refund would change the money direction.
    [Fact]
    public async Task A_null_currency_on_a_readable_receipt_defaults_to_RSD()
    {
        var answer = new FunctionCallContent(
            "call_1", "read_receipt",
            new Dictionary<string, object?>
            {
                ["readable"] = true,
                ["unreadable_reason"] = null,
                ["seller_name"] = "Maxi",
                ["seller_tax_id"] = null,
                ["fiscal_number"] = null,
                ["issued_at"] = null,
                ["currency"] = null,
                ["total"] = 100,
                ["payment_method"] = null,
                ["kind"] = "sale",
                ["lines"] = new[]
                {
                    new Dictionary<string, object?> { ["name"] = "Bread", ["quantity"] = 1, ["unit_price"] = 100, ["total"] = 100 },
                },
            });
        var provider = new ScriptedChatClient().Answer(answer);
        var vision = new ChatReceiptVision(new FixedChatClientFactory(provider), NoopTimer, NullLogger<ChatReceiptVision>.Instance);

        var result = await vision.ReadAsync(TinyImage, "image/jpeg", null, TestContext.Current.CancellationToken);

        result.Receipt.Should().NotBeNull();
        result.Receipt!.Currency.Should().Be(Noof.Ledger.Domain.CurrencyCode.Rsd);
    }

    [Fact]
    public async Task A_null_kind_on_a_readable_receipt_is_reported_as_unclear_rather_than_a_silent_sale_guess()
    {
        var answer = new FunctionCallContent(
            "call_1", "read_receipt",
            new Dictionary<string, object?>
            {
                ["readable"] = true,
                ["unreadable_reason"] = null,
                ["seller_name"] = "Maxi",
                ["seller_tax_id"] = null,
                ["fiscal_number"] = null,
                ["issued_at"] = null,
                ["currency"] = "RSD",
                ["total"] = 100,
                ["payment_method"] = null,
                ["kind"] = null,
                ["lines"] = new[]
                {
                    new Dictionary<string, object?> { ["name"] = "Bread", ["quantity"] = 1, ["unit_price"] = 100, ["total"] = 100 },
                },
            });
        var provider = new ScriptedChatClient().Answer(answer);
        var vision = new ChatReceiptVision(new FixedChatClientFactory(provider), NoopTimer, NullLogger<ChatReceiptVision>.Instance);

        var result = await vision.ReadAsync(TinyImage, "image/jpeg", null, TestContext.Current.CancellationToken);

        result.Receipt.Should().NotBeNull();
        result.Receipt!.Kind.Should().Be(Noof.Ledger.Domain.ReceiptKind.Sale, "there is no null to hold, so it defaults to the common case");
        result.KindUnclear.Should().BeTrue("a guessed Sale on an actual refund would change the money direction, so this must not be trusted silently");
    }

    [Fact]
    public async Task An_unreadable_receipt_accepts_null_currency_and_kind_without_faulting()
    {
        var answer = new FunctionCallContent(
            "call_1", "read_receipt",
            new Dictionary<string, object?>
            {
                ["readable"] = false,
                ["unreadable_reason"] = "blurry",
                ["seller_name"] = null,
                ["seller_tax_id"] = null,
                ["fiscal_number"] = null,
                ["issued_at"] = null,
                ["currency"] = null,
                ["total"] = null,
                ["payment_method"] = null,
                ["kind"] = null,
                ["lines"] = Array.Empty<object>(),
            });
        var provider = new ScriptedChatClient().Answer(answer);
        var vision = new ChatReceiptVision(new FixedChatClientFactory(provider), NoopTimer, NullLogger<ChatReceiptVision>.Instance);

        var result = await vision.ReadAsync(TinyImage, "image/jpeg", null, TestContext.Current.CancellationToken);

        result.Receipt.Should().BeNull();
        result.Unreadable.Should().Be(ReceiptUnreadableReason.Blurry);
    }

    [Fact]
    public async Task A_scripted_client_that_advances_the_clock_gives_one_model_readReceipt_timing()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var timer = new OperationTimer(clock, new SlowOperationOptions());
        var logger = new CapturingLogger<ChatReceiptVision>();
        var provider = new ScriptedChatClient().AnswerAfterDelay(clock, TimeSpan.FromMilliseconds(10), Answer());
        var vision = new ChatReceiptVision(new FixedChatClientFactory(provider), timer, logger);

        await vision.ReadAsync(TinyImage, "image/jpeg", null, TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(entry => (string)entry.Properties["Operation"] == "model.readReceipt");
    }

    [Fact]
    public async Task An_oversized_image_never_reaches_the_provider()
    {
        var provider = new ScriptedChatClient();
        var vision = new ChatReceiptVision(new FixedChatClientFactory(provider), NoopTimer, NullLogger<ChatReceiptVision>.Instance);
        var oversized = new byte[(5 * 1024 * 1024) + 1];

        var act = () => vision.ReadAsync(oversized, "image/jpeg", null, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Terminal);
        provider.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("123456789", "123456789")]
    [InlineData("12345678", null)]
    [InlineData("1234567890", null)]
    [InlineData("PIB123456", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" 123456789 ", "123456789")]
    public async Task A_printed_tax_id_is_accepted_only_when_it_is_exactly_9_digits(string? printed, string? expected)
    {
        var vision = new ChatReceiptVision(
            new FixedChatClientFactory(new ScriptedChatClient().Answer(AnswerWith(sellerTaxId: printed))), NoopTimer,
            NullLogger<ChatReceiptVision>.Instance);

        var result = await vision.ReadAsync(TinyImage, "image/jpeg", null, TestContext.Current.CancellationToken);

        result.Receipt!.SellerTaxId.Should().Be(expected);
    }

    [Theory]
    [InlineData("PIB123456", true)]
    [InlineData("123456789", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(" 123456789 ", false)]
    public async Task A_malformed_printed_tax_id_is_reported_alongside_the_receipt_not_only_dropped(string? printed, bool expectedMalformed)
    {
        var vision = new ChatReceiptVision(
            new FixedChatClientFactory(new ScriptedChatClient().Answer(AnswerWith(sellerTaxId: printed))), NoopTimer,
            NullLogger<ChatReceiptVision>.Instance);

        var result = await vision.ReadAsync(TinyImage, "image/jpeg", null, TestContext.Current.CancellationToken);

        result.SellerTaxIdMalformed.Should().Be(expectedMalformed);
        result.Receipt.Should().NotBeNull("a malformed tax id is a reason for the operator to confirm, not to fail the whole read");
    }

    [Theory]
    [InlineData("2WJCQFGP-2WJCQFGP-66360", "2WJCQFGP-2WJCQFGP-66360")]
    [InlineData("2wjcqfgp-2wjcqfgp-66360", null)]
    [InlineData("2WJCQFGP-2WJCQFGP", null)]
    [InlineData("not-a-fiscal-number", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(" 2WJCQFGP-2WJCQFGP-66360 ", "2WJCQFGP-2WJCQFGP-66360")]
    public async Task A_printed_fiscal_number_is_accepted_only_when_well_formed(string? printed, string? expected)
    {
        var vision = new ChatReceiptVision(
            new FixedChatClientFactory(new ScriptedChatClient().Answer(AnswerWith(fiscalNumber: printed))), NoopTimer,
            NullLogger<ChatReceiptVision>.Instance);

        var result = await vision.ReadAsync(TinyImage, "image/jpeg", null, TestContext.Current.CancellationToken);

        result.Receipt!.FiscalNumber.Should().Be(expected);
    }

    static FunctionCallContent AnswerWith(string? sellerTaxId = "123456789", string? fiscalNumber = null) => new(
        "call_1", "read_receipt",
        new Dictionary<string, object?>
        {
            ["readable"] = true,
            ["unreadable_reason"] = null,
            ["seller_name"] = "Maxi",
            ["seller_tax_id"] = sellerTaxId,
            ["fiscal_number"] = fiscalNumber,
            ["issued_at"] = null,
            ["currency"] = "RSD",
            ["total"] = 100,
            ["payment_method"] = null,
            ["kind"] = "sale",
            ["lines"] = new[]
            {
                new Dictionary<string, object?> { ["name"] = "Bread", ["quantity"] = 1, ["unit_price"] = 100, ["total"] = 100 },
            },
        });

    static FunctionCallContent SlipAnswer(
        Dictionary<string, object?>? exchange,
        bool readable = true,
        string? kind = "exchange",
        string? sellerTaxId = "101234567",
        string? fiscalNumber = null,
        decimal? total = null,
        string? currency = null,
        bool withALine = false) => new(
        "call_1", "read_receipt",
        new Dictionary<string, object?>
        {
            ["readable"] = readable,
            ["unreadable_reason"] = readable ? null : "blurry",
            ["seller_name"] = "Menjačnica Dukat",
            ["seller_tax_id"] = sellerTaxId,
            ["fiscal_number"] = fiscalNumber,
            ["issued_at"] = "2026-09-28T11:42:00",
            ["currency"] = currency,
            ["total"] = total,
            ["payment_method"] = null,
            ["kind"] = kind,
            ["lines"] = withALine
                ? new object[] { new Dictionary<string, object?> { ["name"] = "Bread", ["quantity"] = 1, ["unit_price"] = 100, ["total"] = 100 } }
                : Array.Empty<object>(),
            ["exchange"] = exchange,
        });

    static Dictionary<string, object?> SlipFigures(
        decimal? givenAmount = 100m,
        string? givenCurrency = "EUR",
        decimal? receivedAmount = 11734.56m,
        string? receivedCurrency = "RSD",
        decimal? rate = 117.3456m,
        Dictionary<string, object?>? commission = null,
        string? slipNumber = "0004711/2026") => new()
    {
        ["given_amount"] = givenAmount,
        ["given_currency"] = givenCurrency,
        ["received_amount"] = receivedAmount,
        ["received_currency"] = receivedCurrency,
        ["rate"] = rate,
        ["commission"] = commission,
        ["slip_number"] = slipNumber,
    };

    static Task<ReceiptVisionResult> ReadAnswerAsync(FunctionCallContent answer) =>
        new ChatReceiptVision(new FixedChatClientFactory(new ScriptedChatClient().Answer(answer)), NoopTimer, NullLogger<ChatReceiptVision>.Instance)
            .ReadAsync(TinyImage, "image/jpeg", null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_slip_is_read_as_an_exchange_receipt_with_no_lines_and_the_dinars_received_as_its_total()
    {
        var result = await ReadAnswerAsync(SlipAnswer(SlipFigures()));

        result.Unreadable.Should().BeNull();
        result.KindUnclear.Should().BeFalse();
        result.SellerTaxIdMalformed.Should().BeFalse();
        var slip = result.Receipt!;
        slip.Source.Should().Be(ReceiptSource.Vision);
        slip.Kind.Should().Be(ReceiptKind.Exchange);
        slip.SellerName.Should().Be("Menjačnica Dukat");
        slip.SellerTaxId.Should().Be("101234567");
        slip.IssuedAt.Should().Be(new DateTimeOffset(2026, 9, 28, 11, 42, 0, TimeSpan.FromHours(2)));
        slip.Currency.Should().Be(CurrencyCode.Rsd);
        slip.Total.Should().Be(11734.56m);
        slip.Lines.Should().BeEmpty();
        result.Exchange.Should().Be(new ExtractedExchange(100m, "EUR", 11734.56m, "RSD", 117.3456m, null, null, "0004711/2026"));
    }

    [Fact]
    public async Task An_incomplete_slip_is_still_a_receipt_with_its_exchange_never_unreadable()
    {
        var result = await ReadAnswerAsync(SlipAnswer(SlipFigures(receivedAmount: null)));

        result.Unreadable.Should().BeNull("an unread amount on a slip is asked for by a reply, not reported as an unreadable photo");
        result.Receipt!.Kind.Should().Be(ReceiptKind.Exchange);
        result.Receipt.Total.Should().Be(0m);
        result.Exchange.Should().Be(new ExtractedExchange(100m, "EUR", null, "RSD", 117.3456m, null, null, "0004711/2026"));
    }

    public static TheoryData<string?, decimal?, string?, decimal?, decimal> DinarSides => new()
    {
        { "EUR", 100m, "RSD", 11734.56m, 11734.56m },
        { "RSD", 11850m, "EUR", 100m, 11850m },
        { "EUR", 100m, "RSD", null, 0m },
        { "RSD", null, "EUR", 100m, 0m },
        { null, 100m, null, 11734.56m, 0m },
    };

    [Theory]
    [MemberData(nameof(DinarSides))]
    public async Task A_slips_total_is_its_dinar_side_or_zero_when_that_side_is_unread(
        string? givenCurrency, decimal? givenAmount, string? receivedCurrency, decimal? receivedAmount, decimal expectedTotal)
    {
        var result = await ReadAnswerAsync(SlipAnswer(SlipFigures(
            givenAmount: givenAmount, givenCurrency: givenCurrency, receivedAmount: receivedAmount, receivedCurrency: receivedCurrency)));

        result.Receipt!.Total.Should().Be(expectedTotal);
        result.Receipt.Currency.Should().Be(CurrencyCode.Rsd);
    }

    [Fact]
    public async Task Readable_false_on_a_slip_is_still_unreadable_and_carries_no_exchange()
    {
        var result = await ReadAnswerAsync(SlipAnswer(SlipFigures(), readable: false));

        result.Receipt.Should().BeNull();
        result.Exchange.Should().BeNull();
        result.Unreadable.Should().Be(ReceiptUnreadableReason.Blurry);
    }

    [Fact]
    public async Task An_exchange_kind_without_its_figures_is_a_slip_with_every_figure_unread()
    {
        var result = await ReadAnswerAsync(SlipAnswer(exchange: null));

        result.Unreadable.Should().BeNull();
        result.Receipt!.Kind.Should().Be(ReceiptKind.Exchange);
        result.Receipt.Total.Should().Be(0m);
        result.Exchange.Should().Be(new ExtractedExchange(null, null, null, null, null, null, null, null));
    }

    [Fact]
    public async Task A_slips_malformed_tax_id_is_dropped_and_reported_as_for_a_receipt()
    {
        var result = await ReadAnswerAsync(SlipAnswer(SlipFigures(), sellerTaxId: "PIB 10123"));

        result.Receipt!.SellerTaxId.Should().BeNull();
        result.SellerTaxIdMalformed.Should().BeTrue();
        result.Exchange.Should().NotBeNull();
    }

    [Fact]
    public async Task A_slip_number_is_kept_as_printed_and_a_fiscal_number_on_a_slip_is_never_taken()
    {
        var result = await ReadAnswerAsync(SlipAnswer(
            SlipFigures(slipNumber: " MB-0004711/26 "), fiscalNumber: "2WJCQFGP-2WJCQFGP-66360"));

        result.Exchange!.SlipNumber.Should().Be("MB-0004711/26");
        result.Receipt!.FiscalNumber.Should().BeNull("a slip's number is not a fiscal number and must never reach the fiscal duplicate index");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_slip_number_is_unread(string printed)
    {
        var result = await ReadAnswerAsync(SlipAnswer(SlipFigures(slipNumber: printed)));

        result.Exchange!.SlipNumber.Should().BeNull();
    }

    [Theory]
    [InlineData("EUR", "EUR")]
    [InlineData(" eur ", "EUR")]
    [InlineData("CHF", "CHF")]
    [InlineData("DIN", "RSD")]
    [InlineData(" din ", "RSD")]
    [InlineData("ДИН", "RSD")]
    [InlineData("дин", "RSD")]
    [InlineData("dinar", null)]
    [InlineData("RS", null)]
    [InlineData("€", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task A_printed_currency_is_kept_only_as_a_three_letter_code(string? printed, string? expected)
    {
        var result = await ReadAnswerAsync(SlipAnswer(SlipFigures(givenCurrency: printed)));

        result.Exchange!.GivenCurrency.Should().Be(expected);
    }

    [Fact]
    public async Task A_printed_commission_is_read_with_its_currency()
    {
        var commission = new Dictionary<string, object?> { ["amount"] = 35.13m, ["currency"] = "rsd" };

        var result = await ReadAnswerAsync(SlipAnswer(SlipFigures(receivedAmount: 11699.43m, commission: commission)));

        result.Exchange!.CommissionAmount.Should().Be(35.13m);
        result.Exchange.CommissionCurrency.Should().Be("RSD");
        result.Receipt!.Total.Should().Be(11699.43m);
    }

    [Theory]
    [InlineData("sale", ReceiptKind.Sale, false)]
    [InlineData("refund", ReceiptKind.Refund, false)]
    [InlineData(null, ReceiptKind.Sale, true)]
    public async Task Exchange_figures_are_ignored_unless_the_kind_is_exchange(string? kind, ReceiptKind expectedKind, bool expectedUnclear)
    {
        var result = await ReadAnswerAsync(SlipAnswer(SlipFigures(), kind: kind, total: 100m, currency: "RSD", withALine: true));

        result.Exchange.Should().BeNull();
        result.Receipt!.Kind.Should().Be(expectedKind);
        result.KindUnclear.Should().Be(expectedUnclear);
    }

    sealed class FixedChatClientFactory(IChatClient client) : IChatClientFactory
    {
        public Task<IChatClient> CreateAsync(CancellationToken cancellationToken) => Task.FromResult(client);
    }
}
