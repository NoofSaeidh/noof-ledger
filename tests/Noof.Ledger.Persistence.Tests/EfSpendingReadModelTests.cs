using AwesomeAssertions;
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
        DateOnly? occurredOn = null) => new()
    {
        Id = Guid.NewGuid(),
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

    static readonly Guid FeesAndChargesId = new("00000000-0000-0000-0001-000000000013");

    static Transaction NewTransferRecord(Guid walletId, DateTimeOffset occurredAt, TransactionStatus status = TransactionStatus.Completed)
    {
        var transaction = NewTransaction(walletId, occurredAt, "Europe/Belgrade", status);
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
        var withFee = NewTransferRecord(wise.Id, now.AddMinutes(-1));
        var stated = NewTransferRecord(wise.Id, now);
        db.Transactions.AddRange(withFee, stated);
        db.Transfers.AddRange(
            NewTransfer(withFee.Id, wise, 101m, raiffeisen, 11700m, TransferLeg.From),
            NewTransfer(stated.Id, wise, 50m, raiffeisen, 5867.50m, statedRate: 117.35m, statedRateBase: CurrencyCode.Eur));
        db.LineItems.Add(NewFeeLine(withFee.Id, new Money(1m, CurrencyCode.Eur)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(10, RecentView.Transfers, TestContext.Current.CancellationToken);

        recent.Single(r => r.Id == withFee.Id).Transfer!.Rate.Should().Be(
            new ExchangeRate(CurrencyCode.Eur, 117m, CurrencyCode.Rsd),
            "101 EUR left the wallet but 1 EUR of it was the fee: 100 EUR bought 11 700 RSD, not 101");
        recent.Single(r => r.Id == stated.Id).Transfer!.Rate.Should().Be(
            new ExchangeRate(CurrencyCode.Eur, 117.35m, CurrencyCode.Rsd),
            "a stated rate is shown as stated, never re-derived from rounded amounts");
    }
}
