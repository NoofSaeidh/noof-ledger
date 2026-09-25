namespace Noof.Ledger.E2E.Tests;

// Task 11: launches the published host from a directory that is NOT its own install directory -
// exactly the mistake ops/RUNBOOK.md's "Start the published app from its own directory" section
// warns against - to prove Program.cs's ContentRootPath = AppContext.BaseDirectory fix actually
// makes the current directory not matter. The database stays unreachable; this fixture only cares
// about static assets, never about the sign-in form itself.
public sealed class DifferentWorkingDirectoryHostFixture : IAsyncLifetime
{
    readonly HostProcess host = new();

    public string BaseUrl => host.BaseUrl;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var publishDirectory = await HostProcess.PublishHostAsync(cancellationToken);

        await host.StartAsync(publishDirectory, new Dictionary<string, string>
        {
            ["Database__MigrateOnStartup"] = "true",
            ["ConnectionStrings__Ledger"] = "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2",
            ["Backup__Enabled"] = "false",
        }, cancellationToken, waitForDatabaseReady: false, workingDirectory: Path.GetTempPath());
    }

    public ValueTask DisposeAsync() => host.DisposeAsync();
}
