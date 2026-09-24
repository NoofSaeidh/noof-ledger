using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class MoneyModelMigrationTests(PostgresFixture fixture)
{
    const string LastMigrationBeforeTheMoneyModel = "20260923224236_AddVoiceCapture";

    static readonly Guid MainWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly Guid Completed = new("bbbbbbbb-0000-0000-0000-000000000001");
    static readonly Guid CancelledAfterItsReading = new("bbbbbbbb-0000-0000-0000-000000000002");
    static readonly Guid CompletedWithNoLines = new("bbbbbbbb-0000-0000-0000-000000000005");

    [Fact]
    public async Task Every_existing_expense_gets_minus_its_lines_per_currency_in_the_wallet_it_was_captured_into()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await fixture.CreateContextAsync();
        await db.GetService<IMigrator>().MigrateAsync(LastMigrationBeforeTheMoneyModel, cancellationToken);

        // The shape noof_ledger has when the operator next starts the host: every record in the one seeded wallet.
        // A cancelled record keeps its lines, so it gets entries too - the balance leaves it out by its status,
        // and Restore brings it back with its money intact. A completed reading that found nothing has no lines,
        // so it gets no entry - not a zero one.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO public.transactions
                (id, wallet_id, raw_text, capture_kind, status, time_zone_id, occurred_at, occurred_on,
                 telegram_chat_id, telegram_message_id, created_at)
            VALUES
                ('bbbbbbbb-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000001',
                 'кофе 250, хлеб 100.5, coffee 3.50 eur', 0, 1, 'Europe/Belgrade',
                 '2026-09-20T08:00:00Z', '2026-09-20', 1, 1, '2026-09-20T08:00:00Z'),
                ('bbbbbbbb-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000001',
                 'такси 900', 0, 3, 'Europe/Belgrade',
                 '2026-09-20T09:00:00Z', '2026-09-20', 1, 2, '2026-09-20T09:00:00Z'),
                ('bbbbbbbb-0000-0000-0000-000000000003', '00000000-0000-0000-0000-000000000001',
                 'хлеб 80', 0, 0, 'Europe/Belgrade',
                 '2026-09-20T10:00:00Z', '2026-09-20', 1, 3, '2026-09-20T10:00:00Z'),
                ('bbbbbbbb-0000-0000-0000-000000000004', '00000000-0000-0000-0000-000000000001',
                 'непонятно что', 0, 2, 'Europe/Belgrade',
                 '2026-09-20T11:00:00Z', '2026-09-20', 1, 4, '2026-09-20T11:00:00Z'),
                ('bbbbbbbb-0000-0000-0000-000000000005', '00000000-0000-0000-0000-000000000001',
                 'привет', 0, 1, 'Europe/Belgrade',
                 '2026-09-20T12:00:00Z', '2026-09-20', 1, 5, '2026-09-20T12:00:00Z');

            INSERT INTO public.line_items
                (id, transaction_id, description, category_id, categorized_by, merchant_id, amount, currency)
            VALUES
                ('cccccccc-0000-0000-0000-000000000001', 'bbbbbbbb-0000-0000-0000-000000000001', 'кофе', NULL, 1, NULL, 250, 'RSD'),
                ('cccccccc-0000-0000-0000-000000000002', 'bbbbbbbb-0000-0000-0000-000000000001', 'хлеб', NULL, 4, NULL, 100.5, 'RSD'),
                ('cccccccc-0000-0000-0000-000000000003', 'bbbbbbbb-0000-0000-0000-000000000001', 'coffee', NULL, 1, NULL, 3.5, 'EUR'),
                ('cccccccc-0000-0000-0000-000000000004', 'bbbbbbbb-0000-0000-0000-000000000002', 'такси', NULL, 1, NULL, 900, 'RSD');
            """,
            cancellationToken);

        await db.Database.MigrateAsync(cancellationToken);

        var entries = await db.Entries.AsNoTracking().ToListAsync(cancellationToken);
        entries.Select(e => (e.TransactionId, e.WalletId, e.Amount, e.Role)).Should().BeEquivalentTo(
        [
            (Completed, MainWalletId, new Money(-350.5m, CurrencyCode.Rsd), EntryRole.Principal),
            (Completed, MainWalletId, new Money(-3.5m, CurrencyCode.Eur), EntryRole.Principal),
            (CancelledAfterItsReading, MainWalletId, new Money(-900m, CurrencyCode.Rsd), EntryRole.Principal),
        ]);
        entries.Should().NotContain(e => e.TransactionId == CompletedWithNoLines,
            "a completed record with no lines moved no money, so it gets no entry at all");

        (await db.Transactions.AsNoTracking().Select(t => t.Kind).Distinct().ToListAsync(cancellationToken))
            .Should().Equal(TransactionKind.Expense);
        (await db.Transactions.AsNoTracking().CountAsync(t => t.WalletId == MainWalletId, cancellationToken))
            .Should().Be(5, "every existing record keeps the wallet it was captured into");
    }
}
