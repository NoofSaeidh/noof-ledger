using System.Net;
using System.Text.Json;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Categorization;
using AnthropicTool = Anthropic.Models.Messages.Tool;

namespace Noof.Ledger.Ai.Anthropic;

// The provider-specific edge of the chat pipeline, directly above the SDK's own IChatClient
// adapter: provider-neutral requests go out in Anthropic's terms, and whatever the SDK throws comes
// back as a ModelCallException, so nothing above this class ever catches an SDK type.
internal sealed class AnthropicTranslatingChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, Translate(options), cancellationToken);
        }
        catch (AnthropicApiException ex)
        {
            var exception = new ModelCallException(
                Classify(ex.StatusCode), $"Anthropic call failed with status {(int)ex.StatusCode}.", ex);
            throw IsAccountLevel(ex.StatusCode) ? exception.AsAccountLevel() : exception;
        }
        catch (AnthropicException ex)
        {
            // The SDK wraps a transport failure in AnthropicIOException, and an unreadable response
            // in AnthropicInvalidDataException - neither says anything about the request being wrong.
            throw new ModelCallException(ModelFailureKind.Transient, "The Anthropic call failed before an answer came back.", ex);
        }
        catch (TaskCanceledException ex)
        {
            // The SDK's own Timeout surfaces as TaskCanceledException, the same type .NET uses for
            // caller-requested cancellation, and this layer cannot reliably tell the two apart. Both
            // are Transient: a host shutting down does not retry anyway, because it is going down.
            throw new ModelCallException(ModelFailureKind.Transient, "The Anthropic call timed out or was cancelled.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelCallException(ModelFailureKind.Transient, "A network failure occurred calling Anthropic.", ex);
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"{nameof(AnthropicTranslatingChatClient)} does not support streaming: a streamed response " +
            "has no single point to translate strictness onto or normalise an SDK exception from.");

    static ChatOptions? Translate(ChatOptions? options)
    {
        if (options?.Tools is not { } tools || !tools.Any(tool => tool.IsStrict()))
            return options;

        var translated = options.Clone();
        translated.Tools = [.. tools.Select(tool =>
            tool is AIFunctionDeclaration declaration && declaration.IsStrict() ? new StrictDeclaration(declaration) : tool)];
        return translated;
    }

    static ModelFailureKind Classify(HttpStatusCode statusCode) => (int)statusCode switch
    {
        400 or 401 or 402 or 403 or 404 or 413 => ModelFailureKind.Terminal,
        _ => ModelFailureKind.Transient,
    };

    // 401/402/403 are Terminal for a reason that has nothing to do with this specific request - a
    // bad key, no credit, a revoked permission. Every other queued job would fail identically against
    // the same account, which is exactly what CategorizationWorker uses this to guard against.
    static bool IsAccountLevel(HttpStatusCode statusCode) => (int)statusCode is 401 or 402 or 403;

    // The SDK adapter copies AdditionalProperties[nameof(Tool.Strict)] onto the wire tool's "strict"
    // field and ignores keys it does not know (decompiled, and documented on AsIChatClient). A
    // wrapper rather than the tool itself: what FunctionInvokingChatClient holds stays untouched.
    sealed class StrictDeclaration(AIFunctionDeclaration tool) : AIFunctionDeclaration
    {
        public override string Name => tool.Name;

        public override string Description => tool.Description;

        public override JsonElement JsonSchema => tool.JsonSchema;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } =
            new AdditionalPropertiesDictionary { [nameof(AnthropicTool.Strict)] = true };
    }
}
