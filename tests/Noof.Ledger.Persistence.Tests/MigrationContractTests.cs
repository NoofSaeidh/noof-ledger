using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

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
}
