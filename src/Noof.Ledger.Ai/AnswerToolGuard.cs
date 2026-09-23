using Microsoft.Extensions.AI;

namespace Noof.Ledger.Ai;

// Sits below FunctionInvokingChatClient. Once a tool result is in the history the model has had its
// lookup, and the only thing left is the answer: the answer tool is all it is offered, and it is
// forced. FunctionInvokingChatClient cannot be told this - it resets a required ToolMode after the
// first iteration and strips every function declaration from its last one - so the rule lives here,
// where the request is about to leave for the provider.
internal sealed class AnswerToolGuard(IChatClient innerClient, AIFunctionDeclaration answerTool) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> history = [.. messages];
        return base.GetResponseAsync(history, HoldsToolResult(history) ? ForceAnswer(options) : options, cancellationToken);
    }

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
