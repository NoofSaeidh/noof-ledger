using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Fx;

namespace Noof.Ledger.Fx;

// Never throws for a remote failure (IFxRateSource's contract): each one is a null and one Warning with a fixed phrase,
// as SufReceiptClient reports its failures. Only the caller's own cancellation escapes.
internal sealed class OpenErApiRateSource(
    HttpClient httpClient, TimeProvider timeProvider, OpenErApiEndOfLifeNotice endOfLifeNotice, IOperationTimer timer,
    ILogger<OpenErApiRateSource> logger) : IFxRateSource
{
    const string LatestEurPath = "v6/latest/EUR";

    public async Task<FxRateSnapshot?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        using var timing = timer.Start(logger, TimedOperations.FxFetchRates);

        if (await ReadBodyAsync(cancellationToken) is not { } body)
            return null;

        OpenErApiPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<OpenErApiPayload>(WithoutByteOrderMark(body));
        }
        catch (JsonException)
        {
            return Rejected("the response was not valid JSON");
        }

        if (payload is null)
            return Rejected("the response was empty");

        if (payload.TimeEolUnix is { } endOfLife && endOfLife > 0 && endOfLifeNotice.TryClaim())
            logger.EndOfLifeAnnounced(endOfLife);

        var (snapshot, rejection) = payload.Read(timeProvider.GetUtcNow());
        return rejection is null ? snapshot : Rejected(rejection);
    }

    // Bytes, not a string: JSON is UTF-8 (RFC 8259), and ReadAsStringAsync throws for a charset in Content-Type it does
    // not know, which would escape as an exception instead of a null.
    async Task<byte[]?> ReadBodyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(LatestEurPath, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return NotFetched($"status {(int)response.StatusCode}");

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return NotFetched("the request timed out");
        }
        catch (HttpRequestException exception)
        {
            return NotFetched(ConnectionFailure(exception));
        }
    }

    byte[]? NotFetched(string reason)
    {
        logger.RatesNotFetched(reason);
        return null;
    }

    FxRateSnapshot? Rejected(string reason)
    {
        logger.RatesRejected(reason);
        return null;
    }

    static ReadOnlySpan<byte> WithoutByteOrderMark(byte[] body) =>
        body.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? body.AsSpan(Encoding.UTF8.Preamble.Length) : body;

    static string ConnectionFailure(HttpRequestException exception) => exception.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "the host name could not be resolved",
        HttpRequestError.ConnectionError => "the connection failed",
        HttpRequestError.SecureConnectionError => "a secure connection could not be established",
        _ => "the request failed",
    };
}
