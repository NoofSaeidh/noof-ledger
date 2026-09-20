using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Auth;

public sealed class EfUserStore(LedgerDbContext db) : IUserStore
{
    public Task<AppUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), cancellationToken);

    public async Task UpsertAsync(AppUser user, CancellationToken cancellationToken)
    {
        var existing = await FindByUsernameAsync(user.Username, cancellationToken);

        if (existing is null)
            db.Users.Add(user);
        else
            existing.PasswordHash = user.PasswordHash;

        await db.SaveChangesAsync(cancellationToken);
    }
}
