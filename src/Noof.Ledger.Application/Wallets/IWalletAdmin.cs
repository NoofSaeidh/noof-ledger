using Noof.Ledger.Domain;
using PaymentMethod = Noof.Ledger.Application.Receipts.PaymentMethod;

namespace Noof.Ledger.Application.Wallets;

public sealed record WalletDetails(
    Guid Id, string Name, CurrencyCode Currency, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency, bool Archived,
    PaymentMethod? DefaultForPayment = null);

public sealed record NewWallet(
    string Name, CurrencyCode Currency, decimal OpeningBalance, DateOnly OpeningDate, IReadOnlyList<string> Aliases,
    bool IsDefaultForCurrency);

// A caller that lets these through has a bug, not an operator error, so the page checks before it calls: a name
// blank after trimming throws ArgumentException, an unknown wallet id KeyNotFoundException, and making an archived
// wallet a default InvalidOperationException. Nothing is written in any of those cases.
public interface IWalletAdmin
{
    Task<IReadOnlyList<WalletDetails>> ListAsync(CancellationToken cancellationToken);

    Task<Guid> CreateAsync(NewWallet wallet, CancellationToken cancellationToken);

    Task RenameAsync(Guid walletId, string name, CancellationToken cancellationToken);

    Task SetAliasesAsync(Guid walletId, IReadOnlyList<string> aliases, CancellationToken cancellationToken);

    Task MakeDefaultForCurrencyAsync(Guid walletId, CancellationToken cancellationToken);

    // method is Card, Cash or null (none); the unique index per payment method means setting one
    // atomically clears whichever other wallet held it. Any other PaymentMethod value throws
    // ArgumentOutOfRangeException - a wallet cannot be the default for Transfer/Voucher/Other/Mixed.
    Task SetPaymentDefaultAsync(Guid walletId, PaymentMethod? method, CancellationToken cancellationToken);

    Task ArchiveAsync(Guid walletId, CancellationToken cancellationToken);
}
