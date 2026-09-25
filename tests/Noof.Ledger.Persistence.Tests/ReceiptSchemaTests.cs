using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Configurations;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class ReceiptSchemaTests(PostgresFixture fixture)
{
    static readonly Guid DefaultWalletId = new("00000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task Migrations_create_the_receipt_tables_from_empty()
    {
        await using var db = await fixture.CreateContextAsync();

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var applied = await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        applied.Should().Contain(id => id.EndsWith("_AddReceipts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Line_items_created_before_the_migration_are_backfilled_with_a_dense_per_transaction_ordinal()
    {
        await using var db = await fixture.CreateContextAsync();
        var migrator = db.GetService<IMigrator>();

        var beforeReceipts = db.Database.GetMigrations()
            .Single(id => id.EndsWith("_AddAppLog", StringComparison.Ordinal));
        await migrator.MigrateAsync(beforeReceipts, TestContext.Current.CancellationToken);

        var transactionId = Guid.NewGuid();
        var otherTransactionId = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO public.transactions
                (id, wallet_id, raw_text, status, time_zone_id, occurred_at, occurred_on, telegram_chat_id, telegram_message_id, created_at)
            VALUES
                (@t1, @wallet, 'coffee 250', 1, 'Europe/Belgrade', '2026-09-25T09:00:00Z', '2026-09-25', 900, 1, '2026-09-25T09:00:00Z'),
                (@t2, @wallet, 'lunch 800', 1, 'Europe/Belgrade', '2026-09-25T09:00:00Z', '2026-09-25', 900, 2, '2026-09-25T09:00:00Z');
            """,
            [new NpgsqlParameter("t1", transactionId), new NpgsqlParameter("t2", otherTransactionId), new NpgsqlParameter("wallet", DefaultWalletId)],
            TestContext.Current.CancellationToken);

        var (firstId, secondId, thirdId, otherId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        // Ids are not inserted in ascending order - the backfill must sort by id itself, not by
        // insertion order, since id order is the only order the pre-migration heap ever guaranteed.
        var ids = new[] { thirdId, firstId, secondId }.OrderBy(id => id).ToArray();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO public.line_items (id, transaction_id, description, categorized_by, amount, currency)
            VALUES
                (@a, @t1, 'first by id', 1, 100.00, 'EUR'),
                (@b, @t1, 'second by id', 1, 200.00, 'EUR'),
                (@c, @t1, 'third by id', 1, 300.00, 'EUR'),
                (@d, @t2, 'only line of the other transaction', 1, 50.00, 'EUR');
            """,
            [
                new NpgsqlParameter("a", ids[0]), new NpgsqlParameter("b", ids[1]), new NpgsqlParameter("c", ids[2]),
                new NpgsqlParameter("d", otherId), new NpgsqlParameter("t1", transactionId), new NpgsqlParameter("t2", otherTransactionId),
            ],
            TestContext.Current.CancellationToken);

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var ordinalsForFirst = await db.Database.SqlQueryRaw<int>(
            """
            SELECT ordinal AS "Value" FROM public.line_items WHERE transaction_id = @t1 ORDER BY id
            """,
            new NpgsqlParameter("t1", transactionId))
            .ToListAsync(TestContext.Current.CancellationToken);
        ordinalsForFirst.Should().Equal([1, 2, 3], "each pre-existing line item keeps id order as its ordinal, per transaction");

        var ordinalForOther = await db.Database.SqlQueryRaw<int>(
            """
            SELECT ordinal AS "Value" FROM public.line_items WHERE transaction_id = @t2
            """,
            new NpgsqlParameter("t2", otherTransactionId))
            .SingleAsync(TestContext.Current.CancellationToken);
        ordinalForOther.Should().Be(1, "the backfill restarts at 1 for every transaction, it does not run across the whole table");

        var isNullable = await db.Database.SqlQueryRaw<string>(
            """
            SELECT is_nullable AS "Value" FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'line_items' AND column_name = 'ordinal'
            """).SingleAsync(TestContext.Current.CancellationToken);
        isNullable.Should().Be("NO", "the column ends NOT NULL once every pre-existing row has a real ordinal");
    }

    [Fact]
    public async Task A_second_merchant_cannot_claim_a_tax_id_already_in_use()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var taxId = $"P{Guid.NewGuid():N}"[..20];
        db.Merchants.Add(new Merchant { Id = Guid.NewGuid(), DisplayName = "First", Kind = MerchantKind.Retail, TaxId = taxId });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.Merchants.Add(new Merchant { Id = Guid.NewGuid(), DisplayName = "Second", Kind = MerchantKind.Retail, TaxId = taxId });

        var act = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<DbUpdateException>();
        assertion.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be(MerchantConfiguration.TaxIdIndex);
    }

    [Fact]
    public async Task Two_merchants_may_both_have_no_tax_id()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Merchants.Add(new Merchant { Id = Guid.NewGuid(), DisplayName = "No PIB 1", Kind = MerchantKind.Retail, TaxId = null });
        db.Merchants.Add(new Merchant { Id = Guid.NewGuid(), DisplayName = "No PIB 2", Kind = MerchantKind.Retail, TaxId = null });

        var act = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("the partial index only guards non-null tax ids");
    }
}
