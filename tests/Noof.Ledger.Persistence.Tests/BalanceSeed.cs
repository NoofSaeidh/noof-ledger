using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

// Writes the rows the balance rule reads straight into the context. The tests that use it pin the rule itself;
// turning a model's answer into these rows is EfCategorizationStore's job and is tested there.
internal static class BalanceSeed
{
    static int nextMessageId = 60_000;

    public static Wallet AddWallet(LedgerDbContext db, CurrencyCode currency, string name = "Raiffeisen", bool archived = false)
    {
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            Name = name,
            Currency = currency,
            Archived = archived,
            CreatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        };
        db.Wallets.Add(wallet);
        return wallet;
    }

    public static Transaction Spend(
        LedgerDbContext db, Wallet wallet, decimal amount, DateOnly on, DateTimeOffset at,
        TransactionStatus status = TransactionStatus.Completed, CurrencyCode? currency = null, DateTimeOffset? createdAt = null) =>
        AddWithEntry(db, wallet, TransactionKind.Expense, -amount, on, at, status, currency, createdAt);

    public static Transaction Earn(
        LedgerDbContext db, Wallet wallet, decimal amount, DateOnly on, DateTimeOffset at,
        TransactionStatus status = TransactionStatus.Completed, CurrencyCode? currency = null, DateTimeOffset? createdAt = null) =>
        AddWithEntry(db, wallet, TransactionKind.Income, amount, on, at, status, currency, createdAt);

    public static Transaction State(
        LedgerDbContext db, Wallet wallet, decimal stated, DateOnly on, DateTimeOffset at,
        TransactionStatus status = TransactionStatus.Completed, CurrencyCode? currency = null, DateTimeOffset? createdAt = null)
    {
        var record = AddRecord(db, wallet, TransactionKind.BalanceCheck, on, at, status, createdAt);
        db.BalanceChecks.Add(new BalanceCheck
        {
            TransactionId = record.Id,
            WalletId = wallet.Id,
            Stated = new Money(stated, currency ?? wallet.Currency),
            ComputedBefore = 0m,
        });
        return record;
    }

    static Transaction AddWithEntry(
        LedgerDbContext db, Wallet wallet, TransactionKind kind, decimal signedAmount, DateOnly on, DateTimeOffset at,
        TransactionStatus status, CurrencyCode? currency, DateTimeOffset? createdAt)
    {
        var record = AddRecord(db, wallet, kind, on, at, status, createdAt);
        db.Entries.Add(new Entry
        {
            Id = Guid.NewGuid(),
            TransactionId = record.Id,
            WalletId = wallet.Id,
            Amount = new Money(signedAmount, currency ?? wallet.Currency),
            Role = EntryRole.Principal,
        });
        return record;
    }

    static Transaction AddRecord(
        LedgerDbContext db, Wallet wallet, TransactionKind kind, DateOnly on, DateTimeOffset at,
        TransactionStatus status, DateTimeOffset? createdAt)
    {
        var record = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = wallet.Id,
            Kind = kind,
            RawText = "seeded",
            Status = status,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = at,
            OccurredOn = on,
            TelegramChatId = 1,
            TelegramMessageId = Interlocked.Increment(ref nextMessageId),
            CreatedAt = createdAt ?? at,
        };
        db.Transactions.Add(record);
        return record;
    }
}
