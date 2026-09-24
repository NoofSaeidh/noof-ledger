using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Wallets;

public sealed record WalletDetails(
    Guid Id, string Name, CurrencyCode Currency, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency, bool Archived);

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

    Task ArchiveAsync(Guid walletId, CancellationToken cancellationToken);
}
