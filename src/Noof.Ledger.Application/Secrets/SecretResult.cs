namespace Noof.Ledger.Application.Secrets;

public readonly record struct SecretResult(SecretState State, string? Value);
