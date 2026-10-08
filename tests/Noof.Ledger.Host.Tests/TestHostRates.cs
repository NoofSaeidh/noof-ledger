using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Noof.Ledger.Application.Fx;

namespace Noof.Ledger.Host.Tests;

// Spec P-2: no test host reaches open.er-api.com. A host whose database gate opens (every class over a
// migrated clone) starts FxRateWorker, whose first tick would fetch from an empty archive; this stub answers instead,
// as a remote failure does. UseTempLogDirectory calls it, the one helper every test host must call
// (TestHostLogDirectoryTests).
internal static class TestHostRates
{
    public static IWebHostBuilder UseNoRemoteRates(this IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFxRateSource>();
            services.AddSingleton<IFxRateSource>(new NoRemoteRateSource());
        });
}

internal sealed class NoRemoteRateSource : IFxRateSource
{
    public Task<FxRateSnapshot?> FetchLatestAsync(CancellationToken cancellationToken) => Task.FromResult<FxRateSnapshot?>(null);
}
