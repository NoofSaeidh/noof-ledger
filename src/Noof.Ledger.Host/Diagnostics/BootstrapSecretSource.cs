namespace Noof.Ledger.Host.Diagnostics;

// The one secret a bootstrap logger can redact before the DI container - and therefore
// SecretSnapshot's full probe-driven set - exists: the database password, already known from the
// same connection string Program.cs resolves to build the bootstrap logger itself.
internal sealed class BootstrapSecretSource(string databasePassword) : ISecretValueSource
{
    public IReadOnlyCollection<string> CurrentValues { get; } =
        databasePassword.Length >= SecretSnapshot.MinimumSecretLength ? [databasePassword] : [];
}
