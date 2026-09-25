using System.Text.Json.Serialization;

namespace Noof.Ledger.Receipts.Suf;

internal sealed record SufApiResponse(
    [property: JsonPropertyName("invoiceRequest")] SufInvoiceRequest? InvoiceRequest,
    [property: JsonPropertyName("invoiceResult")] SufInvoiceResult? InvoiceResult,
    [property: JsonPropertyName("journal")] string? Journal);

internal sealed record SufInvoiceRequest(
    [property: JsonPropertyName("businessName")] string? BusinessName,
    [property: JsonPropertyName("taxId")] string? TaxId,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("locationName")] string? LocationName);

internal sealed record SufInvoiceResult(
    [property: JsonPropertyName("invoiceNumber")] string? InvoiceNumber,
    [property: JsonPropertyName("totalAmount")] decimal? TotalAmount,
    [property: JsonPropertyName("sdcTime")] string? SdcTime);
