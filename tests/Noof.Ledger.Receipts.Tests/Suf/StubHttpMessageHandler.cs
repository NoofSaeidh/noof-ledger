using System.Net;

namespace Noof.Ledger.Receipts.Tests.Suf;

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;

    public HttpRequestMessage? LastRequest { get; private set; }

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        this.respond = respond;

    public static StubHttpMessageHandler Returning(HttpStatusCode statusCode, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body),
        }));

    public static StubHttpMessageHandler NeverResponding() =>
        new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return await respond(request, cancellationToken);
    }
}
