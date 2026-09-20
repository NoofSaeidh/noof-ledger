using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Auth;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfUserStoreTests(PostgresFixture fixture)
{
    static AppUser NewUser(string username) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        PasswordHash = "hash",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Round_trips_a_user()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new EfUserStore(db);

        await store.UpsertAsync(NewUser("noof"), TestContext.Current.CancellationToken);
        var found = await store.FindByUsernameAsync("noof", TestContext.Current.CancellationToken);

        found.Should().NotBeNull();
        found!.Username.Should().Be("noof");
    }

    [Fact]
    public async Task Finds_a_user_regardless_of_case()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new EfUserStore(db);

        await store.UpsertAsync(NewUser("Noof"), TestContext.Current.CancellationToken);

        var found = await store.FindByUsernameAsync("nOOf", TestContext.Current.CancellationToken);

        found.Should().NotBeNull();
    }

    [Fact]
    public async Task A_second_user_differing_only_by_case_is_rejected_by_the_database()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new EfUserStore(db);

        await store.UpsertAsync(NewUser("noof"), TestContext.Current.CancellationToken);

        var act = async () => await store.UpsertAsync(NewUser("NOOF"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Upserting_an_existing_username_replaces_the_hash_instead_of_adding_a_row()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new EfUserStore(db);
        var user = NewUser("noof");

        await store.UpsertAsync(user, TestContext.Current.CancellationToken);
        user.PasswordHash = "replaced";
        await store.UpsertAsync(user, TestContext.Current.CancellationToken);

        var all = await db.Users.ToListAsync(TestContext.Current.CancellationToken);
        all.Should().ContainSingle();
        all[0].PasswordHash.Should().Be("replaced");
    }

    [Fact]
    public async Task An_unknown_username_returns_null()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new EfUserStore(db);

        var found = await store.FindByUsernameAsync("nobody", TestContext.Current.CancellationToken);

        found.Should().BeNull();
    }
}
