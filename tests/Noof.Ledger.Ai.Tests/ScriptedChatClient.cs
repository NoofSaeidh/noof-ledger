using Microsoft.Extensions.AI;

namespace Noof.Ledger.Ai.Tests;

// Stands in for a provider below the layer under test: answers strictly from a queue and records
// every request it was given. Like StubHttpMessageHandler, an unqueued request throws rather than
// inventing an answer, so one call too many fails the test instead of passing it quietly.
public sealed class ScriptedChatClient : IChatClient
{
    readonly Queue<Func<ChatResponse>> answers = new();

    public List<RecordedChatCall> Requests { get; } = [];

    public ScriptedChatClient Answer(params AIContent[] contents)
    {
        answers.Enqueue(() => new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
        return this;
    }

    public ScriptedChatClient Fail(Exception exception)
    {
        answers.Enqueue(() => throw exception);
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(new RecordedChatCall([.. messages], options));

        if (answers.Count == 0)
            throw new InvalidOperationException(
                $"ScriptedChatClient received request #{Requests.Count} with no answer queued.");

        return Task.FromResult(answers.Dequeue()());
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Nothing in this assembly streams.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

public sealed record RecordedChatCall(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);
