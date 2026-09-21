namespace Noof.Ledger.Application.Secrets;

public interface ISecretStore
{
    Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken);

    // Reports state and age without ever returning the secret. Decrypts internally and discards the
    // result, so a lost key ring is reported as Unreadable rather than as a healthy Present.
    Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, string plaintext, CancellationToken cancellationToken);

    // Inserts only if the key is still Missing; returns false without writing anything if another
    // call already claimed it first, however close the race. Exists for callers where "first
    // writer wins" is the correct outcome, unlike SetAsync's plain upsert.
    Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken);
}
