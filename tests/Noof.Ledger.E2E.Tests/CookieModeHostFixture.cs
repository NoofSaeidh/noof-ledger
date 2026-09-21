using System.Diagnostics;
using Npgsql;
using Noof.Ledger.TestKit;

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
