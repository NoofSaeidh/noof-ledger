using AwesomeAssertions;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Cli;

namespace Noof.Ledger.Host.Tests;

public class UserCommandCancellationTests
{
    [Fact]
    public async Task Passes_the_given_token_through_to_the_store()
    {
        var store = new RecordingUserStore();
        using var cancellation = new CancellationTokenSource();

        var exitCode = await UserCommand.UpsertPasswordAsync(store, CreateUser(), "noof", cancellation.Token);

        exitCode.Should().Be(0);
        store.ReceivedToken.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task Reports_cancellation_cleanly_instead_of_letting_it_escape()
    {
        var exitCode = await UserCommand.UpsertPasswordAsync(
            new ThrowsOperationCanceledUserStore(), CreateUser(), "noof", CancellationToken.None);

        exitCode.Should().Be(130, "a graceful Ctrl+C during the upsert must not crash the process");
    }

    static AppUser CreateUser() => new()
    {
        Id = Guid.NewGuid(),
        Username = "noof",
        PasswordHash = "hash",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    sealed class RecordingUserStore : IUserStore
    {
        public CancellationToken ReceivedToken { get; private set; }

        public Task<AppUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by this test.");

        public Task UpsertAsync(AppUser user, CancellationToken cancellationToken)
        {
            ReceivedToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    sealed class ThrowsOperationCanceledUserStore : IUserStore
    {
        public Task<AppUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by this test.");

        public Task UpsertAsync(AppUser user, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }
}
