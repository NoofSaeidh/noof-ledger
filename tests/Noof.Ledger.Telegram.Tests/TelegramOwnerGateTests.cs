using AwesomeAssertions;
using NSubstitute;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramOwnerGateTests
{
    [Fact]
    public async Task The_first_chat_to_message_claims_ownership()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null));
        secretStore.TrySetIfMissingAsync(SecretKeys.TelegramOwnerChatId, "111", Arg.Any<CancellationToken>())
            .Returns(true);
        var gate = new TelegramOwnerGate(secretStore);

        var allowed = await gate.IsAllowedAsync(111L, TestContext.Current.CancellationToken);

        allowed.Should().BeTrue();
        await secretStore.Received(1).TrySetIfMissingAsync(SecretKeys.TelegramOwnerChatId, "111", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Losing_the_claim_race_to_another_chat_is_rejected_not_an_error()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null), new SecretResult(SecretState.Present, "222"));
        secretStore.TrySetIfMissingAsync(SecretKeys.TelegramOwnerChatId, "111", Arg.Any<CancellationToken>())
            .Returns(false);
        var gate = new TelegramOwnerGate(secretStore);

        var allowed = await gate.IsAllowedAsync(111L, TestContext.Current.CancellationToken);

        allowed.Should().BeFalse("chat 222 committed first; 111 lost the race and is not the owner");
    }

    [Fact]
    public async Task The_owner_chat_is_allowed_on_every_later_message()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, "111"));
        var gate = new TelegramOwnerGate(secretStore);

        var allowed = await gate.IsAllowedAsync(111L, TestContext.Current.CancellationToken);

        allowed.Should().BeTrue();
        await secretStore.DidNotReceive().TrySetIfMissingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stranger_is_rejected_once_an_owner_is_set()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, "111"));
        var gate = new TelegramOwnerGate(secretStore);

        var allowed = await gate.IsAllowedAsync(999L, TestContext.Current.CancellationToken);

        allowed.Should().BeFalse();
        await secretStore.DidNotReceive().TrySetIfMissingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unreadable_owner_record_fails_closed_instead_of_reclaiming_ownership()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Unreadable, null));
        var gate = new TelegramOwnerGate(secretStore);

        var allowed = await gate.IsAllowedAsync(111L, TestContext.Current.CancellationToken);

        allowed.Should().BeFalse();
    }
}
