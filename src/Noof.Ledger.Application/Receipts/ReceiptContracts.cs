using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Receipts;

public enum ReceiptSource
{
    FiscalQr = 0,
    Vision = 1,
}

public enum ReceiptKind
{
    Sale = 0,
    Refund = 1,
    Copy = 2,
    Training = 3,
    Proforma = 4,
    Advance = 5,
}

public enum PaymentMethod
{
    Card = 0,
    Cash = 1,
    Transfer = 2,
    Voucher = 3,
    Other = 4,
    Mixed = 5,
}

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

public sealed record ReceiptCategorizationRequest(
    IReadOnlyList<ReceiptLineToCategorize> Lines,
    string? SellerName,
    string? SellerTaxId,
    bool MerchantKnown,
    string? Caption);

public sealed record ReceiptLineCategory(int Ordinal, string CategorySlug);

public sealed record ReceiptCategorization(
    IReadOnlyList<ReceiptLineCategory> Lines,
    string? MerchantCanonicalName,
    Guid? WalletId);

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
