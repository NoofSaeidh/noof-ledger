using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Reporting;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfTransactionListTests(PostgresFixture fixture)
{
    static int nextTelegramMessageId;

    static Wallet NewWallet(string name, CurrencyCode currency) => new()
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

    static Transaction NewTransaction(
        Guid? walletId, DateTimeOffset occurredAt, TransactionKind kind, TransactionStatus status,
        string? rawText = "test capture", DateOnly? occurredOn = null) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = walletId,
        RawText = rawText,
        Kind = kind,
        Status = status,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = occurredAt,
        OccurredOn = occurredOn ?? DateOnly.FromDateTime(occurredAt.UtcDateTime),
        TelegramChatId = 1,
        TelegramMessageId = Interlocked.Increment(ref nextTelegramMessageId),
        CreatedAt = occurredAt,
    };

    static LineItem NewLineItem(Guid transactionId, string description, Money amount, Guid? categoryId) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        Description = description,
        Amount = amount,
        CategoryId = categoryId,
        CategorizedBy = CategorizationAuthority.Model,
        MerchantId = null,
    };

    static Entry NewEntry(Guid transactionId, Guid walletId, Money amount) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        WalletId = walletId,
        Amount = amount,
        Role = EntryRole.Principal,
    };

    static BalanceCheck NewBalanceCheck(Guid transactionId, Guid walletId, Money stated, decimal computedBefore) => new()
    {
        TransactionId = transactionId,
        WalletId = walletId,
        Stated = stated,
        ComputedBefore = computedBefore,
    };

    [Fact]
    public async Task Rows_are_ordered_newest_first_by_occurred_on_then_occurred_at()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet("Cash", CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var day = new DateOnly(2026, 9, 10);
        var oldest = NewTransaction(wallet.Id, new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero), TransactionKind.Expense, TransactionStatus.Completed, occurredOn: day);
        var newest = NewTransaction(wallet.Id, new DateTimeOffset(2026, 9, 10, 18, 0, 0, TimeSpan.Zero), TransactionKind.Expense, TransactionStatus.Completed, occurredOn: day);
        var evenNewerDay = NewTransaction(wallet.Id, new DateTimeOffset(2026, 9, 11, 1, 0, 0, TimeSpan.Zero), TransactionKind.Expense, TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 11));
        db.Transactions.AddRange(oldest, newest, evenNewerDay);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var list = new EfTransactionList(db);

        var page = await list.QueryAsync(new TransactionListFilter(), pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);

        page.Rows.Select(r => r.Id).Should().ContainInOrder(evenNewerDay.Id, newest.Id, oldest.Id);
    }

    [Fact]
    public async Task Date_range_filters_are_inclusive_on_both_ends()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet("Cash", CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var before = NewTransaction(wallet.Id, new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero), TransactionKind.Expense, TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 9));
        var from = NewTransaction(wallet.Id, new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero), TransactionKind.Expense, TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 10));
        var to = NewTransaction(wallet.Id, new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero), TransactionKind.Expense, TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 12));
        var after = NewTransaction(wallet.Id, new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero), TransactionKind.Expense, TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 13));
        db.Transactions.AddRange(before, from, to, after);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var list = new EfTransactionList(db);

        var page = await list.QueryAsync(
            new TransactionListFilter(From: new DateOnly(2026, 9, 10), To: new DateOnly(2026, 9, 12)),
            pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);

        page.Rows.Select(r => r.Id).Should().BeEquivalentTo([from.Id, to.Id]);
    }

    [Fact]
    public async Task Wallet_kind_and_status_filters_each_narrow_independently()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var walletA = NewWallet("Wallet A", CurrencyCode.Eur);
        var walletB = NewWallet("Wallet B", CurrencyCode.Eur);
        db.Wallets.AddRange(walletA, walletB);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var matching = NewTransaction(walletA.Id, now, TransactionKind.Income, TransactionStatus.Completed);
        var wrongWallet = NewTransaction(walletB.Id, now, TransactionKind.Income, TransactionStatus.Completed);
        var wrongKind = NewTransaction(walletA.Id, now, TransactionKind.Expense, TransactionStatus.Completed);
        var wrongStatus = NewTransaction(walletA.Id, now, TransactionKind.Income, TransactionStatus.Failed);
        db.Transactions.AddRange(matching, wrongWallet, wrongKind, wrongStatus);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var list = new EfTransactionList(db);

        var byWallet = await list.QueryAsync(new TransactionListFilter(WalletId: walletA.Id), 0, 50, TestContext.Current.CancellationToken);
        byWallet.Rows.Select(r => r.Id).Should().BeEquivalentTo([matching.Id, wrongKind.Id, wrongStatus.Id]);

        var byKind = await list.QueryAsync(new TransactionListFilter(Kind: TransactionKind.Income), 0, 50, TestContext.Current.CancellationToken);
        byKind.Rows.Select(r => r.Id).Should().BeEquivalentTo([matching.Id, wrongWallet.Id, wrongStatus.Id],
            "the Kind filter alone does not also narrow by status - wrongStatus is Income, just Failed");

        var byStatus = await list.QueryAsync(new TransactionListFilter(Status: TransactionStatus.Completed), 0, 50, TestContext.Current.CancellationToken);
        byStatus.Rows.Select(r => r.Id).Should().BeEquivalentTo([matching.Id, wrongWallet.Id, wrongKind.Id]);
    }

    [Fact]
    public async Task Text_filter_is_a_case_insensitive_contains_with_wildcards_escaped()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet("Cash", CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var matching = NewTransaction(wallet.Id, now, TransactionKind.Expense, TransactionStatus.Completed, rawText: "Coffee at Lidl");
        var literalPercent = NewTransaction(wallet.Id, now, TransactionKind.Expense, TransactionStatus.Completed, rawText: "50% off sale");
        var unrelated = NewTransaction(wallet.Id, now, TransactionKind.Expense, TransactionStatus.Completed, rawText: "Taxi ride");
        db.Transactions.AddRange(matching, literalPercent, unrelated);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var list = new EfTransactionList(db);

        var byText = await list.QueryAsync(new TransactionListFilter(Text: "coffee"), 0, 50, TestContext.Current.CancellationToken);
        byText.Rows.Select(r => r.Id).Should().BeEquivalentTo([matching.Id]);

        var byLiteralPercent = await list.QueryAsync(new TransactionListFilter(Text: "50%"), 0, 50, TestContext.Current.CancellationToken);
        byLiteralPercent.Rows.Select(r => r.Id).Should().BeEquivalentTo([literalPercent.Id],
            "a literal % the operator typed must match literally, not as a wildcard that also matches everything else");
    }

    [Fact]
    public async Task Paging_returns_the_requested_page_and_the_total_count_across_all_pages()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet("Cash", CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var transactions = Enumerable.Range(0, 5)
            .Select(i => NewTransaction(wallet.Id, now.AddMinutes(i), TransactionKind.Expense, TransactionStatus.Completed))
            .ToList();
        db.Transactions.AddRange(transactions);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var list = new EfTransactionList(db);

        var firstPage = await list.QueryAsync(new TransactionListFilter(), pageIndex: 0, pageSize: 2, TestContext.Current.CancellationToken);
        firstPage.TotalCount.Should().Be(5);
        firstPage.Rows.Select(r => r.Id).Should().Equal(transactions[4].Id, transactions[3].Id);

        var secondPage = await list.QueryAsync(new TransactionListFilter(), pageIndex: 1, pageSize: 2, TestContext.Current.CancellationToken);
        secondPage.TotalCount.Should().Be(5);
        secondPage.Rows.Select(r => r.Id).Should().Equal(transactions[2].Id, transactions[1].Id);
    }

    [Fact]
    public async Task Amounts_come_from_line_items_for_an_expense()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet("Cash", CurrencyCode.Eur);
        var category = NewCategory("test-list-expense", "Groceries");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var expense = NewTransaction(wallet.Id, now, TransactionKind.Expense, TransactionStatus.Completed);
        db.Transactions.Add(expense);
        db.LineItems.AddRange(
            NewLineItem(expense.Id, "bread", new Money(2m, CurrencyCode.Eur), category.Id),
            NewLineItem(expense.Id, "milk", new Money(1.5m, CurrencyCode.Eur), category.Id));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var list = new EfTransactionList(db);

        var page = await list.QueryAsync(new TransactionListFilter(), 0, 50, TestContext.Current.CancellationToken);

        var row = page.Rows.Single();
        row.Amounts.Should().ContainSingle().Which.Should().Be(new Money(3.5m, CurrencyCode.Eur));
        row.Categories.Should().Equal("Groceries");
    }

    [Fact]
    public async Task Amounts_come_from_entries_for_an_income()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet("Cash", CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var income = NewTransaction(wallet.Id, now, TransactionKind.Income, TransactionStatus.Completed);
        db.Transactions.Add(income);
        db.Entries.Add(NewEntry(income.Id, wallet.Id, new Money(500m, CurrencyCode.Eur)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var list = new EfTransactionList(db);

        var page = await list.QueryAsync(new TransactionListFilter(), 0, 50, TestContext.Current.CancellationToken);

        page.Rows.Single().Amounts.Should().ContainSingle().Which.Should().Be(new Money(500m, CurrencyCode.Eur));
    }

    [Fact]
    public async Task Amount_is_the_stated_amount_for_a_balance_check()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet("Cash", CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var statement = NewTransaction(wallet.Id, now, TransactionKind.BalanceCheck, TransactionStatus.Completed);
        db.Transactions.Add(statement);
        db.BalanceChecks.Add(NewBalanceCheck(statement.Id, wallet.Id, new Money(1000m, CurrencyCode.Eur), 950m));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var list = new EfTransactionList(db);

        var page = await list.QueryAsync(new TransactionListFilter(), 0, 50, TestContext.Current.CancellationToken);

        page.Rows.Single().Amounts.Should().ContainSingle().Which.Should().Be(new Money(1000m, CurrencyCode.Eur));
    }

    [Fact]
    public async Task A_transaction_with_no_wallet_yet_shows_an_empty_wallet_name()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var captured = NewTransaction(null, now, TransactionKind.Expense, TransactionStatus.Captured);
        db.Transactions.Add(captured);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var list = new EfTransactionList(db);

        var page = await list.QueryAsync(new TransactionListFilter(), 0, 50, TestContext.Current.CancellationToken);

        page.Rows.Single().WalletName.Should().BeEmpty();
    }
}
