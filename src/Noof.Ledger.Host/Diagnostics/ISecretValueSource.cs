namespace Noof.Ledger.Host.Diagnostics;

// The seam SecretRedactor depends on instead of SecretSnapshot directly, so a unit test can hand
// it a fixed set of values with no ISecretStore, no database and no clock.
internal interface ISecretValueSource
{
    IReadOnlyCollection<string> CurrentValues { get; }
}
