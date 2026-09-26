using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Host.Startup;

namespace Noof.Ledger.Host.Tests;

// Fixtures that point at an unreachable database (LoginEndpointTests, ThemeTests-style factories)
// still need the gate open, because Login.razor and Home.razor show DatabaseGateBanner instead of
// the form/data while it is not Ready. DatabaseStartupService would otherwise keep retrying the
// dead connection and leave the gate in Waiting forever, so its hosted-service registration is
// removed here rather than raced against.
static class ReadyDatabaseGate
{
    public static void Register(IServiceCollection services)
    {
        var hostedService = services.FirstOrDefault(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(DatabaseStartupService));
        if (hostedService is not null)
            services.Remove(hostedService);

        services.AddSingleton<IDatabaseGate>(new AlwaysReady());
    }

    sealed class AlwaysReady : IDatabaseGate
    {
        public DatabaseState State => DatabaseState.Ready;

        public string? Detail => null;

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
