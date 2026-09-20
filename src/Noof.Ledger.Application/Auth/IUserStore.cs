using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Auth;

public interface IUserStore
{
    Task<AppUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken);

    Task UpsertAsync(AppUser user, CancellationToken cancellationToken);
}
