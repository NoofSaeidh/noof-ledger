using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Editing;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfRecordEditorTests(PostgresFixture fixture)
{
    static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
    static readonly Guid DefaultWalletId = new("00000000-0000-0000-0000-000000000001");

    static async Task<Transaction> SeedAsync(LedgerDbContext db, TransactionStatus status = TransactionStatus.Completed)
    {
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = DefaultWalletId,
            RawText = "кофе 250",
            Status = status,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
            OccurredOn = new DateOnly(2026, 9, 21),
            TelegramChatId = 111,
            TelegramMessageId = 5,
            BotMessageId = 42,
            CreatedAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        return transaction;
    }

    [Fact]
    public async Task Finds_a_record_by_its_echo_and_nothing_by_another_message()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);
        var editor = new EfRecordEditor(db, Clock);

        (await editor.FindByBotMessageAsync(111, 42, TestContext.Current.CancellationToken))
            .Should().Be(new EchoTarget(transaction.Id, 42));
        (await editor.FindByBotMessageAsync(111, 5, TestContext.Current.CancellationToken)).Should().BeNull();
        (await editor.FindByBotMessageAsync(999, 42, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Cancelling_sets_Cancelled_and_records_what_it_was_before()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);
        var editor = new EfRecordEditor(db, Clock);

        (await editor.CancelAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeTrue();

        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(TestContext.Current.CancellationToken)).Status.Should().Be(TransactionStatus.Cancelled);
        var revision = await db.TransactionRevisions.SingleAsync(TestContext.Current.CancellationToken);
        revision.Kind.Should().Be(RevisionKind.Cancel);
        revision.StatusBefore.Should().Be(TransactionStatus.Completed);
        revision.StatusAfter.Should().Be(TransactionStatus.Cancelled);
    }

    [Fact]
    public async Task Cancelling_twice_changes_nothing_the_second_time()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);
        var editor = new EfRecordEditor(db, Clock);

        await editor.CancelAsync(transaction.Id, TestContext.Current.CancellationToken);
        (await editor.CancelAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeFalse();

        db.ChangeTracker.Clear();
        (await db.TransactionRevisions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Restoring_puts_back_the_status_from_before_the_cancellation()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db, TransactionStatus.Failed);
        var editor = new EfRecordEditor(db, Clock);
        await editor.CancelAsync(transaction.Id, TestContext.Current.CancellationToken);

        (await editor.RestoreAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeTrue();

        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(TestContext.Current.CancellationToken)).Status.Should().Be(TransactionStatus.Failed);
        var revisions = await db.TransactionRevisions.OrderBy(r => r.RevisionNumber).ToListAsync(TestContext.Current.CancellationToken);
        revisions.Select(r => r.Kind).Should().Equal(RevisionKind.Cancel, RevisionKind.Restore);
        revisions[1].StatusAfter.Should().Be(TransactionStatus.Failed);
    }

    [Fact]
    public async Task Restoring_a_record_that_is_not_cancelled_does_nothing()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);

        (await new EfRecordEditor(db, Clock).RestoreAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeFalse();

        (await db.TransactionRevisions.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }
}
