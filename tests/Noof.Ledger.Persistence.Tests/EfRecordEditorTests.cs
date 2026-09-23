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

    [Fact]
    public async Task A_reply_to_the_edit_prompt_finds_the_record_and_its_echo()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);
        var editor = new EfRecordEditor(db, Clock);

        await editor.AttachPromptAsync(transaction.Id, 77, TestContext.Current.CancellationToken);

        (await editor.FindByBotMessageAsync(111, 77, TestContext.Current.CancellationToken))
            .Should().Be(new EchoTarget(transaction.Id, 42), "the echo, not the prompt, is what gets edited afterwards");
    }

    [Fact]
    public async Task Finds_a_record_by_the_persons_own_message()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);

        (await new EfRecordEditor(db, Clock).FindByUserMessageAsync(111, 5, TestContext.Current.CancellationToken))
            .Should().Be(new EchoTarget(transaction.Id, 42));
    }

    [Fact]
    public async Task A_correction_is_queued_once_even_when_telegram_delivers_the_reply_twice()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);
        var editor = new EfRecordEditor(db, Clock);

        (await editor.RequestCorrectionAsync(transaction.Id, "нет, 1500", 8, Clock.GetUtcNow(), TestContext.Current.CancellationToken)).Should().BeTrue();
        (await editor.RequestCorrectionAsync(transaction.Id, "нет, 1500", 8, Clock.GetUtcNow(), TestContext.Current.CancellationToken)).Should().BeFalse();

        db.ChangeTracker.Clear();
        var job = await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.Kind.Should().Be(JobKind.Correct);
        job.Instruction.Should().Be("нет, 1500");
        job.SourceMessageId.Should().Be(8);
        job.Status.Should().Be(JobStatus.Pending);
        job.RunAfter.Should().Be(Clock.GetUtcNow());
    }

    [Fact]
    public async Task A_corrections_instruction_day_is_the_replys_own_local_day_not_the_original_messages()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);
        var editor = new EfRecordEditor(db, Clock);
        // 22:30 UTC on the 23rd is already past midnight in Belgrade (UTC+2 in September) - the
        // 24th there. The transaction's own SeedAsync day (the 21st) must play no part.
        var sentAt = new DateTimeOffset(2026, 9, 23, 22, 30, 0, TimeSpan.Zero);

        await editor.RequestCorrectionAsync(transaction.Id, "нет, 1500", 8, sentAt, TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        (await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken)).InstructionDay
            .Should().Be(new DateOnly(2026, 9, 24));
    }

    [Fact]
    public async Task An_edited_original_replaces_the_raw_text_and_queues_a_fresh_reading()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);
        var editor = new EfRecordEditor(db, Clock);

        (await editor.ReplaceRawTextAsync(transaction.Id, "кофе 300", TestContext.Current.CancellationToken)).Should().BeTrue();

        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(TestContext.Current.CancellationToken)).RawText.Should().Be("кофе 300");
        (await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken)).Kind.Should().Be(JobKind.Reinterpret);
    }

    [Fact]
    public async Task An_edit_that_leaves_the_text_as_it_was_queues_nothing()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);

        (await new EfRecordEditor(db, Clock).ReplaceRawTextAsync(transaction.Id, "кофе 250", TestContext.Current.CancellationToken))
            .Should().BeFalse();

        (await db.CategorizationJobs.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }
}
