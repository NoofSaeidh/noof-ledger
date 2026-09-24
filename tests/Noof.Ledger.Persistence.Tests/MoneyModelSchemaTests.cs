using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Backup;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class MoneyModelSchemaTests(PostgresFixture fixture)
{
    static readonly Guid MainWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
    static int nextMessageId = 3000;

    static Transaction NewTransaction(
        CaptureKind captureKind, long? chatId, int? messageId,
        Guid? walletId = null, TransactionKind kind = TransactionKind.Expense, string? rawText = "кофе 250") => new()
    {
        Id = Guid.NewGuid(),
        WalletId = walletId,
        Kind = kind,
        RawText = rawText,
        CaptureKind = captureKind,
        Status = TransactionStatus.Completed,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = Now,
        OccurredOn = new DateOnly(2026, 9, 24),
        TelegramChatId = chatId,
        TelegramMessageId = messageId,
        CreatedAt = Now,
    };

    static Transaction TextCapture(Guid? walletId = null) =>
        NewTransaction(CaptureKind.Text, 111, Interlocked.Increment(ref nextMessageId), walletId);

    static Transaction OpeningBalance() =>
        NewTransaction(CaptureKind.Manual, chatId: null, messageId: null, MainWalletId, TransactionKind.BalanceCheck, "Opening balance");

    static Entry NewEntry(Guid transactionId, Guid walletId, Money amount) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        WalletId = walletId,
        Amount = amount,
        Role = EntryRole.Principal,
    };

    async Task<LedgerDbContext> MigratedAsync()
    {
        var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return db;
    }

    static async Task<string?> ViolatedConstraintAsync(LedgerDbContext db)
    {
        try
        {
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException postgres)
        {
            return postgres.ConstraintName;
        }
    }

    [Fact]
    public async Task A_manual_record_with_no_telegram_message_is_accepted()
    {
        await using var db = await MigratedAsync();
        var opening = OpeningBalance();
        db.Transactions.Add(opening);

        (await ViolatedConstraintAsync(db)).Should().BeNull();

        db.ChangeTracker.Clear();
        var stored = await db.Transactions.SingleAsync(t => t.Id == opening.Id, TestContext.Current.CancellationToken);
        stored.CaptureKind.Should().Be(CaptureKind.Manual);
        stored.Kind.Should().Be(TransactionKind.BalanceCheck);
        stored.TelegramChatId.Should().BeNull();
        stored.TelegramMessageId.Should().BeNull();
    }

    [Fact]
    public async Task A_manual_record_that_names_a_telegram_message_is_refused()
    {
        await using var db = await MigratedAsync();
        db.Transactions.Add(NewTransaction(CaptureKind.Manual, 111, 5, MainWalletId, TransactionKind.BalanceCheck, "Opening balance"));

        (await ViolatedConstraintAsync(db)).Should().Be("ck_transactions_telegram_ids_match_capture_kind");
    }

    [Fact]
    public async Task A_text_capture_without_its_telegram_message_is_refused()
    {
        await using var db = await MigratedAsync();
        db.Transactions.Add(NewTransaction(CaptureKind.Text, chatId: null, messageId: null));

        (await ViolatedConstraintAsync(db)).Should().Be("ck_transactions_telegram_ids_match_capture_kind");
    }

    [Fact]
    public async Task A_manual_record_without_text_is_refused()
    {
        await using var db = await MigratedAsync();
        db.Transactions.Add(NewTransaction(CaptureKind.Manual, null, null, MainWalletId, TransactionKind.BalanceCheck, rawText: null));

        (await ViolatedConstraintAsync(db)).Should().Be("ck_transactions_capture_has_content");
    }

    [Fact]
    public async Task Any_number_of_manual_records_share_the_telegram_message_index()
    {
        await using var db = await MigratedAsync();
        db.Transactions.AddRange(OpeningBalance(), OpeningBalance());

        (await ViolatedConstraintAsync(db)).Should().BeNull("the idempotency index only covers records that came from Telegram");
    }

    [Fact]
    public async Task A_capture_may_name_no_wallet_yet()
    {
        await using var db = await MigratedAsync();
        var capture = TextCapture();
        db.Transactions.Add(capture);

        (await ViolatedConstraintAsync(db)).Should().BeNull();

        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(t => t.Id == capture.Id, TestContext.Current.CancellationToken))
            .WalletId.Should().BeNull();
    }

    [Fact]
    public async Task An_entry_keeps_its_sign_and_every_digit()
    {
        await using var db = await MigratedAsync();
        var capture = TextCapture(MainWalletId);
        db.Transactions.Add(capture);
        db.Entries.AddRange(
            NewEntry(capture.Id, MainWalletId, new Money(-123456789012345.6789m, CurrencyCode.Kzt)),
            NewEntry(capture.Id, MainWalletId, new Money(0.01m, CurrencyCode.Eur)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var stored = await db.Entries.AsNoTracking()
            .Where(e => e.TransactionId == capture.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Select(e => e.Amount).Should().BeEquivalentTo(
            [new Money(-123456789012345.6789m, CurrencyCode.Kzt), new Money(0.01m, CurrencyCode.Eur)]);
        stored.Should().OnlyContain(e => e.Role == EntryRole.Principal && e.WalletId == MainWalletId);
    }

    [Fact]
    public async Task Deleting_a_record_deletes_its_entries_and_its_checkpoint()
    {
        await using var db = await MigratedAsync();
        var expense = TextCapture(MainWalletId);
        var opening = OpeningBalance();
        db.Transactions.AddRange(expense, opening);
        db.Entries.Add(NewEntry(expense.Id, MainWalletId, new Money(-250m, CurrencyCode.Rsd)));
        db.BalanceChecks.Add(new BalanceCheck
        {
            TransactionId = opening.Id,
            WalletId = MainWalletId,
            Stated = new Money(45000m, CurrencyCode.Rsd),
            ComputedBefore = 0m,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM public.transactions WHERE id IN ({expense.Id}, {opening.Id})", TestContext.Current.CancellationToken);

        (await db.Entries.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await db.BalanceChecks.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task A_wallet_that_holds_entries_cannot_be_deleted()
    {
        await using var db = await MigratedAsync();
        var wallet = new Wallet { Id = Guid.NewGuid(), Name = "Wise EUR", Currency = CurrencyCode.Eur };
        var capture = TextCapture();
        db.Wallets.Add(wallet);
        db.Transactions.Add(capture);
        db.Entries.Add(NewEntry(capture.Id, wallet.Id, new Money(-3.5m, CurrencyCode.Eur)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var act = () => db.Database.ExecuteSqlAsync(
            $"DELETE FROM public.wallets WHERE id = {wallet.Id}", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("FK_entries_wallets_wallet_id");
    }

    [Fact]
    public async Task A_checkpoint_keeps_the_stated_amount_and_what_was_computed_before_it()
    {
        await using var db = await MigratedAsync();
        var statement = NewTransaction(
            CaptureKind.Text, 111, Interlocked.Increment(ref nextMessageId), MainWalletId, TransactionKind.BalanceCheck, "на райфе 45 тысяч");
        var checkpoint = new BalanceCheck
        {
            TransactionId = statement.Id,
            WalletId = MainWalletId,
            Stated = new Money(45000m, CurrencyCode.Rsd),
            ComputedBefore = 44800.5m,
        };
        db.Transactions.Add(statement);
        db.BalanceChecks.Add(checkpoint);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var stored = await db.BalanceChecks.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        stored.Should().BeEquivalentTo(checkpoint);
    }

    [Fact]
    public async Task A_backup_run_is_kept_as_written()
    {
        await using var db = await MigratedAsync();
        var run = new BackupRun
        {
            Id = Guid.NewGuid(),
            StartedAt = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2026, 9, 24, 3, 0, 12, TimeSpan.Zero),
            Succeeded = false,
            Error = "pg_dump exited with code 1",
        };
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var stored = await db.BackupRuns.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        stored.Should().BeEquivalentTo(run);
    }
}
