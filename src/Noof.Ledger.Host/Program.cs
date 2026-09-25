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

    // Task 11: the content root must never depend on the process's current directory. Before
    // this, `dotnet Noof.Ledger.Host.dll` launched from anywhere but its own install directory (or
    // `dotnet run` from a directory other than the project's own) resolved every static asset
    // against the wrong folder - MapStaticAssets then answered every request 200 with an empty
    // body instead of 404, so the page rendered unstyled with no Blazor script and nothing logged.
    // AppContext.BaseDirectory is the directory the running assembly actually lives in, which is
    // stable regardless of the caller's working directory.
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    // Static web assets (the mapping the fingerprinted `Assets[...]` links in App.razor resolve
    // through) are wired in automatically only for the Development environment. `run.ps1 start`
    // runs Production from source (no publish step), so this call is what keeps that path from
    // exhibiting the same empty-body bug as an unpublished, wrongly-launched exe. It is a no-op
    // against published output, which carries its assets in wwwroot instead of this manifest.
    builder.WebHost.UseStaticWebAssets();

    var ledgerConnectionString = LedgerConnectionString.Resolve(builder.Configuration.GetConnectionString("Ledger"));

    builder.Host.UseSerilog((context, services, loggerConfiguration) =>
        LoggingSetup.Configure(loggerConfiguration, context.Configuration, logDirectory, ledgerConnectionString, services));

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

    // M-2 (Phase 5 final review): DatabaseStartupService (registered here) must be the first
    // hosted service - every other worker awaits its gate, and AddNoofHostDiagnostics registers one
    // (SecretSnapshotRefreshWorker).
    builder.Services.AddNoofDatabaseGate();

    builder.Services.AddNoofHostDiagnostics(ledgerConnectionString);

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
