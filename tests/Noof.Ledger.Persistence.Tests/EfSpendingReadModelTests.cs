using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Reporting;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfSpendingReadModelTests(PostgresFixture fixture)
{
    static int nextTelegramMessageId;

    static Wallet NewWallet(CurrencyCode currency, string name = "Cash") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Currency = currency,
    };

    static Category NewCategory(string slug, string nameEn) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        NameEn = nameEn,
        NameRu = nameEn,
        IsActive = true,
    };

    static Merchant NewMerchant(string displayName) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = displayName,
        Kind = MerchantKind.Retail,
    };

    static Transaction NewTransaction(
        Guid? walletId, DateTimeOffset occurredAt, string timeZoneId, TransactionStatus status,
        DateOnly? occurredOn = null, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        WalletId = walletId,
        RawText = "test capture",
        Status = status,
        TimeZoneId = timeZoneId,
        OccurredAt = occurredAt,
        OccurredOn = occurredOn ?? DateOnly.FromDateTime(occurredAt.UtcDateTime),
        TelegramChatId = 1,
        TelegramMessageId = Interlocked.Increment(ref nextTelegramMessageId),
        CreatedAt = occurredAt,
    };

    static LineItem NewLineItem(
        Guid transactionId, string description, Money amount, Guid? categoryId, Guid? merchantId, int ordinal = 1) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        Description = description,
        Amount = amount,
        CategoryId = categoryId,
        CategorizedBy = CategorizationAuthority.Model,
        MerchantId = merchantId,
        Ordinal = ordinal,
    };

    static readonly Guid GroceriesId = new("00000000-0000-0000-0001-000000000001");
    static readonly Guid SubscriptionsId = new("00000000-0000-0000-0001-000000000011");
    static readonly Guid FeesAndChargesId = new("00000000-0000-0000-0001-000000000013");
    static readonly Guid CoffeeId = new("00000000-0000-0000-0001-000000000017");
    static readonly Guid SalaryId = new("00000000-0000-0000-0001-000000000022");
    static readonly Guid RefundId = new("00000000-0000-0000-0001-000000000023");
    static readonly Guid OtherIncomeId = new("00000000-0000-0000-0001-000000000025");

    static Transaction NewTransferRecord(
        Guid walletId, DateTimeOffset occurredAt, TransactionStatus status = TransactionStatus.Completed, Guid? id = null)
    {
        var transaction = NewTransaction(walletId, occurredAt, "Europe/Belgrade", status, id: id);
        transaction.Kind = TransactionKind.Transfer;
        return transaction;
    }

    static Transfer NewTransfer(
        Guid transactionId, Wallet from, decimal fromAmount, Wallet to, decimal toAmount,
        TransferLeg? feeLeg = null, decimal? statedRate = null, CurrencyCode? statedRateBase = null) => new()
    {
        TransactionId = transactionId,
        FromWalletId = from.Id,
        From = new Money(fromAmount, from.Currency),
        ToWalletId = to.Id,
        To = new Money(toAmount, to.Currency),
        FeeLeg = feeLeg,
        StatedRate = statedRate,
        StatedRateBase = statedRateBase,
    };

    static LineItem NewFeeLine(Guid transactionId, Money amount, int ordinal = 1) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        Description = "Fee",
        Amount = amount,
        CategoryId = FeesAndChargesId,
        CategorizedBy = CategorizationAuthority.Rule,
        MerchantId = null,
        Ordinal = ordinal,
        Role = EntryRole.Fee,
    };

    [Fact]
    public async Task RecentAsync_returns_transactions_newest_first_including_ones_awaiting_categorisation_and_ones_that_failed()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wallet = NewWallet(CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var oldest = NewTransaction(wallet.Id, now.AddHours(-2), "Europe/Belgrade", TransactionStatus.Failed);
        var middle = NewTransaction(wallet.Id, now.AddHours(-1), "Europe/Belgrade", TransactionStatus.Captured);
        var newest = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        db.Transactions.AddRange(oldest, middle, newest);
        db.LineItems.Add(NewLineItem(newest.Id, "coffee", new Money(3.5m, CurrencyCode.Eur), null, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(10, RecentView.All, TestContext.Current.CancellationToken);

        recent.Select(r => r.Id).Should().ContainInOrder(newest.Id, middle.Id, oldest.Id);
        recent.Single(r => r.Id == oldest.Id).Status.Should().Be(TransactionStatus.Failed);
        recent.Single(r => r.Id == oldest.Id).Items.Should().BeEmpty("a failed transaction still has to show up, not vanish");
        recent.Single(r => r.Id == middle.Id).Items.Should().BeEmpty("still awaiting categorisation");
        var newestRow = recent.Single(r => r.Id == newest.Id);
        newestRow.WalletName.Should().Be(wallet.Name);
        newestRow.OccurredOn.Should().Be(new DateOnly(2026, 9, 15));
        newestRow.Items.Should().ContainSingle().Which.Description.Should().Be("coffee");
    }

    [Fact]
    public async Task RecentAsync_resolves_category_and_merchant_names_and_leaves_them_null_when_absent()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wallet = NewWallet(CurrencyCode.Eur);
        var category = NewCategory("test-recent-groceries", "Groceries");
        var merchant = NewMerchant("Lidl");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        db.Merchants.Add(merchant);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A fixed literal, not DateTimeOffset.UtcNow: timestamptz keeps microseconds while a
        // .NET tick is 100ns, so a value with a non-zero final tick digit does not survive the
        // round trip and an exact-equality assertion fails on roughly nine runs in ten.
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var transaction = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        db.Transactions.Add(transaction);
        db.LineItems.AddRange(
            NewLineItem(transaction.Id, "bread", new Money(2m, CurrencyCode.Eur), category.Id, merchant.Id),
            NewLineItem(transaction.Id, "cash withdrawal fee", new Money(1m, CurrencyCode.Eur), null, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(10, RecentView.All, TestContext.Current.CancellationToken);

        recent.Single().Items.Should().BeEquivalentTo(
        [
            new RecentLineItem("bread", new Money(2m, CurrencyCode.Eur), "Groceries", "Lidl"),
            new RecentLineItem("cash withdrawal fee", new Money(1m, CurrencyCode.Eur), null, null),
        ]);
    }

    [Fact]
    public async Task RecentAsync_respects_the_limit_and_returns_only_the_most_recent_ones()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wallet = NewWallet(CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // A fixed literal, not DateTimeOffset.UtcNow: timestamptz keeps microseconds while a
        // .NET tick is 100ns, so a value with a non-zero final tick digit does not survive the
        // round trip and an exact-equality assertion fails on roughly nine runs in ten.
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var transactions = Enumerable.Range(0, 5)
            .Select(i => NewTransaction(wallet.Id, now.AddMinutes(i), "Europe/Belgrade", TransactionStatus.Captured))
            .ToList();
        db.Transactions.AddRange(transactions);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(2, RecentView.All, TestContext.Current.CancellationToken);

        recent.Select(r => r.Id).Should().Equal(
            [transactions[4].Id, transactions[3].Id],
            "only the two most recently occurred transactions should survive the limit");
    }

    [Fact]
    public async Task ThisMonthAsync_buckets_by_the_day_the_purchase_happened_not_when_the_message_was_sent()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wallet = NewWallet(CurrencyCode.Eur);
        var category = NewCategory("test-occurred-on", "Occurred On");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // "вчера" typed on 1 September is an August purchase; a message sent late on 31 August
        // and dated 1 September belongs to September.
        var backdated = NewTransaction(wallet.Id, new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero), "Europe/Belgrade",
            TransactionStatus.Completed, occurredOn: new DateOnly(2026, 8, 31));
        var forwardDated = NewTransaction(wallet.Id, new DateTimeOffset(2026, 8, 31, 23, 30, 0, TimeSpan.Zero), "Europe/Belgrade",
            TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 1));
        db.Transactions.AddRange(backdated, forwardDated);
        db.LineItems.AddRange(
            NewLineItem(backdated.Id, "august", new Money(20m, CurrencyCode.Eur), category.Id, null),
            NewLineItem(forwardDated.Id, "september", new Money(10m, CurrencyCode.Eur), category.Id, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db,
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero)), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.FirstDay.Should().Be(new DateOnly(2026, 9, 1));
        summary.Totals.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new MonthTotal("Occurred On", CurrencyCode.Eur, 10m));
    }

    [Fact]
    public async Task Cancelled_records_are_left_out_of_recent_and_of_the_month()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wallet = NewWallet(CurrencyCode.Eur);
        var category = NewCategory("test-cancelled", "Cancelled Test");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var kept = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        var cancelled = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Cancelled);
        db.Transactions.AddRange(kept, cancelled);
        db.LineItems.AddRange(
            NewLineItem(kept.Id, "kept", new Money(5m, CurrencyCode.Eur), category.Id, null),
            NewLineItem(cancelled.Id, "cancelled", new Money(1000m, CurrencyCode.Eur), category.Id, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        (await readModel.RecentAsync(10, RecentView.All, TestContext.Current.CancellationToken)).Select(r => r.Id).Should().Equal(kept.Id);
        (await readModel.ThisMonthAsync(TestContext.Current.CancellationToken)).Totals.Should().ContainSingle()
            .Which.Amount.Should().Be(5m);
    }

    [Fact]
    public async Task RecentAsync_gives_a_time_of_day_only_when_the_purchase_day_is_the_send_day()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wallet = NewWallet(CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // 10:15 UTC is 12:15 in Belgrade in September.
        var sentAt = new DateTimeOffset(2026, 9, 21, 10, 15, 0, TimeSpan.Zero);
        var sameDay = NewTransaction(wallet.Id, sentAt, "Europe/Belgrade", TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 21));
        var backdated = NewTransaction(wallet.Id, sentAt, "Europe/Belgrade", TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 20));
        db.Transactions.AddRange(sameDay, backdated);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(sentAt), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(10, RecentView.All, TestContext.Current.CancellationToken);

        recent.Select(r => r.Id).Should().Equal(sameDay.Id, backdated.Id);
        recent[0].LocalTime.Should().Be(new TimeOnly(12, 15));
        recent[1].LocalTime.Should().BeNull("a backdated purchase gets no invented time of day (D3)");
    }

    [Fact]
    public async Task ThisMonthAsync_never_adds_two_currencies_together()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wallet = NewWallet(CurrencyCode.Rsd);
        var category = NewCategory("test-no-fx-mixing", "Groceries");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var rsdTransaction = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        var eurTransaction = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        db.Transactions.AddRange(rsdTransaction, eurTransaction);
        db.LineItems.AddRange(
            NewLineItem(rsdTransaction.Id, "market", new Money(1500m, CurrencyCode.Rsd), category.Id, null),
            NewLineItem(rsdTransaction.Id, "market again", new Money(500m, CurrencyCode.Rsd), category.Id, null),
            NewLineItem(eurTransaction.Id, "import", new Money(20m, CurrencyCode.Eur), category.Id, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.Totals.Should().HaveCount(2, "RSD and EUR must never collapse into one combined figure");
        summary.Totals.Should().Contain(new MonthTotal("Groceries", CurrencyCode.Rsd, 2000m));
        summary.Totals.Should().Contain(new MonthTotal("Groceries", CurrencyCode.Eur, 20m));
    }

    [Fact]
    public async Task ThisMonthAsync_labels_a_line_item_with_no_category_instead_of_dropping_it()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wallet = NewWallet(CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var transaction = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Captured);
        db.Transactions.Add(transaction);
        db.LineItems.Add(NewLineItem(transaction.Id, "unlabelled", new Money(9.99m, CurrencyCode.Eur), null, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.Totals.Should().ContainSingle("uncategorised money must still be counted, never silently omitted")
            .Which.Should().BeEquivalentTo(new MonthTotal(EfSpendingReadModel.UncategorisedLabel, CurrencyCode.Eur, 9.99m));
    }

    [Fact]
    public async Task ThisMonthAsync_excludes_line_items_outside_the_current_month_boundaries()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wallet = NewWallet(CurrencyCode.Eur);
        var category = NewCategory("test-outside-boundaries", "Groceries");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var lastMonth = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var thisMonth = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var nextMonth = new DateTimeOffset(2026, 10, 1, 0, 0, 1, TimeSpan.Zero);
        var lastMonthTransaction = NewTransaction(wallet.Id, lastMonth, "Etc/UTC", TransactionStatus.Completed);
        var thisMonthTransaction = NewTransaction(wallet.Id, thisMonth, "Etc/UTC", TransactionStatus.Completed);
        var nextMonthTransaction = NewTransaction(wallet.Id, nextMonth, "Etc/UTC", TransactionStatus.Completed);
        db.Transactions.AddRange(lastMonthTransaction, thisMonthTransaction, nextMonthTransaction);
        db.LineItems.AddRange(
            NewLineItem(lastMonthTransaction.Id, "august", new Money(1m, CurrencyCode.Eur), category.Id, null),
            NewLineItem(thisMonthTransaction.Id, "september", new Money(2m, CurrencyCode.Eur), category.Id, null),
            NewLineItem(nextMonthTransaction.Id, "october", new Money(4m, CurrencyCode.Eur), category.Id, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(thisMonth), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.Totals.Should().ContainSingle().Which.Should().BeEquivalentTo(new MonthTotal("Groceries", CurrencyCode.Eur, 2m));
    }

    [Fact]
    public async Task RecentAsync_shows_a_capture_that_has_no_wallet_yet()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var captured = NewTransaction(null, now, "Europe/Belgrade", TransactionStatus.Captured);
        db.Transactions.Add(captured);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(10, RecentView.All, TestContext.Current.CancellationToken);

        recent.Should().ContainSingle(r => r.Id == captured.Id, "a message awaiting its reading has no wallet yet and must still show")
            .Which.WalletName.Should().BeEmpty();
    }

    [Fact]
    public async Task ThisMonthAsync_counts_expenses_only_never_income_or_statements()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wallet = NewWallet(CurrencyCode.Rsd);
        var category = NewCategory("test-expenses-only", "Groceries");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var expense = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        var salary = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        salary.Kind = TransactionKind.Income;
        var statement = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        statement.Kind = TransactionKind.BalanceCheck;
        db.Transactions.AddRange(expense, salary, statement);
        db.LineItems.AddRange(
            NewLineItem(expense.Id, "market", new Money(250m, CurrencyCode.Rsd), category.Id, null),
            NewLineItem(salary.Id, "salary", new Money(2000m, CurrencyCode.Eur), category.Id, null),
            NewLineItem(statement.Id, "statement", new Money(45_000m, CurrencyCode.Rsd), category.Id, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.Totals.Should().ContainSingle().Which.Should().Be(
            new MonthTotal("Groceries", CurrencyCode.Rsd, 250m),
            "a salary is not spending, and neither is a statement of what the wallet holds");
    }

    [Fact]
    public async Task RecentAsync_marks_each_row_with_its_kind_and_gives_a_transfer_its_line()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var raiffeisen = NewWallet(CurrencyCode.Rsd, "Raiffeisen RSD");
        var cash = NewWallet(CurrencyCode.Rsd, "Cash RSD");
        db.Wallets.AddRange(raiffeisen, cash);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var expense = NewTransaction(raiffeisen.Id, now.AddMinutes(-2), "Europe/Belgrade", TransactionStatus.Completed);
        var salary = NewTransaction(raiffeisen.Id, now.AddMinutes(-1), "Europe/Belgrade", TransactionStatus.Completed);
        salary.Kind = TransactionKind.Income;
        var withdrawal = NewTransferRecord(raiffeisen.Id, now);
        db.Transactions.AddRange(expense, salary, withdrawal);
        db.Transfers.Add(NewTransfer(withdrawal.Id, raiffeisen, 10150m, cash, 10000m, TransferLeg.From));
        db.LineItems.Add(NewFeeLine(withdrawal.Id, new Money(150m, CurrencyCode.Rsd)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(10, RecentView.All, TestContext.Current.CancellationToken);

        recent.Single(r => r.Id == expense.Id).Kind.Should().Be(TransactionKind.Expense);
        recent.Single(r => r.Id == expense.Id).Transfer.Should().BeNull();
        recent.Single(r => r.Id == salary.Id).Kind.Should().Be(TransactionKind.Income);
        var row = recent.Single(r => r.Id == withdrawal.Id);
        row.Kind.Should().Be(TransactionKind.Transfer);
        row.Transfer.Should().Be(new TransferLine(
            "Raiffeisen RSD", new Money(10150m, CurrencyCode.Rsd), "Cash RSD", new Money(10000m, CurrencyCode.Rsd),
            new Money(150m, CurrencyCode.Rsd), TransferLeg.From, null),
            "what each wallet moved, the fee on the leg that paid it, and no rate between two dinar wallets");
        row.Items.Should().ContainSingle().Which.Should().Be(
            new RecentLineItem("Fee", new Money(150m, CurrencyCode.Rsd), "Fees & Charges", null, EntryRole.Fee),
            "a fee line is marked as a fee, so the page never shows it as a purchase");
    }

    [Fact]
    public async Task RecentAsync_views_split_transfers_from_spending_and_income()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wise = NewWallet(CurrencyCode.Eur, "Wise EUR");
        var cash = NewWallet(CurrencyCode.Eur, "Cash EUR");
        db.Wallets.AddRange(wise, cash);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var expense = NewTransaction(wise.Id, now.AddMinutes(-4), "Europe/Belgrade", TransactionStatus.Completed);
        var salary = NewTransaction(wise.Id, now.AddMinutes(-3), "Europe/Belgrade", TransactionStatus.Completed);
        salary.Kind = TransactionKind.Income;
        var statement = NewTransaction(wise.Id, now.AddMinutes(-2), "Europe/Belgrade", TransactionStatus.Completed);
        statement.Kind = TransactionKind.BalanceCheck;
        var awaiting = NewTransaction(null, now.AddMinutes(-1), "Europe/Belgrade", TransactionStatus.Captured);
        var withdrawal = NewTransferRecord(wise.Id, now);
        db.Transactions.AddRange(expense, salary, statement, awaiting, withdrawal);
        db.Transfers.Add(NewTransfer(withdrawal.Id, wise, 200m, cash, 200m));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var spendingAndIncome = await readModel.RecentAsync(10, RecentView.SpendingAndIncome, TestContext.Current.CancellationToken);
        var transfers = await readModel.RecentAsync(10, RecentView.Transfers, TestContext.Current.CancellationToken);
        var all = await readModel.RecentAsync(10, RecentView.All, TestContext.Current.CancellationToken);

        spendingAndIncome.Select(r => r.Id).Should().Equal(
            [awaiting.Id, statement.Id, salary.Id, expense.Id],
            "a transfer is seen on its own (T-9); everything else, a record not yet read included, is the default lens");
        transfers.Select(r => r.Id).Should().Equal(withdrawal.Id);
        all.Select(r => r.Id).Should().Equal(withdrawal.Id, awaiting.Id, statement.Id, salary.Id, expense.Id);
    }

    [Fact]
    public async Task A_transfer_line_derives_its_rate_from_the_principals_unless_one_was_stated()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wise = NewWallet(CurrencyCode.Eur, "Wise EUR");
        var raiffeisen = NewWallet(CurrencyCode.Rsd, "Raiffeisen RSD");
        db.Wallets.AddRange(wise, raiffeisen);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var withFee = NewTransferRecord(wise.Id, now.AddMinutes(-3));
        var withFeeOnTo = NewTransferRecord(wise.Id, now.AddMinutes(-2));
        var stated = NewTransferRecord(wise.Id, now.AddMinutes(-1));
        var statedInTo = NewTransferRecord(raiffeisen.Id, now);
        db.Transactions.AddRange(withFee, withFeeOnTo, stated, statedInTo);
        db.Transfers.AddRange(
            NewTransfer(withFee.Id, wise, 101m, raiffeisen, 11700m, TransferLeg.From),
            NewTransfer(withFeeOnTo.Id, wise, 100m, raiffeisen, 11600m, TransferLeg.To),
            NewTransfer(stated.Id, wise, 50m, raiffeisen, 5867.50m, statedRate: 117.35m, statedRateBase: CurrencyCode.Eur),
            NewTransfer(statedInTo.Id, raiffeisen, 5867.50m, wise, 50m, statedRate: 117.35m, statedRateBase: CurrencyCode.Eur));
        db.LineItems.AddRange(
            NewFeeLine(withFee.Id, new Money(1m, CurrencyCode.Eur)),
            NewFeeLine(withFeeOnTo.Id, new Money(100m, CurrencyCode.Rsd)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(10, RecentView.Transfers, TestContext.Current.CancellationToken);

        recent.Single(r => r.Id == withFee.Id).Transfer!.Rate.Should().Be(
            new ExchangeRate(CurrencyCode.Eur, 117m, CurrencyCode.Rsd),
            "101 EUR left the wallet but 1 EUR of it was the fee: 100 EUR bought 11 700 RSD, not 101");
        recent.Single(r => r.Id == withFeeOnTo.Id).Transfer!.Rate.Should().Be(
            new ExchangeRate(CurrencyCode.Eur, 117m, CurrencyCode.Rsd),
            "11 600 RSD arrived after a 100 RSD fee was taken from it: 100 EUR bought 11 700 RSD, not 11 600");
        recent.Single(r => r.Id == stated.Id).Transfer!.Rate.Should().Be(
            new ExchangeRate(CurrencyCode.Eur, 117.35m, CurrencyCode.Rsd),
            "a stated rate is shown as stated, never re-derived from rounded amounts");
        recent.Single(r => r.Id == statedInTo.Id).Transfer!.Rate.Should().Be(
            new ExchangeRate(CurrencyCode.Eur, 117.35m, CurrencyCode.Rsd),
            "a rate stated in the currency bought is quoted against the currency paid");
    }

    [Fact]
    public async Task ThisMonthAsync_reports_income_by_category_as_received_and_never_as_spent()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var wise = NewWallet(CurrencyCode.Eur, "Wise EUR");
        db.Wallets.Add(wise);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var coffee = NewTransaction(wise.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        var salary = NewTransaction(wise.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        salary.Kind = TransactionKind.Income;
        var freelance = NewTransaction(wise.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        freelance.Kind = TransactionKind.Income;
        db.Transactions.AddRange(coffee, salary, freelance);
        db.LineItems.AddRange(
            NewLineItem(coffee.Id, "coffee", new Money(3.20m, CurrencyCode.Eur), CoffeeId, null),
            NewLineItem(salary.Id, "salary", new Money(2800m, CurrencyCode.Eur), SalaryId, null),
            NewLineItem(freelance.Id, "freelance", new Money(450m, CurrencyCode.Eur), OtherIncomeId, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.Totals.Should().BeEquivalentTo([new MonthTotal("Coffee", CurrencyCode.Eur, 3.20m)]);
        summary.Received.Should().BeEquivalentTo(
        [
            new MonthTotal("Salary", CurrencyCode.Eur, 2800m),
            new MonthTotal("Other income", CurrencyCode.Eur, 450m),
        ]);
    }

    [Fact]
    public async Task ThisMonthAsync_is_identical_with_and_without_the_months_transfer_principals_and_counts_their_fees()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var cashEur = NewWallet(CurrencyCode.Eur, "Cash EUR");
        var cashRsd = NewWallet(CurrencyCode.Rsd, "Cash RSD");
        var raiffeisen = NewWallet(CurrencyCode.Rsd, "Raiffeisen RSD");
        db.Wallets.AddRange(cashEur, cashRsd, raiffeisen);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var market = NewTransaction(cashRsd.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        var salary = NewTransaction(cashEur.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        salary.Kind = TransactionKind.Income;
        db.Transactions.AddRange(market, salary);
        db.LineItems.AddRange(
            NewLineItem(market.Id, "market", new Money(2500m, CurrencyCode.Rsd), GroceriesId, null),
            NewLineItem(salary.Id, "salary", new Money(2800m, CurrencyCode.Eur), SalaryId, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);
        var withoutTransfers = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        var exchange = NewTransferRecord(cashEur.Id, now.AddHours(1));
        var withdrawal = NewTransferRecord(raiffeisen.Id, now.AddHours(2));
        db.Transactions.AddRange(exchange, withdrawal);
        db.Transfers.AddRange(
            NewTransfer(exchange.Id, cashEur, 100m, cashRsd, 11700m),
            NewTransfer(withdrawal.Id, raiffeisen, 10150m, cashRsd, 10000m, TransferLeg.From));
        db.LineItems.AddRange(
            NewFeeLine(withdrawal.Id, new Money(150m, CurrencyCode.Rsd)),
            // A principal line on a transfer is something the write path never leaves (spec §2); the report must
            // filter it by role rather than trust that.
            NewLineItem(exchange.Id, "stray principal", new Money(100m, CurrencyCode.Eur), GroceriesId, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var withTransfers = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        withoutTransfers.Totals.Should().BeEquivalentTo([new MonthTotal("Groceries", CurrencyCode.Rsd, 2500m)]);
        withoutTransfers.Received.Should().BeEquivalentTo([new MonthTotal("Salary", CurrencyCode.Eur, 2800m)]);
        withTransfers.Received.Should().BeEquivalentTo(withoutTransfers.Received);
        withTransfers.Totals.Should().BeEquivalentTo(
        [
            new MonthTotal("Groceries", CurrencyCode.Rsd, 2500m),
            new MonthTotal("Fees & Charges", CurrencyCode.Rsd, 150m),
        ],
            "a transfer only moves the operator's own money; the fee it cost is the one part that is spending (T-5, T-9)");
    }

    [Fact]
    public async Task ThisMonthAsync_keeps_a_foreign_line_no_charge_prices_in_its_own_currency()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = NewWallet(CurrencyCode.Kzt, "Kaspi KZT");
        db.Wallets.Add(kaspi);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var spending = NewTransaction(kaspi.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        db.Transactions.Add(spending);
        db.LineItems.AddRange(
            NewLineItem(spending.Id, "App Store", new Money(30m, CurrencyCode.Usd), SubscriptionsId, null),
            NewFeeLine(spending.Id, new Money(156m, CurrencyCode.Kzt), ordinal: 2));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.Totals.Should().BeEquivalentTo(
        [
            new MonthTotal("Subscriptions", CurrencyCode.Usd, 30m),
            new MonthTotal("Fees & Charges", CurrencyCode.Kzt, 156m),
        ],
            "with no charge there is no figure in the wallet's currency, and This month never converts (8b spec A-1, §2)");
    }

    [Fact]
    public async Task ThisMonthAsync_counts_a_charged_foreign_spending_as_its_share_of_the_charge_in_the_wallets_currency()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = NewWallet(CurrencyCode.Kzt, "Kaspi KZT");
        db.Wallets.Add(kaspi);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var spending = NewTransaction(kaspi.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        db.Transactions.Add(spending);
        db.LineItems.AddRange(
            NewLineItem(spending.Id, "App Store", new Money(20.00m, CurrencyCode.Usd), SubscriptionsId, null, ordinal: 1),
            NewLineItem(spending.Id, "iCloud", new Money(5.00m, CurrencyCode.Usd), SubscriptionsId, null, ordinal: 2),
            NewLineItem(spending.Id, "Coffee beans", new Money(5.00m, CurrencyCode.Usd), CoffeeId, null, ordinal: 3),
            NewFeeLine(spending.Id, new Money(156.00m, CurrencyCode.Kzt), ordinal: 4));
        db.Charges.Add(new Charge
        {
            TransactionId = spending.Id,
            Currency = CurrencyCode.Usd,
            ChargedAmount = 15600.10m,
            FeeAmount = 156.00m,
            RateUsed = 520.003333333333m,
            Source = ChargeSource.WalletTerms,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        // 15600.10 over 20, 5, 5: exact 10400.0666…, 2600.0166… twice, cut to 10400.06 + 2600.01 + 2600.01 = 15600.08;
        // the three tie on the fraction cut off, so the first two take the two cents: 10400.07 + 2600.02 and 2600.01.
        summary.Totals.Should().BeEquivalentTo(
        [
            new MonthTotal("Subscriptions", CurrencyCode.Kzt, 13000.09m),
            new MonthTotal("Coffee", CurrencyCode.Kzt, 2600.01m),
            new MonthTotal("Fees & Charges", CurrencyCode.Kzt, 156.00m),
        ], "a foreign spending counts as what left the wallet, its charge, split across its lines (8b spec A-1)");
    }

    [Fact]
    public async Task ThisMonthAsync_gives_a_discount_line_under_a_charge_its_negative_share()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = NewWallet(CurrencyCode.Kzt, "Kaspi KZT");
        db.Wallets.Add(kaspi);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var spending = NewTransaction(kaspi.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        db.Transactions.Add(spending);
        db.LineItems.AddRange(
            NewLineItem(spending.Id, "Annual plan", new Money(30.00m, CurrencyCode.Usd), SubscriptionsId, null, ordinal: 1),
            NewLineItem(spending.Id, "Promo discount", new Money(-5.00m, CurrencyCode.Usd), CoffeeId, null, ordinal: 2));
        db.Charges.Add(new Charge
        {
            TransactionId = spending.Id,
            Currency = CurrencyCode.Usd,
            ChargedAmount = 2500.00m,
            FeeAmount = 0m,
            RateUsed = 100m,
            Source = ChargeSource.WalletTerms,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        (await readModel.ThisMonthAsync(TestContext.Current.CancellationToken)).Totals.Should().BeEquivalentTo(
        [
            new MonthTotal("Subscriptions", CurrencyCode.Kzt, 3000.00m),
            new MonthTotal("Coffee", CurrencyCode.Kzt, -500.00m),
        ], "a discount line under a charge takes its negative share, and the shares still add up to the charge");
    }

    [Fact]
    public async Task ThisMonthAsync_subtracts_a_fiscal_refund_from_its_categories_and_never_counts_it_as_received()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var raiffeisen = NewWallet(CurrencyCode.Rsd, "Raiffeisen");
        db.Wallets.Add(raiffeisen);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var purchase = NewTransaction(raiffeisen.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        var fiscalRefund = NewTransaction(raiffeisen.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        fiscalRefund.Kind = TransactionKind.Income;
        var saidRefund = NewTransaction(raiffeisen.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        saidRefund.Kind = TransactionKind.Income;
        db.Transactions.AddRange(purchase, fiscalRefund, saidRefund);
        db.LineItems.AddRange(
            NewLineItem(purchase.Id, "groceries", new Money(500.00m, CurrencyCode.Rsd), GroceriesId, null),
            NewLineItem(fiscalRefund.Id, "returned groceries", new Money(800.00m, CurrencyCode.Rsd), GroceriesId, null, ordinal: 1),
            NewLineItem(fiscalRefund.Id, "returned coffee", new Money(100.00m, CurrencyCode.Rsd), CoffeeId, null, ordinal: 2),
            NewLineItem(saidRefund.Id, "a friend paid me back", new Money(50.00m, CurrencyCode.Rsd), RefundId, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO receipts (id, transaction_id, source, receipt_kind, created_at, total, currency)
            VALUES ({Guid.NewGuid()}, {fiscalRefund.Id}, {(int)ReceiptSource.FiscalQr}, {(int)ReceiptKind.Refund}, {now}, 900.00, 'RSD')
            """,
            TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.Totals.Should().BeEquivalentTo(
        [
            new MonthTotal("Groceries", CurrencyCode.Rsd, -300.00m),
            new MonthTotal("Coffee", CurrencyCode.Rsd, -100.00m),
        ], "a fiscal refund reduces spending in its categories, which may go below zero (8b spec SS-19)");
        summary.Received.Should().BeEquivalentTo(
            [new MonthTotal("Refund", CurrencyCode.Rsd, 50.00m)],
            "only a fiscal Refund receipt makes a refund; an income the operator said stays received");
    }

    [Fact]
    public async Task ThisMonthAsync_still_counts_a_line_on_a_record_with_no_wallet_in_its_own_currency()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var captured = NewTransaction(null, now, "Europe/Belgrade", TransactionStatus.Captured);
        db.Transactions.Add(captured);
        db.LineItems.Add(NewLineItem(captured.Id, "bread", new Money(7.50m, CurrencyCode.Eur), GroceriesId, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        (await readModel.ThisMonthAsync(TestContext.Current.CancellationToken)).Totals.Should().BeEquivalentTo(
            [new MonthTotal("Groceries", CurrencyCode.Eur, 7.50m)],
            "the summary drops a line with no wallet currency to count in; This month, which never converts, keeps it");
    }

    [Fact]
    public async Task TransfersThisMonthAsync_lists_the_months_transfers_newest_first_and_nothing_else()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var raiffeisen = NewWallet(CurrencyCode.Rsd, "Raiffeisen RSD");
        var cash = NewWallet(CurrencyCode.Rsd, "Cash RSD");
        db.Wallets.AddRange(raiffeisen, cash);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var lastMonth = NewTransferRecord(raiffeisen.Id, new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        var early = NewTransferRecord(raiffeisen.Id, new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));
        var cancelled = NewTransferRecord(raiffeisen.Id, new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero), TransactionStatus.Cancelled);
        var late = NewTransferRecord(raiffeisen.Id, new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));
        var expense = NewTransaction(raiffeisen.Id, new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero), "Europe/Belgrade", TransactionStatus.Completed);
        db.Transactions.AddRange(lastMonth, early, cancelled, late, expense);
        db.Transfers.AddRange(
            NewTransfer(lastMonth.Id, raiffeisen, 1000m, cash, 1000m),
            NewTransfer(early.Id, raiffeisen, 2000m, cash, 2000m),
            NewTransfer(cancelled.Id, raiffeisen, 3000m, cash, 3000m),
            NewTransfer(late.Id, raiffeisen, 10150m, cash, 10000m, TransferLeg.From));
        db.LineItems.Add(NewFeeLine(late.Id, new Money(150m, CurrencyCode.Rsd)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db,
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero)), TimeZoneInfo.Utc);

        var transfers = await readModel.TransfersThisMonthAsync(TestContext.Current.CancellationToken);

        transfers.Should().Equal(
            new MonthTransfer(late.Id, new DateOnly(2026, 9, 12), new TransferLine(
                "Raiffeisen RSD", new Money(10150m, CurrencyCode.Rsd), "Cash RSD", new Money(10000m, CurrencyCode.Rsd),
                new Money(150m, CurrencyCode.Rsd), TransferLeg.From, null)),
            new MonthTransfer(early.Id, new DateOnly(2026, 9, 2), new TransferLine(
                "Raiffeisen RSD", new Money(2000m, CurrencyCode.Rsd), "Cash RSD", new Money(2000m, CurrencyCode.Rsd),
                null, null, null)));
    }

    [Fact]
    public async Task TransfersThisMonthAsync_orders_one_days_transfers_by_time_then_by_id()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var raiffeisen = NewWallet(CurrencyCode.Rsd, "Raiffeisen RSD");
        var cash = NewWallet(CurrencyCode.Rsd, "Cash RSD");
        db.Wallets.AddRange(raiffeisen, cash);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var morning = NewTransferRecord(raiffeisen.Id, new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero),
            id: new Guid("00000000-0000-0000-0000-0000000000a3"));
        var eveningLowerId = NewTransferRecord(raiffeisen.Id, new DateTimeOffset(2026, 9, 12, 18, 0, 0, TimeSpan.Zero),
            id: new Guid("00000000-0000-0000-0000-0000000000a1"));
        var eveningHigherId = NewTransferRecord(raiffeisen.Id, new DateTimeOffset(2026, 9, 12, 18, 0, 0, TimeSpan.Zero),
            id: new Guid("00000000-0000-0000-0000-0000000000a2"));
        db.Transactions.AddRange(morning, eveningLowerId, eveningHigherId);
        db.Transfers.AddRange(
            NewTransfer(morning.Id, raiffeisen, 1000m, cash, 1000m),
            NewTransfer(eveningLowerId.Id, raiffeisen, 2000m, cash, 2000m),
            NewTransfer(eveningHigherId.Id, raiffeisen, 3000m, cash, 3000m));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db,
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero)), TimeZoneInfo.Utc);

        var transfers = await readModel.TransfersThisMonthAsync(TestContext.Current.CancellationToken);

        transfers.Select(t => t.Id).Should().Equal(eveningHigherId.Id, eveningLowerId.Id, morning.Id);
    }
}
