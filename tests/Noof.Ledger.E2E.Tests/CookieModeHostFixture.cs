using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Host.Startup;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Secrets;
using Noof.Ledger.TestKit;
using Npgsql;

namespace Noof.Ledger.E2E.Tests;

public sealed class CookieModeHostFixture : IAsyncLifetime
{
    public const string Username = "e2e";
    public const string Password = "correct-horse-battery-staple";

    readonly HostProcess host = new();
    string cloneDatabaseName = string.Empty;

    public bool DatabaseUnavailable { get; private set; }

    public string BaseUrl => host.BaseUrl;

    public IReadOnlyList<string> CapturedOutputLines => host.CapturedOutputLines;

    // Lets a test seed rows directly into the same clone database the spawned host process reads
    // from - the dashboard has nothing to save through the UI itself, unlike the secrets page, so
    // this is the only way to get a categorised transaction in front of it without also building and
    // running the categorisation worker inside this test.
    public string ConnectionString => DatabaseSettings.For(cloneDatabaseName);

    // Reads back what the browser-driven save actually persisted, bypassing the UI (which by
    // design never shows a saved secret's plaintext -- see SecretsPageSourceTests). Talks to the
    // same clone database and the same DPAPI-protected key ring directory
    // (Program.cs: %LocalApplicationData%\NoofLedger\dp-keys) the spawned host process itself uses,
    // so decrypting a value the host encrypted moments earlier just works: DPAPI is scoped to the
    // current Windows user, not to a process, and both this test and the host run as that user.
    public async Task<string?> ReadStoredSecretAsync(string key, CancellationToken cancellationToken)
    {
        // DataProtectionSetup.Configure calls ProtectKeysWithDpapi, which is Windows-only -- this
        // whole suite (like the app it drives) already only ever runs on Windows, so this guard is
        // just what tells the platform-compatibility analyzer that, rather than a real fallback.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("This suite only runs on Windows, same as the app.");

        var contextOptions = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(DatabaseSettings.For(cloneDatabaseName))
            .Options;
        await using var db = new LedgerDbContext(contextOptions);

        var keyRingDirectory = new DirectoryInfo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "dp-keys"));
        var services = new ServiceCollection();
        DataProtectionSetup.Configure(services, keyRingDirectory);
        await using var dataProtectionServices = services.BuildServiceProvider();
        var dataProtection = dataProtectionServices.GetRequiredService<IDataProtectionProvider>();

        var store = new EfSecretStore(db, dataProtection, TimeProvider.System);
        var result = await store.GetAsync(key, cancellationToken);
        return result.Value;
    }

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        if (!await DatabaseIsReachableAsync(cancellationToken))
        {
            DatabaseUnavailable = true;
            return;
        }

        // If anything below fails partway - the clone was created but the host never started, say -
        // a leaked database is the same class of problem as a leaked process, so clean up on our own
        // rather than trusting the test runner to call DisposeAsync after a thrown InitializeAsync.
        try
        {
            var publishDirectory = await HostProcess.PublishHostAsync(cancellationToken);

            cloneDatabaseName = $"noof_e2e_{Guid.NewGuid():N}";
            await CreateCloneAsync(cloneDatabaseName, cancellationToken);

            var cloneConnectionString = DatabaseSettings.For(cloneDatabaseName);
            await SeedUserAsync(publishDirectory, cloneConnectionString, cancellationToken);

            await host.StartAsync(publishDirectory, new Dictionary<string, string>
            {
                ["Auth__Mode"] = "Cookie",
                ["Database__MigrateOnStartup"] = "false",
                ["ConnectionStrings__Ledger"] = cloneConnectionString,
            }, cancellationToken);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The host and the clone database are independent resources - a problem disposing one (a
        // slow-to-release file handle, say) must never skip cleanup of the other, which is exactly
        // the operator's real data non-negotiable this fixture exists to protect.
        try
        {
            await host.DisposeAsync();
        }
        finally
        {
            await DropCloneAsync();
        }
    }

    async Task DropCloneAsync()
    {
        if (cloneDatabaseName.Length is 0)
            return;

        NpgsqlConnection.ClearAllPools();

        await using var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
        await admin.OpenAsync(CancellationToken.None);
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{cloneDatabaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync(CancellationToken.None);
    }

    static async Task<bool> DatabaseIsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
            await connection.OpenAsync(cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static async Task CreateCloneAsync(string name, CancellationToken cancellationToken)
    {
        await using var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
        await admin.OpenAsync(cancellationToken);
        await using var create = new NpgsqlCommand(
            $"CREATE DATABASE \"{name}\" TEMPLATE {DatabaseSettings.TemplateDatabase}", admin);
        await create.ExecuteNonQueryAsync(cancellationToken);
    }

    static async Task SeedUserAsync(string publishDirectory, string connectionString, CancellationToken cancellationToken)
    {
        var dll = Path.Combine(publishDirectory, "Noof.Ledger.Host.dll");

        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { dll, "user", "set-password", Username },
            WorkingDirectory = publishDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["ConnectionStrings__Ledger"] = connectionString;

        using var seed = Process.Start(start) ?? throw new InvalidOperationException("Failed to start the seeding process.");

        // UserCommand.ReadPassword falls back to Console.ReadLine when stdin is redirected, which is
        // exactly this call.
        await seed.StandardInput.WriteLineAsync(Password);
        seed.StandardInput.Close();

        var stdoutTask = seed.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = seed.StandardError.ReadToEndAsync(cancellationToken);
        await seed.WaitForExitAsync(cancellationToken);

        if (seed.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Seeding the e2e user failed with exit code {seed.ExitCode}.\n{await stdoutTask}\n{await stderrTask}");
        }
    }
}
