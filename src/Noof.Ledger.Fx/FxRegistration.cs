using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Fx;

namespace Noof.Ledger.Fx;

[SuppressMessage("Maintainability", "CA1515",
    Justification = "The one public type in this assembly, and the only way the Host can register IFxRateSource "
        + "without naming an implementation.")]
public static class FxRegistration
{
    const string HttpClientName = "open-er-api";

    public static IServiceCollection AddNoofFx(this IServiceCollection services)
    {
        // No credential rides on this client - the endpoint is keyless - but RemoveAllLoggers keeps the pattern every
        // other outbound HttpClient in this repo follows. No resilience handler either: its exceptions are not ones
        // OpenErApiRateSource turns into a null.
        services.AddHttpClient(HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://open.er-api.com/");
            client.Timeout = TimeSpan.FromSeconds(15);
        }).RemoveAllLoggers();

        services.AddSingleton<OpenErApiEndOfLifeNotice>();
        services.AddScoped<IFxRateSource>(sp => new OpenErApiRateSource(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<OpenErApiEndOfLifeNotice>(),
            sp.GetRequiredService<IOperationTimer>(),
            sp.GetRequiredService<ILogger<OpenErApiRateSource>>()));

        return services;
    }
}
