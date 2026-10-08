using System.Net;

namespace Noof.Ledger.Fx.Tests;

internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public static StubHttpMessageHandler Returning(HttpStatusCode statusCode, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) }));

    public static StubHttpMessageHandler Throwing(Exception exception) =>
        new((_, _) => Task.FromException<HttpResponseMessage>(exception));

    public static StubHttpMessageHandler NeverResponding() =>
        new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return respond(request, cancellationToken);
    }
}
