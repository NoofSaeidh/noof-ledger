using System.Net;
using System.Text;

namespace Noof.Ledger.Ai.Tests;

// Answers requests strictly in the order they were enqueued and records every request it saw
// (method, URI, headers, body) so a test can assert what the SDK actually sent, not just what it
// returned. Throwing on an unqueued request — rather than e.g. returning a default 200 — is
// deliberate: it turns "the code made one extra call it shouldn't have" into an immediate,
// unambiguous test failure instead of a silently-passing assertion on a truncated request list.
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    readonly Queue<(HttpStatusCode StatusCode, string Body)> responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public StubHttpMessageHandler Enqueue(HttpStatusCode statusCode, string body)
    {
        responses.Enqueue((statusCode, body));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body));

        if (responses.Count == 0)
            throw new InvalidOperationException(
                $"StubHttpMessageHandler received request #{Requests.Count} to {request.RequestUri} with no canned response queued. " +
                "This means the code under test sent more requests than the test expected.");

        var (statusCode, responseBody) = responses.Dequeue();
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        };
    }
}

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body);
