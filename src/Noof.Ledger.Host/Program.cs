using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Noof.Ledger.Ai;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Host.Auth;
using Noof.Ledger.Host.Cli;
using Noof.Ledger.Host.Endpoints;
using Noof.Ledger.Host.Startup;
using Noof.Ledger.Host.Workers;
using Noof.Ledger.Persistence;
using Noof.Ledger.Telegram;
using Noof.Ledger.Web.Components;

if (UserCommand.TryParse(args, out var cliUsername))
{
    Environment.ExitCode = await UserCommand.RunAsync(cliUsername, args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(CaptureTimeZoneGuard.Resolve(
    builder.Configuration["Capture:TimeZone"] ?? "Europe/Belgrade"));

var dataProtectionKeyRingDirectory = new DirectoryInfo(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "dp-keys"));
DataProtectionSetup.Configure(builder.Services, dataProtectionKeyRingDirectory);

builder.Services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();

var categorizationOptions = new CategorizationWorkerOptions();
builder.Configuration.GetSection("Categorization").Bind(categorizationOptions);

builder.Services.AddNoofPersistence(builder.Configuration, categorizationOptions.MaxAttempts);

builder.Services.AddAuthentication(AuthSchemes.Cookie)
    .AddCookie(AuthSchemes.Cookie, options =>
    {
        options.LoginPath = "/account/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(180);
        options.SlidingExpiration = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

// Deny by default. Every page declares [Authorize] and an architecture test holds it to that, but
// nothing holds a minimal-API endpoint to it - a MapGet added in a later phase would answer anyone
// who can reach the port. With a fallback policy the omission costs a redirect to the login page
// instead of handing out the ledger, so the guarantee survives code nobody has written yet.
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddNoofTelegram();

builder.Services.AddNoofAi(builder.Configuration);

builder.Services.AddNoofWorkers(categorizationOptions);

var app = builder.Build();

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
    // ApplicationStopping, not CancellationToken.None: a Ctrl+C during startup migration should
    // cancel the in-flight MigrateAsync rather than let it run unattended. EF applies each
    // migration inside its own transaction, so a cancel here rolls back the migration in
    // progress and leaves the schema at its last fully-applied version - never a half-applied
    // one - which Migrate() can safely retry on the next start.
    await app.Services.MigrateNoofDatabaseAsync(app.Lifetime.ApplicationStopping);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Anonymous, or the fallback policy above redirects the sign-in page's own stylesheet and Blazor
// script to the sign-in page. The page would still render - unstyled and inert - to the one visitor
// who cannot sign in to report it.
app.MapStaticAssets().AllowAnonymous();
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
        LoopbackGuard.AssertSafe([.. addresses ?? []]);
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

internal partial class Program;
