using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Noof.Ledger.Ai;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Application.Chat;
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

var authMode = builder.Configuration["Auth:Mode"] ?? "Off";
var cookieMode = authMode.Equals("Cookie", StringComparison.OrdinalIgnoreCase);

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
builder.Services.AddSingleton(categorizationOptions);

builder.Services.AddNoofPersistence(builder.Configuration, categorizationOptions.MaxAttempts);

var authentication = builder.Services.AddAuthentication(
    cookieMode ? AuthSchemes.Cookie : AuthSchemes.LocalOwner);

authentication.AddCookie(AuthSchemes.Cookie, options =>
{
    options.LoginPath = "/account/login";
    options.ExpireTimeSpan = TimeSpan.FromDays(180);
    options.SlidingExpiration = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// LocalOwnerHandler authenticates every request as the owner with no credential check, so it must
// not exist at all in Cookie mode; the cookie scheme, by contrast, is harmless whenever it isn't
// the default, so it can stay registered in both modes.
if (!cookieMode)
    authentication.AddScheme<AuthenticationSchemeOptions, LocalOwnerHandler>(AuthSchemes.LocalOwner, null);

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// A log-level filter is a suppression a more specific configured category can override at
// runtime - Logging:LogLevel:System.Net.Http.HttpClient.telegram.LogicalHandler beats a filter on
// the shorter prefix and puts the full request URI, bot token included, at Information. Removing
// the logging handlers from the pipeline instead means there is nothing left to re-enable.
builder.Services.AddHttpClient("telegram").RemoveAllLoggers();

builder.Services.AddSingleton<TelegramClientHandle>();
builder.Services.AddSingleton<ITelegramBotClientFactory, TelegramBotClientFactory>();
builder.Services.AddSingleton<IChatNotifier, TelegramChatNotifier>();
builder.Services.AddScoped<TelegramOwnerGate>();
builder.Services.AddScoped<TelegramUpdateOffsetStore>();
builder.Services.AddScoped<ITelegramUpdateRouter, TelegramUpdateRouter>();
builder.Services.AddHostedService<TelegramPollingService>();

builder.Services.AddNoofAi(builder.Configuration);

builder.Services.AddHostedService(sp => new CategorizationWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<CategorizationWorkerOptions>(),
    CategorizationWorker.CreateWorkerId(),
    sp.GetRequiredService<ILogger<CategorizationWorker>>()));

var app = builder.Build();

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
    await app.Services.MigrateNoofDatabaseAsync();

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
