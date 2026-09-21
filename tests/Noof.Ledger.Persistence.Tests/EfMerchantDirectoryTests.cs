using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Noof.Ledger.Persistence.Categorization;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfMerchantDirectoryTests(PostgresFixture fixture)
{
    // A fresh Guid keeps every test's folded key unique against every other test's, and against
    // the fixed strings a future seed migration might add - a collision here would be a unique
    // constraint violation unrelated to what the test is actually checking.
    static string Folded(string seed) => $"TEST {seed} {Guid.NewGuid():N}".ToUpperInvariant();

    [Fact]
    public async Task LinkAliasAsync_creates_a_merchant_and_an_alias_keyed_on_the_folded_text()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var directory = new EfMerchantDirectory(db, time);
        var folded = Folded("LIDL");

        var merchantId = await directory.LinkAliasAsync(folded, "Lidl", TestContext.Current.CancellationToken);

        var merchant = await db.Merchants.AsNoTracking()
            .SingleAsync(m => m.Id == merchantId, TestContext.Current.CancellationToken);
        merchant.DisplayName.Should().Be("Lidl");
        var alias = await db.MerchantAliases.AsNoTracking()
            .SingleAsync(a => a.Folded == folded, TestContext.Current.CancellationToken);
        alias.MerchantId.Should().Be(merchantId);
        // BeCloseTo, not Be: Postgres timestamptz stores microsecond precision, DateTimeOffset.UtcNow()
        // ticks carry 100ns precision, and this test (unlike EfCaptureStoreTests' fixed literals) uses
        // the real clock - an exact equality check is flaky on whatever the sub-microsecond digit is.
        alias.CreatedAt.Should().BeCloseTo(time.GetUtcNow(), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task LinkAliasAsync_the_loser_of_a_race_returns_the_winners_id_and_leaves_no_orphaned_merchant()
    {
        await using var dbA = await fixture.CreateContextAsync();
        await dbA.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var optionsB = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(dbA.Database.GetConnectionString())
            .Options;
        await using var dbB = new LedgerDbContext(optionsB);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var directoryA = new EfMerchantDirectory(dbA, time);
        var directoryB = new EfMerchantDirectory(dbB, time);
        var folded = Folded("MAXI");

        await using var txA = await dbA.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var idA = await directoryA.LinkAliasAsync(folded, "Maxi (winner)", TestContext.Current.CancellationToken);

        var idBTask = directoryB.LinkAliasAsync(folded, "Maxi (loser)", TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(idBTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        finished.Should().NotBeSameAs(idBTask,
            "the loser's insert must block on the winner's uncommitted alias row, not race past it undetected");

        await txA.CommitAsync(TestContext.Current.CancellationToken);

        var idB = await idBTask;

        idB.Should().Be(idA, "the table decides identity - the loser must adopt the winner's merchant id, not mint its own");
        (await dbA.Merchants.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "merchant and alias were staged in one SaveChangesAsync call, so the loser's alias violation rolled its merchant insert back too - there is no orphan to clean up");
        var merchant = await dbA.Merchants.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        merchant.DisplayName.Should().Be("Maxi (winner)");
    }

    [Fact]
    public async Task Changing_an_existing_alias_fails_loudly_instead_of_silently_rewriting_history()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var folded = Folded("SPAR");
        await directory.LinkAliasAsync(folded, "Spar", TestContext.Current.CancellationToken);

        var act = async () => await db.Database.ExecuteSqlRawAsync(
            "UPDATE merchant_aliases SET merchant_id = @newMerchantId WHERE folded = @folded",
            [new NpgsqlParameter("newMerchantId", Guid.NewGuid()), new NpgsqlParameter("folded", folded)],
            TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<PostgresException>();
        assertion.WithMessage("*write-once*");
    }

    [Fact]
    public async Task LinkAliasAsync_rejects_a_folded_key_longer_than_256_characters()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var tooLong = new string('X', 257);

        var act = () => directory.LinkAliasAsync(tooLong, "Whatever", TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ArgumentException>();
        assertion.And.ParamName.Should().Be("folded");
    }

    [Fact]
    public async Task LinkAliasAsync_rejects_a_display_name_longer_than_256_characters()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var tooLong = new string('X', 257);

        var act = () => directory.LinkAliasAsync(Folded("OK"), tooLong, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ArgumentException>();
        assertion.And.ParamName.Should().Be("displayName");
    }

    [Fact]
    public async Task AliasesAsync_returns_every_alias_with_its_merchants_display_name()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var folded = Folded("IDEA");
        var merchantId = await directory.LinkAliasAsync(folded, "Idea", TestContext.Current.CancellationToken);

        var aliases = await directory.AliasesAsync(TestContext.Current.CancellationToken);

        var entry = aliases.Single(a => a.Folded == folded);
        entry.MerchantId.Should().Be(merchantId);
        entry.DisplayName.Should().Be("Idea");
    }

    [Fact]
    public async Task MerchantsAsync_returns_every_merchant_as_an_option()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var merchantId = await directory.LinkAliasAsync(Folded("DM"), "DM", TestContext.Current.CancellationToken);

        var merchants = await directory.MerchantsAsync(TestContext.Current.CancellationToken);

        merchants.Single(m => m.Id == merchantId).DisplayName.Should().Be("DM");
    }
}
