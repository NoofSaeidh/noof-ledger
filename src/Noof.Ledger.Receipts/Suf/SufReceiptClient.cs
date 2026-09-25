using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.Receipts.Journal;

namespace Noof.Ledger.Receipts.Suf;

internal sealed class SufReceiptClient(HttpClient httpClient) : IFiscalReceiptClient
{
    static readonly string UserAgent =
        $"noof-ledger/{typeof(SufReceiptClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"} (personal receipt lookup)";

    public async Task<FiscalFetchResult> FetchAsync(FiscalQrPayload payload, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, payload.VerificationUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd(UserAgent);
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("The request to the Tax Administration timed out.", null);
        }
        catch (HttpRequestException exception)
        {
            return Failure($"The request to the Tax Administration failed: {exception.Message}", null);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return Failure($"The Tax Administration returned status {(int)response.StatusCode}.", (int)response.StatusCode);

            SufApiResponse? parsed;
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                parsed = JsonSerializer.Deserialize<SufApiResponse>(body);
            }
            catch (JsonException)
            {
                return Failure("The Tax Administration's response was not valid JSON.", (int)response.StatusCode);
            }

            if (parsed?.Journal is null)
                return Failure("The Tax Administration's response had no journal.", (int)response.StatusCode);

            var journal = FiscalJournalParser.Parse(parsed.Journal);
            if (journal is null || journal.Lines.Count == 0)
                return Failure("The journal had no recognisable line items.", (int)response.StatusCode);

            var receipt = new ExtractedReceipt(
                Source: Noof.Ledger.Application.Receipts.ReceiptSource.FiscalQr,
                VerificationUrl: payload.VerificationUrl,
                SellerTaxId: parsed.InvoiceRequest?.TaxId ?? journal.SellerTaxId,
                SellerName: parsed.InvoiceRequest?.BusinessName ?? journal.SellerName,
                SellerAddress: parsed.InvoiceRequest?.Address ?? journal.SellerAddress,
                LocationName: parsed.InvoiceRequest?.LocationName ?? journal.LocationName,
                FiscalNumber: parsed.InvoiceResult?.InvoiceNumber ?? journal.FiscalNumber,
                IssuedAt: ParseSdcTime(parsed.InvoiceResult?.SdcTime) ?? journal.IssuedAt,
                Total: parsed.InvoiceResult?.TotalAmount ?? journal.Total,
                Currency: CurrencyCode.Rsd,
                Kind: payload.Kind,
                PaymentMethod: journal.PaymentMethod,
                QrTotal: payload.Total,
                Lines: journal.Lines);

            return new FiscalFetchResult(receipt, null);
        }
    }

    static DateTimeOffset? ParseSdcTime(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    static FiscalFetchResult Failure(string reason, int? statusCode) => new(null, new FiscalFetchFailure(reason, statusCode));
}
