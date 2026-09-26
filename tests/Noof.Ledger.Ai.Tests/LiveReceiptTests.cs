using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Noof.Ledger.Ai.Anthropic;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using NSubstitute;

namespace Noof.Ledger.Ai.Tests;

// Spends real money against the real Anthropic API, one live test per receipt tool. Both start by
// checking LiveModelGate.TryGetApiKey and Assert.Skip when it is false, exactly as LiveModelTests
// does, so this class is silent and green in the default `dotnet test` run.
public sealed class LiveReceiptTests
{
    // A tiny, entirely synthetic 8x8 white PNG built for this test - never a real receipt. Vision
    // reads whatever it can from an all-white image (essentially nothing), which is fine: the
    // assertion is only that the strict tool round-trips end to end against the real API, not that
    // the answer is accurate.
    static readonly byte[] SyntheticReceiptPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAFUlEQVR4nGP8//8/AzbAhFV0" +
        "0EoAAFbUAw037MyjAAAAAElFTkSuQmCC");

    static AnthropicChatClientFactory CreateFactory(string apiKey) =>
        new(new FixedSecretStore(apiKey), new HttpClient(), new AnthropicOptions());

    [Fact]
    public async Task Read_receipt_round_trips_against_the_real_API_for_a_synthetic_image()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var vision = new ChatReceiptVision(CreateFactory(apiKey));

        var receipt = await vision.ReadAsync(SyntheticReceiptPng, "image/png", null, TestContext.Current.CancellationToken);

        receipt.Source.Should().Be(ReceiptSource.Vision);
        receipt.Kind.Should().BeOneOf(ReceiptKind.Sale, ReceiptKind.Refund);
    }

    [Fact]
    public async Task Categorize_receipt_round_trips_against_the_real_API_and_covers_every_ordinal()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var categoryCatalog = Substitute.For<ICategoryCatalog>();
        categoryCatalog.ActiveAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new CategoryEntry(Guid.NewGuid(), "groceries", "Groceries", "Продукты", null),
            new CategoryEntry(Guid.NewGuid(), "other", "Other", "Прочее", null),
        ]);
        var walletDirectory = Substitute.For<IWalletDirectory>();
        walletDirectory.ActiveAsync(Arg.Any<CancellationToken>()).Returns([]);

        var categorizer = new ChatReceiptCategorizer(
            CreateFactory(apiKey), categoryCatalog, walletDirectory, NullLogger<ChatReceiptCategorizer>.Instance);
        var request = new ReceiptCategorizationRequest(
            [new ReceiptLineToCategorize(1, "Mleko", 1, 120), new ReceiptLineToCategorize(2, "Hleb", 2, 180.50m)],
            "Maxi", "123456789", MerchantKnown: false, Caption: null);

        var result = await categorizer.CategorizeAsync(request, TestContext.Current.CancellationToken);

        result.Lines.Select(line => line.Ordinal).Should().BeEquivalentTo([1, 2]);
    }

    sealed class FixedSecretStore(string plaintext) : ISecretStore
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new SecretResult(SecretState.Present, plaintext));

        public Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new SecretStatus(SecretState.Present, DateTimeOffset.UnixEpoch));

        public Task SetAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake is read-only - the live suite never writes a secret.");

        public Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake is read-only - the live suite never writes a secret.");
    }
}
