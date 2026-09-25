using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Ai;

// The vision fallback: one forced, strict read_receipt call carrying the photo as a DataContent
// image. Reached only when the fiscal QR could not be decoded or the Tax Administration site did
// not answer - Noof.Ledger.Receipts is the path that reads a receipt without ever asking the model.
internal sealed class ChatReceiptVision(IChatClientFactory clientFactory) : IReceiptVision
{
    const string ReadReceiptName = "read_receipt";
    const string ReadReceiptDescription = "Record what a photographed shop receipt prints.";

    // Downscaling a large photo is the capture worker's job, not this class's - this only refuses
    // an image too large to be a reasonable receipt photo at all, so the worker has something
    // concrete to log instead of an opaque provider rejection.
    const int MaxImageBytes = 5 * 1024 * 1024;

    static readonly TimeZoneInfo Belgrade = TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade");
    static readonly JsonElement ReadReceiptSchema = ReceiptVisionSchema.BuildReadReceipt();

    public async Task<ExtractedReceipt> ReadAsync(
        ReadOnlyMemory<byte> image, string mediaType, decimal? qrTotal, CancellationToken cancellationToken)
    {
        if (image.Length > MaxImageBytes)
        {
            throw new ModelCallException(
                ModelFailureKind.Terminal,
                $"Receipt image is {image.Length} bytes, over the {MaxImageBytes}-byte limit.");
        }

        var readReceipt = new SchemaTool(ReadReceiptName, ReadReceiptDescription, ReadReceiptSchema);

        using var chat = await clientFactory.CreateAsync(cancellationToken);

        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, [new DataContent(image, mediaType), new TextContent(ReceiptVisionPrompt.Instruction)])],
            new ChatOptions
            {
                Tools = [readReceipt],
                ToolMode = ChatToolMode.RequireSpecific(ReadReceiptName),
            },
            cancellationToken);

        if (FindCall(response) is not { } call)
        {
            throw new ModelCallException(
                ModelFailureKind.Transient,
                $"{ReadReceiptName} produced no tool call (finish reason: {response.FinishReason}).");
        }

        var payload = ToPayload(call);
        if (payload is null)
            throw new ModelCallException(ModelFailureKind.Transient, $"{ReadReceiptName} returned an empty payload.");

        return ToExtractedReceipt(payload, qrTotal);
    }

    static FunctionCallContent? FindCall(ChatResponse response) =>
        response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(call => call.Name == ReadReceiptName);

    // Same reasoning as ChatCategorizer.ToPayload: FunctionCallContent.Arguments is read straight
    // off the response's own token text via a re-serialise/deserialise round trip, never a double.
    static ReadReceiptPayload? ToPayload(FunctionCallContent call) =>
        JsonSerializer.Deserialize<ReadReceiptPayload>(JsonSerializer.SerializeToElement(call.Arguments));

    static ExtractedReceipt ToExtractedReceipt(ReadReceiptPayload payload, decimal? qrTotal)
    {
        var lines = payload.Lines
            .Select((line, index) => new ExtractedReceiptLine(
                index + 1, line.Name, line.Quantity, Unit: null, line.UnitPrice, line.Total, TaxLabel: null))
            .ToList();

        return new ExtractedReceipt(
            Application.Receipts.ReceiptSource.Vision,
            VerificationUrl: null,
            payload.SellerTaxId,
            payload.SellerName,
            SellerAddress: null,
            LocationName: null,
            FiscalNumber: null,
            ParseIssuedAt(payload.IssuedAt),
            payload.Total,
            new CurrencyCode(payload.Currency),
            payload.Kind == "refund" ? Application.Receipts.ReceiptKind.Refund : Application.Receipts.ReceiptKind.Sale,
            MapPaymentMethod(payload.PaymentMethod),
            qrTotal,
            lines);
    }

    static DateTimeOffset? ParseIssuedAt(string? issuedAt)
    {
        if (string.IsNullOrWhiteSpace(issuedAt))
            return null;

        var local = DateTime.Parse(issuedAt, CultureInfo.InvariantCulture, DateTimeStyles.None | DateTimeStyles.NoCurrentDateDefault);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Belgrade.GetUtcOffset(local));
    }

    static Application.Receipts.PaymentMethod? MapPaymentMethod(string? method) => method switch
    {
        "card" => Application.Receipts.PaymentMethod.Card,
        "cash" => Application.Receipts.PaymentMethod.Cash,
        "transfer" => Application.Receipts.PaymentMethod.Transfer,
        "voucher" => Application.Receipts.PaymentMethod.Voucher,
        "other" => Application.Receipts.PaymentMethod.Other,
        "mixed" => Application.Receipts.PaymentMethod.Mixed,
        _ => null,
    };

    sealed record ReadReceiptPayload(
        [property: JsonPropertyName("seller_name")] string? SellerName,
        [property: JsonPropertyName("seller_tax_id")] string? SellerTaxId,
        [property: JsonPropertyName("issued_at")] string? IssuedAt,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("total")] decimal Total,
        [property: JsonPropertyName("payment_method")] string? PaymentMethod,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("lines")] IReadOnlyList<ReadReceiptLineDto> Lines);

    sealed record ReadReceiptLineDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("quantity")] decimal Quantity,
        [property: JsonPropertyName("unit_price")] decimal UnitPrice,
        [property: JsonPropertyName("total")] decimal Total);
}
