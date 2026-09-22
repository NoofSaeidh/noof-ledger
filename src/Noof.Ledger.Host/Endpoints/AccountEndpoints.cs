using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Auth;

namespace Noof.Ledger.Host.Endpoints;

internal static class AccountEndpoints
{
    static readonly AppUser ThrowawayUser = new()
    {
        Id = Guid.Empty,
        Username = string.Empty,
        PasswordHash = string.Empty,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    static readonly string ThrowawayHash = new PasswordHasherAdapter().Hash(ThrowawayUser, Guid.NewGuid().ToString());

    public static void MapAccountEndpoints(this IEndpointRouteBuilder routes) =>
        // [FromForm] is what engages antiforgery validation here; it no longer also disambiguates
        // the route from the Blazor page (that's /account/login/submit's job now), so dropping it
        // would silently return 200 with no CSRF check instead of a routing error.
        routes.MapPost("/account/login/submit", async (
            [FromForm] string username,
            [FromForm] string password,
            HttpContext context,
            IUserStore users,
            IPasswordHasher hasher,
            CancellationToken cancellationToken) =>
        {
            var user = await users.FindByUsernameAsync(username, cancellationToken);

            // Always pay the PBKDF2 cost, even for an unknown username, so a missing account and a
            // wrong password are not distinguishable by response latency (Cookie mode is reachable
            // over the network, per LoopbackGuard).
            var verifyResult = hasher.Verify(user ?? ThrowawayUser, user?.PasswordHash ?? ThrowawayHash, password);

            if (user is null || verifyResult is PasswordVerifyResult.Failed)
                return Results.Redirect("/account/login?failed=1");

            Claim[] claims =
            [
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.Username),
            ];

            await context.SignInAsync(
                AuthSchemes.Cookie,
                new ClaimsPrincipal(new ClaimsIdentity(claims, AuthSchemes.Cookie)),
                new AuthenticationProperties { IsPersistent = true });

            return Results.Redirect("/");
        }).AllowAnonymous();
}
