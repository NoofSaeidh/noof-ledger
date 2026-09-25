using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Host.Diagnostics;

// The known-secret set SecretRedactor checks every property and exception message against.
// Built from every key the app already has a name for (SecretKeys constants plus every
// registered ISecretProbe's SecretKey - the same set Secrets.razor lists), never from
// enumerating the store itself, because ISecretStore has no "list everything" method by design.
//
// ISecretProbe is resolved fresh from a scope inside RefreshAsync, exactly like ISecretStore,
// rather than taken as a constructor parameter and enumerated once at registration time. The
// registered probes pull in IHttpClientFactory and ISecretStore themselves; constructing them
// from the special, short-lived IServiceProvider that Serilog's UseSerilog builds to configure
// the logger mid-`WebApplicationBuilder.Build()` - before the real DI container exists - hung the
// host indefinitely. Fetching them lazily, only when the background refresh worker actually calls
// RefreshAsync against the real root IServiceScopeFactory, keeps LoggingSetup.Configure itself
// free of any scoped resolution.
internal sealed class SecretSnapshot(IServiceScopeFactory scopeFactory, string databasePassword)
    : ISecretValueSource
{
    const int MinimumSecretLength = 8;

    volatile IReadOnlyCollection<string> current = databasePassword.Length >= MinimumSecretLength
        ? [databasePassword]
        : [];

    public IReadOnlyCollection<string> CurrentValues => current;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        var probes = scope.ServiceProvider.GetServices<ISecretProbe>();

        string[] keys = [SecretKeys.TelegramBotToken, SecretKeys.TelegramOwnerChatId, .. probes.Select(probe => probe.SecretKey)];

        var values = new List<string>();
        if (databasePassword.Length >= MinimumSecretLength)
            values.Add(databasePassword);

        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            var result = await store.GetAsync(key, cancellationToken);
            if (result is { State: SecretState.Present, Value.Length: >= MinimumSecretLength })
                values.Add(result.Value);
        }

        current = values;
    }
}
