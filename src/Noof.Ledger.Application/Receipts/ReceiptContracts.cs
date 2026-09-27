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
    long TransactionTypeCounter)
{
    // The printed fiscal number, same shape a real receipt shows and ChatReceiptVision's own
    // FiscalNumberPattern accepts - the one place this is composed, so a QR-decoded fallback and
    // anything else that needs it never re-derive it differently.
    public string FiscalNumber => $"{RequestedBy}-{SignedBy}-{TotalCounter}";
}

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

// Why the model could not read the photo reliably enough to record a receipt from it - reported by
// read_receipt itself (2026-09-27) rather than invented: a receipt it cannot read must say so, never
// approximate one.
public enum ReceiptUnreadableReason { TooSmall, Blurry, NotAReceipt, CutOff, Other }

// Shaped like FiscalFetchResult: exactly one of Receipt/Unreadable is set. Receipt is null when the
// model reported the photo unreadable, or when it reported readable but left the total or every line
// missing - a contradiction this layer treats the same as an honest "unreadable" rather than trust.
// SellerTaxIdMalformed (2026-09-27) is set only alongside a non-null Receipt: the model printed a tax
// id that does not match a real PIB's shape, so Receipt.SellerTaxId is already null (never trusted for
// storage), but the caller still needs to know a tax id was there and looked wrong, to decide whether
// this receipt needs the operator's own confirmation before it is categorised.
// KindUnclear (Copilot finding, PR #3) is set only alongside a non-null Receipt too: the model left
// kind null because it could not read whether the receipt was a sale or a refund. Receipt.Kind is
// still a concrete ReceiptKind (defaulted to Sale, the overwhelmingly common case) because the type
// has no null to hold - but a guessed Sale on what is actually a refund would change the money
// direction, so the caller must not categorise on the guess alone; it holds the receipt behind the
// operator's own confirmation instead of trusting it silently.
public sealed record ReceiptVisionResult(
    ExtractedReceipt? Receipt, ReceiptUnreadableReason? Unreadable, bool SellerTaxIdMalformed = false, bool KindUnclear = false);

public interface IReceiptVision
{
    Task<ReceiptVisionResult> ReadAsync(
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

// N-8 (Phase 6 re-review): a receipt correction cannot change the date or the amounts - both come
// from the receipt itself - and the model, not C# text matching, is what decides whether a
// correction asked for either, since it is the one reading the operator's own words.
public enum UnsupportedChangeKind { None, Date, Amount }

public sealed record ReceiptCategorization(
    IReadOnlyList<ReceiptLineCategory> Lines,
    string? MerchantCanonicalName,
    Guid? WalletId,
    UnsupportedChangeKind UnsupportedChange = UnsupportedChangeKind.None);

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
