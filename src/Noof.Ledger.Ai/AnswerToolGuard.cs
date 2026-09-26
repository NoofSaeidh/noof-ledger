using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Ai;

// Sits below FunctionInvokingChatClient. Once a tool result is in the history the model has had its
// lookup, and the only thing left is the answer: the answer tool is all it is offered, and it is
// forced. FunctionInvokingChatClient cannot be told this - it resets a required ToolMode after the
// first iteration and strips every function declaration from its last one - so the rule lives here,
// where the request is about to leave for the provider.
internal sealed class AnswerToolGuard(IChatClient innerClient, AIFunctionDeclaration answerTool, IOperationTimer timer, ILogger logger)
    : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var timing = timer.Start(logger, TimedOperations.ModelRequest);
        List<ChatMessage> history = [.. messages];
        return await base.GetResponseAsync(history, HoldsToolResult(history) ? ForceAnswer(options) : options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"{nameof(AnswerToolGuard)} does not support streaming: a streamed response has no single " +
            "point to inspect before it reaches the caller, so the forced-answer rule above could not run.");

    static bool HoldsToolResult(List<ChatMessage> history) =>
        history.Exists(message => message.Contents.Any(content => content is FunctionResultContent));

    ChatOptions ForceAnswer(ChatOptions? options)
    {
        var forced = options?.Clone() ?? new ChatOptions();
        forced.Tools = [answerTool];
        forced.ToolMode = ChatToolMode.RequireSpecific(answerTool.Name);
        return forced;
    }
}
