using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Noof.Ledger.Domain;
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var dbA = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var tooLong = new string('X', 257);

        var act = () => directory.LinkAliasAsync(tooLong, "Whatever", TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ArgumentException>();
        assertion.And.ParamName.Should().Be("folded");
    }

    [Fact]
    public async Task LinkAliasAsync_rejects_a_display_name_longer_than_256_characters()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var tooLong = new string('X', 257);

        var act = () => directory.LinkAliasAsync(Folded("OK"), tooLong, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ArgumentException>();
        assertion.And.ParamName.Should().Be("displayName");
    }

    [Fact]
    public async Task AliasesAsync_returns_every_alias_with_its_merchants_display_name()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var folded = Folded("IDEA");
        var merchantId = await directory.LinkAliasAsync(folded, "Idea", TestContext.Current.CancellationToken);

        var aliases = await directory.AliasesAsync(TestContext.Current.CancellationToken);

        var entry = aliases.Single(a => a.Folded == folded);
        entry.MerchantId.Should().Be(merchantId);
        entry.DisplayName.Should().Be("Idea");
    }

    [Fact]
    public async Task LinkAliasAsync_a_new_spelling_of_a_known_merchants_display_name_attaches_to_the_existing_merchant()
    {
        // The ChatCategorizer prompt tells the model to answer canonicalization with "THAT
        // existing display name exactly" when it recognises the merchant under a new spelling
        // (e.g. "МАКСИ" for a merchant already known as "MAXI"). The folded alias key differs, so
        // the race-loser path in LinkAliasAsync never fires - reusing the merchant has to be a
        // deliberate lookup on display name, not a side effect of the alias PK collision.
        await using var db = await fixture.CreateMigratedContextAsync();
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var displayName = $"Maxi {Guid.NewGuid():N}";
        var firstFolded = Folded("MAXI-LATIN");
        var secondFolded = Folded("MAXI-CYRILLIC");

        var firstId = await directory.LinkAliasAsync(firstFolded, displayName, TestContext.Current.CancellationToken);
        var secondId = await directory.LinkAliasAsync(secondFolded, displayName, TestContext.Current.CancellationToken);

        secondId.Should().Be(firstId, "a second spelling of an already-known merchant must attach to the existing merchant, not mint a duplicate");
        (await db.Merchants.CountAsync(m => m.DisplayName == displayName, TestContext.Current.CancellationToken)).Should().Be(1);
        var secondAlias = await db.MerchantAliases.AsNoTracking()
            .SingleAsync(a => a.Folded == secondFolded, TestContext.Current.CancellationToken);
        secondAlias.MerchantId.Should().Be(firstId);
    }

    [Fact]
    public async Task MerchantsAsync_returns_every_merchant_as_an_option()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var merchantId = await directory.LinkAliasAsync(Folded("DM"), "DM", TestContext.Current.CancellationToken);

        var merchants = await directory.MerchantsAsync(TestContext.Current.CancellationToken);

        merchants.Single(m => m.Id == merchantId).DisplayName.Should().Be("DM");
    }

    [Fact]
    public async Task FindByTaxIdAsync_returns_null_when_no_merchant_carries_the_pib()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());

        var found = await directory.FindByTaxIdAsync($"P{Guid.NewGuid():N}"[..20], TestContext.Current.CancellationToken);

        found.Should().BeNull();
    }

    [Fact]
    public async Task LinkTaxIdAsync_sets_the_pib_once_and_FindByTaxIdAsync_then_resolves_it()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var merchantId = await directory.LinkAliasAsync(Folded("PEKARA"), "Pekara", TestContext.Current.CancellationToken);
        var taxId = $"P{Guid.NewGuid():N}"[..20];

        await directory.LinkTaxIdAsync(merchantId, taxId, TestContext.Current.CancellationToken);

        (await directory.FindByTaxIdAsync(taxId, TestContext.Current.CancellationToken)).Should().Be(merchantId);
    }

    [Fact]
    public async Task LinkTaxIdAsync_is_write_once_a_second_call_with_a_different_pib_is_ignored()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var merchantId = await directory.LinkAliasAsync(Folded("PEKARA2"), "Pekara 2", TestContext.Current.CancellationToken);
        var firstTaxId = $"P{Guid.NewGuid():N}"[..20];
        var secondTaxId = $"P{Guid.NewGuid():N}"[..20];
        await directory.LinkTaxIdAsync(merchantId, firstTaxId, TestContext.Current.CancellationToken);

        await directory.LinkTaxIdAsync(merchantId, secondTaxId, TestContext.Current.CancellationToken);

        (await directory.FindByTaxIdAsync(firstTaxId, TestContext.Current.CancellationToken)).Should().Be(merchantId);
        (await directory.FindByTaxIdAsync(secondTaxId, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task LinkTaxIdAsync_when_another_merchant_already_claimed_the_pib_leaves_ours_unset()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var firstMerchant = await directory.LinkAliasAsync(Folded("A"), "Shop A", TestContext.Current.CancellationToken);
        var secondMerchant = await directory.LinkAliasAsync(Folded("B"), "Shop B", TestContext.Current.CancellationToken);
        var taxId = $"P{Guid.NewGuid():N}"[..20];
        await directory.LinkTaxIdAsync(firstMerchant, taxId, TestContext.Current.CancellationToken);

        await directory.LinkTaxIdAsync(secondMerchant, taxId, TestContext.Current.CancellationToken);

        (await directory.FindByTaxIdAsync(taxId, TestContext.Current.CancellationToken)).Should().Be(firstMerchant,
            "the first merchant to claim a PIB keeps it; a later claim by another merchant is dropped, not fought over");
    }

    [Fact]
    public async Task VenueForTaxIdAsync_creates_an_exchange_venue_carrying_the_PIB()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());

        var venueId = await directory.VenueForTaxIdAsync("123456789", "Menjačnica Zlatnik", TestContext.Current.CancellationToken);

        var venue = await db.Merchants.AsNoTracking().SingleAsync(m => m.Id == venueId, TestContext.Current.CancellationToken);
        venue.Kind.Should().Be(MerchantKind.ExchangeVenue);
        venue.TaxId.Should().Be("123456789");
        venue.DisplayName.Should().Be("Menjačnica Zlatnik");
    }

    // A-9: one PIB is one legal entity - a shop that also runs an exchange desk stays the one merchant.
    [Fact]
    public async Task VenueForTaxIdAsync_returns_the_shop_that_already_carries_the_PIB_whatever_its_kind()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var shop = new Merchant { Id = Guid.NewGuid(), DisplayName = "Maxi", Kind = MerchantKind.Retail, TaxId = "123456789" };
        db.Merchants.Add(shop);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var merchantsBefore = await db.Merchants.CountAsync(TestContext.Current.CancellationToken);
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());

        var venueId = await directory.VenueForTaxIdAsync("123456789", "Menjačnica Maxi", TestContext.Current.CancellationToken);

        venueId.Should().Be(shop.Id);
        (await db.Merchants.CountAsync(TestContext.Current.CancellationToken)).Should().Be(merchantsBefore);
        (await db.Merchants.AsNoTracking().SingleAsync(m => m.Id == shop.Id, TestContext.Current.CancellationToken))
            .Kind.Should().Be(MerchantKind.Retail, "the existing row is used, never re-kinded");
    }

    [Fact]
    public async Task VenueForTaxIdAsync_asked_again_returns_the_same_venue()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());

        var first = await directory.VenueForTaxIdAsync("123456789", "Menjačnica Zlatnik", TestContext.Current.CancellationToken);
        var second = await directory.VenueForTaxIdAsync("123456789", "MENJAČNICA ZLATNIK", TestContext.Current.CancellationToken);

        second.Should().Be(first);
    }

    [Fact]
    public async Task VenueForTaxIdAsync_the_loser_of_a_race_returns_the_winners_venue_and_adds_no_second_merchant()
    {
        await using var dbA = await fixture.CreateMigratedContextAsync();
        var optionsB = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(dbA.Database.GetConnectionString())
            .Options;
        await using var dbB = new LedgerDbContext(optionsB);
        var directoryA = new EfMerchantDirectory(dbA, new FakeTimeProvider());
        var directoryB = new EfMerchantDirectory(dbB, new FakeTimeProvider());

        await using var txA = await dbA.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var idA = await directoryA.VenueForTaxIdAsync("123456789", "Menjačnica Zlatnik (winner)", TestContext.Current.CancellationToken);

        var idBTask = directoryB.VenueForTaxIdAsync("123456789", "Menjačnica Zlatnik (loser)", TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(idBTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        finished.Should().NotBeSameAs(idBTask,
            "the loser's pre-check cannot see the winner's uncommitted venue, so its insert must block on the PIB's unique index");

        await txA.CommitAsync(TestContext.Current.CancellationToken);

        var idB = await idBTask;

        idB.Should().Be(idA, "the PIB's unique index decides identity - the loser adopts the winner's venue");
        var venues = await dbA.Merchants.AsNoTracking()
            .Where(m => m.TaxId == "123456789")
            .ToListAsync(TestContext.Current.CancellationToken);
        venues.Should().ContainSingle().Which.DisplayName.Should().Be("Menjačnica Zlatnik (winner)");
    }

    [Fact]
    public async Task VenueForTaxIdAsync_rejects_a_display_name_longer_than_256_characters()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());

        var act = () => directory.VenueForTaxIdAsync("123456789", new string('x', 257), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>().WithParameterName("displayName");
    }
}
