using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Secrets;
using Npgsql;

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

    public async Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken)
    {
        var existing = await db.Secrets.FindAsync([key], cancellationToken);
        if (existing is not null)
            return false;

        var secret = new AppSecret
        {
            Key = key,
            Ciphertext = Protector(key).Protect(plaintext),
            UpdatedAt = timeProvider.GetUtcNow(),
        };
        db.Secrets.Add(secret);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsAppSecretPrimaryKeyViolation(ex))
        {
            // Another call won the race between our FindAsync and this SaveChangesAsync. That row
            // is real; ours never committed. Detach it so a later read goes back to the database
            // instead of returning this uncommitted, never-persisted entity from the identity map.
            db.Entry(secret).State = EntityState.Detached;
            return false;
        }
    }

    static bool IsAppSecretPrimaryKeyViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_app_secret" };

    IDataProtector Protector(string key) => dataProtection.CreateProtector($"Noof.Ledger.Secrets.{key}");
}
