using Noof.Ledger.Persistence;

namespace Noof.Ledger.Host.Startup;

internal sealed class DatabaseStartupProbe(IServiceProvider services) : IDatabaseStartupProbe
{
    public Task OpenConnectionAsync(CancellationToken cancellationToken) =>
        services.OpenNoofDatabaseConnectionAsync(cancellationToken);

    public Task MigrateAsync(CancellationToken cancellationToken) =>
        services.MigrateNoofDatabaseAsync(cancellationToken);
}
