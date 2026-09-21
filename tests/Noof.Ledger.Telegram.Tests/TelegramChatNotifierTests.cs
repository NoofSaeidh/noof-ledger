using AwesomeAssertions;
using NSubstitute;
using Noof.Ledger.Telegram;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramChatNotifierTests
{
    [Fact]
    public async Task SendAsync_sends_the_text_and_returns_the_new_message_id()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Message { Id = 555 });
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        var messageId = await notifier.SendAsync(42L, "Saved.", TestContext.Current.CancellationToken);

        messageId.Should().Be(555);
        await client.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId.Identifier == 42L && r.Text == "Saved."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditAsync_edits_the_stored_message_by_chat_and_message_id()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Message { Id = 555 });
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        await notifier.EditAsync(42L, 555, "Spent 12.34 EUR at Merc.", TestContext.Current.CancellationToken);

        await client.Received(1).SendRequest(
            Arg.Is<EditMessageTextRequest>(r => r.ChatId.Identifier == 42L && r.MessageId == 555 && r.Text == "Spent 12.34 EUR at Merc."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_throws_when_no_client_is_ready_yet()
    {
        var notifier = new TelegramChatNotifier(new TelegramClientHandle());

        var act = () => notifier.SendAsync(42L, "hi", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
