using Microsoft.AspNetCore.Identity;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Auth;

public sealed class PasswordHasherAdapter : IPasswordHasher
{
    readonly PasswordHasher<AppUser> hasher = new();

    public string Hash(AppUser user, string password) => hasher.HashPassword(user, password);

    public PasswordVerifyResult Verify(AppUser user, string hash, string password)
    {
        try
        {
            return hasher.VerifyHashedPassword(user, hash, password) switch
            {
                PasswordVerificationResult.Success => PasswordVerifyResult.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordVerifyResult.SuccessRehashNeeded,
                _ => PasswordVerifyResult.Failed,
            };
        }
        catch (FormatException)
        {
            // A corrupted or hand-edited password_hash column is not valid base64;
            // treat it as a failed login instead of a 500 on the login page.
            return PasswordVerifyResult.Failed;
        }
    }
}
