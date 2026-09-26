using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.Receipts.Journal;

namespace Noof.Ledger.Receipts.Suf;

internal sealed partial class SufReceiptClient(HttpClient httpClient) : IFiscalReceiptClient
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
                // I-4 (2026-09-25 final review): sdcTime carries no UTC offset ("2026-09-25T12:30:00"),
                // and it is Belgrade local time, not UTC - the journal's own ПФР време for the same
                // receipt names the same wall-clock hour. The journal is parsed as Belgrade local
                // already (FiscalJournalParser.ParseBelgradeTime) and unambiguous, so it wins whenever
                // present; sdcTime is only the fallback, and then it is read the same way, never as UTC.
                IssuedAt: journal.IssuedAt ?? ParseSdcTime(parsed.InvoiceResult?.SdcTime),
                Total: parsed.InvoiceResult?.TotalAmount ?? journal.Total,
                Currency: CurrencyCode.Rsd,
                Kind: payload.Kind,
                PaymentMethod: journal.PaymentMethod,
                QrTotal: payload.Total,
                Lines: journal.Lines);

            return new FiscalFetchResult(receipt, null);
        }
    }

    static DateTimeOffset? ParseSdcTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        if (trimmed.EndsWith('Z') || ExplicitOffsetSuffix().IsMatch(trimmed))
            return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset)
                ? withOffset
                : null;

        if (!DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out var local))
            return null;

        var belgrade = TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade");
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, belgrade.GetUtcOffset(unspecified));
    }

    static FiscalFetchResult Failure(string reason, int? statusCode) => new(null, new FiscalFetchFailure(reason, statusCode));

    [GeneratedRegex(@"[+-]\d{2}:?\d{2}$")]
    private static partial Regex ExplicitOffsetSuffix();
}
