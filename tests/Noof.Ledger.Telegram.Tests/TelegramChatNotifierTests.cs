using AwesomeAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Chat;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramChatNotifierTests
{
    static readonly string[] CancelEditLabels = ["Отменить", "Изменить"];
    static readonly string[] CancelEditCallbackData = ["cancel", "edit"];

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
    public async Task EditAsync_edits_the_message_and_attaches_one_row_of_record_buttons()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>()).Returns(new Message { Id = 555 });
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        await notifier.EditAsync(42L, 555, new EchoMessage("Записал", [RecordAction.Cancel, RecordAction.Edit]),
            TestContext.Current.CancellationToken);

        await client.Received(1).SendRequest(
            Arg.Is<EditMessageTextRequest>(r => r.ChatId.Identifier == 42L && r.MessageId == 555 && r.Text == "Записал"
                && r.ReplyMarkup!.InlineKeyboard.Single().Select(b => b.Text).SequenceEqual(CancelEditLabels)
                && r.ReplyMarkup.InlineKeyboard.Single().Select(b => b.CallbackData).SequenceEqual(CancelEditCallbackData)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditAsync_with_no_actions_sends_no_keyboard()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>()).Returns(new Message { Id = 555 });
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        await notifier.EditAsync(42L, 555, new EchoMessage("Исправляю…", []), TestContext.Current.CancellationToken);

        await client.Received(1).SendRequest(Arg.Is<EditMessageTextRequest>(r => r.ReplyMarkup == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditAsync_ignores_telegrams_message_is_not_modified_refusal()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException(
                "Bad Request: message is not modified: specified new message content and reply markup are exactly the same as a current content and reply markup of the message", 400));
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        var act = () => notifier.EditAsync(42L, 555, new EchoMessage("Отменено", [RecordAction.Restore]), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("a second tap produced the text the message already shows");
    }

    [Fact]
    public async Task EditAsync_still_throws_any_other_refusal()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: message to edit not found", 400));
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        var act = () => notifier.EditAsync(42L, 555, new EchoMessage("x", []), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ApiRequestException>();
    }

    [Fact]
    public async Task SendAsync_throws_when_no_client_is_ready_yet()
    {
        var notifier = new TelegramChatNotifier(new TelegramClientHandle());

        var act = () => notifier.SendAsync(42L, "hi", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
