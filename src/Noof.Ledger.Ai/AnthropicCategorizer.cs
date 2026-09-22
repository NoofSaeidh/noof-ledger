using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai;

public sealed class AnthropicCategorizer(IAnthropicClientFactory clientFactory, AnthropicOptions options) : ICategorizer
{
    const string RecordSpendingName = "record_spending";
    const string RecordSpendingDescription = "Record every distinct spending line item found in the message.";

    const string ListMerchantsName = "list_merchants";
    const string ListMerchantsDescription =
        "List every merchant this ledger already knows, with the id to use for each. " +
        "Call this only if the message names a merchant that is not in the known merchants given to you, " +
        "and you want to check whether it is already known under a different spelling.";

    const string CanonicalizeMerchantName = "canonicalize_merchant";
    const string CanonicalizeMerchantDescription = "The display name to store for this merchant.";

    static readonly JsonElement CanonicalizeMerchantSchema = BuildCanonicalizeMerchantSchema();

    public async Task<CategorizationProposal> ProposeAsync(CategorizationRequest request, CancellationToken cancellationToken)
    {
        var raw = await clientFactory.CreateAsync(cancellationToken);
        var chat = raw.AsIChatClient(options.Model);

        // System is the SAME across turns; per-request data - categories, hints - lives only in the
        // user turn, built by CategorizationPrompt.BuildUserTurn. Instructions lands as the request's
        // "system" per the captured HTTP body (fact 1/2 above).
        var userTurn = CategorizationPrompt.BuildUserTurn(request.RawText, request.Categories, request.MerchantHints);
        var recordSpendingSchema = CategorizationSchema.BuildRecordSpending(request.Categories, request.MerchantHints);
        var listMerchantsTool = new RawSchemaFunctionDeclaration(
            ListMerchantsName, ListMerchantsDescription, CategorizationSchema.BuildListMerchants());

        var messages = new List<ChatMessage> { new(ChatRole.User, userTurn) };

        var firstOptions = new ChatOptions
        {
            MaxOutputTokens = options.MaxTokens,
            Instructions = CategorizationPrompt.System,
            Tools = [listMerchantsTool],
            ToolMode = ChatToolMode.Auto,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(recordSpendingSchema, RecordSpendingName, RecordSpendingDescription),
        };

        var firstResponse = await CallAsync(chat, messages, firstOptions, cancellationToken);

        if (TryGetText(firstResponse, out var firstJson))
            return ToProposal(firstJson);

        if (!TryGetFunctionCall(firstResponse, ListMerchantsName, out var call))
        {
            throw new ModelCallException(
                ModelFailureKind.Transient,
                $"First turn produced neither a JSON answer nor a {ListMerchantsName} call (finish reason: {firstResponse.FinishReason}).");
        }

        // AllMerchants, not MerchantHints: the hints are the handful the local scan already found
        // and the model has already seen. It only calls this tool when none of them fit, so
        // answering with the same short list would make the tool pointless - which is exactly why
        // the contract carries AllMerchants as a second, prompt-free field (Locked design decision 9).
        messages.AddRange(firstResponse.Messages);
        messages.Add(new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent(call.CallId, JsonSerializer.Serialize(request.AllMerchants))]));

        // The loop runs exactly once: this is the ONLY follow-up request this method ever sends,
        // regardless of what it gets back. No tools are offered this time - that is what
        // structurally rules out a third request, rather than relying on the model to behave
        // (Locked design decision 3).
        var followUpOptions = new ChatOptions
        {
            MaxOutputTokens = options.MaxTokens,
            Instructions = CategorizationPrompt.System,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(recordSpendingSchema, RecordSpendingName, RecordSpendingDescription),
        };

        var followUpResponse = await CallAsync(chat, messages, followUpOptions, cancellationToken);

        if (TryGetText(followUpResponse, out var followUpJson))
            return ToProposal(followUpJson);

        throw new ModelCallException(
            ModelFailureKind.Transient,
            $"Second turn, after answering {ListMerchantsName}, still produced no JSON answer (finish reason: {followUpResponse.FinishReason}).");
    }

    public async Task<string> CanonicalizeMerchantAsync(
        string merchantText, IReadOnlyList<MerchantOption> knownMerchants, CancellationToken cancellationToken)
    {
        var raw = await clientFactory.CreateAsync(cancellationToken);
        var chat = raw.AsIChatClient(options.Model);

        var instructions =
            $"""
            Decide the display name to store for a merchant mentioned in a spending message. The
            message named: "{merchantText}". If it is clearly the same merchant as one already
            known, answer with THAT existing display name exactly, character for character.
            Otherwise answer with a short, tidy display name for the new merchant.

            Known merchants (id and display name, as JSON): {JsonSerializer.Serialize(knownMerchants)}
            """;

        var callOptions = new ChatOptions
        {
            MaxOutputTokens = 256,
            Instructions = instructions,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                CanonicalizeMerchantSchema, CanonicalizeMerchantName, CanonicalizeMerchantDescription),
        };

        var response = await CallAsync(chat, [new ChatMessage(ChatRole.User, merchantText)], callOptions, cancellationToken);

        if (!TryGetText(response, out var json))
        {
            throw new ModelCallException(
                ModelFailureKind.Transient,
                $"{CanonicalizeMerchantName} produced no JSON answer (finish reason: {response.FinishReason}).");
        }

        var payload = JsonSerializer.Deserialize<CanonicalizeMerchantPayload>(json);
        if (payload is null || string.IsNullOrWhiteSpace(payload.DisplayName))
            throw new ModelCallException(ModelFailureKind.Transient, $"{CanonicalizeMerchantName} returned an empty display_name.");

        return payload.DisplayName;
    }

    static async Task<ChatResponse> CallAsync(
        IChatClient chat, IList<ChatMessage> messages, ChatOptions callOptions, CancellationToken cancellationToken)
    {
        try
        {
            return await chat.GetResponseAsync(messages, callOptions, cancellationToken);
        }
        catch (AnthropicApiException ex)
        {
            var exception = new ModelCallException(Classify(ex.StatusCode), $"Anthropic call failed with status {(int)ex.StatusCode}.", ex);
            if (IsAccountLevel(ex.StatusCode))
                exception.AsAccountLevel();
            throw exception;
        }
        catch (TaskCanceledException ex)
        {
            // AnthropicClient's own Timeout (from AnthropicOptions) fires as a TaskCanceledException,
            // the same type .NET uses for caller-requested cancellation. This layer cannot reliably
            // tell the two apart from inside a static helper with no access to the original
            // CancellationToken, so both are treated as transient here - a caller-driven cancellation
            // (host shutdown) unwinds through ModelCallException.Transient rather than
            // OperationCanceledException. The worker does not retry on host shutdown regardless,
            // because the process is going down, so this does not change behaviour where it matters.
            throw new ModelCallException(ModelFailureKind.Transient, "The Anthropic call timed out or was cancelled.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelCallException(ModelFailureKind.Transient, "A network failure occurred calling Anthropic.", ex);
        }
    }

    static ModelFailureKind Classify(HttpStatusCode statusCode) => (int)statusCode switch
    {
        400 or 401 or 402 or 403 or 404 or 413 => ModelFailureKind.Terminal,
        _ => ModelFailureKind.Transient,
    };

    // 401/402/403 are the three statuses the locked table marks Terminal for a reason that has
    // nothing to do with this specific request - a bad key, no credit, a revoked permission. Every
    // other queued job would fail identically against the same account, which is exactly what
    // CategorizationWorker uses this to guard against.
    static bool IsAccountLevel(HttpStatusCode statusCode) => (int)statusCode is 401 or 402 or 403;

    static bool TryGetText(ChatResponse response, out string text)
    {
        var builder = new StringBuilder();
        foreach (var message in response.Messages)
        foreach (var content in message.Contents)
        {
            if (content is TextContent textContent)
                builder.Append(textContent.Text);
        }

        text = builder.ToString();
        return text.Length > 0;
    }

    static bool TryGetFunctionCall(ChatResponse response, string name, out FunctionCallContent call)
    {
        foreach (var message in response.Messages)
        foreach (var content in message.Contents)
        {
            if (content is FunctionCallContent candidate && candidate.Name == name)
            {
                call = candidate;
                return true;
            }
        }

        call = null!;
        return false;
    }

    static CategorizationProposal ToProposal(string json)
    {
        var payload = JsonSerializer.Deserialize<RecordSpendingPayload>(json);
        if (payload is null)
            throw new ModelCallException(ModelFailureKind.Transient, $"{RecordSpendingName} returned an empty payload.");

        return new CategorizationProposal([.. payload.Items.Select(i => i.ToProposedLineItem())]);
    }

    static JsonElement BuildCanonicalizeMerchantSchema()
    {
        const string raw = """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["display_name"],
              "properties": {
                "display_name": { "type": "string", "description": "The display name to store for this merchant." }
              }
            }
            """;
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    // AIFunctionDeclaration is abstract in Microsoft.Extensions.AI.Abstractions; this is the minimal
    // subclass needed to declare a tool from a raw JSON Schema instead of one generated by
    // reflection. Declaration-only: it is never asked to invoke anything - FunctionCallContent is
    // handled by hand in ProposeAsync's tool loop instead.
    sealed class RawSchemaFunctionDeclaration : AIFunctionDeclaration
    {
        public RawSchemaFunctionDeclaration(string name, string description, JsonElement schema)
        {
            Name = name;
            Description = description;
            JsonSchema = schema;
        }

        public override string Name { get; }
        public override string Description { get; }
        public override JsonElement JsonSchema { get; }
    }

    // Deserialisation-only DTOs, private to this file: the JSON field names the tool schema
    // promises ("currency", not "currency_code") do not all match ProposedLineItem's C# property
    // names, so this maps explicitly, field by field, rather than trusting a naming-policy
    // convention that is wrong for exactly one field.
    sealed record RecordSpendingPayload([property: JsonPropertyName("items")] IReadOnlyList<ProposedLineItemDto> Items);

    sealed record ProposedLineItemDto(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("amount_quote")] string AmountQuote,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("category_slug")] string CategorySlug,
        [property: JsonPropertyName("known_merchant_id")] string? KnownMerchantId,
        [property: JsonPropertyName("merchant_quote")] string? MerchantQuote)
    {
        public ProposedLineItem ToProposedLineItem() => new(
            Description, AmountQuote, Currency, CategorySlug,
            string.IsNullOrEmpty(KnownMerchantId) ? null : Guid.Parse(KnownMerchantId), MerchantQuote);
    }

    sealed record CanonicalizeMerchantPayload([property: JsonPropertyName("display_name")] string DisplayName);
}
