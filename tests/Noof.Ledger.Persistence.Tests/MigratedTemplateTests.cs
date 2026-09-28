using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace Noof.Ledger.Persistence.Tests;

// EF records a migration as applied by id, so a template that is only ever migrated forward never
// sees an edit to an applied migration's SQL - noof_ledger_test_template spent a phase without the
// merchant_aliases TRUNCATE guard that way. Every test that asserts a trigger, a view or a function
// on a clone is only as good as the template it was cloned from, so this compares the two directly.
// A function's stored source keeps the line endings of whichever checkout ran the migration, hence
// the \r stripping: that difference is git's autocrlf, not drift.
[Collection("postgres")]
public class MigratedTemplateTests(PostgresFixture fixture)
{
    const string MigrationOwnedObjects =
        """
        SELECT "Value" FROM (
            SELECT 'migration ' || "MigrationId" AS "Value" FROM "__EFMigrationsHistory"
            UNION ALL
            SELECT 'column ' || table_name || '.' || column_name || ' ' || data_type || ' '
                   || is_nullable || ' ' || COALESCE(column_default, '')
            FROM information_schema.columns WHERE table_schema = 'public'
            UNION ALL
            SELECT 'constraint ' || conrelid::regclass || ' ' || conname || ' ' || pg_get_constraintdef(oid)
            FROM pg_constraint WHERE connamespace = 'public'::regnamespace
            UNION ALL
            SELECT 'index ' || indexdef FROM pg_indexes WHERE schemaname = 'public'
            UNION ALL
            SELECT 'trigger ' || pg_get_triggerdef(t.oid)
            FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
            WHERE NOT t.tgisinternal AND c.relnamespace = 'public'::regnamespace
            UNION ALL
            SELECT 'view ' || viewname || ' ' || definition FROM pg_views WHERE schemaname = 'public'
            UNION ALL
            SELECT 'function ' || replace(pg_get_functiondef(oid), E'\r', '')
            FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND prokind IN ('f', 'p')
        ) objects
        ORDER BY 1
        """;

    [Fact]
    public async Task A_clone_carries_exactly_what_migrating_an_empty_database_produces()
    {
        await using var fresh = await fixture.CreateContextAsync();
        await fresh.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await using var clone = await fixture.CreateMigratedContextAsync();

        var expected = await ObjectsAsync(fresh);
        var actual = await ObjectsAsync(clone);

        expected.Should().Contain(o => o.StartsWith("trigger CREATE TRIGGER merchant_aliases_no_truncate", StringComparison.Ordinal),
            "a comparison that never sees a migration-owned trigger would pass against any template at all");
        expected.Except(actual).Should().BeEmpty(
            "every test asserting a trigger, view or function on a clone relies on the clone having what the migrations produce today");
        actual.Except(expected).Should().BeEmpty(
            "a clone carrying objects no migration creates is testing a schema no fresh install has");
    }

    static Task<List<string>> ObjectsAsync(LedgerDbContext db) =>
        db.Database.SqlQueryRaw<string>(MigrationOwnedObjects).ToListAsync(TestContext.Current.CancellationToken);
}
