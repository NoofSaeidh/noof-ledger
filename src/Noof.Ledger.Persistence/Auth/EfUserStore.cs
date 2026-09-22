using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Auth;

internal sealed class EfUserStore(LedgerDbContext db) : IUserStore
{
    // CA1862 asks for the StringComparison overload here and is wrong: this predicate is an
    // expression tree EF translates to SQL, and EF Core cannot translate string.Equals with a
    // StringComparison - it throws "Translation of the 'string.Equals' overload with a
    // 'StringComparison' parameter is not supported" at the first query. Verified by applying the
    // suggestion and watching six EfUserStoreTests fail against a real database. ToLower() on both
    // sides is what reaches PostgreSQL as lower(x) = lower(y).
    [SuppressMessage("Performance", "CA1862",
        Justification = "EF Core cannot translate a StringComparison overload into SQL; see the comment above.")]
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
