using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Host.Auth;

namespace Noof.Ledger.Host.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder routes) =>
        routes.MapPost("/account/login", async (
            [FromForm] string username,
            [FromForm] string password,
            HttpContext context,
            IUserStore users,
            IPasswordHasher hasher,
            CancellationToken cancellationToken) =>
        {
            var user = await users.FindByUsernameAsync(username, cancellationToken);

            if (user is null || hasher.Verify(user, user.PasswordHash, password) is PasswordVerifyResult.Failed)
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
        });
}
