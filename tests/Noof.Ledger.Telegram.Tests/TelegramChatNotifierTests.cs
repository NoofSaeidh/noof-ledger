using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.TestKit;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramChatNotifierTests
{
    static readonly string[] CancelEditLabels = ["Cancel", "Edit"];
    static readonly string[] CancelEditCallbackData = ["cancel", "edit"];

    static TelegramChatNotifier Notifier(ITelegramBotClient? client) =>
        new(new TelegramClientHandle { Current = client }, new OperationTimer(TimeProvider.System, new SlowOperationOptions()),
            NullLogger<TelegramChatNotifier>.Instance);

    static (TelegramChatNotifier Notifier, FakeTimeProvider Clock, CapturingLogger<TelegramChatNotifier> Logger) TimedNotifier(
        ITelegramBotClient client)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logger = new CapturingLogger<TelegramChatNotifier>();
        var notifier = new TelegramChatNotifier(
            new TelegramClientHandle { Current = client }, new OperationTimer(clock, new SlowOperationOptions()), logger);
        return (notifier, clock, logger);
    }

    [Fact]
    public async Task SendAsync_sends_the_text_and_returns_the_new_message_id()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Message { Id = 555 });
        var notifier = Notifier(client);

        var messageId = await notifier.SendAsync(42L, "Saved.", TestContext.Current.CancellationToken);

        messageId.Should().Be(555);
        await client.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.ChatId.Identifier == 42L && r.Text == "Saved."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_logs_telegram_sendMessage_exactly_once()
    {
        var client = Substitute.For<ITelegramBotClient>();
        var (notifier, clock, logger) = TimedNotifier(client);
        client.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => { clock.Advance(TimeSpan.FromMilliseconds(10)); return new Message { Id = 555 }; });

        await notifier.SendAsync(42L, "Saved.", TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(entry => (string)entry.Properties["Operation"] == "telegram.sendMessage");
    }

    [Fact]
    public async Task EditAsync_logs_telegram_editMessage_exactly_once()
    {
        var client = Substitute.For<ITelegramBotClient>();
        var (notifier, clock, logger) = TimedNotifier(client);
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => { clock.Advance(TimeSpan.FromMilliseconds(10)); return new Message { Id = 555 }; });

        await notifier.EditAsync(42L, 555, new EchoMessage("Recorded", []), TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(entry => (string)entry.Properties["Operation"] == "telegram.editMessage");
    }

    [Fact]
    public async Task AskAsync_logs_telegram_askReply_exactly_once()
    {
        var client = Substitute.For<ITelegramBotClient>();
        var (notifier, clock, logger) = TimedNotifier(client);
        client.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => { clock.Advance(TimeSpan.FromMilliseconds(10)); return new Message { Id = 77 }; });

        await notifier.AskAsync(42L, 555, "What should I fix?", TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(entry => (string)entry.Properties["Operation"] == "telegram.askReply");
    }

    [Fact]
    public async Task AnswerActionAsync_logs_telegram_answerCallback_exactly_once()
    {
        var client = Substitute.For<ITelegramBotClient>();
        var (notifier, clock, logger) = TimedNotifier(client);
        client.SendRequest(Arg.Any<AnswerCallbackQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => { clock.Advance(TimeSpan.FromMilliseconds(10)); return true; });

        await notifier.AnswerActionAsync("cb-1", TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(entry => (string)entry.Properties["Operation"] == "telegram.answerCallback");
    }

    [Fact]
    public async Task EditAsync_edits_the_message_and_attaches_one_row_of_record_buttons()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>()).Returns(new Message { Id = 555 });
        var notifier = Notifier(client);

        await notifier.EditAsync(42L, 555, new EchoMessage("Recorded", [RecordAction.Cancel, RecordAction.Edit]),
            TestContext.Current.CancellationToken);

        await client.Received(1).SendRequest(
            Arg.Is<EditMessageTextRequest>(r => r.ChatId.Identifier == 42L && r.MessageId == 555 && r.Text == "Recorded"
                && r.ReplyMarkup!.InlineKeyboard.Single().Select(b => b.Text).SequenceEqual(CancelEditLabels)
                && r.ReplyMarkup.InlineKeyboard.Single().Select(b => b.CallbackData).SequenceEqual(CancelEditCallbackData)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditAsync_with_no_actions_sends_no_keyboard()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>()).Returns(new Message { Id = 555 });
        var notifier = Notifier(client);

        await notifier.EditAsync(42L, 555, new EchoMessage("Correcting…", []), TestContext.Current.CancellationToken);

        await client.Received(1).SendRequest(Arg.Is<EditMessageTextRequest>(r => r.ReplyMarkup == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditAsync_ignores_telegrams_message_is_not_modified_refusal()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException(
                "Bad Request: message is not modified: specified new message content and reply markup are exactly the same as a current content and reply markup of the message", 400));
        var notifier = Notifier(client);

        var act = () => notifier.EditAsync(42L, 555, new EchoMessage("Cancelled", [RecordAction.Restore]), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("a second tap produced the text the message already shows");
    }

    [Fact]
    public async Task EditAsync_still_throws_any_other_refusal()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: message to edit not found", 400));
        var notifier = Notifier(client);

        var act = () => notifier.EditAsync(42L, 555, new EchoMessage("x", []), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ApiRequestException>();
    }

    [Fact]
    public async Task SendAsync_throws_when_no_client_is_ready_yet()
    {
        var notifier = Notifier(null);

        var act = () => notifier.SendAsync(42L, "hi", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AnswerActionAsync_answers_the_callback_query()
    {
        var client = Substitute.For<ITelegramBotClient>();
        var notifier = Notifier(client);

        await notifier.AnswerActionAsync("cb-1", TestContext.Current.CancellationToken);

        await client.Received(1).SendRequest(Arg.Is<AnswerCallbackQueryRequest>(r => r.CallbackQueryId == "cb-1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnswerActionAsync_ignores_a_refusal_because_the_answer_is_cosmetic()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<AnswerCallbackQueryRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: query is too old and response timeout expired or query ID is invalid", 400));
        var notifier = Notifier(client);

        var act = () => notifier.AnswerActionAsync("cb-1", TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("a press handled after downtime must still cancel; only the spinner is lost");
    }

    [Fact]
    public async Task AskAsync_sends_a_force_reply_prompt_quoting_the_echo()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new Message { Id = 77 });
        var notifier = Notifier(client);

        var promptId = await notifier.AskAsync(42L, 555, "What should I fix?", TestContext.Current.CancellationToken);

        promptId.Should().Be(77);
        await client.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text == "What should I fix?" && r.ReplyParameters!.MessageId == 555
                && r.ReplyMarkup is ForceReplyMarkup),
            Arg.Any<CancellationToken>());
    }
}
