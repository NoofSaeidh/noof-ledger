using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai.Tests;

// Stands in for the encrypted app_secret table: every key is in the one state it was built with.
// Read-only, and it records which keys were asked for, so a test can pin the exact secret a
// provider reads as well as whether it ever read the plaintext at all.
public sealed class StubSecretStore(SecretState state, string? value = null) : ISecretStore
{
    public Exception? FailReadsWith { get; init; }

    public List<string> PlaintextReads { get; } = [];

    public List<string> StatusReads { get; } = [];

    public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken)
    {
        PlaintextReads.Add(key);
        return FailReadsWith is { } exception
            ? Task.FromException<SecretResult>(exception)
            : Task.FromResult(new SecretResult(state, value));
    }

    public Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken)
    {
        StatusReads.Add(key);
        return Task.FromResult(new SecretStatus(state, state is SecretState.Present ? DateTimeOffset.UnixEpoch : null));
    }

    public Task SetAsync(string key, string plaintext, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This stub is read-only.");

    public Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This stub is read-only.");
}
