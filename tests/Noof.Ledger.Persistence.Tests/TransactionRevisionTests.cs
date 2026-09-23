using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;
using Noof.Ledger.Persistence.Editing;
using Noof.Ledger.Persistence.Revisions;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class TransactionRevisionTests(PostgresFixture fixture)
{
    static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
    static readonly Guid DefaultWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly Guid CoffeeCategoryId = new("00000000-0000-0000-0001-000000000017");

    static async Task<Guid> SeedTransactionAsync(LedgerDbContext db)
    {
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = DefaultWalletId,
            RawText = "кофе 250",
            Status = TransactionStatus.Captured,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
            OccurredOn = new DateOnly(2026, 9, 21),
            TelegramChatId = 1,
            TelegramMessageId = 1,
            CreatedAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return transaction.Id;
    }

    static CategorizedLineItem Coffee(decimal amount) => new("кофе", new Money(amount, CurrencyCode.Rsd), CoffeeCategoryId, null);

    [Fact]
    public async Task A_first_reading_appends_an_initial_revision_with_a_snapshot_of_what_was_written()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transactionId = await SeedTransactionAsync(db);

        await new EfCategorizationStore(db, Clock).ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21)), TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var revision = await db.TransactionRevisions.SingleAsync(TestContext.Current.CancellationToken);
        revision.RevisionNumber.Should().Be(1);
        revision.Kind.Should().Be(RevisionKind.Initial);
        revision.StatusBefore.Should().Be(TransactionStatus.Captured);
        revision.StatusAfter.Should().Be(TransactionStatus.Completed);
        revision.Instruction.Should().BeNull();
        revision.CreatedAt.Should().Be(Clock.GetUtcNow());

        using var snapshot = JsonDocument.Parse(revision.Snapshot);
        snapshot.RootElement.GetProperty("raw_text").GetString().Should().Be("кофе 250");
        snapshot.RootElement.GetProperty("occurred_on").GetString().Should().Be("2026-09-21");
        var item = snapshot.RootElement.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("amount").ValueKind.Should().Be(JsonValueKind.String, "an amount is never a JSON number");
        decimal.Parse(item.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture).Should().Be(250m);
        item.GetProperty("currency").GetString().Should().Be("RSD");
    }

    [Fact]
    public async Task A_correction_appends_the_next_revision_with_its_instruction()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transactionId = await SeedTransactionAsync(db);
        var store = new EfCategorizationStore(db, Clock);

        await store.ApplyAsync(transactionId, new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21)), TestContext.Current.CancellationToken);
        await store.ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(1500m)], new DateOnly(2026, 9, 21), JobKind.Correct, "нет, 1500"),
            TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var revisions = await db.TransactionRevisions.OrderBy(r => r.RevisionNumber).ToListAsync(TestContext.Current.CancellationToken);
        revisions.Select(r => (r.RevisionNumber, r.Kind)).Should().Equal((1, RevisionKind.Initial), (2, RevisionKind.Correction));
        revisions[1].Instruction.Should().Be("нет, 1500");
        revisions[1].StatusBefore.Should().Be(TransactionStatus.Completed);
    }

    [Fact]
    public async Task A_reinterpretation_is_recorded_as_an_edit()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transactionId = await SeedTransactionAsync(db);

        await new EfCategorizationStore(db, Clock).ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(300m)], new DateOnly(2026, 9, 21), JobKind.Reinterpret), TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        (await db.TransactionRevisions.SingleAsync(TestContext.Current.CancellationToken)).Kind.Should().Be(RevisionKind.Edit);
    }

    [Theory]
    [InlineData("UPDATE public.transaction_revisions SET instruction = 'rewritten'")]
    [InlineData("DELETE FROM public.transaction_revisions")]
    [InlineData("TRUNCATE public.transaction_revisions")]
    public async Task The_history_cannot_be_rewritten(string sql)
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transactionId = await SeedTransactionAsync(db);
        await new EfCategorizationStore(db, Clock).ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21)), TestContext.Current.CancellationToken);

        var act = () => db.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
    }

    [Fact]
    public async Task A_correction_applied_to_a_cancelled_record_keeps_it_cancelled()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transactionId = await SeedTransactionAsync(db);
        var store = new EfCategorizationStore(db, Clock);
        await store.ApplyAsync(transactionId, new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21)), TestContext.Current.CancellationToken);
        await new EfRecordEditor(db, Clock).CancelAsync(transactionId, TestContext.Current.CancellationToken);

        await store.ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(1500m)], new DateOnly(2026, 9, 21), JobKind.Correct, "нет, 1500"),
            TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken))
            .Status.Should().Be(TransactionStatus.Cancelled, "only Restore brings a cancelled record back");
    }
}
