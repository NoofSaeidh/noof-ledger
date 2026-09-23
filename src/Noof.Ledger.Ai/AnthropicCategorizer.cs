using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai;

internal sealed class AnthropicCategorizer(IAnthropicClientFactory clientFactory, AnthropicOptions options) : ICategorizer
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
        var userTurn = CategorizationPrompt.BuildUserTurn(request);
        var recordSpendingSchema = CategorizationSchema.BuildRecordSpending(request.Categories, request.MerchantHints);
        var recordSpendingTool = new RawSchemaFunctionDeclaration(RecordSpendingName, RecordSpendingDescription, recordSpendingSchema);
        var listMerchantsTool = new RawSchemaFunctionDeclaration(
            ListMerchantsName, ListMerchantsDescription, CategorizationSchema.BuildListMerchants());

        var messages = new List<ChatMessage> { new(ChatRole.User, userTurn) };

        // Both tools are offered and one is forced (RequireAny, not Auto): the model answers either
        // record_spending directly or list_merchants first, never plain text (operator, 2026-09-23).
        var firstOptions = new ChatOptions
        {
            MaxOutputTokens = options.MaxTokens,
            Instructions = CategorizationPrompt.System,
            Tools = [listMerchantsTool, recordSpendingTool],
            ToolMode = ChatToolMode.RequireAny,
        };

        var firstResponse = await CallAsync(chat, messages, firstOptions, cancellationToken);

        if (TryGetFunctionCall(firstResponse, RecordSpendingName, out var firstCall))
            return ToProposal(firstCall);

        if (!TryGetFunctionCall(firstResponse, ListMerchantsName, out var call))
        {
            throw new ModelCallException(
                ModelFailureKind.Transient,
                $"First turn produced neither {RecordSpendingName} nor {ListMerchantsName} (finish reason: {firstResponse.FinishReason}).");
        }

        // AllMerchants, not MerchantHints: the hints are the handful the local scan already found
        // and the model has already seen. It only calls this tool when none of them fit, so
        // answering with the same short list would make the tool pointless - which is exactly why
        // the contract carries AllMerchants as a second, prompt-free field (Locked design decision 9).
        messages.AddRange(firstResponse.Messages);
        messages.Add(new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent(call.CallId, JsonSerializer.Serialize(request.AllMerchants))]));

        // The loop runs exactly once: only record_spending is offered on the follow-up, and it is
        // forced with RequireSpecific, so there is structurally no tool left for a third request to
        // reach for (Locked design decision 3) - enforced by the API, not by the model behaving.
        var followUpOptions = new ChatOptions
        {
            MaxOutputTokens = options.MaxTokens,
            Instructions = CategorizationPrompt.System,
            Tools = [recordSpendingTool],
            ToolMode = ChatToolMode.RequireSpecific(RecordSpendingName),
        };

        var followUpResponse = await CallAsync(chat, messages, followUpOptions, cancellationToken);

        if (TryGetFunctionCall(followUpResponse, RecordSpendingName, out var followUpCall))
            return ToProposal(followUpCall);

        throw new ModelCallException(
            ModelFailureKind.Transient,
            $"Second turn, after answering {ListMerchantsName}, still produced no {RecordSpendingName} call (finish reason: {followUpResponse.FinishReason}).");
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

        var canonicalizeMerchantTool = new RawSchemaFunctionDeclaration(
            CanonicalizeMerchantName, CanonicalizeMerchantDescription, CanonicalizeMerchantSchema);

        var callOptions = new ChatOptions
        {
            MaxOutputTokens = 256,
            Instructions = instructions,
            Tools = [canonicalizeMerchantTool],
            ToolMode = ChatToolMode.RequireSpecific(CanonicalizeMerchantName),
        };

        var response = await CallAsync(chat, [new ChatMessage(ChatRole.User, merchantText)], callOptions, cancellationToken);

        if (!TryGetFunctionCall(response, CanonicalizeMerchantName, out var call))
        {
            throw new ModelCallException(
                ModelFailureKind.Transient,
                $"{CanonicalizeMerchantName} produced no tool call (finish reason: {response.FinishReason}).");
        }

        var payload = ToPayload<CanonicalizeMerchantPayload>(call);
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

    static CategorizationProposal ToProposal(FunctionCallContent call)
    {
        var payload = ToPayload<RecordSpendingPayload>(call);
        if (payload is null)
            throw new ModelCallException(ModelFailureKind.Transient, $"{RecordSpendingName} returned an empty payload.");

        return new CategorizationProposal([.. payload.Items.Select(i => i.ToProposedLineItem())], payload.OccurredOn);
    }

    // FunctionCallContent.Arguments holds one JsonElement per top-level parameter, produced by
    // System.Text.Json with no object converter - the response's own token text, not a re-encoded
    // value (decompiled, operator 2026-09-23; Verified facts table). Re-serialising the dictionary
    // and deserialising it into the payload record in one step is what keeps "amount" a decimal
    // read straight off that token text, never a double.
    static T? ToPayload<T>(FunctionCallContent call) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToElement(call.Arguments));

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
    // handled by hand in ProposeAsync's tool loop instead. strict defaults to true because every
    // tool this categorizer declares already has "additionalProperties": false and every property
    // in "required" (Global Constraints). The Anthropic adapter copies
    // AdditionalProperties["Strict"] onto the wire Tool.strict - decompiled, not read from docs
    // (Verified facts table).
    sealed class RawSchemaFunctionDeclaration : AIFunctionDeclaration
    {
        public RawSchemaFunctionDeclaration(string name, string description, JsonElement schema, bool strict = true)
        {
            Name = name;
            Description = description;
            JsonSchema = schema;
            if (strict)
                AdditionalProperties = new AdditionalPropertiesDictionary { ["Strict"] = true };
        }

        public override string Name { get; }
        public override string Description { get; }
        public override JsonElement JsonSchema { get; }
        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } = new AdditionalPropertiesDictionary();
    }

    // Deserialisation-only DTOs, private to this file: the JSON field names the tool schema
    // promises ("currency", not "currency_code") do not all match ProposedLineItem's C# property
    // names, so this maps explicitly, field by field, rather than trusting a naming-policy
    // convention that is wrong for exactly one field.
    sealed record RecordSpendingPayload(
        [property: JsonPropertyName("items")] IReadOnlyList<ProposedLineItemDto> Items,
        [property: JsonPropertyName("occurred_on")] string? OccurredOn);

    sealed record ProposedLineItemDto(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("category_slug")] string CategorySlug,
        [property: JsonPropertyName("known_merchant_id")] string? KnownMerchantId,
        [property: JsonPropertyName("merchant_name")] string? MerchantName)
    {
        public ProposedLineItem ToProposedLineItem() => new(
            Description, Amount, Currency, CategorySlug,
            string.IsNullOrEmpty(KnownMerchantId) ? null : Guid.Parse(KnownMerchantId), MerchantName);
    }

    sealed record CanonicalizeMerchantPayload([property: JsonPropertyName("display_name")] string DisplayName);
}
