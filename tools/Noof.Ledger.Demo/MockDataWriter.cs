using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Backup;

namespace Noof.Ledger.Demo;

internal static class MockDataWriter
{
    public static async Task WriteAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await WriteUserAsync(services, cancellationToken);
        await WriteSecretsAsync(services.GetRequiredService<ISecretStore>(), cancellationToken);
        await WriteBackupRunAsync(services.GetRequiredService<LedgerDbContext>(), cancellationToken);
    }

    static async Task WriteUserAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var user = new AppUser
        {
            Id = MockData.UserId,
            Username = MockData.Username,
            PasswordHash = string.Empty,
            CreatedAt = MockData.Now,
        };
        user.PasswordHash = services.GetRequiredService<IPasswordHasher>().Hash(user, MockData.Password);

        await services.GetRequiredService<IUserStore>().UpsertAsync(user, cancellationToken);
    }

    static async Task WriteSecretsAsync(ISecretStore secrets, CancellationToken cancellationToken)
    {
        await secrets.SetAsync(MockData.AnthropicKeySecret, MockData.FakeKey, cancellationToken);
        await secrets.SetAsync(MockData.GroqKeySecret, MockData.FakeKey, cancellationToken);
    }

    static async Task WriteBackupRunAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        // The real clock, not MockData.Now: the Backups health check measures a backup's age against the
        // host's own clock, and anything older than 26 hours reads as stale.
        var finishedAt = DateTimeOffset.UtcNow.AddHours(-2);

        db.BackupRuns.Add(new BackupRun
        {
            Id = Guid.Parse("7a1c0000-0000-4000-8000-0000000000bb"),
            StartedAt = finishedAt.AddMinutes(-1),
            FinishedAt = finishedAt,
            Succeeded = true,
            FileName = "noof_ledger_demo-mock.dump",
            SizeBytes = 1_048_576,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
