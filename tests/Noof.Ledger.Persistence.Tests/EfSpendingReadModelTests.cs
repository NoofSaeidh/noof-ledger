using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Reporting;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfSpendingReadModelTests(PostgresFixture fixture)
{
    static int nextTelegramMessageId;

    static Wallet NewWallet(CurrencyCode currency) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Cash",
        Currency = currency,
        IsDefault = false,
    };

    static Category NewCategory(string slug, string nameEn) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        NameEn = nameEn,
        NameRu = nameEn,
        IsActive = true,
    };

    static Merchant NewMerchant(string displayName) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = displayName,
        Kind = MerchantKind.Retail,
    };

    static Transaction NewTransaction(
        Guid walletId, DateTimeOffset occurredAt, string timeZoneId, TransactionStatus status) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = walletId,
        RawText = "test capture",
        Status = status,
        TimeZoneId = timeZoneId,
        OccurredAt = occurredAt,
        TelegramChatId = 1,
        TelegramMessageId = Interlocked.Increment(ref nextTelegramMessageId),
        CreatedAt = occurredAt,
    };

    static LineItem NewLineItem(
        Guid transactionId, string description, Money amount, Guid? categoryId, Guid? merchantId) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        Description = description,
        Amount = amount,
        CategoryId = categoryId,
        CategorizedBy = CategorizationAuthority.Model,
        MerchantId = merchantId,
    };

    [Fact]
    public async Task RecentAsync_returns_transactions_newest_first_including_ones_awaiting_categorisation_and_ones_that_failed()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var oldest = NewTransaction(wallet.Id, now.AddHours(-2), "Europe/Belgrade", TransactionStatus.Failed);
        var middle = NewTransaction(wallet.Id, now.AddHours(-1), "Europe/Belgrade", TransactionStatus.Captured);
        var newest = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        db.Transactions.AddRange(oldest, middle, newest);
        db.LineItems.Add(NewLineItem(newest.Id, "coffee", new Money(3.5m, CurrencyCode.Eur), null, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(10, TestContext.Current.CancellationToken);

        recent.Select(r => r.Id).Should().ContainInOrder(newest.Id, middle.Id, oldest.Id);
        recent.Single(r => r.Id == oldest.Id).Status.Should().Be(TransactionStatus.Failed);
        recent.Single(r => r.Id == oldest.Id).Items.Should().BeEmpty("a failed transaction still has to show up, not vanish");
        recent.Single(r => r.Id == middle.Id).Items.Should().BeEmpty("still awaiting categorisation");
        var newestRow = recent.Single(r => r.Id == newest.Id);
        newestRow.WalletName.Should().Be(wallet.Name);
        newestRow.TimeZoneId.Should().Be("Europe/Belgrade");
        newestRow.Items.Should().ContainSingle().Which.Description.Should().Be("coffee");
    }

    [Fact]
    public async Task RecentAsync_resolves_category_and_merchant_names_and_leaves_them_null_when_absent()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        var category = NewCategory("test-recent-groceries", "Groceries");
        var merchant = NewMerchant("Lidl");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        db.Merchants.Add(merchant);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A fixed literal, not DateTimeOffset.UtcNow: timestamptz keeps microseconds while a
        // .NET tick is 100ns, so a value with a non-zero final tick digit does not survive the
        // round trip and an exact-equality assertion fails on roughly nine runs in ten.
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var transaction = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        db.Transactions.Add(transaction);
        db.LineItems.AddRange(
            NewLineItem(transaction.Id, "bread", new Money(2m, CurrencyCode.Eur), category.Id, merchant.Id),
            NewLineItem(transaction.Id, "cash withdrawal fee", new Money(1m, CurrencyCode.Eur), null, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(10, TestContext.Current.CancellationToken);

        recent.Single().Items.Should().BeEquivalentTo(
        [
            new RecentLineItem("bread", new Money(2m, CurrencyCode.Eur), "Groceries", "Lidl"),
            new RecentLineItem("cash withdrawal fee", new Money(1m, CurrencyCode.Eur), null, null),
        ]);
    }

    [Fact]
    public async Task RecentAsync_respects_the_limit_and_returns_only_the_most_recent_ones()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A fixed literal, not DateTimeOffset.UtcNow: timestamptz keeps microseconds while a
        // .NET tick is 100ns, so a value with a non-zero final tick digit does not survive the
        // round trip and an exact-equality assertion fails on roughly nine runs in ten.
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
            db.Transactions.Add(NewTransaction(wallet.Id, now.AddMinutes(i), "Europe/Belgrade", TransactionStatus.Captured));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(2, TestContext.Current.CancellationToken);

        recent.Select(r => r.OccurredAt).Should().BeEquivalentTo([now.AddMinutes(4), now.AddMinutes(3)],
            "only the two most recently occurred transactions should survive the limit");
    }

    [Fact]
    public async Task ThisMonthAsync_buckets_each_row_by_its_own_stored_time_zone_not_a_naive_UTC_bucket()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        var category = NewCategory("test-month-boundary", "Boundary Test");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The identical UTC instant, stored under two different zones. A naive `date_trunc('month',
        // occurred_at)` would put BOTH rows in August, because occurred_at - the UTC instant - is the
        // same value for both. Correct per-row bucketing must split them: +14 rolls the local wall
        // clock into September, -11 keeps it in August.
        var instant = new DateTimeOffset(2026, 8, 31, 23, 30, 0, TimeSpan.Zero);
        var rollsIntoSeptember = NewTransaction(wallet.Id, instant, "Etc/GMT-14", TransactionStatus.Completed);
        var staysInAugust = NewTransaction(wallet.Id, instant, "Etc/GMT+11", TransactionStatus.Completed);
        db.Transactions.AddRange(rollsIntoSeptember, staysInAugust);
        db.LineItems.AddRange(
            NewLineItem(rollsIntoSeptember.Id, "september spend", new Money(10m, CurrencyCode.Eur), category.Id, null),
            NewLineItem(staysInAugust.Id, "august spend", new Money(20m, CurrencyCode.Eur), category.Id, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        // "This month" is decided once, from the operator's CURRENTLY configured zone - fixed to UTC
        // here so the test does not depend on the build machine's own zone. 2026-09-15 UTC is
        // unambiguously September under UTC.
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.FirstDay.Should().Be(new DateOnly(2026, 9, 1));
        summary.Totals.Should()
            .ContainSingle("only the row whose OWN zone rolls the instant into September belongs in this bucket")
            .Which.Should().BeEquivalentTo(new MonthTotal("Boundary Test", CurrencyCode.Eur, 10m));
    }

    [Fact]
    public async Task ThisMonthAsync_never_adds_two_currencies_together()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Rsd);
        var category = NewCategory("test-no-fx-mixing", "Groceries");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var rsdTransaction = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        var eurTransaction = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        db.Transactions.AddRange(rsdTransaction, eurTransaction);
        db.LineItems.AddRange(
            NewLineItem(rsdTransaction.Id, "market", new Money(1500m, CurrencyCode.Rsd), category.Id, null),
            NewLineItem(rsdTransaction.Id, "market again", new Money(500m, CurrencyCode.Rsd), category.Id, null),
            NewLineItem(eurTransaction.Id, "import", new Money(20m, CurrencyCode.Eur), category.Id, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.Totals.Should().HaveCount(2, "RSD and EUR must never collapse into one combined figure");
        summary.Totals.Should().Contain(new MonthTotal("Groceries", CurrencyCode.Rsd, 2000m));
        summary.Totals.Should().Contain(new MonthTotal("Groceries", CurrencyCode.Eur, 20m));
    }

    [Fact]
    public async Task ThisMonthAsync_labels_a_line_item_with_no_category_instead_of_dropping_it()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var transaction = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Captured);
        db.Transactions.Add(transaction);
        db.LineItems.Add(NewLineItem(transaction.Id, "unlabelled", new Money(9.99m, CurrencyCode.Eur), null, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.Totals.Should().ContainSingle("uncategorised money must still be counted, never silently omitted")
            .Which.Should().BeEquivalentTo(new MonthTotal(EfSpendingReadModel.UncategorisedLabel, CurrencyCode.Eur, 9.99m));
    }

    [Fact]
    public async Task ThisMonthAsync_excludes_line_items_outside_the_current_month_boundaries()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        var category = NewCategory("test-outside-boundaries", "Groceries");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var lastMonth = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var thisMonth = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var nextMonth = new DateTimeOffset(2026, 10, 1, 0, 0, 1, TimeSpan.Zero);
        var lastMonthTransaction = NewTransaction(wallet.Id, lastMonth, "Etc/UTC", TransactionStatus.Completed);
        var thisMonthTransaction = NewTransaction(wallet.Id, thisMonth, "Etc/UTC", TransactionStatus.Completed);
        var nextMonthTransaction = NewTransaction(wallet.Id, nextMonth, "Etc/UTC", TransactionStatus.Completed);
        db.Transactions.AddRange(lastMonthTransaction, thisMonthTransaction, nextMonthTransaction);
        db.LineItems.AddRange(
            NewLineItem(lastMonthTransaction.Id, "august", new Money(1m, CurrencyCode.Eur), category.Id, null),
            NewLineItem(thisMonthTransaction.Id, "september", new Money(2m, CurrencyCode.Eur), category.Id, null),
            NewLineItem(nextMonthTransaction.Id, "october", new Money(4m, CurrencyCode.Eur), category.Id, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(thisMonth), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.Totals.Should().ContainSingle().Which.Should().BeEquivalentTo(new MonthTotal("Groceries", CurrencyCode.Eur, 2m));
    }
}
