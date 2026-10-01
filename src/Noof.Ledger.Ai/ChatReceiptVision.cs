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
// An exchange-office slip has no fiscal QR at all, so this is the only reader it has.
internal sealed class ChatReceiptVision(
    IChatClientFactory clientFactory, IOperationTimer timer, ILogger<ChatReceiptVision> logger) : IReceiptVision
{
    const string ReadReceiptName = "read_receipt";
    const string ReadReceiptDescription = "Record what a photographed shop receipt or exchange-office slip prints.";
    const string ExchangeKind = "exchange";

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

    // Any code-shaped value, not only CurrencyCode.Supported: a slip for a currency no wallet holds
    // (CHF) must read as CHF, not as an unreadable currency the bot would then ask for. Three letters
    // also fit receipt_exchanges' varchar(3).
    static readonly Regex CurrencyCodePattern = new(@"^[A-Z]{3}$", RegexOptions.Compiled);

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

        if (!payload.Readable)
            return Unreadable(payload);

        var taxIdMalformed = !string.IsNullOrWhiteSpace(payload.SellerTaxId) && !TaxIdPattern.IsMatch(payload.SellerTaxId.Trim());

        // Ahead of the contradiction check below, which is a receipt's: a slip has no lines by nature,
        // and one with an amount left unread is still a slip - the operator supplies the missing figure
        // by a reply, which an "unreadable" answer would never let them do. Never with a qrTotal, though:
        // that is a fiscal QR that decoded, so the document is a fiscal receipt and "exchange" is the
        // model misreading it - taken as a slip, its lines would be dropped for good, since the worker
        // restores the QR's kind, total and date but has no lines to restore.
        if (payload.Kind == ExchangeKind && qrTotal is null)
            return ToSlipResult(payload, taxIdMalformed);

        // Readable but no total, or readable but no lines, is a contradiction treated as unreadable
        // rather than trusted: a receipt this layer cannot vouch for must come back as unreadable,
        // never as a half-built ExtractedReceipt.
        if (payload.Total is not { } total || payload.Lines.Count == 0)
            return Unreadable(payload);

        // KindUnclear's rationale (why Kind still defaults to Sale, and why that default is never
        // trusted silently): ReceiptContracts.cs, next to ReceiptVisionResult. An "exchange" reaching
        // here is a fiscal receipt misread as a slip, so whether it is a sale or a refund is unread too.
        var kindUnclear = payload.Kind is null or ExchangeKind;
        return new ReceiptVisionResult(ToExtractedReceipt(payload, total, qrTotal), null, taxIdMalformed, kindUnclear);
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

    static ReceiptVisionResult Unreadable(ReadReceiptPayload payload) =>
        new(null, MapUnreadableReason(payload.UnreadableReason));

    static ExtractedReceipt ToExtractedReceipt(ReadReceiptPayload payload, decimal total, decimal? qrTotal)
    {
        var lines = payload.Lines
            .Select((line, index) => new ExtractedReceiptLine(
                index + 1, line.Name, line.Quantity, Unit: null, line.UnitPrice, line.Total, TaxLabel: null))
            .ToList();

        return VisionReceipt(
            payload,
            AcceptIfWellFormed(payload.FiscalNumber, FiscalNumberPattern),
            total,
            new CurrencyCode(payload.Currency ?? CurrencyCode.Rsd.Value),
            payload.Kind == "refund" ? ReceiptKind.Refund : ReceiptKind.Sale,
            qrTotal,
            lines);
    }

    // A slip's reading is a starting point the operator corrects, like spoken amounts (spec T-8): its
    // figures are kept as read, unvalidated, and 7b's assessment decides whether it is recorded, held
    // or incomplete. FiscalNumber stays null whatever the model put there - a slip number must never
    // reach the fiscal duplicate index.
    static ReceiptVisionResult ToSlipResult(ReadReceiptPayload payload, bool taxIdMalformed)
    {
        var exchange = ToExtractedExchange(payload.Exchange);
        var slip = VisionReceipt(
            payload, fiscalNumber: null, DinarSide(exchange), CurrencyCode.Rsd, ReceiptKind.Exchange, qrTotal: null, lines: []);

        return new ReceiptVisionResult(slip, null, taxIdMalformed, Exchange: exchange);
    }

    // What every vision read shares, receipt or slip: no verification URL, address or location (only
    // a fiscal QR/SUF read has them), and the seller's tax id only when well-formed.
    static ExtractedReceipt VisionReceipt(
        ReadReceiptPayload payload,
        string? fiscalNumber,
        decimal total,
        CurrencyCode currency,
        ReceiptKind kind,
        decimal? qrTotal,
        IReadOnlyList<ExtractedReceiptLine> lines) => new(
        ReceiptSource.Vision,
        VerificationUrl: null,
        AcceptIfWellFormed(payload.SellerTaxId, TaxIdPattern),
        payload.SellerName,
        SellerAddress: null,
        LocationName: null,
        fiscalNumber,
        ParseIssuedAt(payload.IssuedAt),
        total,
        currency,
        kind,
        MapPaymentMethod(payload.PaymentMethod),
        qrTotal,
        lines);

    static ExtractedExchange ToExtractedExchange(ReadExchangeDto? read) => new(
        read?.GivenAmount,
        CurrencyCodeOrNull(read?.GivenCurrency),
        read?.ReceivedAmount,
        CurrencyCodeOrNull(read?.ReceivedCurrency),
        read?.Rate,
        read?.Commission?.Amount,
        CurrencyCodeOrNull(read?.Commission?.Currency),
        SlipNumberOrNull(read?.SlipNumber));

    // receipts.total is NOT NULL, so a slip whose dinar side is unread stores 0; nothing reads a slip's
    // total - receipt_exchanges keeps what was read, and the transfer is the money.
    static decimal DinarSide(ExtractedExchange exchange) =>
        exchange.GivenCurrency == CurrencyCode.Rsd.Value ? exchange.GivenAmount ?? 0m
        : exchange.ReceivedCurrency == CurrencyCode.Rsd.Value ? exchange.ReceivedAmount ?? 0m
        : 0m;

    static string? CurrencyCodeOrNull(string? printed)
    {
        if (string.IsNullOrWhiteSpace(printed))
            return null;

        var code = printed.Trim().ToUpperInvariant();
        return code switch
        {
            // Serbian slips often print the dinar as DIN or ДИН rather than its ISO code.
            "DIN" or "ДИН" => CurrencyCode.Rsd.Value,
            _ when CurrencyCodePattern.IsMatch(code) => code,
            _ => null,
        };
    }

    static string? SlipNumberOrNull(string? printed) =>
        string.IsNullOrWhiteSpace(printed) ? null : printed.Trim();

    static string? AcceptIfWellFormed(string? printed, Regex pattern)
    {
        if (string.IsNullOrWhiteSpace(printed))
            return null;

        var trimmed = printed.Trim();
        return pattern.IsMatch(trimmed) ? trimmed : null;
    }

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
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("total")] decimal? Total,
        [property: JsonPropertyName("payment_method")] string? PaymentMethod,
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("lines")] IReadOnlyList<ReadReceiptLineDto> Lines,
        [property: JsonPropertyName("exchange")] ReadExchangeDto? Exchange);

    sealed record ReadReceiptLineDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("quantity")] decimal Quantity,
        [property: JsonPropertyName("unit_price")] decimal UnitPrice,
        [property: JsonPropertyName("total")] decimal Total);

    sealed record ReadExchangeDto(
        [property: JsonPropertyName("given_amount")] decimal? GivenAmount,
        [property: JsonPropertyName("given_currency")] string? GivenCurrency,
        [property: JsonPropertyName("received_amount")] decimal? ReceivedAmount,
        [property: JsonPropertyName("received_currency")] string? ReceivedCurrency,
        [property: JsonPropertyName("rate")] decimal? Rate,
        [property: JsonPropertyName("commission")] ReadCommissionDto? Commission,
        [property: JsonPropertyName("slip_number")] string? SlipNumber);

    sealed record ReadCommissionDto(
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("currency")] string Currency);
}
