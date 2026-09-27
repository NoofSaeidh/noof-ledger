using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
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

    sealed class FixedChatClientFactory(IChatClient client) : IChatClientFactory
    {
        public Task<IChatClient> CreateAsync(CancellationToken cancellationToken) => Task.FromResult(client);
    }
}
