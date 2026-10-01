using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.Wallets;

// Every change is a set-based statement, never a tracked entity: the page's context lives as long as its circuit, and
// a tracked wallet keeps values another statement has since overwritten, so SaveChanges would skip a column it
// believes unchanged.
internal sealed class EfWalletAdmin(LedgerDbContext db, TimeProvider timeProvider, TimeZoneInfo captureZone) : IWalletAdmin
{
    const string OpeningBalanceText = "Opening balance";

    public async Task<IReadOnlyList<WalletDetails>> ListAsync(CancellationToken cancellationToken)
    {
        var wallets = await db.Wallets.AsNoTracking().ToListAsync(cancellationToken);

        return
        [
            .. wallets
                .OrderBy(wallet => wallet.Archived)
                .ThenBy(wallet => wallet.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(wallet => wallet.Id)
                .Select(wallet => new WalletDetails(
                    wallet.Id, wallet.Name, wallet.Currency, wallet.Aliases, wallet.IsDefaultForCurrency, wallet.Archived,
                    ToContract(wallet.DefaultForPayment))),
        ];
    }

    public async Task<Guid> CreateAsync(NewWallet wallet, CancellationToken cancellationToken)
    {
        var name = RequireName(wallet.Name);
        var now = timeProvider.GetUtcNow();
        var walletId = Guid.NewGuid();
        var opening = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            Kind = TransactionKind.BalanceCheck,
            CaptureKind = CaptureKind.Manual,
            RawText = OpeningBalanceText,
            Status = TransactionStatus.Completed,
            TimeZoneId = captureZone.Id,
            OccurredOn = wallet.OpeningDate,
            // Midnight, so the opening balance precedes everything recorded on its own day.
            OccurredAt = ZonedClock.StartOfDay(wallet.OpeningDate, captureZone.Id),
            CreatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        if (wallet.IsDefaultForCurrency)
            await ClearDefaultAsync(wallet.Currency, walletId, cancellationToken);

        db.Wallets.Add(new Wallet
        {
            Id = walletId,
            Name = name,
            Currency = wallet.Currency,
            Aliases = Normalise(wallet.Aliases),
            IsDefaultForCurrency = wallet.IsDefaultForCurrency,
            CreatedAt = now,
        });
        db.Transactions.Add(opening);
        db.BalanceChecks.Add(new BalanceCheck
        {
            TransactionId = opening.Id,
            WalletId = walletId,
            Stated = new Money(wallet.OpeningBalance, wallet.Currency),
            ComputedBefore = 0m,
        });
        await db.SaveChangesAsync(cancellationToken);

        // Born completed: there was no earlier state, and Captured would claim a pipeline this record never went through.
        await RevisionLog.AppendAsync(db, opening, RevisionKind.Initial, null, TransactionStatus.Completed, now, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return walletId;
    }

    public async Task RenameAsync(Guid walletId, string name, CancellationToken cancellationToken)
    {
        var trimmed = RequireName(name);

        RequireFound(walletId, await WalletById(walletId)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.Name, trimmed), cancellationToken));
    }

    public async Task SetAliasesAsync(Guid walletId, IReadOnlyList<string> aliases, CancellationToken cancellationToken)
    {
        var normalised = Normalise(aliases);

        RequireFound(walletId, await WalletById(walletId)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.Aliases, normalised), cancellationToken));
    }

    public async Task MakeDefaultForCurrencyAsync(Guid walletId, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var wallet = await WalletById(walletId).AsNoTracking()
            .Select(w => new { w.Currency, w.Archived })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw Unknown(walletId);

        if (wallet.Archived)
            throw new InvalidOperationException(
                $"Wallet {walletId} is archived; an archived wallet cannot be the default for {wallet.Currency}.");

        // Clear, then set, as two statements executed on the spot. ix_wallets_one_default_per_currency is checked row by
        // row, so the new default written before the old one is cleared - an order SaveChanges' batching is free to
        // choose - would violate it.
        await ClearDefaultAsync(wallet.Currency, walletId, cancellationToken);
        await WalletById(walletId)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.IsDefaultForCurrency, true), cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public async Task SetPaymentDefaultAsync(Guid walletId, PaymentMethod? method, CancellationToken cancellationToken)
    {
        var domainMethod = ToDomain(method);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var wallet = await WalletById(walletId).AsNoTracking()
            .Select(w => new { w.Archived, w.Currency })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw Unknown(walletId);

        if (domainMethod is not null)
        {
            if (wallet.Archived)
                throw new InvalidOperationException(
                    $"Wallet {walletId} is archived; an archived wallet cannot be the payment default.");

            // Clear, then set - the same reasoning as MakeDefaultForCurrencyAsync: the unique index
            // is checked row by row, so writing the new default before the old one is cleared would
            // violate it. Only the same currency's default moves: each currency keeps its own (T-13).
            await db.Wallets
                .Where(w => w.DefaultForPayment == domainMethod && w.Currency == wallet.Currency && w.Id != walletId)
                .ExecuteUpdateAsync(set => set.SetProperty(w => w.DefaultForPayment, (WalletPaymentDefault?)null), cancellationToken);
        }

        await WalletById(walletId)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.DefaultForPayment, domainMethod), cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public async Task ArchiveAsync(Guid walletId, CancellationToken cancellationToken)
    {
        // An archived wallet is hidden from capture, so it cannot stay the currency or payment default
        // capture resolves to.
        RequireFound(walletId, await WalletById(walletId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(w => w.Archived, true)
                    .SetProperty(w => w.IsDefaultForCurrency, false)
                    .SetProperty(w => w.DefaultForPayment, (WalletPaymentDefault?)null),
                cancellationToken));
    }

    IQueryable<Wallet> WalletById(Guid walletId) => db.Wallets.Where(w => w.Id == walletId);

    Task<int> ClearDefaultAsync(CurrencyCode currency, Guid exceptWalletId, CancellationToken cancellationToken) =>
        db.Wallets
            .Where(w => w.Currency == currency && w.IsDefaultForCurrency && w.Id != exceptWalletId)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.IsDefaultForCurrency, false), cancellationToken);

    static void RequireFound(Guid walletId, int updatedRows)
    {
        if (updatedRows == 0)
            throw Unknown(walletId);
    }

    static KeyNotFoundException Unknown(Guid walletId) => new($"There is no wallet {walletId}.");

    static string RequireName(string name) =>
        name.Trim() is { Length: > 0 } trimmed ? trimmed : throw new ArgumentException("A wallet needs a name.", nameof(name));

    static WalletPaymentDefault? ToDomain(PaymentMethod? method) => method switch
    {
        null => null,
        PaymentMethod.Card => WalletPaymentDefault.Card,
        PaymentMethod.Cash => WalletPaymentDefault.Cash,
        _ => throw new ArgumentOutOfRangeException(
            nameof(method), method, "Only Card, Cash or null can be a wallet's payment default."),
    };

    static PaymentMethod? ToContract(WalletPaymentDefault? method) => method switch
    {
        null => null,
        WalletPaymentDefault.Card => PaymentMethod.Card,
        WalletPaymentDefault.Cash => PaymentMethod.Cash,
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown wallet payment default."),
    };

    static string[] Normalise(IEnumerable<string> aliases) =>
        [.. aliases.Select(alias => alias.Trim()).Where(alias => alias.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
}
