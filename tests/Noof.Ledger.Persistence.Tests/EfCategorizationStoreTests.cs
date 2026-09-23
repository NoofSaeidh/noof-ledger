using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfCategorizationStoreTests(PostgresFixture fixture)
{
    static Wallet NewWallet(string name = "Cash") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Currency = CurrencyCode.Eur,
        IsDefault = false,
    };

    static Transaction NewTransaction(
        Guid walletId, long chatId = 1, int messageId = 1,
        DateTimeOffset? occurredAt = null, DateOnly? occurredOn = null) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = walletId,
        RawText = "coffee 3.50, milk 1.20",
        Status = TransactionStatus.Captured,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        OccurredOn = occurredOn ?? DateOnly.FromDateTime(DateTime.UtcNow),
        TelegramChatId = chatId,
        TelegramMessageId = messageId,
        BotMessageId = 42,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    static CategorizationOutcome Outcome(params CategorizedLineItem[] items) => new(items, new DateOnly(2026, 9, 21));

    static Category NewCategory() => new()
    {
        Id = Guid.NewGuid(),
        Slug = $"test-{Guid.NewGuid():N}",
        NameEn = "Test category",
        NameRu = "Тестовая категория",
        IsActive = true,
    };

    static Merchant NewMerchant() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Test merchant",
        Kind = MerchantKind.Retail,
    };

    static async Task<(Guid TransactionId, Guid CategoryId, Guid MerchantId)> SeedAsync(
        LedgerDbContext db, CancellationToken cancellationToken)
    {
        var wallet = NewWallet();
        var transaction = NewTransaction(wallet.Id);
        var category = NewCategory();
        var merchant = NewMerchant();
        db.Wallets.Add(wallet);
        db.Transactions.Add(transaction);
        db.Categories.Add(category);
        db.Merchants.Add(merchant);
        await db.SaveChangesAsync(cancellationToken);
        return (transaction.Id, category.Id, merchant.Id);
    }

    [Fact]
    public async Task ApplyAsync_writes_model_authored_lines_and_completes_the_transaction()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var (transactionId, categoryId, merchantId) = await SeedAsync(db, TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);
        var items = new[] { new CategorizedLineItem("Coffee", new Money(3.50m, CurrencyCode.Eur), categoryId, merchantId) };

        await store.ApplyAsync(transactionId, Outcome(items), TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var transaction = await db.Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);
        transaction.Status.Should().Be(TransactionStatus.Completed);
        var lines = await db.LineItems.AsNoTracking()
            .Where(l => l.TransactionId == transactionId).ToListAsync(TestContext.Current.CancellationToken);
        lines.Should().ContainSingle();
        lines[0].Description.Should().Be("Coffee");
        lines[0].Amount.Should().Be(new Money(3.50m, CurrencyCode.Eur));
        lines[0].CategoryId.Should().Be(categoryId);
        lines[0].MerchantId.Should().Be(merchantId);
        lines[0].CategorizedBy.Should().Be(CategorizationAuthority.Model);
    }

    [Fact]
    public async Task ApplyAsync_replaces_a_previous_model_authored_line_rather_than_appending_to_it()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var (transactionId, categoryId, merchantId) = await SeedAsync(db, TestContext.Current.CancellationToken);
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Description = "Stale model guess",
            Amount = new Money(9.99m, CurrencyCode.Eur),
            CategoryId = categoryId,
            CategorizedBy = CategorizationAuthority.Model,
            MerchantId = null,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);
        var items = new[] { new CategorizedLineItem("Coffee", new Money(3.50m, CurrencyCode.Eur), categoryId, merchantId) };

        await store.ApplyAsync(transactionId, Outcome(items), TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var lines = await db.LineItems.AsNoTracking()
            .Where(l => l.TransactionId == transactionId).ToListAsync(TestContext.Current.CancellationToken);
        lines.Should().ContainSingle("the stale model line must be replaced, not accumulated alongside the new one");
        lines[0].Description.Should().Be("Coffee");
    }

    [Fact]
    public async Task ApplyAsync_leaves_a_user_authored_line_untouched()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var (transactionId, categoryId, merchantId) = await SeedAsync(db, TestContext.Current.CancellationToken);
        var userLineId = Guid.NewGuid();
        db.LineItems.Add(new LineItem
        {
            Id = userLineId,
            TransactionId = transactionId,
            Description = "Hand-corrected by the user",
            Amount = new Money(1.00m, CurrencyCode.Eur),
            CategoryId = categoryId,
            CategorizedBy = CategorizationAuthority.User,
            MerchantId = null,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);
        var items = new[] { new CategorizedLineItem("Coffee", new Money(3.50m, CurrencyCode.Eur), categoryId, merchantId) };

        await store.ApplyAsync(transactionId, Outcome(items), TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var lines = await db.LineItems.AsNoTracking()
            .Where(l => l.TransactionId == transactionId).ToListAsync(TestContext.Current.CancellationToken);
        lines.Should().HaveCount(2, "the user's line survives alongside the new model line");
        var userLine = lines.Single(l => l.Id == userLineId);
        userLine.Description.Should().Be("Hand-corrected by the user");
        userLine.Amount.Should().Be(new Money(1.00m, CurrencyCode.Eur));
        userLine.CategorizedBy.Should().Be(CategorizationAuthority.User);
    }

    [Fact]
    public async Task Concurrent_ApplyAsync_calls_for_the_same_transaction_do_not_double_the_line_items()
    {
        // Reproduces the reviewer's scenario: a worker whose lease expired mid-job and a second
        // host process both categorize the same transaction. Under READ COMMITTED, without a lock
        // taken up front, both DELETEs see nothing to remove (neither has committed yet), both
        // INSERTs succeed, and both COMMIT - two line items instead of one. Taking SELECT ... FOR
        // UPDATE as the first statement inside ApplyAsync's transaction makes the second caller
        // wait for the first to commit, then see (and replace) its rows, the same way
        // EfJobQueueTests' concurrent-claim test proves SKIP LOCKED serializes ClaimAsync.
        await using var dbA = await fixture.CreateContextAsync();
        await dbA.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var (transactionId, categoryId, merchantId) = await SeedAsync(dbA, TestContext.Current.CancellationToken);

        var connectionString = dbA.Database.GetConnectionString();
        await using var dbB = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options);

        var storeA = new EfCategorizationStore(dbA);
        var storeB = new EfCategorizationStore(dbB);
        var itemsA = new[] { new CategorizedLineItem("Coffee", new Money(3.50m, CurrencyCode.Eur), categoryId, merchantId) };
        var itemsB = new[] { new CategorizedLineItem("Milk", new Money(1.20m, CurrencyCode.Eur), categoryId, merchantId) };

        await Task.WhenAll(
            storeA.ApplyAsync(transactionId, Outcome(itemsA), TestContext.Current.CancellationToken),
            storeB.ApplyAsync(transactionId, Outcome(itemsB), TestContext.Current.CancellationToken));

        await using var verify = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options);
        var lines = await verify.LineItems.AsNoTracking()
            .Where(l => l.TransactionId == transactionId).ToListAsync(TestContext.Current.CancellationToken);
        lines.Should().ContainSingle(
            "the two concurrent calls must serialize on the transaction row - whichever committed second replaces the first's line, it must not double it");
    }

    [Fact]
    public async Task ApplyAsync_is_idempotent_applying_the_same_result_twice_leaves_the_same_rows()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var (transactionId, categoryId, merchantId) = await SeedAsync(db, TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);
        var items = new[]
        {
            new CategorizedLineItem("Coffee", new Money(3.50m, CurrencyCode.Eur), categoryId, merchantId),
            new CategorizedLineItem("Milk", new Money(1.20m, CurrencyCode.Eur), categoryId, null),
        };

        await store.ApplyAsync(transactionId, Outcome(items), TestContext.Current.CancellationToken);
        await store.ApplyAsync(transactionId, Outcome(items), TestContext.Current.CancellationToken);

        // The second ApplyAsync deletes the first call's line items with a raw SQL DELETE, which
        // the change tracker never learns about - the first call's LineItem entities stay tracked
        // as Unchanged even though their rows are gone. ChangeTracker.Clear() is required before
        // this read, or a query touching the identity map can serve those stale instances instead
        // of what is actually in the database - LineItemMoneyMappingTests hit this same class of
        // defect once already (a tracked client-side value surviving a round trip the DB rounded).
        db.ChangeTracker.Clear();
        var lines = await db.LineItems.AsNoTracking()
            .Where(l => l.TransactionId == transactionId).ToListAsync(TestContext.Current.CancellationToken);
        lines.Should().HaveCount(2, "applying an identical result twice must not double the bill");
        lines.Select(l => l.Description).Should().BeEquivalentTo(["Coffee", "Milk"]);
    }

    [Fact]
    public async Task ApplyAsync_commits_nothing_when_the_transaction_fails_before_commit()
    {
        var connectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();
        Guid transactionId, categoryId, merchantId;

        await using (var seed = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options))
        {
            await seed.Database.MigrateAsync(TestContext.Current.CancellationToken);
            (transactionId, categoryId, merchantId) = await SeedAsync(seed, TestContext.Current.CancellationToken);
        }

        await using var breaking = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(new ThrowsBeforeCommitInterceptor())
                .Options);
        var store = new EfCategorizationStore(breaking);
        var items = new[] { new CategorizedLineItem("Coffee", new Money(3.50m, CurrencyCode.Eur), categoryId, merchantId) };

        var act = async () => await store.ApplyAsync(transactionId, Outcome(items), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var verify = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options);
        var transaction = await verify.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);
        transaction.Status.Should().Be(TransactionStatus.Captured,
            "the commit never happened, so the status flip must not be visible either");
        (await verify.LineItems.CountAsync(l => l.TransactionId == transactionId, TestContext.Current.CancellationToken))
            .Should().Be(0, "the insert was staged inside the same rolled-back transaction as the failed commit");
    }

    [Fact]
    public async Task GetSubjectAsync_returns_the_record_as_stored_with_its_lines()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet("Main Wallet");
        // 22:30 UTC is 00:30 the next day in Belgrade: SentOn is the Belgrade day, OccurredOn is what is stored.
        var transaction = NewTransaction(wallet.Id, chatId: 777, messageId: 5,
            occurredAt: new DateTimeOffset(2026, 9, 21, 22, 30, 0, TimeSpan.Zero), occurredOn: new DateOnly(2026, 9, 20));
        var category = NewCategory();
        var merchant = NewMerchant();
        db.AddRange(wallet, transaction, category, merchant);
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            Description = "Coffee",
            Amount = new Money(3.50m, CurrencyCode.Eur),
            CategoryId = category.Id,
            CategorizedBy = CategorizationAuthority.Model,
            MerchantId = merchant.Id,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);

        var subject = await store.GetSubjectAsync(transaction.Id, TestContext.Current.CancellationToken);

        subject.Should().NotBeNull();
        subject!.RawText.Should().Be("coffee 3.50, milk 1.20");
        subject.TelegramChatId.Should().Be(777);
        subject.BotMessageId.Should().Be(42);
        subject.WalletName.Should().Be("Main Wallet");
        subject.Status.Should().Be(TransactionStatus.Captured);
        subject.SentOn.Should().Be(new DateOnly(2026, 9, 22));
        subject.OccurredOn.Should().Be(new DateOnly(2026, 9, 20));
        subject.Lines.Should().Equal(
            new RecordedLine("Coffee", new Money(3.50m, CurrencyCode.Eur), category.Slug, "Тестовая категория", "Test merchant"));
    }

    [Fact]
    public async Task ApplyAsync_stores_the_day_the_outcome_names()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var (transactionId, categoryId, _) = await SeedAsync(db, TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);
        var items = new[] { new CategorizedLineItem("Coffee", new Money(3.50m, CurrencyCode.Eur), categoryId, null) };

        await store.ApplyAsync(transactionId, new CategorizationOutcome(items, new DateOnly(2026, 9, 20)), TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken))
            .OccurredOn.Should().Be(new DateOnly(2026, 9, 20));
    }

    [Fact]
    public async Task GetSubjectAsync_returns_null_when_the_transaction_is_gone()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);

        var subject = await store.GetSubjectAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        subject.Should().BeNull();
    }

    [Fact]
    public async Task MarkFailedAsync_sets_status_to_failed_and_nothing_else()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet();
        var transaction = NewTransaction(wallet.Id);
        db.Wallets.Add(wallet);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);

        await store.MarkFailedAsync(transaction.Id, TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var reloaded = await db.Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(TransactionStatus.Failed);
        reloaded.RawText.Should().Be(transaction.RawText);
        reloaded.TimeZoneId.Should().Be(transaction.TimeZoneId);
        reloaded.BotMessageId.Should().Be(transaction.BotMessageId);
    }
}
