using Noof.Ledger.TestKit;
using Npgsql;

namespace Noof.Ledger.Demo.Tests;

public sealed class DemoTestDatabase : IAsyncLifetime
{
    public string Name { get; } = $"noof_test_{Guid.NewGuid():N}";
    internal DemoPaths Paths { get; } = new(Directory.CreateTempSubdirectory("noof-demo-test-").FullName);
    public string Admin => DatabaseSettings.AdminConnectionString;
    public string ConnectionString => DemoDatabase.For(Admin, Name);
    public bool Unavailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(Admin + ";Timeout=3");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception unreachable) when (unreachable is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            Unavailable = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!Unavailable)
            await DatabaseSettings.DropDatabaseAsync(Name, CancellationToken.None);

        Directory.Delete(Paths.Root, recursive: true);
    }
}
