using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;
using Noof.Ledger.Persistence.Editing;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class LedgerWritePathTests(PostgresFixture fixture)
{
    static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
    static readonly Guid MainWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly Guid GroceriesId = new("00000000-0000-0000-0001-000000000001");
    static readonly Guid CoffeeId = new("00000000-0000-0000-0001-000000000017");
    static int nextMessageId;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    async Task<LedgerDbContext> LedgerAsync()
    {
        var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(Ct);
        return db;
    }

    static LedgerDbContext Context(string connectionString, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).AddInterceptors(interceptors).Options);

    static async Task<Guid> AddWalletAsync(LedgerDbContext db, string name, CurrencyCode currency)
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), Name = name, Currency = currency, CreatedAt = Clock.GetUtcNow() };
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(Ct);
        return wallet.Id;
    }

    // A text capture as EfCaptureStore leaves it: no wallet yet, sent at 10:00 UTC on the given day.
    static async Task<Guid> CaptureAsync(LedgerDbContext db, DateOnly sentOn)
    {
        var sentAt = new DateTimeOffset(sentOn.Year, sentOn.Month, sentOn.Day, 10, 0, 0, TimeSpan.Zero);
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            RawText = "test capture",
            Status = TransactionStatus.Captured,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = sentAt,
            OccurredOn = sentOn,
            TelegramChatId = 1,
            TelegramMessageId = Interlocked.Increment(ref nextMessageId),
            CreatedAt = sentAt,
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(Ct);
        return transaction.Id;
    }

    static CategorizedLineItem Line(decimal amount, CurrencyCode currency, Guid? categoryId = null) =>
        new("line", new Money(amount, currency), categoryId ?? GroceriesId, null);

    static CategorizationOutcome Expense(DateOnly day, Guid walletId, params CategorizedLineItem[] lines) =>
        new(lines, day, TransactionKind: TransactionKind.Expense, WalletId: walletId);

    static CategorizationOutcome Income(DateOnly day, Guid walletId, params CategorizedLineItem[] lines) =>
        new(lines, day, TransactionKind: TransactionKind.Income, WalletId: walletId);

    static CategorizationOutcome Statement(DateOnly day, Guid walletId, decimal amount, CurrencyCode currency) =>
        new([], day, TransactionKind: TransactionKind.BalanceCheck, WalletId: walletId, StatedBalance: new Money(amount, currency));

    static CategorizationOutcome AsCorrection(CategorizationOutcome outcome) =>
        outcome with { Kind = JobKind.Correct, Instruction = "correction" };

    static Task ApplyAsync(LedgerDbContext db, Guid transactionId, CategorizationOutcome outcome) =>
        new EfCategorizationStore(db, Clock).ApplyAsync(transactionId, outcome, Ct);

    static async Task<IReadOnlyList<(Guid WalletId, Money Amount, EntryRole Role)>> EntriesOfAsync(LedgerDbContext db, Guid transactionId)
    {
        var entries = await db.Entries.AsNoTracking().Where(entry => entry.TransactionId == transactionId).ToListAsync(Ct);
        return [.. entries.OrderBy(entry => entry.Amount.Currency.Value).Select(entry => (entry.WalletId, entry.Amount, entry.Role))];
    }

    static Task<BalanceCheck> CheckpointOfAsync(LedgerDbContext db, Guid transactionId) =>
        db.BalanceChecks.AsNoTracking().SingleAsync(check => check.TransactionId == transactionId, Ct);

    // Read through the view the echo and the dashboard read, never recomputed here. Null: the wallet has no
    // balance row in that currency at all.
    static async Task<decimal?> BalanceAsync(LedgerDbContext db, Guid walletId, CurrencyCode currency)
    {
        var rows = await db.Database.SqlQuery<decimal>(
            $"""SELECT balance AS "Value" FROM wallet_balances WHERE wallet_id = {walletId} AND currency = {currency.Value}""")
            .ToListAsync(Ct);
        return rows.Count == 0 ? null : rows.Single();
    }

    [Fact]
    public async Task An_expense_posts_one_negative_entry_per_currency_to_its_wallet()
    {
        await using var db = await LedgerAsync();
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);

        await ApplyAsync(db, id, Expense(day, MainWalletId,
            Line(250m, CurrencyCode.Rsd, CoffeeId), Line(1000m, CurrencyCode.Rsd), Line(3.50m, CurrencyCode.Eur, CoffeeId)));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, id)).Should().Equal(
            (MainWalletId, new Money(-3.50m, CurrencyCode.Eur), EntryRole.Principal),
            (MainWalletId, new Money(-1250m, CurrencyCode.Rsd), EntryRole.Principal));
        var stored = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id, Ct);
        stored.Kind.Should().Be(TransactionKind.Expense);
        stored.WalletId.Should().Be(MainWalletId, "the capture had no wallet; the outcome chose one");
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(-1250m);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Eur)).Should().Be(-3.50m, "an entry keeps its line's currency (M10)");
    }

    [Fact]
    public async Task An_income_posts_positive_entries()
    {
        await using var db = await LedgerAsync();
        var wise = await AddWalletAsync(db, "Wise EUR", CurrencyCode.Eur);
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);

        await ApplyAsync(db, id, Income(day, wise, Line(2000m, CurrencyCode.Eur), Line(150.25m, CurrencyCode.Eur)));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, id)).Should().Equal((wise, new Money(2150.25m, CurrencyCode.Eur), EntryRole.Principal));
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id, Ct)).Kind.Should().Be(TransactionKind.Income);
        (await BalanceAsync(db, wise, CurrencyCode.Eur)).Should().Be(2150.25m);
    }

    [Fact]
    public async Task A_balance_statement_is_a_checkpoint_holding_what_the_app_computed_just_before_it()
    {
        await using var db = await LedgerAsync();

        var opening = await CaptureAsync(db, new DateOnly(2026, 9, 10));
        await ApplyAsync(db, opening, Statement(new DateOnly(2026, 9, 10), MainWalletId, 10000m, CurrencyCode.Rsd));

        var bread = await CaptureAsync(db, new DateOnly(2026, 9, 12));
        await ApplyAsync(db, bread, Expense(new DateOnly(2026, 9, 12), MainWalletId, Line(1500m, CurrencyCode.Rsd)));
        var coffee = await CaptureAsync(db, new DateOnly(2026, 9, 14));
        await ApplyAsync(db, coffee, Expense(new DateOnly(2026, 9, 14), MainWalletId, Line(500m, CurrencyCode.Rsd, CoffeeId)));
        var cancelled = await CaptureAsync(db, new DateOnly(2026, 9, 15));
        await ApplyAsync(db, cancelled, Expense(new DateOnly(2026, 9, 15), MainWalletId, Line(700m, CurrencyCode.Rsd)));
        await new EfRecordEditor(db, Clock).CancelAsync(cancelled, Ct);
        var refund = await CaptureAsync(db, new DateOnly(2026, 9, 18));
        await ApplyAsync(db, refund, Income(new DateOnly(2026, 9, 18), MainWalletId, Line(300m, CurrencyCode.Rsd)));
        var later = await CaptureAsync(db, new DateOnly(2026, 9, 25));
        await ApplyAsync(db, later, Expense(new DateOnly(2026, 9, 25), MainWalletId, Line(999m, CurrencyCode.Rsd)));

        // Applied after a purchase dated later than it: processing order never changes a balance (M6).
        var statement = await CaptureAsync(db, new DateOnly(2026, 9, 21));
        await ApplyAsync(db, statement, Statement(new DateOnly(2026, 9, 21), MainWalletId, 9000m, CurrencyCode.Rsd));

        // Sent the day after the statement about the day before it: "вчера купил..." still falls before it.
        var yesterday = await CaptureAsync(db, new DateOnly(2026, 9, 22));
        await ApplyAsync(db, yesterday, Expense(new DateOnly(2026, 9, 20), MainWalletId, Line(4321m, CurrencyCode.Rsd)));

        db.ChangeTracker.Clear();
        var first = await CheckpointOfAsync(db, opening);
        first.Stated.Should().Be(new Money(10000m, CurrencyCode.Rsd));
        first.ComputedBefore.Should().Be(0m, "nothing came before the first statement");

        var checkpoint = await CheckpointOfAsync(db, statement);
        checkpoint.WalletId.Should().Be(MainWalletId);
        checkpoint.Stated.Should().Be(new Money(9000m, CurrencyCode.Rsd));
        checkpoint.ComputedBefore.Should().Be(8300m,
            "10000 stated, then -1500 and -500 and +300; the cancelled 700 and the later 999 do not count");

        (await EntriesOfAsync(db, statement)).Should().BeEmpty("a statement is a checkpoint, not an adjustment entry");
        (await db.LineItems.CountAsync(line => line.TransactionId == statement, Ct)).Should().Be(0);
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == statement, Ct)).Kind.Should().Be(TransactionKind.BalanceCheck);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(8001m,
            "the latest checkpoint's 9000 plus only what came after it: -999; the 4321 dated before it is absorbed");
    }

    [Fact]
    public async Task A_correction_that_moves_a_statements_day_recomputes_what_came_before_it()
    {
        await using var db = await LedgerAsync();
        var bread = await CaptureAsync(db, new DateOnly(2026, 9, 12));
        await ApplyAsync(db, bread, Expense(new DateOnly(2026, 9, 12), MainWalletId, Line(1500m, CurrencyCode.Rsd)));
        var later = await CaptureAsync(db, new DateOnly(2026, 9, 25));
        await ApplyAsync(db, later, Expense(new DateOnly(2026, 9, 25), MainWalletId, Line(999m, CurrencyCode.Rsd)));
        var statement = await CaptureAsync(db, new DateOnly(2026, 9, 21));
        await ApplyAsync(db, statement, Statement(new DateOnly(2026, 9, 21), MainWalletId, 9000m, CurrencyCode.Rsd));

        db.ChangeTracker.Clear();
        (await CheckpointOfAsync(db, statement)).ComputedBefore.Should().Be(-1500m);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(8001m);

        // "это было вчера" a few days later: only the day changes, the wallet and the amount stay.
        await ApplyAsync(db, statement, AsCorrection(Statement(new DateOnly(2026, 9, 26), MainWalletId, 9000m, CurrencyCode.Rsd)));

        db.ChangeTracker.Clear();
        (await CheckpointOfAsync(db, statement)).ComputedBefore.Should().Be(-2499m,
            "the 999 on Sept 25 now falls before the statement, so what the app computed just before it changes");
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(9000m, "nothing is dated after Sept 26");
    }

    [Fact]
    public async Task A_correction_from_an_expense_to_a_balance_statement_and_back_swaps_entries_for_a_checkpoint()
    {
        await using var db = await LedgerAsync();
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);

        await ApplyAsync(db, id, Expense(day, MainWalletId, Line(250m, CurrencyCode.Rsd)));
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(-250m);

        await ApplyAsync(db, id, AsCorrection(Statement(day, MainWalletId, 45000m, CurrencyCode.Rsd)));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, id)).Should().BeEmpty();
        (await db.LineItems.CountAsync(line => line.TransactionId == id, Ct)).Should().Be(0);
        var checkpoint = await CheckpointOfAsync(db, id);
        checkpoint.Stated.Should().Be(new Money(45000m, CurrencyCode.Rsd));
        checkpoint.ComputedBefore.Should().Be(0m, "the record's own former expense is excluded from what came before it");
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(45000m);

        await ApplyAsync(db, id, AsCorrection(Expense(day, MainWalletId, Line(300m, CurrencyCode.Rsd))));

        db.ChangeTracker.Clear();
        (await db.BalanceChecks.CountAsync(check => check.TransactionId == id, Ct)).Should().Be(0,
            "a record that is no longer a statement keeps no checkpoint");
        (await EntriesOfAsync(db, id)).Should().Equal((MainWalletId, new Money(-300m, CurrencyCode.Rsd), EntryRole.Principal));
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id, Ct)).Kind.Should().Be(TransactionKind.Expense);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(-300m);
    }

    [Fact]
    public async Task A_correction_that_changes_the_wallet_moves_the_entries()
    {
        await using var db = await LedgerAsync();
        var wise = await AddWalletAsync(db, "Wise EUR", CurrencyCode.Eur);
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);

        await ApplyAsync(db, id, Expense(day, MainWalletId, Line(3.50m, CurrencyCode.Eur, CoffeeId)));
        await ApplyAsync(db, id, AsCorrection(Expense(day, wise, Line(3.50m, CurrencyCode.Eur, CoffeeId))));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, id)).Should().Equal((wise, new Money(-3.50m, CurrencyCode.Eur), EntryRole.Principal));
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id, Ct)).WalletId.Should().Be(wise);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Eur)).Should().BeNull("nothing is left in the wallet it moved out of");
        (await BalanceAsync(db, wise, CurrencyCode.Eur)).Should().Be(-3.50m);
    }

    [Fact]
    public async Task A_balance_statement_moved_to_another_wallet_moves_its_checkpoint()
    {
        await using var db = await LedgerAsync();
        var cash = await AddWalletAsync(db, "Cash", CurrencyCode.Rsd);
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);

        await ApplyAsync(db, id, Statement(day, MainWalletId, 45000m, CurrencyCode.Rsd));
        await ApplyAsync(db, id, AsCorrection(Statement(day, cash, 45000m, CurrencyCode.Rsd)));

        db.ChangeTracker.Clear();
        (await CheckpointOfAsync(db, id)).WalletId.Should().Be(cash);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().BeNull();
        (await BalanceAsync(db, cash, CurrencyCode.Rsd)).Should().Be(45000m);
    }

    [Fact]
    public async Task Cancel_and_restore_keep_the_postings_and_the_balance_follows_the_status()
    {
        await using var db = await LedgerAsync();
        var editor = new EfRecordEditor(db, Clock);
        var statement = await CaptureAsync(db, new DateOnly(2026, 9, 10));
        await ApplyAsync(db, statement, Statement(new DateOnly(2026, 9, 10), MainWalletId, 1000m, CurrencyCode.Rsd));
        var coffee = await CaptureAsync(db, new DateOnly(2026, 9, 12));
        await ApplyAsync(db, coffee, Expense(new DateOnly(2026, 9, 12), MainWalletId, Line(250m, CurrencyCode.Rsd, CoffeeId)));
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(750m);

        await editor.CancelAsync(coffee, Ct);
        (await EntriesOfAsync(db, coffee)).Should().ContainSingle("Cancel changes the status, not the postings");
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(1000m);

        await editor.RestoreAsync(coffee, Ct);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(750m);

        await editor.CancelAsync(statement, Ct);
        (await db.BalanceChecks.CountAsync(check => check.TransactionId == statement, Ct)).Should().Be(1);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(-250m,
            "with its only checkpoint cancelled, the wallet is the sum of its entries");

        await editor.RestoreAsync(statement, Ct);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(750m);
    }

    [Fact]
    public async Task Entries_always_agree_with_the_lines_they_are_summed_from()
    {
        await using var db = await LedgerAsync();
        var wise = await AddWalletAsync(db, "Wise EUR", CurrencyCode.Eur);
        var day = new DateOnly(2026, 9, 10);
        var mixed = await CaptureAsync(db, day);
        await ApplyAsync(db, mixed, Expense(day, MainWalletId, Line(250m, CurrencyCode.Rsd), Line(3.50m, CurrencyCode.Eur)));
        var salary = await CaptureAsync(db, day);
        await ApplyAsync(db, salary, Income(day, wise, Line(2000m, CurrencyCode.Eur)));
        var statement = await CaptureAsync(db, day);
        await ApplyAsync(db, statement, Statement(day, MainWalletId, 45000m, CurrencyCode.Rsd));
        var refund = await CaptureAsync(db, day);
        await ApplyAsync(db, refund, Expense(day, MainWalletId, Line(100m, CurrencyCode.Rsd)));
        await ApplyAsync(db, refund, AsCorrection(Income(day, wise, Line(100m, CurrencyCode.Eur))));

        const string disagreeing = """
            SELECT count(*)::int AS "Value"
            FROM (
                SELECT t.id AS transaction_id, li.currency,
                       CASE t.kind WHEN 1 THEN 1 ELSE -1 END * SUM(li.amount) AS expected
                FROM transactions t
                JOIN line_items li ON li.transaction_id = t.id
                WHERE t.kind IN (0, 1)
                GROUP BY t.id, t.kind, li.currency
            ) lines
            FULL JOIN (
                SELECT transaction_id, currency, SUM(amount) AS actual
                FROM entries
                GROUP BY transaction_id, currency
            ) posted ON posted.transaction_id = lines.transaction_id AND posted.currency = lines.currency
            WHERE lines.expected IS DISTINCT FROM posted.actual
            """;
        const string misplaced = """
            SELECT count(*)::int AS "Value"
            FROM entries e
            JOIN transactions t ON t.id = e.transaction_id
            WHERE e.wallet_id IS DISTINCT FROM t.wallet_id
            """;

        (await db.Entries.CountAsync(Ct)).Should().BeGreaterThan(0, "a rule over no entries would prove nothing");
        (await db.Database.SqlQueryRaw<int>(disagreeing).ToListAsync(Ct)).Single()
            .Should().Be(0, "every expense and income has exactly minus/plus its lines per currency, and a statement has none (M5)");
        (await db.Database.SqlQueryRaw<int>(misplaced).ToListAsync(Ct)).Single()
            .Should().Be(0, "an entry is always in its record's wallet");
    }

    [Fact]
    public async Task Nothing_is_posted_when_the_write_fails_before_commit()
    {
        var connectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();
        var day = new DateOnly(2026, 9, 10);
        Guid expenseId, statementId;

        await using (var seed = Context(connectionString))
        {
            await seed.Database.MigrateAsync(Ct);
            expenseId = await CaptureAsync(seed, day);
            statementId = await CaptureAsync(seed, day);
        }

        await using (var breaking = Context(connectionString, new ThrowsBeforeCommitInterceptor()))
        {
            var expense = async () => await ApplyAsync(breaking, expenseId, Expense(day, MainWalletId, Line(250m, CurrencyCode.Rsd)));
            await expense.Should().ThrowAsync<InvalidOperationException>();
        }

        await using (var breaking = Context(connectionString, new ThrowsBeforeCommitInterceptor()))
        {
            var statement = async () => await ApplyAsync(breaking, statementId, Statement(day, MainWalletId, 45000m, CurrencyCode.Rsd));
            await statement.Should().ThrowAsync<InvalidOperationException>();
        }

        await using var verify = Context(connectionString);
        (await verify.Entries.CountAsync(Ct)).Should().Be(0, "the entries were written inside the rolled-back transaction");
        (await verify.BalanceChecks.CountAsync(Ct)).Should().Be(0, "so was the checkpoint");
        (await BalanceAsync(verify, MainWalletId, CurrencyCode.Rsd)).Should().BeNull();
    }
}
