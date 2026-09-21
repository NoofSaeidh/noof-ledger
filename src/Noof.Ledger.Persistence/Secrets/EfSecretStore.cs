using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Persistence.Secrets;

public sealed class EfSecretStore(LedgerDbContext db, IDataProtectionProvider dataProtection, TimeProvider timeProvider)
    : ISecretStore
{
    public async Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken)
    {
        var row = await db.Secrets.FindAsync([key], cancellationToken);
        if (row is null)
            return new SecretResult(SecretState.Missing, null);

        try
        {
            var plaintext = Protector(key).Unprotect(row.Ciphertext);
            return new SecretResult(SecretState.Present, plaintext);
        }
        catch (CryptographicException)
        {
            return new SecretResult(SecretState.Unreadable, null);
        }
    }

    public async Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken)
    {
        var row = await db.Secrets.FindAsync([key], cancellationToken);
        if (row is null)
            return new SecretStatus(SecretState.Missing, null);

        try
        {
            // The plaintext is discarded on purpose: this method exists so the UI can learn the
            // state without ever being handed the value.
            _ = Protector(key).Unprotect(row.Ciphertext);
            return new SecretStatus(SecretState.Present, row.UpdatedAt);
        }
        catch (CryptographicException)
        {
            return new SecretStatus(SecretState.Unreadable, row.UpdatedAt);
        }
    }

    public async Task SetAsync(string key, string plaintext, CancellationToken cancellationToken)
    {
        var ciphertext = Protector(key).Protect(plaintext);
        var now = timeProvider.GetUtcNow();

        var existing = await db.Secrets.FindAsync([key], cancellationToken);
        if (existing is null)
        {
            db.Secrets.Add(new AppSecret { Key = key, Ciphertext = ciphertext, UpdatedAt = now });
        }
        else
        {
            existing.Ciphertext = ciphertext;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    IDataProtector Protector(string key) => dataProtection.CreateProtector($"Noof.Ledger.Secrets.{key}");
}
