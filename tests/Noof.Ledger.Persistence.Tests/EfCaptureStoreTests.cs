using System.Globalization;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Capture;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfCaptureStoreTests(PostgresFixture fixture)
{
    static Wallet DefaultWallet() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Cash",
        Currency = CurrencyCode.Eur,
        IsDefault = true,
    };

    static CapturedMessage NewMessage(long chatId = 1, int messageId = 100, DateTimeOffset? sentAt = null) =>
        new(chatId, messageId, "coffee 3.50", sentAt ?? DateTimeOffset.UnixEpoch);

    // The AddCaptureModel migration seeds exactly one default wallet (SeedDataTests proves it),
    // enforced by the ix_wallets_single_default partial unique index. These tests want full
    // control over which wallet is "the" default, so the seeded row is removed first - otherwise
    // adding a second IsDefault = true wallet is a unique-constraint violation, not a fixture bug.
    static async Task RemoveSeededDefaultWalletAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        var seeded = await db.Wallets.SingleAsync(cancellationToken);
        db.Wallets.Remove(seeded);
        await db.SaveChangesAsync(cancellationToken);
    }

    [Fact]
    public async Task Throws_when_no_wallet_is_marked_default()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await RemoveSeededDefaultWalletAsync(db, TestContext.Current.CancellationToken);
        var store = new EfCaptureStore(db, new FakeTimeProvider());

        var act = async () =>
            await store.CaptureAsync(NewMessage(), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*default*");
    }

    [Fact]
    public async Task Captures_a_transaction_and_a_pending_job_in_one_call()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await RemoveSeededDefaultWalletAsync(db, TestContext.Current.CancellationToken);
        var wallet = DefaultWallet();
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var store = new EfCaptureStore(db, new FakeTimeProvider(now));
        var message = NewMessage(sentAt: now);

        var transactionId = await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        var transaction = await db.Transactions.SingleAsync(TestContext.Current.CancellationToken);
        transaction.Id.Should().Be(transactionId);
        transaction.WalletId.Should().Be(wallet.Id);
        transaction.RawText.Should().Be("coffee 3.50");
        transaction.Status.Should().Be(TransactionStatus.Captured);
        transaction.TimeZoneId.Should().Be("Europe/Belgrade");
        transaction.TelegramChatId.Should().Be(1);
        transaction.TelegramMessageId.Should().Be(100);
        transaction.BotMessageId.Should().BeNull();
        transaction.CreatedAt.Should().Be(now);

        var job = await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.TransactionId.Should().Be(transactionId);
        job.Status.Should().Be(JobStatus.Pending);
        job.AttemptCount.Should().Be(0);
        job.RunAfter.Should().Be(now);
        job.CreatedAt.Should().Be(now);
        job.UpdatedAt.Should().Be(now);
    }

    [Fact]
    public async Task Occurred_at_is_the_telegram_messages_own_timestamp_not_when_it_was_processed()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await RemoveSeededDefaultWalletAsync(db, TestContext.Current.CancellationToken);
        db.Wallets.Add(DefaultWallet());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var sentAt = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var processedAt = sentAt.AddHours(3);
        var store = new EfCaptureStore(db, new FakeTimeProvider(processedAt));
        var message = NewMessage(sentAt: sentAt);

        await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        var transaction = await db.Transactions.SingleAsync(TestContext.Current.CancellationToken);
        transaction.OccurredAt.Should().Be(sentAt, "an outage delayed processing, not the purchase itself");
        transaction.CreatedAt.Should().Be(processedAt);
    }

    [Fact]
    public async Task Replaying_the_same_chat_and_message_id_returns_the_existing_transaction_without_writing_a_second_job()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await RemoveSeededDefaultWalletAsync(db, TestContext.Current.CancellationToken);
        db.Wallets.Add(DefaultWallet());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCaptureStore(db, new FakeTimeProvider());
        var message = NewMessage();

        var first = await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);
        var second = await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        second.Should().Be(first);
        (await db.Transactions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await db.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task A_replay_succeeds_even_if_no_wallet_is_marked_default_any_more()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await RemoveSeededDefaultWalletAsync(db, TestContext.Current.CancellationToken);
        var wallet = DefaultWallet();
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCaptureStore(db, new FakeTimeProvider());
        var message = NewMessage();
        var first = await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        wallet.IsDefault = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var second = await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        second.Should().Be(first, "a replay must not fail just because the wallet lookup would now throw");
    }

    [Fact]
    public async Task A_failure_before_commit_leaves_neither_row_behind()
    {
        var connectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();

        await using (var seed = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options))
        {
            await seed.Database.MigrateAsync(TestContext.Current.CancellationToken);
            await RemoveSeededDefaultWalletAsync(seed, TestContext.Current.CancellationToken);
            seed.Wallets.Add(DefaultWallet());
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var breaking = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(new ThrowsBeforeCommitInterceptor())
                .Options);
        var store = new EfCaptureStore(breaking, new FakeTimeProvider());

        var act = async () =>
            await store.CaptureAsync(NewMessage(), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var verify = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options);
        (await verify.Transactions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "the commit never happened, so the transaction insert must not be visible either");
        (await verify.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    // ICaptureStore.CaptureAsync promises idempotency on (ChatId, MessageId). A plain sequential
    // replay test (above) cannot tell "idempotent" from "the loser throws a raw 23505 that a caller
    // happens not to hit" - only genuine concurrency does. This forces it the same way the owner-claim
    // race test does: hold the winning insert open inside an uncommitted transaction so the second
    // caller's insert must block on the database lock, not race past it, before either result is
    // observed.
    [Fact]
    public async Task Concurrent_CaptureAsync_calls_for_the_same_chat_and_message_let_exactly_one_caller_insert()
    {
        await using var dbA = await fixture.CreateContextAsync();
        await dbA.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await RemoveSeededDefaultWalletAsync(dbA, TestContext.Current.CancellationToken);
        dbA.Wallets.Add(DefaultWallet());
        await dbA.SaveChangesAsync(TestContext.Current.CancellationToken);

        var optionsB = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(dbA.Database.GetConnectionString()!)
            .Options;
        await using var dbB = new LedgerDbContext(optionsB);

        var storeA = new EfCaptureStore(dbA, new FakeTimeProvider());
        var storeB = new EfCaptureStore(dbB, new FakeTimeProvider());
        var message = NewMessage();

        await using var txA = await dbA.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var idA = await storeA.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        var idBTask = storeB.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(idBTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        finished.Should().NotBeSameAs(idBTask,
            "the second capture's insert must block on the first's uncommitted row, not race past it undetected");

        await txA.CommitAsync(TestContext.Current.CancellationToken);

        var idB = await idBTask;

        idB.Should().Be(idA, "both callers captured the identical (ChatId, MessageId); the loser must return the winner's id, not throw");
        (await dbA.Transactions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await dbA.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Theory]
    [InlineData("2026-09-21T21:50:00Z", "2026-09-21")]
    [InlineData("2026-09-21T22:30:00Z", "2026-09-22")]
    public async Task Occurred_on_is_the_local_day_the_message_was_sent_in_the_capture_time_zone(string sentAt, string expectedDay)
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        // Processed the next morning: the offline queue must not move a purchase to the day it was read (D2).
        var store = new EfCaptureStore(db, new FakeTimeProvider(new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero)));

        await store.CaptureAsync(
            NewMessage(sentAt: DateTimeOffset.Parse(sentAt, CultureInfo.InvariantCulture)),
            "Europe/Belgrade",
            TestContext.Current.CancellationToken);

        var transaction = await db.Transactions.SingleAsync(TestContext.Current.CancellationToken);
        transaction.OccurredOn.Should().Be(DateOnly.Parse(expectedDay, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task AttachBotMessageAsync_stamps_the_bot_message_id_onto_the_row()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await RemoveSeededDefaultWalletAsync(db, TestContext.Current.CancellationToken);
        db.Wallets.Add(DefaultWallet());
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCaptureStore(db, new FakeTimeProvider());
        var transactionId =
            await store.CaptureAsync(NewMessage(), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await store.AttachBotMessageAsync(transactionId, 555, TestContext.Current.CancellationToken);

        var transaction = await db.Transactions.SingleAsync(TestContext.Current.CancellationToken);
        transaction.BotMessageId.Should().Be(555);
    }
}
