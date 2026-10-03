using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics.BugReports;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.TestKit;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram.Tests;

public class BugCommandHandlerTests
{
    const long Owner = 111L;
    const long Stranger = 999L;
    const string DeliveredText = "Reply to the echo with the amount.\n\n1 finding on this record";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed record Harness(
        BugCommandHandler Handler, IBugReportStore Store, IChatNotifier ChatNotifier, IRecordEditor Editor,
        ISecretStore SecretStore, CapturingLogger<BugCommandHandler> Logger);

    static Harness Create(SecretResult? ownerSecret = null)
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(ownerSecret ?? new SecretResult(SecretState.Present, "111"));
        var editor = Substitute.For<IRecordEditor>();
        editor.FindByBotMessageAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((EchoTarget?)null);
        editor.FindByUserMessageAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((EchoTarget?)null);
        var store = Substitute.For<IBugReportStore>();
        store.FileAsync(Arg.Any<NewBugReport>(), Arg.Any<CancellationToken>()).Returns(new BugReportSaved(12, Created: true));
        var chatNotifier = Substitute.For<IChatNotifier>();
        var logger = new CapturingLogger<BugCommandHandler>();

        var handler = new BugCommandHandler(new TelegramOwnerGate(secretStore), editor, store, chatNotifier, logger);
        return new Harness(handler, store, chatNotifier, editor, secretStore, logger);
    }

    static Message BugMessage(long chatId, int messageId, int? replyTo = null) => new()
    {
        Id = messageId,
        Chat = new Chat { Id = chatId },
        Text = "/bug",
        Date = DateTime.UtcNow,
        ReplyToMessage = replyTo is { } repliedTo ? new Message { Id = repliedTo, Chat = new Chat { Id = chatId } } : null,
    };

    // The address is spelled out, not composed: it must stay what the BugReportsReplyTo migration back-filled.
    static NewBugReport Filed(string replyTo, string? text, Guid? transactionId) =>
        new(BugReportSource.Telegram, replyTo, text, transactionId, null, null);

    static CallbackQuery Press(long chatId, string data) => new() { Id = "cb-7", Data = data, From = new User { Id = chatId } };

    static Message Pressed(long chatId, string? text = DeliveredText) => new() { Id = 60, Chat = new Chat { Id = chatId }, Text = text };

    [Fact]
    public async Task The_owners_bug_is_saved_with_its_text_and_answered_with_its_number()
    {
        var harness = Create();

        await harness.Handler.HandleAsync(BugMessage(Owner, 5), "the total looks off", Ct);

        await harness.Store.Received(1).FileAsync(
            Filed("111:5", "the total looks off", null), Arg.Any<CancellationToken>());
        await harness.ChatNotifier.Received(1).ReplyToBugReportAsync("111:5", "Bug report #12 saved.", null, Arg.Any<CancellationToken>());
        var saved = harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 6202).Subject;
        saved.Level.Should().Be(LogLevel.Information);
        saved.Properties["Number"].Should().Be(12);
        saved.Properties["Linked"].Should().Be(false);
        saved.Properties["Created"].Should().Be(true);
    }

    [Fact]
    public async Task A_bug_replying_to_a_records_echo_or_edit_prompt_is_linked_to_that_record()
    {
        var harness = Create();
        var transactionId = Guid.NewGuid();
        harness.Editor.FindByBotMessageAsync(Owner, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));

        await harness.Handler.HandleAsync(BugMessage(Owner, 8, replyTo: 42), "сумма не та", Ct);

        await harness.Store.Received(1).FileAsync(
            Filed("111:8", "сумма не та", transactionId), Arg.Any<CancellationToken>());
        await harness.Editor.DidNotReceiveWithAnyArgs().FindByUserMessageAsync(default, default, Arg.Any<CancellationToken>());
        harness.Logger.Entries.Single(entry => entry.EventId.Id == 6202).Properties["Linked"].Should().Be(true);
    }

    [Fact]
    public async Task A_bug_replying_to_the_message_that_captured_a_record_is_linked_to_it()
    {
        var harness = Create();
        var transactionId = Guid.NewGuid();
        harness.Editor.FindByUserMessageAsync(Owner, 5, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));

        await harness.Handler.HandleAsync(BugMessage(Owner, 9, replyTo: 5), null, Ct);

        await harness.Store.Received(1).FileAsync(
            Filed("111:9", null, transactionId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_bug_replying_to_the_bots_own_saved_message_is_unlinked_and_queues_no_correction()
    {
        var harness = Create();

        await harness.Handler.HandleAsync(BugMessage(Owner, 14, replyTo: 13), "and another thing", Ct);

        await harness.Store.Received(1).FileAsync(
            Filed("111:14", "and another thing", null), Arg.Any<CancellationToken>());
        await harness.Editor.DidNotReceiveWithAnyArgs().RequestCorrectionAsync(default, default!, default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_redelivered_bug_is_answered_with_the_number_it_was_saved_under()
    {
        var harness = Create();
        harness.Store.FileAsync(Arg.Any<NewBugReport>(), Arg.Any<CancellationToken>()).Returns(new BugReportSaved(12, Created: false));

        await harness.Handler.HandleAsync(BugMessage(Owner, 5), "the total looks off", Ct);

        await harness.ChatNotifier.Received(1).ReplyToBugReportAsync("111:5", "Bug report #12 saved.", null, Arg.Any<CancellationToken>());
        harness.Logger.Entries.Single(entry => entry.EventId.Id == 6202).Properties["Created"].Should().Be(false);
    }

    [Fact]
    public async Task A_report_that_could_not_be_saved_is_never_announced()
    {
        var harness = Create();
        harness.Store.FileAsync(Arg.Any<NewBugReport>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database unreachable"));

        var act = () => harness.Handler.HandleAsync(BugMessage(Owner, 5), "x", Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await harness.ChatNotifier.DidNotReceiveWithAnyArgs().ReplyToBugReportAsync(default!, default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_strangers_bug_is_logged_at_debug_and_gets_nothing_else()
    {
        var harness = Create();

        await harness.Handler.HandleAsync(BugMessage(Stranger, 5, replyTo: 42), "x", Ct);

        harness.Store.ReceivedCalls().Should().BeEmpty();
        harness.ChatNotifier.ReceivedCalls().Should().BeEmpty();
        harness.Editor.ReceivedCalls().Should().BeEmpty();
        harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 6201)
            .Which.Level.Should().Be(LogLevel.Debug);
    }

    [Fact]
    public async Task A_bug_on_an_unowned_bot_never_claims_it()
    {
        var harness = Create(new SecretResult(SecretState.Missing, null));

        await harness.Handler.HandleAsync(BugMessage(Owner, 5), "x", Ct);

        await harness.SecretStore.DidNotReceiveWithAnyArgs().TrySetIfMissingAsync(default!, default!, Arg.Any<CancellationToken>());
        harness.Store.ReceivedCalls().Should().BeEmpty();
        harness.ChatNotifier.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task The_owners_close_press_closes_the_report_and_says_so_under_the_reply_without_the_button()
    {
        var harness = Create();
        harness.Store.SetStatusAsync(12, BugReportStatus.Closed, Arg.Any<CancellationToken>()).Returns(true);

        await harness.Handler.HandleCloseAsync(Press(Owner, "bug:close:12"), Pressed(Owner), Ct);

        await harness.ChatNotifier.Received(1).AnswerActionAsync("cb-7", Arg.Any<CancellationToken>());
        await harness.ChatNotifier.Received(1).EditAsync(Owner, 60,
            Arg.Is<EchoMessage>(message => message.Text == DeliveredText + "\n\nBug report #12 closed." && message.Actions.Count == 0),
            Arg.Any<CancellationToken>());
        var closed = harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 6204).Subject;
        closed.Level.Should().Be(LogLevel.Information);
        closed.Properties["Number"].Should().Be(12);
    }

    [Fact]
    public async Task A_close_press_on_a_message_with_no_text_says_only_that_the_report_is_closed()
    {
        var harness = Create();
        harness.Store.SetStatusAsync(12, BugReportStatus.Closed, Arg.Any<CancellationToken>()).Returns(true);

        await harness.Handler.HandleCloseAsync(Press(Owner, "bug:close:12"), Pressed(Owner, text: null), Ct);

        await harness.ChatNotifier.Received(1).EditAsync(Owner, 60,
            Arg.Is<EchoMessage>(message => message.Text == "Bug report #12 closed." && message.Actions.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_repeated_close_press_is_answered_and_changes_nothing()
    {
        var harness = Create();
        harness.Store.SetStatusAsync(12, BugReportStatus.Closed, Arg.Any<CancellationToken>()).Returns(false);

        await harness.Handler.HandleCloseAsync(Press(Owner, "bug:close:12"), Pressed(Owner), Ct);

        await harness.ChatNotifier.Received(1).AnswerActionAsync("cb-7", Arg.Any<CancellationToken>());
        await harness.ChatNotifier.DidNotReceiveWithAnyArgs().EditAsync(default, default, default!, Arg.Any<CancellationToken>());
        harness.Logger.Entries.Should().NotContain(entry => entry.EventId.Id == 6204);
    }

    [Theory]
    [InlineData("bug:close:abc")]
    [InlineData("bug:close:")]
    [InlineData("bug:close:-3")]
    [InlineData("bug:open:12")]
    public async Task A_malformed_press_is_answered_and_nothing_else(string data)
    {
        var harness = Create();

        await harness.Handler.HandleCloseAsync(Press(Owner, data), Pressed(Owner), Ct);

        await harness.ChatNotifier.Received(1).AnswerActionAsync("cb-7", Arg.Any<CancellationToken>());
        harness.Store.ReceivedCalls().Should().BeEmpty();
        await harness.ChatNotifier.DidNotReceiveWithAnyArgs().EditAsync(default, default, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_strangers_close_press_is_not_even_answered()
    {
        var harness = Create();

        await harness.Handler.HandleCloseAsync(Press(Stranger, "bug:close:12"), Pressed(Stranger), Ct);

        harness.ChatNotifier.ReceivedCalls().Should().BeEmpty();
        harness.Store.ReceivedCalls().Should().BeEmpty();
        harness.Logger.Entries.Should().ContainSingle(entry => entry.EventId.Id == 6203)
            .Which.Level.Should().Be(LogLevel.Debug);
    }

    [Fact]
    public async Task A_close_press_on_an_unowned_bot_never_claims_it_and_is_not_answered()
    {
        var harness = Create(new SecretResult(SecretState.Missing, null));

        await harness.Handler.HandleCloseAsync(Press(Owner, "bug:close:12"), Pressed(Owner), Ct);

        await harness.SecretStore.DidNotReceiveWithAnyArgs().TrySetIfMissingAsync(default!, default!, Arg.Any<CancellationToken>());
        harness.ChatNotifier.ReceivedCalls().Should().BeEmpty();
        harness.Store.ReceivedCalls().Should().BeEmpty();
    }
}
