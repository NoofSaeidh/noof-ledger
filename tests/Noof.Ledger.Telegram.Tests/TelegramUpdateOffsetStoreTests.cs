using AwesomeAssertions;
using NSubstitute;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Telegram;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramUpdateOffsetStoreTests
{
    [Fact]
    public async Task GetAsync_returns_null_when_nothing_has_been_persisted_yet()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(TelegramUpdateOffsetStore.Key, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null));
        var store = new TelegramUpdateOffsetStore(secretStore);

        var offset = await store.GetAsync(TestContext.Current.CancellationToken);

        offset.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_parses_a_previously_saved_offset()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(TelegramUpdateOffsetStore.Key, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, "482913"));
        var store = new TelegramUpdateOffsetStore(secretStore);

        var offset = await store.GetAsync(TestContext.Current.CancellationToken);

        offset.Should().Be(482913);
    }

    [Fact]
    public async Task GetAsync_treats_an_unreadable_record_the_same_as_missing()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(TelegramUpdateOffsetStore.Key, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Unreadable, null));
        var store = new TelegramUpdateOffsetStore(secretStore);

        var offset = await store.GetAsync(TestContext.Current.CancellationToken);

        offset.Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_persists_the_offset_as_a_string()
    {
        var secretStore = Substitute.For<ISecretStore>();
        var store = new TelegramUpdateOffsetStore(secretStore);

        await store.SetAsync(482914, TestContext.Current.CancellationToken);

        await secretStore.Received(1).SetAsync(TelegramUpdateOffsetStore.Key, "482914", TestContext.Current.CancellationToken);
    }
}
