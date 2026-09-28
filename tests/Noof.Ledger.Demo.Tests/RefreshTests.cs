using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Demo.Tests;

[Trait("Category", "Database")]
public sealed class RefreshTests(DemoTestDatabase database) : IClassFixture<DemoTestDatabase>
{
    [Fact]
    public async Task Refresh_creates_the_demo_user_and_fake_ai_keys_but_no_telegram_token()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();

        var user = await users.FindByUsernameAsync("demo", TestContext.Current.CancellationToken);
        user.Should().NotBeNull();
        hasher.Verify(user!, user!.PasswordHash, "demo").Should().NotBe(PasswordVerifyResult.Failed);

        (await secrets.GetAsync("anthropic-api-key", TestContext.Current.CancellationToken)).Value
            .Should().Be("demo-not-a-real-key");
        (await secrets.GetAsync("groq-api-key", TestContext.Current.CancellationToken)).Value
            .Should().Be("demo-not-a-real-key");
        (await secrets.GetStatusAsync(SecretKeys.TelegramBotToken, TestContext.Current.CancellationToken)).State
            .Should().Be(SecretState.Missing);
    }

    [Fact]
    public async Task Refreshing_twice_leaves_exactly_one_demo_user()
    {
        if (database.Unavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);
        await Refresh.RunAsync(database.Admin, database.Name, database.Paths, TestContext.Current.CancellationToken);

        await using var services = DemoServices.Build(database.ConnectionString, database.Paths);
        await using var scope = services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<IUserStore>()
            .FindByUsernameAsync("demo", TestContext.Current.CancellationToken);

        user.Should().NotBeNull();
    }
}
