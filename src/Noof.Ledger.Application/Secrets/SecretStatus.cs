namespace Noof.Ledger.Application.Secrets;

// Deliberately carries no value. See ISecretStore.GetStatusAsync.
public readonly record struct SecretStatus(SecretState State, DateTimeOffset? UpdatedAt);
