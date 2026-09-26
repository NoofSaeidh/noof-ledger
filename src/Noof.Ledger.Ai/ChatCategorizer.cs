using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Ai;

// Everything about categorisation that does not depend on who answers it: the prompt, the schemas,
// the list_merchants round trip, reading the answer back. The provider is whatever IChatClient the
// factory hands out, and every failure it can have arrives here already as a ModelCallException.
internal sealed class ChatCategorizer(IChatClientFactory clientFactory, IOperationTimer timer, ILogger<ChatCategorizer> logger) : ICategorizer
{
    const string RecordTransactionName = "record_transaction";
    const string RecordTransactionDescription =
        "Record the spending, income or balance statement described in the message.";

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
        using var timing = timer.Start(logger, TimedOperations.ModelCategorize);

        var recordTransaction = new SchemaTool(
            RecordTransactionName, RecordTransactionDescription,
            CategorizationSchema.BuildRecordTransaction(request.Categories, request.MerchantHints, request.Wallets ?? []));

        // AllMerchants, not MerchantHints: the hints are the handful the local scan already found and
        // the model has already seen. It only calls this tool when none of them fit, so answering with
        // the same short list would make the tool pointless (Locked design decision 9).
        var listMerchants = new SchemaFunction(
            ListMerchantsName, ListMerchantsDescription,
            CategorizationSchema.BuildListMerchants(), () => JsonSerializer.Serialize(request.AllMerchants));

        // record_transaction is declaration-only, so a call to it ends the loop and comes back here;
        // list_merchants is answered locally and sent back. AnswerToolGuard makes that follow-up offer
        // record_transaction alone and force it, so there is structurally no second lookup to reach for
        // (Locked design decision 3) - enforced by the API, not by the model behaving.
        //
        // 1, not 2: the limit counts round trips, and once it is reached FunctionInvokingChatClient
        // still sends one last request with every declaration stripped - which the guard re-arms.
        // So 1 means at most two provider calls; 2 lets a second lookup through and makes three.
        using var chat = new FunctionInvokingChatClient(
            new AnswerToolGuard(await clientFactory.CreateAsync(cancellationToken), recordTransaction, timer, logger))
        {
            MaximumIterationsPerRequest = 1,
        };

        // Both tools are offered and one is forced (RequireAny, not Auto): the model answers either
        // record_transaction directly or list_merchants first, never plain text (operator, 2026-09-23).
        // System is the same across turns; per-request data lives only in the user turn.
        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, CategorizationPrompt.BuildUserTurn(request))],
            new ChatOptions
            {
                Instructions = CategorizationPrompt.System,
                Tools = [listMerchants, recordTransaction],
                ToolMode = ChatToolMode.RequireAny,
            },
            cancellationToken);

        return FindCall(response, RecordTransactionName) is { } call
            ? ToProposal(call)
            : throw new ModelCallException(
                ModelFailureKind.Transient,
                $"The model answered without a {RecordTransactionName} call (finish reason: {response.FinishReason}).");
    }

    public async Task<string> CanonicalizeMerchantAsync(
        string merchantText, IReadOnlyList<MerchantOption> knownMerchants, CancellationToken cancellationToken)
    {
        using var timing = timer.Start(logger, TimedOperations.ModelCanonicalize);

        using var chat = await clientFactory.CreateAsync(cancellationToken);

        var instructions =
            $"""
            Decide the display name to store for a merchant mentioned in a spending message. The
            message named: "{merchantText}". If it is clearly the same merchant as one already
            known, answer with THAT existing display name exactly, character for character.
            Otherwise answer with a short, tidy display name for the new merchant.

            Known merchants (id and display name, as JSON): {JsonSerializer.Serialize(knownMerchants)}
            """;

        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, merchantText)],
            new ChatOptions
            {
                MaxOutputTokens = 256,
                Instructions = instructions,
                Tools = [new SchemaTool(CanonicalizeMerchantName, CanonicalizeMerchantDescription, CanonicalizeMerchantSchema)],
                ToolMode = ChatToolMode.RequireSpecific(CanonicalizeMerchantName),
            },
            cancellationToken);

        if (FindCall(response, CanonicalizeMerchantName) is not { } call)
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

    static FunctionCallContent? FindCall(ChatResponse response, string name) =>
        response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(call => call.Name == name);

    static CategorizationProposal ToProposal(FunctionCallContent call)
    {
        var payload = ToPayload<RecordTransactionPayload>(call);
        if (payload is null)
            throw new ModelCallException(ModelFailureKind.Transient, $"{RecordTransactionName} returned an empty payload.");

        return new CategorizationProposal(
            [.. payload.Items.Select(i => i.ToProposedLineItem())],
            payload.OccurredOn,
            payload.Kind,
            string.IsNullOrEmpty(payload.WalletId) ? null : Guid.Parse(payload.WalletId),
            payload.BalanceAmount,
            payload.BalanceCurrency);
    }

    // FunctionCallContent.Arguments holds one JsonElement per top-level parameter, produced by
    // System.Text.Json with no object converter - the response's own token text, not a re-encoded
    // value (decompiled, operator 2026-09-23; Verified facts table). Re-serialising the dictionary
    // and deserialising it into the payload record in one step is what keeps "amount" and
    // "balance_amount" decimals read straight off that token text, never a double.
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

    // Deserialisation-only DTOs, private to this file: the JSON field names the tool schema
    // promises ("currency", not "currency_code"; "wallet_id", not "walletId") do not all match the
    // application-layer records' own property names, so this maps explicitly, field by field,
    // rather than trusting a naming-policy convention that is wrong for more than one field.
    sealed record RecordTransactionPayload(
        [property: JsonPropertyName("items")] IReadOnlyList<ProposedLineItemDto> Items,
        [property: JsonPropertyName("occurred_on")] string? OccurredOn,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("wallet_id")] string? WalletId,
        [property: JsonPropertyName("balance_amount")] decimal? BalanceAmount,
        [property: JsonPropertyName("balance_currency")] string? BalanceCurrency);

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
