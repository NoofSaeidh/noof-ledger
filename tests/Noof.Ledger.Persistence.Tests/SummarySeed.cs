using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

internal sealed record SummaryMonth(
    Guid Raiffeisen, Guid CashRsd, Guid Wise, Guid CashEur,
    Guid ForeignPurchase, Guid Withdrawal, Guid FeeOnTo, Guid Exchange, Guid Purchase, Guid Refund, Guid Salary);

// The money cases the summary must reconcile, written through the production write path on IntegritySeed.Day
// (10 September 2026), so charges, fee lines, transfer legs and entries are what the app writes.
internal static class SummarySeed
{
    public static readonly Guid GroceriesId = new("00000000-0000-0000-0001-000000000001");
    public static readonly Guid CoffeeId = new("00000000-0000-0000-0001-000000000017");
    public static readonly Guid SalaryId = new("00000000-0000-0000-0001-000000000022");

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<SummaryMonth> SeedSeptemberAsync(LedgerDbContext db)
    {
        var raiffeisen = await IntegritySeed.AddWalletAsync(db, "Raiffeisen", CurrencyCode.Rsd);
        var cashRsd = await IntegritySeed.AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        var wise = await IntegritySeed.AddWalletAsync(db, "Wise", CurrencyCode.Eur);
        var cashEur = await IntegritySeed.AddWalletAsync(db, "Cash EUR", CurrencyCode.Eur);
        var maxi = await MerchantAsync(db, "Maxi");
        var office = await MerchantAsync(db, "Exchange Office");

        // 30.00 EUR × 117.3333 = 3519.999, charged 3520.00 RSD; the 1 % fee on it is 35.20 RSD.
        await IntegritySeed.AddFxTermsAsync(db, raiffeisen, CurrencyCode.Eur, 117.3333m, 1m);
        var foreignPurchase = await IntegritySeed.RecordAsync(db, IntegritySeed.Expense(raiffeisen,
            new CategorizedLineItem("Groceries", IntegritySeed.Eur(20.00m), GroceriesId, maxi),
            new CategorizedLineItem("Coffee", IntegritySeed.Eur(5.00m), CoffeeId, null),
            new CategorizedLineItem("Groceries", IntegritySeed.Eur(5.00m), GroceriesId, maxi)));

        var withdrawal = await TransferAsync(db, new TransferFacts(
            raiffeisen, IntegritySeed.Rsd(10150.00m), cashRsd, IntegritySeed.Rsd(10000.00m),
            IntegritySeed.Rsd(150.00m), TransferLeg.From, StatedRate: null));
        var feeOnTo = await TransferAsync(db, new TransferFacts(
            wise, IntegritySeed.Eur(200.00m), cashEur, IntegritySeed.Eur(198.00m),
            IntegritySeed.Eur(2.00m), TransferLeg.To, StatedRate: null));
        var exchange = await TransferAsync(db, new TransferFacts(
            wise, IntegritySeed.Eur(100.00m), raiffeisen, IntegritySeed.Rsd(11700.00m),
            IntegritySeed.Eur(1.00m), TransferLeg.From, StatedRate: null, VenueMerchantId: office));

        var purchase = await IntegritySeed.RecordAsync(db, IntegritySeed.Expense(cashRsd,
            new CategorizedLineItem("Groceries", IntegritySeed.Rsd(500.00m), GroceriesId, null)));
        var refund = await IntegritySeed.RecordAsync(db, IntegritySeed.Income(cashRsd,
            new CategorizedLineItem("Returned groceries", IntegritySeed.Rsd(800.00m), GroceriesId, null),
            new CategorizedLineItem("Returned coffee", IntegritySeed.Rsd(100.00m), CoffeeId, null)));
        await MarkFiscalRefundAsync(db, refund, 900.00m);
        var salary = await IntegritySeed.RecordAsync(db, IntegritySeed.Income(wise,
            new CategorizedLineItem("Salary", IntegritySeed.Eur(2800.00m), SalaryId, null)));

        db.ChangeTracker.Clear();
        return new(raiffeisen, cashRsd, wise, cashEur, foreignPurchase, withdrawal, feeOnTo, exchange, purchase, refund, salary);
    }

    public static Task<Guid> TransferAsync(LedgerDbContext db, TransferFacts facts) =>
        IntegritySeed.RecordAsync(db, IntegritySeed.TransferOutcome(facts));

    // A fiscal Refund receipt makes an Income record (ReceiptCategorizationWorker); only its receipt row tells it apart.
    public static Task MarkFiscalRefundAsync(LedgerDbContext db, Guid transactionId, decimal total) =>
        db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO receipts (id, transaction_id, source, receipt_kind, created_at, total, currency)
            VALUES ({Guid.NewGuid()}, {transactionId}, {(int)ReceiptSource.FiscalQr}, {(int)ReceiptKind.Refund},
                    {IntegritySeed.Clock.GetUtcNow()}, {total}, 'RSD')
            """,
            Ct);

    static async Task<Guid> MerchantAsync(LedgerDbContext db, string name)
    {
        var merchant = new Merchant { Id = Guid.NewGuid(), DisplayName = name, Kind = MerchantKind.Retail };
        db.Merchants.Add(merchant);
        await db.SaveChangesAsync(Ct);
        return merchant.Id;
    }
}
