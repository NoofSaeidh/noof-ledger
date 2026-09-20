using System.Text;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Auth;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Auth;

namespace Noof.Ledger.Host.Cli;

public static class UserCommand
{
    public static bool TryParse(string[] args, out string username)
    {
        if (args is ["user", "set-password", var name, ..] && !string.IsNullOrWhiteSpace(name))
        {
            username = name;
            return true;
        }

        username = string.Empty;
        return false;
    }

    public static async Task<int> RunAsync(string username, string[] args)
    {
        // Unqualified "Host" resolves to our own Noof.Ledger.Host namespace here, not
        // Microsoft.Extensions.Hosting.Host, because this file sits inside that namespace.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

        string connectionString;
        try
        {
            connectionString = LedgerConnectionString.Resolve(builder.Configuration.GetConnectionString("Ledger"));
        }
        catch (InvalidOperationException exposed)
        {
            Console.WriteLine(exposed.Message);
            return 1;
        }

        builder.Services.AddDbContext<LedgerDbContext>(options => options.UseNpgsql(connectionString));
        builder.Services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        builder.Services.AddScoped<IUserStore, EfUserStore>();

        var password = ReadPassword();
        if (string.IsNullOrEmpty(password))
        {
            Console.WriteLine("A password is required.");
            return 1;
        }

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();

        var user = new AppUser
        {
            Id = Guid.CreateVersion7(),
            Username = username,
            PasswordHash = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        user.PasswordHash = hasher.Hash(user, password);

        await userStore.UpsertAsync(user, CancellationToken.None);

        Console.WriteLine($"Password set for '{username}'.");
        return 0;
    }

    static string ReadPassword()
    {
        // Console.ReadKey throws InvalidOperationException when stdin is redirected (Task 14 seeds
        // its test user this way, from a child process with redirected stdin), so this is not
        // optional defensive code — it is the only path that works under a pipe.
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? string.Empty;

        var password = new StringBuilder();
        ConsoleKeyInfo key;

        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write('*');
            }
        }

        Console.WriteLine();
        return password.ToString();
    }
}
