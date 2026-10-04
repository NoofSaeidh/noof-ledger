using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;
using Noof.Ledger.Persistence.Diagnostics.Integrity;
using Noof.Ledger.Persistence.Editing;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class LedgerWritePathTests(PostgresFixture fixture)
{
    static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
    static readonly Guid MainWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly Guid GroceriesId = new("00000000-0000-0000-0001-000000000001");
    static readonly Guid CoffeeId = new("00000000-0000-0000-0001-000000000017");
    static readonly Guid FeesId = new("00000000-0000-0000-0001-000000000013");
    static int nextMessageId;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    Task<LedgerDbContext> LedgerAsync() => fixture.CreateMigratedContextAsync();

    static LedgerDbContext Context(string connectionString, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).AddInterceptors(interceptors).Options);

    static async Task<Guid> AddWalletAsync(LedgerDbContext db, string name, CurrencyCode currency)
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), Name = name, Currency = currency, CreatedAt = Clock.GetUtcNow() };
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(Ct);
        return wallet.Id;
    }

    static async Task AddFxTermsAsync(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal rate, decimal feePercent)
    {
        db.WalletFxTerms.Add(new WalletFxTerms { WalletId = walletId, Currency = currency, Rate = rate, FeePercent = feePercent });
        await db.SaveChangesAsync(Ct);
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

    static Money Rsd(decimal amount) => new(amount, CurrencyCode.Rsd);

    static Money Eur(decimal amount) => new(amount, CurrencyCode.Eur);

    static ExchangeRate EurAt117 => new(CurrencyCode.Eur, 117m, CurrencyCode.Rsd);

    static CategorizationOutcome TransferOutcome(DateOnly day, TransferFacts facts) =>
        new([], day, TransactionKind: TransactionKind.Transfer, Transfer: facts);

    static Task<List<LineItem>> LinesOfAsync(LedgerDbContext db, Guid transactionId) =>
        db.LineItems.AsNoTracking().Where(line => line.TransactionId == transactionId).OrderBy(line => line.Ordinal).ToListAsync(Ct);

    static Task<Transfer> TransferOfAsync(LedgerDbContext db, Guid transactionId) =>
        db.Transfers.AsNoTracking().SingleAsync(row => row.TransactionId == transactionId, Ct);

    static Task ApplyAsync(LedgerDbContext db, Guid transactionId, CategorizationOutcome outcome) =>
        new EfCategorizationStore(db, Clock).ApplyAsync(transactionId, outcome, Ct);

    static async Task<IReadOnlyList<(Guid WalletId, Money Amount, EntryRole Role)>> EntriesOfAsync(LedgerDbContext db, Guid transactionId)
    {
        var entries = await db.Entries.AsNoTracking().Where(entry => entry.TransactionId == transactionId).ToListAsync(Ct);
        return
        [
            .. entries
                .OrderBy(entry => entry.Amount.Currency.Value)
                .ThenBy(entry => entry.Role)
                .ThenBy(entry => entry.Amount.Amount)
                .Select(entry => (entry.WalletId, entry.Amount, entry.Role)),
        ];
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
    public async Task A_transfer_moves_money_between_its_two_wallets_and_writes_no_line()
    {
        await using var db = await LedgerAsync();
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);

        await ApplyAsync(db, id, TransferOutcome(day, new TransferFacts(MainWalletId, Rsd(10000m), cash, Rsd(10000m), null, null, null)));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, id)).Should().Equal(
            (MainWalletId, Rsd(-10000m), EntryRole.Principal),
            (cash, Rsd(10000m), EntryRole.Principal));
        var stored = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id, Ct);
        stored.Kind.Should().Be(TransactionKind.Transfer);
        stored.Status.Should().Be(TransactionStatus.Completed);
        stored.WalletId.Should().Be(MainWalletId,
            "transactions.wallet_id holds the source leg, so code reading one wallet per record keeps working; the outcome named none");
        (await LinesOfAsync(db, id)).Should().BeEmpty("a transfer without a fee has no line items at all");
        var transfer = await TransferOfAsync(db, id);
        transfer.FromWalletId.Should().Be(MainWalletId);
        transfer.ToWalletId.Should().Be(cash);
        transfer.FeeLeg.Should().BeNull();
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(-10000m);
        (await BalanceAsync(db, cash, CurrencyCode.Rsd)).Should().Be(10000m);
    }

    [Fact]
    public async Task A_withdrawal_fee_is_a_rule_authored_fee_line_and_a_fee_entry_on_the_source()
    {
        await using var db = await LedgerAsync();
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);

        // "снял 10000 с райфа, комиссия 150": 10150 left the source, 10000 reached the cash (T-12).
        await ApplyAsync(db, id, TransferOutcome(day,
            new TransferFacts(MainWalletId, Rsd(10150m), cash, Rsd(10000m), Rsd(150m), TransferLeg.From, null)));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, id)).Should().Equal(
            (MainWalletId, Rsd(-10000m), EntryRole.Principal),
            (cash, Rsd(10000m), EntryRole.Principal),
            (MainWalletId, Rsd(-150m), EntryRole.Fee));
        var fee = (await LinesOfAsync(db, id)).Should().ContainSingle().Which;
        fee.Role.Should().Be(EntryRole.Fee);
        fee.Description.Should().Be("Fee");
        fee.Amount.Should().Be(Rsd(150m));
        fee.CategoryId.Should().Be(FeesId, "a fee is spending in Fees & Charges (T-5)");
        fee.CategorizedBy.Should().Be(CategorizationAuthority.Rule, "C# writes a fee line, never the model");
        fee.MerchantId.Should().BeNull();
        fee.Ordinal.Should().Be(1);
        (await TransferOfAsync(db, id)).FeeLeg.Should().Be(TransferLeg.From);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(-10150m, "the source's entries sum to its stored amount");
        (await BalanceAsync(db, cash, CurrencyCode.Rsd)).Should().Be(10000m);
    }

    [Fact]
    public async Task An_exchange_fee_kept_by_the_receiving_side_is_taken_from_what_arrived()
    {
        await using var db = await LedgerAsync();
        var cashEur = await AddWalletAsync(db, "Cash EUR", CurrencyCode.Eur);
        var cashRsd = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);

        // 100 EUR at 117 is 11700 RSD; the office kept 200 of it, so 11500 reached the destination.
        await ApplyAsync(db, id, TransferOutcome(day,
            new TransferFacts(cashEur, Eur(100m), cashRsd, Rsd(11500m), Rsd(200m), TransferLeg.To, EurAt117)));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, id)).Should().Equal(
            (cashEur, Eur(-100m), EntryRole.Principal),
            (cashRsd, Rsd(11700m), EntryRole.Principal),
            (cashRsd, Rsd(-200m), EntryRole.Fee));
        (await LinesOfAsync(db, id)).Should().ContainSingle().Which.Amount.Should().Be(Rsd(200m), "a fee line is in its leg's currency");
        var transfer = await TransferOfAsync(db, id);
        transfer.FeeLeg.Should().Be(TransferLeg.To);
        transfer.StatedRate.Should().Be(117m);
        transfer.StatedRateBase.Should().Be(CurrencyCode.Eur);
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id, Ct)).WalletId.Should().Be(cashEur);
        (await BalanceAsync(db, cashEur, CurrencyCode.Eur)).Should().Be(-100m);
        (await BalanceAsync(db, cashRsd, CurrencyCode.Rsd)).Should().Be(11500m);
    }

    [Fact]
    public async Task A_transfer_without_its_legs_with_line_items_or_with_a_fee_but_no_leg_is_refused_before_anything_is_written()
    {
        await using var db = await LedgerAsync();
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);
        var legs = new TransferFacts(MainWalletId, Rsd(10000m), cash, Rsd(10000m), null, null, null);
        CategorizationOutcome[] malformed =
        [
            new([], day, TransactionKind: TransactionKind.Transfer),
            TransferOutcome(day, legs) with { Items = [Line(250m, CurrencyCode.Rsd)] },
            TransferOutcome(day, legs with { From = Rsd(10150m), Fee = Rsd(150m) }),
        ];

        foreach (var outcome in malformed)
        {
            var act = () => ApplyAsync(db, id, outcome);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        db.ChangeTracker.Clear();
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id, Ct)).Status.Should().Be(TransactionStatus.Captured);
        (await LinesOfAsync(db, id)).Should().BeEmpty();
        (await EntriesOfAsync(db, id)).Should().BeEmpty();
        (await db.Transfers.CountAsync(row => row.TransactionId == id, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task A_corrected_transfer_replaces_its_fee_and_drops_one_the_correction_no_longer_names()
    {
        await using var db = await LedgerAsync();
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);
        await ApplyAsync(db, id, TransferOutcome(day,
            new TransferFacts(MainWalletId, Rsd(10150m), cash, Rsd(10000m), Rsd(150m), TransferLeg.From, null)));

        // "комиссия была 200"
        await ApplyAsync(db, id, AsCorrection(TransferOutcome(day,
            new TransferFacts(MainWalletId, Rsd(10200m), cash, Rsd(10000m), Rsd(200m), TransferLeg.From, null))));

        db.ChangeTracker.Clear();
        (await LinesOfAsync(db, id)).Should().ContainSingle("a fee line is replaced, never added to")
            .Which.Amount.Should().Be(Rsd(200m));
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(-10200m);

        // "комиссии не было"
        await ApplyAsync(db, id, AsCorrection(TransferOutcome(day,
            new TransferFacts(MainWalletId, Rsd(10000m), cash, Rsd(10000m), null, null, null))));

        db.ChangeTracker.Clear();
        (await LinesOfAsync(db, id)).Should().BeEmpty(
            "the Rule-authored fee line goes with the fee, though the model-line delete never reaches it");
        (await TransferOfAsync(db, id)).FeeLeg.Should().BeNull();
        (await EntriesOfAsync(db, id)).Should().Equal(
            (MainWalletId, Rsd(-10000m), EntryRole.Principal),
            (cash, Rsd(10000m), EntryRole.Principal));
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(-10000m);
        (await BalanceAsync(db, cash, CurrencyCode.Rsd)).Should().Be(10000m);
    }

    [Fact]
    public async Task A_record_that_stops_being_a_transfer_loses_its_legs_and_its_fee()
    {
        await using var db = await LedgerAsync();
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);
        await ApplyAsync(db, id, TransferOutcome(day,
            new TransferFacts(MainWalletId, Rsd(10150m), cash, Rsd(10000m), Rsd(150m), TransferLeg.From, null)));

        // "это был не перевод, а покупка на 300"
        await ApplyAsync(db, id, AsCorrection(Expense(day, MainWalletId, Line(300m, CurrencyCode.Rsd))));

        db.ChangeTracker.Clear();
        (await db.Transfers.CountAsync(row => row.TransactionId == id, Ct)).Should().Be(0, "a record that is no longer a transfer keeps no legs");
        (await LinesOfAsync(db, id)).Should().ContainSingle().Which.Role.Should().Be(EntryRole.Principal);
        (await EntriesOfAsync(db, id)).Should().Equal((MainWalletId, Rsd(-300m), EntryRole.Principal));
        var stored = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id, Ct);
        stored.Kind.Should().Be(TransactionKind.Expense);
        stored.WalletId.Should().Be(MainWalletId);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(-300m);
        (await BalanceAsync(db, cash, CurrencyCode.Rsd)).Should().BeNull("nothing is left in the wallet the destination leg was in");
    }

    [Fact]
    public async Task A_record_that_becomes_a_transfer_loses_every_principal_line_whoever_wrote_it()
    {
        await using var db = await LedgerAsync();
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);
        await ApplyAsync(db, id, Expense(day, MainWalletId, Line(10000m, CurrencyCode.Rsd)));
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = id,
            Description = "hand-written",
            Amount = Rsd(500m),
            CategoryId = GroceriesId,
            CategorizedBy = CategorizationAuthority.User,
            Ordinal = 2,
        });
        await db.SaveChangesAsync(Ct);

        // "это была не трата, а снятие с райфа, комиссия 150"
        await ApplyAsync(db, id, AsCorrection(TransferOutcome(day,
            new TransferFacts(MainWalletId, Rsd(10150m), cash, Rsd(10000m), Rsd(150m), TransferLeg.From, null))));

        db.ChangeTracker.Clear();
        var fee = (await LinesOfAsync(db, id)).Should()
            .ContainSingle("a transfer has no principal lines by definition, so even a User-authored one goes").Which;
        fee.Role.Should().Be(EntryRole.Fee);
        fee.Ordinal.Should().Be(1, "the fee follows every principal line, and none is left");
        (await EntriesOfAsync(db, id)).Should().Equal(
            (MainWalletId, Rsd(-10000m), EntryRole.Principal),
            (cash, Rsd(10000m), EntryRole.Principal),
            (MainWalletId, Rsd(-150m), EntryRole.Fee));
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(-10150m);
        (await BalanceAsync(db, cash, CurrencyCode.Rsd)).Should().Be(10000m);
    }

    [Fact]
    public async Task Entries_always_agree_with_the_lines_they_are_summed_from()
    {
        await using var db = await LedgerAsync();
        var wise = await AddWalletAsync(db, "Wise EUR", CurrencyCode.Eur);
        var cash = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var cashEur = await AddWalletAsync(db, "Cash EUR", CurrencyCode.Eur);
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
        var withdrawal = await CaptureAsync(db, day);
        await ApplyAsync(db, withdrawal, TransferOutcome(day,
            new TransferFacts(MainWalletId, Rsd(10150m), cash, Rsd(10000m), Rsd(150m), TransferLeg.From, null)));
        var exchange = await CaptureAsync(db, day);
        await ApplyAsync(db, exchange, TransferOutcome(day,
            new TransferFacts(cashEur, Eur(100m), cash, Rsd(11500m), Rsd(200m), TransferLeg.To, EurAt117)));
        var topUp = await CaptureAsync(db, day);
        await ApplyAsync(db, topUp, TransferOutcome(day, new TransferFacts(cash, Rsd(2000m), MainWalletId, Rsd(2000m), null, null, null)));
        var notATransfer = await CaptureAsync(db, day);
        await ApplyAsync(db, notATransfer, TransferOutcome(day,
            new TransferFacts(MainWalletId, Rsd(10150m), cash, Rsd(10000m), Rsd(150m), TransferLeg.From, null)));
        await ApplyAsync(db, notATransfer, AsCorrection(Expense(day, MainWalletId, Line(300m, CurrencyCode.Rsd))));
        var becameATransfer = await CaptureAsync(db, day);
        await ApplyAsync(db, becameATransfer, Expense(day, cash, Line(500m, CurrencyCode.Rsd)));
        await ApplyAsync(db, becameATransfer, AsCorrection(TransferOutcome(day,
            new TransferFacts(cash, Rsd(500m), MainWalletId, Rsd(500m), null, null, null))));
        var kaspi = await AddWalletAsync(db, "Kaspi KZT", CurrencyCode.Kzt);
        await AddFxTermsAsync(db, kaspi, CurrencyCode.Usd, 520m, 1m);
        var charged = await CaptureAsync(db, day);
        await ApplyAsync(db, charged, Expense(day, kaspi, Line(1000m, CurrencyCode.Kzt), Line(30m, CurrencyCode.Usd)));
        var saidCharge = await CaptureAsync(db, day);
        await ApplyAsync(db, saidCharge, Expense(day, kaspi, Line(30m, CurrencyCode.Usd))
            with { Charged = new StatedCharge(new Money(15_400m, CurrencyCode.Kzt), null, false) });
        var unpriced = await CaptureAsync(db, day);
        await ApplyAsync(db, unpriced, Expense(day, kaspi, Line(20m, CurrencyCode.Eur)));
        var noLongerSpending = await CaptureAsync(db, day);
        await ApplyAsync(db, noLongerSpending, Expense(day, kaspi, Line(30m, CurrencyCode.Usd)));
        await ApplyAsync(db, noLongerSpending, AsCorrection(Income(day, kaspi, Line(30m, CurrencyCode.Usd))));

        (await db.Entries.CountAsync(Ct)).Should().BeGreaterThan(0, "a rule over no entries would prove nothing");
        (await db.Transfers.CountAsync(Ct)).Should().Be(4, "a rule over no transfers would prove nothing about them");
        (await db.Charges.CountAsync(Ct)).Should().Be(2, "a rule over no charges would prove nothing about them");
        // The oracle is the production I-1 and I-2 checks: derived from the stored facts, independently of
        // LedgerPostings, which writes the entries.
        (await new PostingsDisagreeCheck(db).FindAsync(IntegrityScope.All, Ct)).Should().BeEmpty(
            "every expense and income has exactly minus/plus its lines per currency and role, a charge replacing the lines it "
            + "prices in the wallet's currency, every transfer posts each leg's stored amount on its own wallet with the fee on "
            + "its leg's, a statement has none (M5, T-12), every entry sits on its record's wallet or a transfer's destination, "
            + "and a charge prices its Principal lines at its own rate with its fees as the fee lines");
        (await new FactsMismatchKindCheck(db).FindAsync(IntegrityScope.All, Ct)).Should().BeEmpty(
            "a transfer has its legs, its source as the record's wallet, a fee leg exactly when it has one fee line, and no "
            + "principal line; a charge sits on a spending in a foreign currency; and no record holds what its kind does not");
    }

    [Fact]
    public async Task A_checkpoint_on_either_leg_absorbs_only_its_own_side_of_a_backdated_transfer()
    {
        await using var db = await LedgerAsync();
        var cashEur = await AddWalletAsync(db, "Cash EUR", CurrencyCode.Eur);
        var cashRsd = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var eurStatement = await CaptureAsync(db, new DateOnly(2026, 9, 20));
        await ApplyAsync(db, eurStatement, Statement(new DateOnly(2026, 9, 20), cashEur, 500m, CurrencyCode.Eur));

        // Told on the 22nd about the 18th: dated before the EUR statement, which already holds its effect.
        var exchange = await CaptureAsync(db, new DateOnly(2026, 9, 22));
        await ApplyAsync(db, exchange, TransferOutcome(new DateOnly(2026, 9, 18),
            new TransferFacts(cashEur, Eur(100m), cashRsd, Rsd(11700m), null, null, EurAt117)));

        (await BalanceAsync(db, cashEur, CurrencyCode.Eur)).Should().Be(500m, "the EUR statement of the 20th already absorbed the 18th's 100 EUR");
        (await BalanceAsync(db, cashRsd, CurrencyCode.Rsd)).Should().Be(11700m, "nothing anchors the RSD side, so its leg counts");

        var rsdStatement = await CaptureAsync(db, new DateOnly(2026, 9, 19));
        await ApplyAsync(db, rsdStatement, Statement(new DateOnly(2026, 9, 19), cashRsd, 15000m, CurrencyCode.Rsd));

        db.ChangeTracker.Clear();
        (await CheckpointOfAsync(db, rsdStatement)).ComputedBefore.Should().Be(11700m, "the destination leg on the 18th came before it");
        (await BalanceAsync(db, cashRsd, CurrencyCode.Rsd)).Should().Be(15000m);
        (await BalanceAsync(db, cashEur, CurrencyCode.Eur)).Should().Be(500m, "a checkpoint on one leg leaves the other wallet alone");

        var back = await CaptureAsync(db, new DateOnly(2026, 9, 21));
        await ApplyAsync(db, back, TransferOutcome(new DateOnly(2026, 9, 21),
            new TransferFacts(cashRsd, Rsd(5850m), cashEur, Eur(50m), null, null, EurAt117)));

        (await BalanceAsync(db, cashRsd, CurrencyCode.Rsd)).Should().Be(9150m, "dated after both checkpoints, both legs count");
        (await BalanceAsync(db, cashEur, CurrencyCode.Eur)).Should().Be(550m);
    }

    [Fact]
    public async Task Cancel_and_restore_of_a_transfer_move_both_balances_with_its_status()
    {
        await using var db = await LedgerAsync();
        var editor = new EfRecordEditor(db, Clock);
        var cashEur = await AddWalletAsync(db, "Cash EUR", CurrencyCode.Eur);
        var cashRsd = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var eurOpening = await CaptureAsync(db, new DateOnly(2026, 9, 1));
        await ApplyAsync(db, eurOpening, Statement(new DateOnly(2026, 9, 1), cashEur, 500m, CurrencyCode.Eur));
        var rsdOpening = await CaptureAsync(db, new DateOnly(2026, 9, 1));
        await ApplyAsync(db, rsdOpening, Statement(new DateOnly(2026, 9, 1), cashRsd, 1000m, CurrencyCode.Rsd));
        var exchange = await CaptureAsync(db, new DateOnly(2026, 9, 10));
        await ApplyAsync(db, exchange, TransferOutcome(new DateOnly(2026, 9, 10),
            new TransferFacts(cashEur, Eur(100m), cashRsd, Rsd(11500m), Rsd(200m), TransferLeg.To, EurAt117)));
        (await BalanceAsync(db, cashEur, CurrencyCode.Eur)).Should().Be(400m);
        (await BalanceAsync(db, cashRsd, CurrencyCode.Rsd)).Should().Be(12500m);

        await editor.CancelAsync(exchange, Ct);

        (await EntriesOfAsync(db, exchange)).Should().HaveCount(3, "Cancel changes the status, not the postings");
        (await db.Transfers.CountAsync(row => row.TransactionId == exchange, Ct)).Should().Be(1, "nor the legs");
        (await BalanceAsync(db, cashEur, CurrencyCode.Eur)).Should().Be(500m);
        (await BalanceAsync(db, cashRsd, CurrencyCode.Rsd)).Should().Be(1000m);

        await editor.RestoreAsync(exchange, Ct);

        (await BalanceAsync(db, cashEur, CurrencyCode.Eur)).Should().Be(400m);
        (await BalanceAsync(db, cashRsd, CurrencyCode.Rsd)).Should().Be(12500m);
    }

    [Fact]
    public async Task Restore_after_a_correction_applied_while_cancelled_restores_completed_and_an_untouched_capture_stays_captured()
    {
        await using var db = await LedgerAsync();
        var editor = new EfRecordEditor(db, Clock);
        var cashEur = await AddWalletAsync(db, "Cash EUR", CurrencyCode.Eur);
        var cashRsd = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var day = new DateOnly(2026, 9, 10);
        var held = await CaptureAsync(db, day);
        var untouched = await CaptureAsync(db, day);
        await editor.CancelAsync(held, Ct);
        await editor.CancelAsync(untouched, Ct);

        // A reply completes the held record while it is cancelled; ApplyAsync keeps it cancelled.
        await ApplyAsync(db, held, AsCorrection(TransferOutcome(day,
            new TransferFacts(cashEur, Eur(100m), cashRsd, Rsd(11700m), null, null, EurAt117))));
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == held, Ct)).Status.Should().Be(TransactionStatus.Cancelled);
        (await BalanceAsync(db, cashEur, CurrencyCode.Eur)).Should().BeNull("a cancelled record moves no balance");

        await editor.RestoreAsync(held, Ct);
        await editor.RestoreAsync(untouched, Ct);

        db.ChangeTracker.Clear();
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == held, Ct)).Status.Should().Be(TransactionStatus.Completed,
            "it was applied after the cancel, so the Captured it had before the cancel no longer describes it (A-22)");
        (await BalanceAsync(db, cashEur, CurrencyCode.Eur)).Should().Be(-100m);
        (await BalanceAsync(db, cashRsd, CurrencyCode.Rsd)).Should().Be(11700m);
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == untouched, Ct)).Status.Should().Be(TransactionStatus.Captured,
            "nothing was applied to it, so Restore puts back the status it had before the cancel, as before");
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
