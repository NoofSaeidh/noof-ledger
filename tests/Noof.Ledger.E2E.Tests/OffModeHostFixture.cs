namespace Noof.Ledger.E2E.Tests;

public sealed class OffModeHostFixture : IAsyncLifetime
{
    readonly HostProcess host = new();

    public string BaseUrl => host.BaseUrl;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var publishDirectory = await HostProcess.PublishHostAsync(cancellationToken);

        await host.StartAsync(publishDirectory, new Dictionary<string, string>
        {
            ["Auth__Mode"] = "Off",
            ["Database__MigrateOnStartup"] = "false",
            ["ConnectionStrings__Ledger"] = "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2",
        }, cancellationToken);
    }

    public ValueTask DisposeAsync() => host.DisposeAsync();
}
