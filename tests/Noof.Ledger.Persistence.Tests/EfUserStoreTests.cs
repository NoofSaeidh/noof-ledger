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
    public async Task Upserting_a_case_variant_updates_the_existing_user_rather_than_adding_one()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new EfUserStore(db);

        await store.UpsertAsync(NewUser("noof"), TestContext.Current.CancellationToken);
        var variant = NewUser("NOOF");
        variant.PasswordHash = "rotated";
        await store.UpsertAsync(variant, TestContext.Current.CancellationToken);

        var all = await db.Users.ToListAsync(TestContext.Current.CancellationToken);
        all.Should().ContainSingle("usernames are case-insensitive, so NOOF and noof are one account");
        all[0].PasswordHash.Should().Be("rotated");
    }

    [Fact]
    public async Task The_database_itself_rejects_a_duplicate_username_differing_only_by_case()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        db.Users.Add(NewUser("noof"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Users.Add(NewUser("NOOF"));
        var act = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>(
            "the lower(username) unique index is the backstop even if a caller bypasses the store");
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
    public async Task Setting_the_password_a_second_time_updates_the_same_user()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new EfUserStore(db);

        // The CLI builds a fresh AppUser with a new Guid on every `user set-password` run,
        // so an upsert keyed on Id would insert a duplicate and hit the unique index here.
        await store.UpsertAsync(NewUser("noof"), TestContext.Current.CancellationToken);
        var second = NewUser("noof");
        second.PasswordHash = "rotated";
        await store.UpsertAsync(second, TestContext.Current.CancellationToken);

        var all = await db.Users.ToListAsync(TestContext.Current.CancellationToken);
        all.Should().ContainSingle();
        all[0].PasswordHash.Should().Be("rotated");
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
