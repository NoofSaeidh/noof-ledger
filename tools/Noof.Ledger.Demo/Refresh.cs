using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.Demo;

internal static class Refresh
{
    public static async Task RunAsync(string adminConnectionString, string database, DemoPaths paths, CancellationToken cancellationToken)
    {
        await DemoDatabase.RecreateAsync(adminConnectionString, database, cancellationToken);

        await using var services = DemoServices.Build(DemoDatabase.For(adminConnectionString, database), paths);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync(cancellationToken);
        await MockDataWriter.WriteAsync(scope.ServiceProvider, cancellationToken);
    }
}
