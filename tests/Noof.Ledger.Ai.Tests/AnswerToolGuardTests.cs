using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace Noof.Ledger.Ai.Tests;

public class AnswerToolGuardTests
{
    static readonly JsonElement NoArguments = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"properties":{},"required":[]}""").RootElement.Clone();

    static readonly SchemaTool Answer = new("record_transaction", "The answer.", NoArguments);
    static readonly SchemaFunction Lookup = new("list_merchants", "A lookup.", NoArguments, () => "[]");

    static readonly ChatMessage UserTurn = new(ChatRole.User, "кофе 250");

    static readonly ChatMessage[] FollowUpHistory =
    [
        UserTurn,
        new(ChatRole.Assistant, [new FunctionCallContent("call_1", "list_merchants")]),
        new(ChatRole.Tool, [new FunctionResultContent("call_1", "[]")]),
    ];

    [Fact]
    public async Task A_first_request_reaches_the_provider_exactly_as_asked()
    {
        var inner = new ScriptedChatClient().Answer(new TextContent("ok"));
        var options = new ChatOptions { Tools = [Lookup, Answer], ToolMode = ChatToolMode.RequireAny };

        await new AnswerToolGuard(inner, Answer).GetResponseAsync([UserTurn], options, TestContext.Current.CancellationToken);

        inner.Requests.Should().ContainSingle().Which.Options.Should().BeSameAs(options);
    }

    [Fact]
    public async Task Once_a_tool_result_is_in_the_history_only_the_answer_tool_is_offered_and_it_is_forced()
    {
        var inner = new ScriptedChatClient().Answer(new TextContent("ok"));
        // What FunctionInvokingChatClient sends on its second iteration: both tools, and ToolMode
        // already reset to null because it was a RequiredChatToolMode.
        var options = new ChatOptions { Tools = [Lookup, Answer] };

        await new AnswerToolGuard(inner, Answer).GetResponseAsync(FollowUpHistory, options, TestContext.Current.CancellationToken);

        var sent = inner.Requests.Should().ContainSingle().Subject.Options!;
        sent.Tools.Should().ContainSingle().Which.Should().BeSameAs(Answer);
        sent.ToolMode.Should().BeOfType<RequiredChatToolMode>().Which.RequiredFunctionName.Should().Be("record_transaction");
    }

    [Fact]
    public async Task The_answer_tool_comes_back_even_when_every_tool_was_stripped_for_the_last_iteration()
    {
        var inner = new ScriptedChatClient().Answer(new TextContent("ok"));
        // FunctionInvokingChatClient's last iteration removes every function declaration and clears
        // ToolMode, so what reaches the guard has no tools at all.
        var options = new ChatOptions { Instructions = "system prompt", MaxOutputTokens = 256 };

        await new AnswerToolGuard(inner, Answer).GetResponseAsync(FollowUpHistory, options, TestContext.Current.CancellationToken);

        var sent = inner.Requests.Should().ContainSingle().Subject.Options!;
        sent.Tools.Should().ContainSingle().Which.Should().BeSameAs(Answer);
        sent.ToolMode.Should().BeOfType<RequiredChatToolMode>().Which.RequiredFunctionName.Should().Be("record_transaction");
        sent.Instructions.Should().Be("system prompt");
        sent.MaxOutputTokens.Should().Be(256);
    }

    [Fact]
    public async Task The_callers_options_are_left_as_they_were()
    {
        var inner = new ScriptedChatClient().Answer(new TextContent("ok"));
        var options = new ChatOptions { Tools = [Lookup, Answer] };

        await new AnswerToolGuard(inner, Answer).GetResponseAsync(FollowUpHistory, options, TestContext.Current.CancellationToken);

        options.Tools.Should().HaveCount(2);
        options.ToolMode.Should().BeNull();
    }

    [Fact]
    public void GetStreamingResponseAsync_is_refused_before_it_ever_reaches_the_inner_client()
    {
        var inner = Substitute.For<IChatClient>();

        var act = () => new AnswerToolGuard(inner, Answer).GetStreamingResponseAsync([UserTurn]);

        act.Should().Throw<NotSupportedException>();
        inner.DidNotReceive().GetStreamingResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }
}
