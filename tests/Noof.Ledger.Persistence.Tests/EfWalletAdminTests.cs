using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Revisions;
using Noof.Ledger.Persistence.Wallets;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfWalletAdminTests(PostgresFixture fixture)
{
    // Seeded by AddCaptureModel into every migrated database; Task 1 made it the RSD default.
    static readonly Guid MainWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
    static readonly DateOnly OpeningDay = new(2026, 9, 1);
    static readonly TimeZoneInfo Belgrade = TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade");
    static int nextMessageId = 70_000;

    async Task<LedgerDbContext> MigratedAsync()
    {
        var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return db;
    }

    static EfWalletAdmin Admin(LedgerDbContext db) => new(db, new FakeTimeProvider(Now), Belgrade);

    static NewWallet Raiffeisen(bool isDefault = false) =>
        new("Raiffeisen RSD", CurrencyCode.Rsd, 45_000.00m, OpeningDay, ["raif"], isDefault);

    static NewWallet Named(string name, CurrencyCode currency, bool isDefault = false) =>
        new(name, currency, 0m, OpeningDay, [], isDefault);

    static Task<Wallet> ReadAsync(LedgerDbContext db, Guid walletId) =>
        db.Wallets.AsNoTracking().SingleAsync(w => w.Id == walletId, TestContext.Current.CancellationToken);

    static Transaction Expense(Guid walletId, DateOnly on, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = walletId,
        Kind = TransactionKind.Expense,
        RawText = "кофе 250",
        Status = TransactionStatus.Completed,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = at,
        OccurredOn = on,
        TelegramChatId = 1,
        TelegramMessageId = Interlocked.Increment(ref nextMessageId),
        CreatedAt = at,
    };

    [Fact]
    public async Task CreateAsync_stores_the_wallet_with_a_trimmed_name_and_clean_aliases()
    {
        await using var db = await MigratedAsync();

        var walletId = await Admin(db).CreateAsync(
            new NewWallet("  Wise EUR  ", CurrencyCode.Eur, 0m, OpeningDay, [" wise ", "", "WISE", "вайз"], false),
            TestContext.Current.CancellationToken);

        var wallet = await ReadAsync(db, walletId);
        wallet.Name.Should().Be("Wise EUR");
        wallet.Currency.Should().Be(CurrencyCode.Eur);
        wallet.Aliases.Should().Equal("wise", "вайз");
        wallet.IsDefaultForCurrency.Should().BeFalse();
        wallet.Archived.Should().BeFalse();
        wallet.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task CreateAsync_writes_the_opening_balance_as_the_wallets_first_checkpoint()
    {
        await using var db = await MigratedAsync();

        var walletId = await Admin(db).CreateAsync(Raiffeisen(), TestContext.Current.CancellationToken);

        var opening = await db.Transactions.AsNoTracking()
            .SingleAsync(t => t.WalletId == walletId, TestContext.Current.CancellationToken);
        opening.Kind.Should().Be(TransactionKind.BalanceCheck);
        opening.CaptureKind.Should().Be(CaptureKind.Manual);
        opening.RawText.Should().Be("Opening balance");
        opening.Status.Should().Be(TransactionStatus.Completed);
        opening.TelegramChatId.Should().BeNull();
        opening.TelegramMessageId.Should().BeNull();
        opening.TimeZoneId.Should().Be("Europe/Belgrade");
        opening.OccurredOn.Should().Be(OpeningDay);
        opening.OccurredAt.Should().Be(
            new DateTimeOffset(2026, 8, 31, 22, 0, 0, TimeSpan.Zero), "local midnight of 1 September in Belgrade, summer time");
        opening.CreatedAt.Should().Be(Now);

        var checkpoint = await db.BalanceChecks.AsNoTracking()
            .SingleAsync(c => c.TransactionId == opening.Id, TestContext.Current.CancellationToken);
        checkpoint.WalletId.Should().Be(walletId);
        checkpoint.Stated.Should().Be(new Money(45_000.00m, CurrencyCode.Rsd));
        checkpoint.ComputedBefore.Should().Be(0m);

        var revision = await db.TransactionRevisions.AsNoTracking()
            .SingleAsync(r => r.TransactionId == opening.Id, TestContext.Current.CancellationToken);
        revision.Kind.Should().Be(RevisionKind.Initial);
        revision.RevisionNumber.Should().Be(1);
        revision.StatusBefore.Should().Be(TransactionStatus.Completed);
        revision.StatusAfter.Should().Be(TransactionStatus.Completed);
        JsonDocument.Parse(revision.Snapshot).RootElement.GetProperty("raw_text").GetString().Should().Be("Opening balance");
    }

    // What wallet_balances would add on top of the opening checkpoint: the records that sort strictly after it by
    // (occurred_on, occurred_at). The view itself arrives with Task 3 in this same wave.
    static async Task<List<Guid>> RecordsAfterTheOpeningAsync(LedgerDbContext db, Guid walletId)
    {
        var openingId = await db.BalanceChecks.AsNoTracking()
            .Where(c => c.WalletId == walletId)
            .Select(c => c.TransactionId)
            .SingleAsync(TestContext.Current.CancellationToken);

        return await db.Database.SqlQuery<Guid>(
            $"""
            SELECT t.id AS "Value"
            FROM public.transactions t
            JOIN public.transactions opening ON opening.id = {openingId}
            WHERE t.id <> opening.id
              AND (t.occurred_on, t.occurred_at) > (opening.occurred_on, opening.occurred_at)
            """).ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_opening_balance_sorts_before_every_record_from_its_own_day_on()
    {
        await using var db = await MigratedAsync();
        var walletId = await Admin(db).CreateAsync(Raiffeisen(), TestContext.Current.CancellationToken);
        var dayBefore = Expense(walletId, new DateOnly(2026, 8, 31), new DateTimeOffset(2026, 8, 31, 20, 0, 0, TimeSpan.Zero));
        var firstSecondOfTheDay = Expense(walletId, OpeningDay, new DateTimeOffset(2026, 8, 31, 22, 0, 1, TimeSpan.Zero));
        var daysLater = Expense(walletId, new DateOnly(2026, 9, 5), new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero));
        db.Transactions.AddRange(dayBefore, firstSecondOfTheDay, daysLater);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await RecordsAfterTheOpeningAsync(db, walletId)).Should().BeEquivalentTo(
            new[] { firstSecondOfTheDay.Id, daysLater.Id },
            "wallet_balances adds only what sorts strictly after a checkpoint, and the opening one sits at local midnight");
    }

    [Fact]
    public async Task CreateAsync_when_the_opening_date_is_after_existing_records_anchors_them_all()
    {
        await using var db = await MigratedAsync();
        var walletId = await Admin(db).CreateAsync(Raiffeisen(), TestContext.Current.CancellationToken);
        // Spent weeks before the operator set the wallet up with 1 September as its opening day.
        var weeksBefore = Expense(walletId, new DateOnly(2026, 8, 20), new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        var dayAfter = Expense(walletId, new DateOnly(2026, 9, 2), new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero));
        db.Transactions.AddRange(weeksBefore, dayAfter);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await RecordsAfterTheOpeningAsync(db, walletId)).Should().ContainSingle().Which.Should().Be(
            dayAfter.Id,
            "the opening balance already contains what was spent before it; counting it again would take it out twice");
    }

    [Fact]
    public async Task CreateAsync_as_the_default_takes_over_from_the_previous_default_of_that_currency()
    {
        await using var db = await MigratedAsync();

        var walletId = await Admin(db).CreateAsync(Raiffeisen(isDefault: true), TestContext.Current.CancellationToken);

        (await ReadAsync(db, walletId)).IsDefaultForCurrency.Should().BeTrue();
        (await ReadAsync(db, MainWalletId)).IsDefaultForCurrency.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_not_as_the_default_leaves_the_current_default_alone()
    {
        await using var db = await MigratedAsync();

        var walletId = await Admin(db).CreateAsync(Raiffeisen(isDefault: false), TestContext.Current.CancellationToken);

        (await ReadAsync(db, walletId)).IsDefaultForCurrency.Should().BeFalse();
        (await ReadAsync(db, MainWalletId)).IsDefaultForCurrency.Should().BeTrue();
    }

    [Fact]
    public async Task A_create_that_fails_leaves_the_previous_default_in_place()
    {
        await using var db = await MigratedAsync();
        var tooLong = new NewWallet(new string('x', 129), CurrencyCode.Rsd, 0m, OpeningDay, [], IsDefaultForCurrency: true);

        var act = () => Admin(db).CreateAsync(tooLong, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
        (await ReadAsync(db, MainWalletId)).IsDefaultForCurrency.Should().BeTrue(
            "clearing the old default and inserting the new wallet are one database transaction");
    }

    [Fact]
    public async Task CreateAsync_refuses_a_blank_name_and_writes_nothing()
    {
        await using var db = await MigratedAsync();

        var act = () => Admin(db).CreateAsync(Named("   ", CurrencyCode.Eur), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
        (await db.Wallets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1, "only the seeded wallet exists");
    }

    [Fact]
    public async Task MakeDefaultForCurrencyAsync_moves_the_default_between_two_wallets_of_one_currency()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        var wiseEur = await admin.CreateAsync(Named("Wise EUR", CurrencyCode.Eur, isDefault: true), TestContext.Current.CancellationToken);
        var cashEur = await admin.CreateAsync(Named("Cash EUR", CurrencyCode.Eur), TestContext.Current.CancellationToken);

        await admin.MakeDefaultForCurrencyAsync(cashEur, TestContext.Current.CancellationToken);

        (await ReadAsync(db, cashEur)).IsDefaultForCurrency.Should().BeTrue();
        (await ReadAsync(db, wiseEur)).IsDefaultForCurrency.Should().BeFalse();
        (await ReadAsync(db, MainWalletId)).IsDefaultForCurrency.Should().BeTrue("RSD's default belongs to another currency");

        await admin.MakeDefaultForCurrencyAsync(wiseEur, TestContext.Current.CancellationToken);

        (await ReadAsync(db, wiseEur)).IsDefaultForCurrency.Should().BeTrue();
        (await ReadAsync(db, cashEur)).IsDefaultForCurrency.Should().BeFalse();
    }

    [Fact]
    public async Task MakeDefaultForCurrencyAsync_on_the_current_default_changes_nothing()
    {
        await using var db = await MigratedAsync();

        await Admin(db).MakeDefaultForCurrencyAsync(MainWalletId, TestContext.Current.CancellationToken);

        (await ReadAsync(db, MainWalletId)).IsDefaultForCurrency.Should().BeTrue();
    }

    [Fact]
    public async Task MakeDefaultForCurrencyAsync_refuses_an_archived_wallet()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        var walletId = await admin.CreateAsync(Raiffeisen(), TestContext.Current.CancellationToken);
        await admin.ArchiveAsync(walletId, TestContext.Current.CancellationToken);

        var act = () => admin.MakeDefaultForCurrencyAsync(walletId, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await ReadAsync(db, walletId)).IsDefaultForCurrency.Should().BeFalse();
        (await ReadAsync(db, MainWalletId)).IsDefaultForCurrency.Should().BeTrue();
    }

    [Fact]
    public async Task ArchiveAsync_hides_the_wallet_gives_up_its_default_and_keeps_its_history()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        var walletId = await admin.CreateAsync(Raiffeisen(isDefault: true), TestContext.Current.CancellationToken);

        await admin.ArchiveAsync(walletId, TestContext.Current.CancellationToken);

        var wallet = await ReadAsync(db, walletId);
        wallet.Archived.Should().BeTrue();
        wallet.IsDefaultForCurrency.Should().BeFalse();
        (await db.BalanceChecks.AsNoTracking().CountAsync(c => c.WalletId == walletId, TestContext.Current.CancellationToken))
            .Should().Be(1, "archiving hides a wallet, it does not erase what happened in it");
    }

    [Fact]
    public async Task RenameAsync_stores_the_trimmed_name()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        var walletId = await admin.CreateAsync(Raiffeisen(), TestContext.Current.CancellationToken);

        await admin.RenameAsync(walletId, "  Raiffeisen  ", TestContext.Current.CancellationToken);

        (await ReadAsync(db, walletId)).Name.Should().Be("Raiffeisen");
    }

    [Fact]
    public async Task RenameAsync_refuses_a_blank_name_and_keeps_the_old_one()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        var walletId = await admin.CreateAsync(Raiffeisen(), TestContext.Current.CancellationToken);

        var act = () => admin.RenameAsync(walletId, " ", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
        (await ReadAsync(db, walletId)).Name.Should().Be("Raiffeisen RSD");
    }

    [Fact]
    public async Task SetAliasesAsync_trims_drops_blanks_and_keeps_the_first_spelling_of_each_word()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        var walletId = await admin.CreateAsync(Raiffeisen(), TestContext.Current.CancellationToken);

        await admin.SetAliasesAsync(
            walletId, ["  Райф ", "", "raif", "РАЙФ", "   ", "Raiffeisen", "RAIF"], TestContext.Current.CancellationToken);

        (await ReadAsync(db, walletId)).Aliases.Should().Equal("Райф", "raif", "Raiffeisen");

        await admin.SetAliasesAsync(walletId, [" ", ""], TestContext.Current.CancellationToken);

        (await ReadAsync(db, walletId)).Aliases.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_lists_every_wallet_archived_last_then_by_name()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        await admin.CreateAsync(Named("wise", CurrencyCode.Eur), TestContext.Current.CancellationToken);
        var cash = await admin.CreateAsync(
            new NewWallet("Cash", CurrencyCode.Rsd, 0m, OpeningDay, ["наличка"], false), TestContext.Current.CancellationToken);
        var alpha = await admin.CreateAsync(Named("Alpha", CurrencyCode.Usd, isDefault: true), TestContext.Current.CancellationToken);
        await admin.ArchiveAsync(alpha, TestContext.Current.CancellationToken);

        var wallets = await admin.ListAsync(TestContext.Current.CancellationToken);

        wallets.Select(w => w.Name).Should().Equal("Cash", "Main Wallet", "wise", "Alpha");
        wallets.Single(w => w.Id == cash).Should().BeEquivalentTo(
            new WalletDetails(cash, "Cash", CurrencyCode.Rsd, ["наличка"], IsDefaultForCurrency: false, Archived: false));
        wallets.Single(w => w.Id == alpha).Archived.Should().BeTrue();
        wallets.Single(w => w.Id == MainWalletId).IsDefaultForCurrency.Should().BeTrue();
    }

    [Fact]
    public async Task Every_method_given_an_unknown_wallet_id_throws_and_changes_nothing()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        var unknown = Guid.NewGuid();

        var rename = () => admin.RenameAsync(unknown, "Cash", TestContext.Current.CancellationToken);
        var setAliases = () => admin.SetAliasesAsync(unknown, ["cash"], TestContext.Current.CancellationToken);
        var makeDefault = () => admin.MakeDefaultForCurrencyAsync(unknown, TestContext.Current.CancellationToken);
        var archive = () => admin.ArchiveAsync(unknown, TestContext.Current.CancellationToken);

        await rename.Should().ThrowAsync<KeyNotFoundException>();
        await setAliases.Should().ThrowAsync<KeyNotFoundException>();
        await makeDefault.Should().ThrowAsync<KeyNotFoundException>();
        await archive.Should().ThrowAsync<KeyNotFoundException>();
        (await ReadAsync(db, MainWalletId)).IsDefaultForCurrency.Should().BeTrue();
    }
}
