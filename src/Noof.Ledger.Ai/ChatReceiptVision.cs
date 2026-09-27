using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Ai;

// The vision fallback: one forced, strict read_receipt call carrying the photo as a DataContent
// image. Reached only when the fiscal QR could not be decoded or the Tax Administration site did
// not answer - Noof.Ledger.Receipts is the path that reads a receipt without ever asking the model.
internal sealed class ChatReceiptVision(
    IChatClientFactory clientFactory, IOperationTimer timer, ILogger<ChatReceiptVision> logger) : IReceiptVision
{
    const string ReadReceiptName = "read_receipt";
    const string ReadReceiptDescription = "Record what a photographed shop receipt prints.";

    // Downscaling a large photo is the capture worker's job, not this class's - this only refuses
    // an image too large to be a reasonable receipt photo at all, so the worker has something
    // concrete to log instead of an opaque provider rejection.
    const int MaxImageBytes = 5 * 1024 * 1024;

    static readonly TimeZoneInfo Belgrade = TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade");
    static readonly JsonElement ReadReceiptSchema = ReceiptVisionSchema.BuildReadReceipt();

    // 2026-09-27: OCR-off-by-a-digit garbage must never collide with, or fail to collide with, a real
    // fiscal receipt's own (seller_tax_id, fiscal_number) duplicate index - so a value the model
    // printed but that does not match its real shape is dropped to null here rather than trusted.
    // Never applied to a fiscal QR/SUF receipt, which reads these from the Tax Administration itself.
    static readonly Regex TaxIdPattern = new(@"^\d{9}$", RegexOptions.Compiled);
    static readonly Regex FiscalNumberPattern = new(@"^[A-Z0-9]{8}-[A-Z0-9]{8}-\d+$", RegexOptions.Compiled);

    public async Task<ReceiptVisionResult> ReadAsync(
        ReadOnlyMemory<byte> image, string mediaType, decimal? qrTotal, CancellationToken cancellationToken)
    {
        if (image.Length > MaxImageBytes)
        {
            throw new ModelCallException(
                ModelFailureKind.Terminal,
                $"Receipt image is {image.Length} bytes, over the {MaxImageBytes}-byte limit.");
        }

        using var timing = timer.Start(logger, TimedOperations.ModelReadReceipt);

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

        // The model's own "I could not read this" (readable: false), or a contradiction - readable but
        // no total, or readable but no lines - treated the same way rather than trusted: a receipt this
        // layer cannot vouch for must come back as unreadable, never as a half-built ExtractedReceipt.
        if (!payload.Readable || payload.Total is null || payload.Lines.Count == 0)
            return new ReceiptVisionResult(null, MapUnreadableReason(payload.UnreadableReason));

        var taxIdMalformed = payload.SellerTaxId is not null && !TaxIdPattern.IsMatch(payload.SellerTaxId);
        return new ReceiptVisionResult(ToExtractedReceipt(payload, qrTotal), null, taxIdMalformed);
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
            ReceiptSource.Vision,
            VerificationUrl: null,
            AcceptIfWellFormed(payload.SellerTaxId, TaxIdPattern),
            payload.SellerName,
            SellerAddress: null,
            LocationName: null,
            AcceptIfWellFormed(payload.FiscalNumber, FiscalNumberPattern),
            ParseIssuedAt(payload.IssuedAt),
            payload.Total!.Value,
            new CurrencyCode(payload.Currency),
            payload.Kind == "refund" ? ReceiptKind.Refund : ReceiptKind.Sale,
            MapPaymentMethod(payload.PaymentMethod),
            qrTotal,
            lines);
    }

    static string? AcceptIfWellFormed(string? printed, Regex pattern) =>
        printed is not null && pattern.IsMatch(printed) ? printed : null;

    static ReceiptUnreadableReason MapUnreadableReason(string? reason) => reason switch
    {
        "too_small" => ReceiptUnreadableReason.TooSmall,
        "blurry" => ReceiptUnreadableReason.Blurry,
        "not_a_receipt" => ReceiptUnreadableReason.NotAReceipt,
        "cut_off" => ReceiptUnreadableReason.CutOff,
        _ => ReceiptUnreadableReason.Other,
    };

    // M-1 (2026-09-25 final review): the model is strict on schema shape, not on content - a date it
    // could not phrase in ISO form must read as "no date", never throw. A FormatException here was
    // being read by the worker as Transient (ExtractReceiptWorker's generic catch), burning up to
    // MaxAttempts more vision calls for one unparseable string before failing a receipt the model did
    // read correctly.
    static DateTimeOffset? ParseIssuedAt(string? issuedAt)
    {
        if (string.IsNullOrWhiteSpace(issuedAt))
            return null;

        if (!DateTime.TryParse(issuedAt, CultureInfo.InvariantCulture, DateTimeStyles.None | DateTimeStyles.NoCurrentDateDefault, out var local))
            return null;

        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Belgrade.GetUtcOffset(local));
    }

    static PaymentMethod? MapPaymentMethod(string? method) => method switch
    {
        "card" => PaymentMethod.Card,
        "cash" => PaymentMethod.Cash,
        "transfer" => PaymentMethod.Transfer,
        "voucher" => PaymentMethod.Voucher,
        "other" => PaymentMethod.Other,
        "mixed" => PaymentMethod.Mixed,
        _ => null,
    };

    sealed record ReadReceiptPayload(
        [property: JsonPropertyName("readable")] bool Readable,
        [property: JsonPropertyName("unreadable_reason")] string? UnreadableReason,
        [property: JsonPropertyName("seller_name")] string? SellerName,
        [property: JsonPropertyName("seller_tax_id")] string? SellerTaxId,
        [property: JsonPropertyName("fiscal_number")] string? FiscalNumber,
        [property: JsonPropertyName("issued_at")] string? IssuedAt,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("total")] decimal? Total,
        [property: JsonPropertyName("payment_method")] string? PaymentMethod,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("lines")] IReadOnlyList<ReadReceiptLineDto> Lines);

    sealed record ReadReceiptLineDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("quantity")] decimal Quantity,
        [property: JsonPropertyName("unit_price")] decimal UnitPrice,
        [property: JsonPropertyName("total")] decimal Total);
}
