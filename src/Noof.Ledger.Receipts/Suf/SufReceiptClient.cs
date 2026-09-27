using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.Receipts.Journal;

namespace Noof.Ledger.Receipts.Suf;

internal sealed partial class SufReceiptClient(
    HttpClient httpClient, IOperationTimer timer, ILogger<SufReceiptClient> logger) : IFiscalReceiptClient
{
    static readonly string UserAgent =
        $"noof-ledger/{typeof(SufReceiptClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"} (personal receipt lookup)";

    public async Task<FiscalFetchResult> FetchAsync(FiscalQrPayload payload, CancellationToken cancellationToken)
    {
        using var timing = timer.Start(logger, TimedOperations.ReceiptFiscalFetch);

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
            return Failure(ClassifyConnectionFailure(exception), exception.StatusCode is { } code ? (int)code : null);
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
            // M-9 (2026-09-25 final review): the body read used to sit outside any catch that covers
            // it, contrary to this method's "never throw except OperationCanceledException" contract -
            // a connection dropped mid-body surfaces as IOException, which HttpContent wraps as
            // HttpRequestException. Under the default HttpCompletionOption this client uses, that
            // already fails inside the SendAsync call above (caught there); this is defence in depth
            // against ever reading the body separately (e.g. HttpCompletionOption.ResponseHeadersRead).
            catch (HttpRequestException exception)
            {
                return Failure(
                    $"The Tax Administration's response body could not be read: {ClassifyConnectionFailure(exception)}",
                    (int)response.StatusCode);
            }
            catch (IOException)
            {
                return Failure("The Tax Administration's response body could not be read: the connection was interrupted.", (int)response.StatusCode);
            }

            if (parsed?.Journal is null)
                return Failure("The Tax Administration's response had no journal.", (int)response.StatusCode);

            ParsedJournal? journal;
            try
            {
                journal = FiscalJournalParser.Parse(parsed.Journal);
            }
            // A malformed journal (an unparseable amount, for example) must be a fetch failure like
            // any other, not an unhandled exception - ExtractReceiptWorker's generic catch would
            // otherwise treat it as a transient retry and skip the QR-total -> vision fallback this
            // worker already has for every other kind of fetch failure.
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                return Failure("The Tax Administration's journal could not be parsed.", (int)response.StatusCode);
            }

            if (journal is null || journal.Lines.Count == 0)
                return Failure("The journal had no recognisable line items.", (int)response.StatusCode);

            var receipt = new ExtractedReceipt(
                Source: ReceiptSource.FiscalQr,
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

    // The reason string becomes FiscalFetchFailure.Reason, which is logged and shown on the trace
    // and health pages verbatim - it must never carry HttpRequestException.Message, which can embed
    // the request URI (the verification link/vl, which CLAUDE.md forbids logging). Every branch here
    // is a fixed phrase built from structured, exception-shape data only (HttpRequestError, the status
    // code), never free text off the exception.
    static string ClassifyConnectionFailure(HttpRequestException exception) => exception.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "The Tax Administration's host name could not be resolved.",
        HttpRequestError.ConnectionError => "The connection to the Tax Administration failed.",
        HttpRequestError.SecureConnectionError => "A secure connection to the Tax Administration could not be established.",
        HttpRequestError.HttpProtocolError => "The Tax Administration returned a malformed HTTP response.",
        _ when exception.StatusCode is { } statusCode => $"The Tax Administration returned status {(int)statusCode}.",
        _ => "The request to the Tax Administration failed.",
    };

    [GeneratedRegex(@"[+-]\d{2}:?\d{2}$")]
    private static partial Regex ExplicitOffsetSuffix();
}
