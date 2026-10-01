using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Reporting;

// One reading of a transfer for every page that shows one, so Home, the list and the trace cannot disagree about
// which amounts a transfer moved or which rate it went at.
internal static class TransferLines
{
    public static async Task<IReadOnlyDictionary<Guid, TransferLine>> ForAsync(
        LedgerDbContext db, List<Guid> transactionIds, CancellationToken cancellationToken)
    {
        if (transactionIds.Count == 0)
            return new Dictionary<Guid, TransferLine>();

        var transfers = await (
                from transfer in db.Transfers.AsNoTracking()
                where transactionIds.Contains(transfer.TransactionId)
                join source in db.Wallets.AsNoTracking() on transfer.FromWalletId equals source.Id
                join destination in db.Wallets.AsNoTracking() on transfer.ToWalletId equals destination.Id
                select new
                {
                    transfer.TransactionId,
                    SourceName = source.Name,
                    transfer.From,
                    DestinationName = destination.Name,
                    transfer.To,
                    transfer.FeeLeg,
                    transfer.StatedRate,
                    transfer.StatedRateBase,
                })
            .ToListAsync(cancellationToken);

        var fees = await db.LineItems.AsNoTracking()
            .Where(line => transactionIds.Contains(line.TransactionId) && line.Role == EntryRole.Fee)
            .Select(line => new { line.TransactionId, line.Amount })
            .ToListAsync(cancellationToken);
        var feeByTransaction = fees.ToLookup(fee => fee.TransactionId, fee => fee.Amount);

        return transfers.ToDictionary(
            transfer => transfer.TransactionId,
            transfer =>
            {
                var fee = feeByTransaction[transfer.TransactionId].Select(amount => (Money?)amount).FirstOrDefault();
                return new TransferLine(
                    transfer.SourceName, transfer.From, transfer.DestinationName, transfer.To, fee, transfer.FeeLeg,
                    RateOf(transfer.From, transfer.To, fee, transfer.FeeLeg, transfer.StatedRate, transfer.StatedRateBase));
            });
    }

    // The principals, not the stored amounts: the fee sits inside its leg's stored amount (T-12), and a rate read off
    // those would charge the fee to the exchange.
    public static ExchangeRate? RateOf(
        Money from, Money to, Money? fee, TransferLeg? feeLeg, decimal? statedRate, CurrencyCode? statedRateBase)
    {
        if (from.Currency == to.Currency)
            return null;

        if (statedRate is { } rate && statedRateBase is { } rateBase)
            return new ExchangeRate(rateBase, rate, rateBase == from.Currency ? to.Currency : from.Currency);

        var sourcePrincipal = fee is { } sourceFee && feeLeg == TransferLeg.From ? from - sourceFee : from;
        var destinationPrincipal = fee is { } destinationFee && feeLeg == TransferLeg.To ? to + destinationFee : to;
        return ExchangeRate.Between(sourcePrincipal, destinationPrincipal);
    }
}
