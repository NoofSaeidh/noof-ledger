using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Auth;

namespace Noof.Ledger.Host.Tests;

public sealed class FakeUserStore : IUserStore
{
    static readonly AppUser Noof = CreateNoofUser();

    public Task<AppUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken) =>
        Task.FromResult(username.Equals(Noof.Username, StringComparison.OrdinalIgnoreCase) ? Noof : null);

    public Task UpsertAsync(AppUser user, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The login endpoint under test never upserts.");

    public static void Register(IServiceCollection services) =>
        services.AddScoped<IUserStore, FakeUserStore>();

    static AppUser CreateNoofUser()
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = "noof",
            PasswordHash = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        user.PasswordHash = new PasswordHasherAdapter().Hash(user, "correct");
        return user;
    }
}
