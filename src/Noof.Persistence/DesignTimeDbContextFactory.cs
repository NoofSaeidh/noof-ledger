using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Noof.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NoofDbContext>
{
    public NoofDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NOOF_DESIGN_TIME_PG")
            ?? "Host=127.0.0.1;Port=5432;Database=noof_finance;Username=postgres";

        var options = new DbContextOptionsBuilder<NoofDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new NoofDbContext(options);
    }
}
