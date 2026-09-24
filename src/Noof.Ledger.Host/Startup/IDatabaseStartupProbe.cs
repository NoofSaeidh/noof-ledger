namespace Noof.Ledger.Host.Startup;

internal interface IDatabaseStartupProbe
{
    Task OpenConnectionAsync(CancellationToken cancellationToken);

    Task MigrateAsync(CancellationToken cancellationToken);
}
