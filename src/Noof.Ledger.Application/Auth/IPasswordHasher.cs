using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Auth;

public interface IPasswordHasher
{
    string Hash(AppUser user, string password);

    PasswordVerifyResult Verify(AppUser user, string hash, string password);
}
