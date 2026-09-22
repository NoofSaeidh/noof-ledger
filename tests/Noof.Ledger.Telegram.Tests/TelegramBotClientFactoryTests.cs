using AwesomeAssertions;
using NSubstitute;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramBotClientFactoryTests
{
    [Fact]
    public void Create_requests_the_named_telegram_http_client()
    {
        const string validlyShapedToken = "123456:AAETopSecretBotTokenValue";
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("telegram").Returns(new HttpClient());
        var factory = new TelegramBotClientFactory(httpClientFactory);

        var client = factory.Create(validlyShapedToken);

        client.Should().NotBeNull();
        httpClientFactory.Received(1).CreateClient("telegram");
    }
}
