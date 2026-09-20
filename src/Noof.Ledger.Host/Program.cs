using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Host.Auth;
using Noof.Ledger.Host.Endpoints;
using Noof.Ledger.Host.Startup;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Auth;
using Noof.Ledger.Web.Components;

var builder = WebApplication.CreateBuilder(args);

var authMode = builder.Configuration["Auth:Mode"] ?? "Off";
var cookieMode = authMode.Equals("Cookie", StringComparison.OrdinalIgnoreCase);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContext<LedgerDbContext>(options =>
    options.UseNpgsql(LedgerConnectionString.Resolve(builder.Configuration.GetConnectionString("Ledger"))));

builder.Services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
builder.Services.AddScoped<IUserStore, EfUserStore>();

var authentication = builder.Services.AddAuthentication(
    cookieMode ? AuthSchemes.Cookie : AuthSchemes.LocalOwner);

if (cookieMode)
{
    authentication.AddCookie(AuthSchemes.Cookie, options =>
    {
        options.LoginPath = "/account/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(180);
        options.SlidingExpiration = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
}
else
{
    authentication.AddScheme<AuthenticationSchemeOptions, LocalOwnerHandler>(AuthSchemes.LocalOwner, null);
}

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAccountEndpoints();

app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var addresses = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses;

    try
    {
        LoopbackGuard.AssertSafe([.. addresses ?? []], authMode);
    }
    catch (InvalidOperationException exposed)
    {
        // Throwing out of an ApplicationStarted callback does NOT stop the host: the hosting layer
        // catches it, logs it, and Kestrel keeps serving. Verified. Shutting down explicitly is the
        // only thing that actually closes the socket.
        app.Services.GetRequiredService<ILogger<Program>>().LogCritical("{Message}", exposed.Message);
        Environment.ExitCode = 1;
        app.Lifetime.StopApplication();
    }
});

app.Run();

public partial class Program;
