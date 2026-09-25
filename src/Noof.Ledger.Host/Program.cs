using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Noof.Ledger.Ai;
using Noof.Ledger.Application;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Host.Auth;
using Noof.Ledger.Host.Cli;
using Noof.Ledger.Host.Diagnostics;
using Noof.Ledger.Host.Endpoints;
using Noof.Ledger.Host.Logging;
using Noof.Ledger.Host.Startup;
using Noof.Ledger.Host.Workers;
using Noof.Ledger.Persistence;
using Noof.Ledger.Telegram;
using Noof.Ledger.Web;
using Noof.Ledger.Web.Components;
using Serilog;

if (UserCommand.TryParse(args, out var cliUsername))
{
    Environment.ExitCode = await UserCommand.RunAsync(cliUsername, args);
    return;
}

var bootstrapConfiguration = LoggingSetup.BuildBootstrapConfiguration(args);
var logDirectory = LoggingSetup.ResolveLogDirectory(bootstrapConfiguration);
Log.Logger = LoggingSetup.CreateBootstrapLogger(logDirectory);

// Two-stage initialization (Serilog's own documented ASP.NET Core shape): the bootstrap logger
// above is live before anything else can fail, and this try/catch/finally is what makes
// "the host never exits on a startup failure without a trace of why" actually true. The `when`
// filter matters for tests: WebApplicationFactory stops a minimal-hosting Program by throwing
// HostAbortedException out of Build(), and that is not a real failure to log as fatal.
try
{
    Log.Information("Starting host; logging to {LogDirectory}", logDirectory);

    var builder = WebApplication.CreateBuilder(args);

    var ledgerConnectionString = LedgerConnectionString.Resolve(builder.Configuration.GetConnectionString("Ledger"));

    builder.Host.UseSerilog((_, services, loggerConfiguration) =>
        LoggingSetup.Configure(loggerConfiguration, logDirectory, ledgerConnectionString, services));

    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services.AddNoofWeb();

    builder.Services.AddNoofApplication();

    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(CaptureTimeZoneGuard.Resolve(
        builder.Configuration["Capture:TimeZone"] ?? "Europe/Belgrade"));

    var dataProtectionKeyRingDirectory = new DirectoryInfo(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "dp-keys"));
    DataProtectionSetup.Configure(builder.Services, dataProtectionKeyRingDirectory);

    builder.Services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();

    var categorizationOptions = new CategorizationWorkerOptions();
    builder.Configuration.GetSection("Categorization").Bind(categorizationOptions);

    var backupOptions = new BackupWorkerOptions();
    builder.Configuration.GetSection("Backup").Bind(backupOptions);

    builder.Services.AddNoofPersistence(builder.Configuration, categorizationOptions.MaxAttempts);

    builder.Services.AddNoofHostDiagnostics(ledgerConnectionString);

    builder.Services.AddNoofDatabaseGate();

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

    builder.Services.AddNoofWorkers(categorizationOptions, backupOptions);
    builder.Services.AddNoofDiagnosticsHost();

    builder.Services.AddNoofDiagnostics();

    var app = builder.Build();

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
    app.MapDiagnosticsEndpoints();

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
            app.Services.GetRequiredService<ILogger<Program>>().LoopbackGuardFailed(exposed.Message);
            Environment.ExitCode = 1;
            app.Lifetime.StopApplication();
        }
    });

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}

internal partial class Program;
