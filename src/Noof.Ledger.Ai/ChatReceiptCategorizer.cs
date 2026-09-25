using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Ai.ReceiptCategorizerLogging;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Application.Wallets;

namespace Noof.Ledger.Ai;

// Categorises a receipt's lines and names its merchant/wallet - never its amounts (a global
// constraint of Phase 6: those come from receipt_lines, not the model). Fetches the category and
// wallet lists itself, the same two calls CategorizationWorker makes for ChatCategorizer, because
// ReceiptCategorizationRequest (the shared contract) carries neither.
internal sealed class ChatReceiptCategorizer(
    IChatClientFactory clientFactory,
    ICategoryCatalog categoryCatalog,
    IWalletDirectory walletDirectory,
    ILogger<ChatReceiptCategorizer> logger) : IReceiptCategorizer
{
    const string CategorizeReceiptName = "categorize_receipt";
    const string CategorizeReceiptDescription =
        "Categorise a receipt's already-read line items and, if applicable, name its merchant and wallet.";

    // The catalogue's own general-purpose fallback (seeded alongside every other expense category) -
    // a receipt is never income, so this is the one fallback slug that applies here.
    const string FallbackCategorySlug = "other";

    public async Task<ReceiptCategorization> CategorizeAsync(ReceiptCategorizationRequest request, CancellationToken cancellationToken)
    {
        var categories = await categoryCatalog.ActiveAsync(cancellationToken);
        var options = categories.Select(category => new CategoryOption(category.Slug, category.NameEn, category.NameRu, category.ParentSlug)).ToList();
        var wallets = await walletDirectory.ActiveAsync(cancellationToken);

        var categorizeReceipt = new SchemaTool(
            CategorizeReceiptName, CategorizeReceiptDescription,
            ReceiptCategorizationSchema.BuildCategorizeReceipt(options, wallets));

        using var chat = await clientFactory.CreateAsync(cancellationToken);

        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, ReceiptCategorizationPrompt.BuildUserTurn(request, options, wallets))],
            new ChatOptions
            {
                Instructions = ReceiptCategorizationPrompt.System,
                Tools = [categorizeReceipt],
                ToolMode = ChatToolMode.RequireSpecific(CategorizeReceiptName),
            },
            cancellationToken);

        if (FindCall(response) is not { } call)
        {
            throw new ModelCallException(
                ModelFailureKind.Transient,
                $"{CategorizeReceiptName} produced no tool call (finish reason: {response.FinishReason}).");
        }

        var payload = ToPayload(call);
        if (payload is null)
            throw new ModelCallException(ModelFailureKind.Transient, $"{CategorizeReceiptName} returned an empty payload.");

        return ToReceiptCategorization(payload, request);
    }

    static FunctionCallContent? FindCall(ChatResponse response) =>
        response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(call => call.Name == CategorizeReceiptName);

    static CategorizeReceiptPayload? ToPayload(FunctionCallContent call) =>
        JsonSerializer.Deserialize<CategorizeReceiptPayload>(JsonSerializer.SerializeToElement(call.Arguments));

    ReceiptCategorization ToReceiptCategorization(CategorizeReceiptPayload payload, ReceiptCategorizationRequest request)
    {
        var answered = new Dictionary<int, string>();
        foreach (var line in payload.Lines)
            answered.TryAdd(line.Ordinal, line.CategorySlug);

        var lines = new List<ReceiptLineCategory>(request.Lines.Count);
        foreach (var requested in request.Lines)
        {
            if (answered.Remove(requested.Ordinal, out var slug))
            {
                lines.Add(new ReceiptLineCategory(requested.Ordinal, slug));
            }
            else
            {
                logger.MissingOrdinal(requested.Ordinal, FallbackCategorySlug);
                lines.Add(new ReceiptLineCategory(requested.Ordinal, FallbackCategorySlug));
            }
        }

        foreach (var extraOrdinal in answered.Keys)
            logger.ExtraOrdinalIgnored(extraOrdinal);

        var walletId = string.IsNullOrEmpty(payload.WalletId) ? (Guid?)null : Guid.Parse(payload.WalletId);
        return new ReceiptCategorization(lines, payload.MerchantName, walletId);
    }

    sealed record CategorizeReceiptPayload(
        [property: JsonPropertyName("lines")] IReadOnlyList<CategorizeReceiptLineDto> Lines,
        [property: JsonPropertyName("merchant_name")] string? MerchantName,
        [property: JsonPropertyName("wallet_id")] string? WalletId);

    sealed record CategorizeReceiptLineDto(
        [property: JsonPropertyName("ordinal")] int Ordinal,
        [property: JsonPropertyName("category_slug")] string CategorySlug);
}
