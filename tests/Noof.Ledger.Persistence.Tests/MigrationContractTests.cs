using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Diagnostics.BugReports;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class MigrationContractTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_create_the_database_from_empty()
    {
        await using var db = await fixture.CreateContextAsync();

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var applied = await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        applied.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Migrating_twice_is_a_no_op()
    {
        await using var db = await fixture.CreateContextAsync();

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var pending = await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken);
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task The_model_has_no_pending_changes()
    {
        await using var db = await fixture.CreateContextAsync();

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "a model change without a migration means the next deploy silently diverges from the schema");
    }

    [Fact]
    public async Task Existing_transactions_get_the_local_day_of_their_own_time_zone()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.GetService<IMigrator>().MigrateAsync("20260921071450_AddAppSecret", TestContext.Current.CancellationToken);

        // 22:30 UTC on 31 August is already 1 September in Belgrade and still 31 August in UTC. This is the
        // row noof_ledger's own history will go through when the operator next starts the host.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO public.transactions
                (id, wallet_id, raw_text, status, time_zone_id, occurred_at, telegram_chat_id, telegram_message_id, created_at)
            VALUES
                ('aaaaaaaa-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000001', 'кофе 250', 1,
                 'Europe/Belgrade', '2026-08-31T22:30:00Z', 1, 1, '2026-08-31T22:30:00Z'),
                ('aaaaaaaa-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000001', 'кофе 250', 1,
                 'Etc/UTC', '2026-08-31T22:30:00Z', 1, 2, '2026-08-31T22:30:00Z');
            """,
            TestContext.Current.CancellationToken);

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var days = await db.Transactions.AsNoTracking()
            .OrderBy(t => t.TelegramMessageId)
            .Select(t => t.OccurredOn)
            .ToListAsync(TestContext.Current.CancellationToken);
        days.Should().Equal(new DateOnly(2026, 9, 1), new DateOnly(2026, 8, 31));
    }

    // Spec R-1 and R-2: reports filed before the renumbering keep their meaning, and a Telegram report's message ids
    // become the reply address the Telegram layer writes - "<chat id>:<message id>".
    [Fact]
    public async Task Bug_reports_filed_before_the_reply_address_keep_their_meaning()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.GetService<IMigrator>().MigrateAsync("20261003144600_AddBugReports", TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO public.bug_reports
                (id, created_at, source, telegram_chat_id, telegram_message_id, status, closed_at, explanation_state,
                 explanation_attempts, explanation_next_at, explanation, looks_like_bug, reply_message_id)
            VALUES
                ('bbbbbbbb-0000-0000-0000-000000000001', '2026-10-02T09:00:00Z', 0, 111, 5, 1, '2026-10-02T10:00:00Z', 1,
                 0, '2026-10-02T09:00:00Z', 'Reply with the amount.', FALSE, 9),
                ('bbbbbbbb-0000-0000-0000-000000000002', '2026-10-02T09:01:00Z', 0, 111, 6, 0, NULL, 0,
                 0, '2026-10-02T09:01:00Z', NULL, NULL, NULL),
                ('bbbbbbbb-0000-0000-0000-000000000003', '2026-10-02T09:02:00Z', 1, NULL, NULL, 0, NULL, 2,
                 3, '2026-10-02T09:02:00Z', NULL, NULL, NULL);
            """,
            TestContext.Current.CancellationToken);

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var reports = await db.BugReports.AsNoTracking()
            .OrderBy(r => r.CreatedAt)
            .Select(r => new { r.Source, r.Status, r.ExplanationState, r.ReplyTo, r.DeliveredAs })
            .ToListAsync(TestContext.Current.CancellationToken);
        reports.Should().Equal(
            new
            {
                Source = BugReportSource.Telegram, Status = BugReportStatus.Closed, ExplanationState = BugExplanationState.Done,
                ReplyTo = (string?)"111:5", DeliveredAs = (string?)"9",
            },
            new
            {
                Source = BugReportSource.Telegram, Status = BugReportStatus.Open, ExplanationState = BugExplanationState.Pending,
                ReplyTo = (string?)"111:6", DeliveredAs = (string?)null,
            },
            new
            {
                Source = BugReportSource.Dashboard, Status = BugReportStatus.Open, ExplanationState = BugExplanationState.Failed,
                ReplyTo = (string?)null, DeliveredAs = (string?)null,
            });
    }

    static ServiceProvider Persistence(string connectionString) => new ServiceCollection()
        .AddNoofPersistence(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ledger"] = connectionString })
                .Build(),
            maxJobAttempts: 8)
        .BuildServiceProvider();

    [Fact]
    public async Task A_migrated_database_counts_no_pending_migration()
    {
        await using var services = Persistence(await fixture.CreateDatabaseConnectionStringAsync());

        (await services.CountPendingNoofMigrationsAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task An_empty_database_counts_every_migration_as_pending()
    {
        await using var services = Persistence(await fixture.CreateEmptyDatabaseConnectionStringAsync());
        using var scope = services.CreateScope();
        var every = scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.GetMigrations().Count();

        (await services.CountPendingNoofMigrationsAsync(TestContext.Current.CancellationToken))
            .Should().Be(every).And.BePositive();
    }
}
