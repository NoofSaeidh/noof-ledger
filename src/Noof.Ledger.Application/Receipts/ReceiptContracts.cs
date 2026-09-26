using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Receipts;

public sealed record ExtractedReceiptLine(
    int Ordinal,
    string Name,
    decimal Quantity,
    string? Unit,
    decimal UnitPrice,
    decimal Total,
    string? TaxLabel);

public sealed record ExtractedReceipt(
    ReceiptSource Source,
    string? VerificationUrl,
    string? SellerTaxId,
    string? SellerName,
    string? SellerAddress,
    string? LocationName,
    string? FiscalNumber,
    DateTimeOffset? IssuedAt,
    decimal Total,
    CurrencyCode Currency,
    ReceiptKind Kind,
    PaymentMethod? PaymentMethod,
    decimal? QrTotal,
    IReadOnlyList<ExtractedReceiptLine> Lines);

public sealed record FiscalQrPayload(
    string VerificationUrl,
    decimal Total,
    DateTimeOffset IssuedAt,
    string RequestedBy,
    string SignedBy,
    ReceiptKind Kind,
    long TotalCounter,
    long TransactionTypeCounter);

public sealed record FiscalQrDecodeResult(FiscalQrPayload? Payload, string? Error);

public sealed record FiscalFetchFailure(string Reason, int? StatusCode);

public sealed record FiscalFetchResult(ExtractedReceipt? Receipt, FiscalFetchFailure? Failure);

public interface IQrReader
{
    string? Read(Stream image);
}

public interface IFiscalQrDecoder
{
    FiscalQrDecodeResult Decode(string verificationUrl);
}

public interface IFiscalReceiptClient
{
    Task<FiscalFetchResult> FetchAsync(FiscalQrPayload payload, CancellationToken cancellationToken);
}

public interface IReceiptVision
{
    Task<ExtractedReceipt> ReadAsync(
        ReadOnlyMemory<byte> image, string mediaType, decimal? qrTotal, CancellationToken cancellationToken);
}

public sealed record ReceiptLineToCategorize(int Ordinal, string Name, decimal Quantity, decimal Total);

// Correction is the operator's own words about a receipt already recorded (I-2, Phase 6 final
// review) - a reply to the receipt echo's Edit prompt, or an edited caption. It can steer categories,
// merchant and wallet only; amounts stay the receipt's own regardless of what it asks (categorize_receipt
// has no amount field to answer with), and AmountChangeDeclined tells the echo when it asked anyway.
public sealed record ReceiptCategorizationRequest(
    IReadOnlyList<ReceiptLineToCategorize> Lines,
    string? SellerName,
    string? SellerTaxId,
    bool MerchantKnown,
    string? Caption,
    string? Correction = null);

public sealed record ReceiptLineCategory(int Ordinal, string CategorySlug);

public sealed record ReceiptCategorization(
    IReadOnlyList<ReceiptLineCategory> Lines,
    string? MerchantCanonicalName,
    Guid? WalletId,
    bool AmountChangeDeclined = false);

public interface IReceiptCategorizer
{
    Task<ReceiptCategorization> CategorizeAsync(
        ReceiptCategorizationRequest request, CancellationToken cancellationToken);
}

public interface IReceiptFetchStatus
{
    DateTimeOffset? LastFailureAt { get; }

    string? LastFailureReason { get; }

    void RecordFailure(DateTimeOffset at, string reason);
}
