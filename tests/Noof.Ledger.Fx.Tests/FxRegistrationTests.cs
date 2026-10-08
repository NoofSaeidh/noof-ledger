using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Fx.Tests;

public class FxRegistrationTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static ServiceProvider Provider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOperationTimer>(new OperationTimer(TimeProvider.System, new SlowOperationOptions()));
        services.AddNoofFx();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    static IEnumerable<HttpMessageHandler> HandlerChain(ServiceProvider provider)
    {
        for (HttpMessageHandler? handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("open-er-api");
             handler is not null;
             handler = (handler as DelegatingHandler)?.InnerHandler)
            yield return handler;
    }

    [Fact]
    public void AddNoofFx_registers_the_open_er_api_rate_source_once_per_scope()
    {
        using var provider = Provider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var source = first.ServiceProvider.GetRequiredService<IFxRateSource>();

        source.Should().BeOfType<OpenErApiRateSource>();
        first.ServiceProvider.GetRequiredService<IFxRateSource>().Should().BeSameAs(source);
        second.ServiceProvider.GetRequiredService<IFxRateSource>().Should().NotBeSameAs(source);
    }

    [Fact]
    public void One_end_of_life_notice_serves_every_scope()
    {
        using var provider = Provider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        first.ServiceProvider.GetRequiredService<OpenErApiEndOfLifeNotice>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<OpenErApiEndOfLifeNotice>());
    }

    [Fact]
    public void The_named_client_points_at_open_er_api_and_gives_up_after_fifteen_seconds()
    {
        using var provider = Provider();

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("open-er-api");

        client.BaseAddress.Should().Be(new Uri("https://open.er-api.com/"));
        client.Timeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void The_named_client_logs_nothing()
    {
        using var provider = Provider();

        HandlerChain(provider).Select(handler => handler.GetType()).Should().NotContain(
            type => type.Namespace == "Microsoft.Extensions.Http.Logging",
            "every outbound client in this repo keeps its requests out of the logs (RemoveAllLoggers)");
    }

    [Fact]
    public void Nothing_sits_between_the_named_client_and_the_network()
    {
        using var provider = Provider();

        HandlerChain(provider).OfType<DelegatingHandler>().Select(handler => handler.GetType().Name).Should().Equal(
            ["LifetimeTrackingHttpMessageHandler"],
            "a resilience handler throws its own exceptions, which the source does not catch, so a remote failure "
            + "would escape as an exception instead of a null");
    }

    [Fact]
    public async Task The_registered_source_asks_open_er_api_for_the_latest_EUR_rates()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, OpenErApiPayloads.Latest());
        using var provider = Provider(services =>
            services.AddHttpClient("open-er-api").ConfigurePrimaryHttpMessageHandler(() => handler));
        using var scope = provider.CreateScope();

        var snapshot = await scope.ServiceProvider.GetRequiredService<IFxRateSource>().FetchLatestAsync(Ct);

        snapshot.Should().NotBeNull();
        handler.Requests.Should().ContainSingle().Which.RequestUri.Should().Be(new Uri("https://open.er-api.com/v6/latest/EUR"));
    }
}
