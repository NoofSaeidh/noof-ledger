using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Diagnostics;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfTransactionTraceTests(PostgresFixture fixture)
{
    static readonly Guid TransactionId = new("00000000-0000-0000-0003-000000000001");

    static async Task SeedTransactionAsync(LedgerDbContext db) =>
        await SeedTransactionAsync(db, TransactionId);

    static async Task SeedTransactionAsync(LedgerDbContext db, Guid id)
    {
        db.Transactions.Add(new Transaction
        {
            Id = id,
            WalletId = new Guid("00000000-0000-0000-0000-000000000001"),
            Kind = TransactionKind.Expense,
            RawText = "кофе 250",
            Status = TransactionStatus.Completed,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
            OccurredOn = new DateOnly(2026, 9, 25),
            TelegramChatId = 1,
            TelegramMessageId = 1,
            CreatedAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    static AppLogEntry StageEvent(long id, DateTimeOffset at, string stage, int eventId, Guid transactionId) => new()
    {
        Id = id,
        LoggedAt = at,
        Level = LogSeverity.Information,
        Source = "Noof.Ledger.Telegram.TelegramPollingService",
        Message = stage,
        Template = stage,
        TransactionId = transactionId,
        PropertiesJson = $$"""{"Stage":"{{stage}}","EventId":{"Id":{{eventId}},"Name":"{{stage}}"},"TransactionId":"{{transactionId}}"}""",
    };

    static AppLogEntry StageFailedEvent(long id, DateTimeOffset at, string failedStage, Guid transactionId) => new()
    {
        Id = id,
        LoggedAt = at,
        Level = LogSeverity.Error,
        Source = "Noof.Ledger.Host.Workers.CategorizationWorker",
        Message = $"{TransactionStages.StageFailed} at stage {failedStage}",
        Template = "{Stage} at stage {FailedStage}",
        TransactionId = transactionId,
        PropertiesJson = $$"""
            {"Stage":"{{TransactionStages.StageFailed}}","FailedStage":"{{failedStage}}","EventId":{"Id":{{TransactionStages.StageFailedEventId}},"Name":"{{TransactionStages.StageFailed}}"},"TransactionId":"{{transactionId}}"}
            """,
    };

    static AppLogEntry NonStageEvent(long id, DateTimeOffset at, Guid transactionId) => new()
    {
        Id = id,
        LoggedAt = at,
        Level = LogSeverity.Debug,
        Source = "Noof.Ledger.Telegram.TelegramPollingService",
        Message = "unrelated line sharing the transaction id",
        Template = "unrelated line sharing the transaction id",
        TransactionId = transactionId,
        PropertiesJson = """{"TransactionId":"irrelevant"}""",
    };

    [Fact]
    public async Task Events_are_ordered_by_logged_at_then_id_with_stage_and_event_id_parsed()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedTransactionAsync(db);
        var t0 = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        db.AppLogs.AddRange(
            StageEvent(3, t0, TransactionStages.Received, TransactionStages.ReceivedEventId, TransactionId),
            StageEvent(1, t0, TransactionStages.Categorized, TransactionStages.CategorizedEventId, TransactionId), // same instant, lower id: sorts first
            // id 2 is lower than id 3 but occurs a second later: an id-only sort would place this
            // ahead of "Received" (id 3); the correct sort (by logged_at first) must not.
            StageEvent(2, t0.AddSeconds(1), TransactionStages.Persisted, TransactionStages.PersistedEventId, TransactionId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await new EfTransactionTrace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Exists.Should().BeTrue();
        trace.Events.Select(e => e.Stage).Should().Equal(
            TransactionStages.Categorized, TransactionStages.Received, TransactionStages.Persisted);
        trace.Events[0].EventId.Should().Be(TransactionStages.CategorizedEventId);
    }

    [Fact]
    public async Task Rows_without_a_transaction_id_or_without_a_stage_are_excluded()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedTransactionAsync(db);
        var t0 = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        db.AppLogs.AddRange(
            StageEvent(1, t0, TransactionStages.Received, TransactionStages.ReceivedEventId, TransactionId),
            NonStageEvent(2, t0.AddSeconds(1), TransactionId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await new EfTransactionTrace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Events.Should().ContainSingle().Which.Stage.Should().Be(TransactionStages.Received);
    }

    [Fact]
    public async Task Pruned_logs_leave_events_empty_but_history_still_present()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedTransactionAsync(db);
        db.TransactionRevisions.Add(new TransactionRevision
        {
            Id = Guid.NewGuid(),
            TransactionId = TransactionId,
            RevisionNumber = 1,
            Kind = RevisionKind.Initial,
            Instruction = null,
            StatusBefore = TransactionStatus.Captured,
            StatusAfter = TransactionStatus.Completed,
            Snapshot = "{}",
            CreatedAt = DateTimeOffset.Parse("2026-09-25T10:00:00Z"),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await new EfTransactionTrace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Exists.Should().BeTrue();
        trace.Events.Should().BeEmpty();
        trace.History.Should().ContainSingle().Which.ChangeKind.Should().Be(nameof(RevisionKind.Initial));
    }

    [Fact]
    public async Task History_uses_the_instruction_when_present_else_the_status_transition()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedTransactionAsync(db);
        db.TransactionRevisions.AddRange(
            new TransactionRevision
            {
                Id = Guid.NewGuid(), TransactionId = TransactionId, RevisionNumber = 1, Kind = RevisionKind.Initial,
                Instruction = null, StatusBefore = TransactionStatus.Captured, StatusAfter = TransactionStatus.Completed,
                Snapshot = "{}", CreatedAt = DateTimeOffset.Parse("2026-09-25T10:00:00Z"),
            },
            new TransactionRevision
            {
                Id = Guid.NewGuid(), TransactionId = TransactionId, RevisionNumber = 2, Kind = RevisionKind.Correction,
                Instruction = "нет, 1500", StatusBefore = TransactionStatus.Completed, StatusAfter = TransactionStatus.Completed,
                Snapshot = "{}", CreatedAt = DateTimeOffset.Parse("2026-09-25T10:01:00Z"),
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await new EfTransactionTrace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.History.Select(h => h.Details).Should().Equal("Captured → Completed", "нет, 1500");
        trace.History.Select(h => h.ChangeKind).Should().Equal(nameof(RevisionKind.Initial), nameof(RevisionKind.Correction));
    }

    [Fact]
    public async Task A_StageFailed_event_carries_the_failed_stage_parsed_from_properties()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedTransactionAsync(db);
        var t0 = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        db.AppLogs.Add(StageFailedEvent(1, t0, TransactionStages.Categorized, TransactionId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await new EfTransactionTrace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        var stageFailed = trace.Events.Should().ContainSingle().Subject;
        stageFailed.Stage.Should().Be(TransactionStages.StageFailed);
        stageFailed.FailedStage.Should().Be(TransactionStages.Categorized);
    }

    [Fact]
    public async Task A_non_failed_event_carries_no_failed_stage()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedTransactionAsync(db);
        var t0 = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        db.AppLogs.Add(StageEvent(1, t0, TransactionStages.Received, TransactionStages.ReceivedEventId, TransactionId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await new EfTransactionTrace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Events.Should().ContainSingle().Which.FailedStage.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_transaction_id_reports_Exists_false_with_empty_events_and_history()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var trace = await new EfTransactionTrace(db).GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        trace.Exists.Should().BeFalse();
        trace.Events.Should().BeEmpty();
        trace.History.Should().BeEmpty();
    }
}
