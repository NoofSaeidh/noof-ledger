using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Balances;

namespace Noof.Ledger.Persistence.Tests;

// Each kind's branch on its own, over rows written straight into the context. Turning an outcome into those rows is
// EfCategorizationStore's job and LedgerWritePathTests' subject.
[Collection("postgres")]
public class LedgerPostingsTests(PostgresFixture fixture)
{
    static readonly Guid GroceriesId = new("00000000-0000-0000-0001-000000000001");
    static readonly Guid FeesId = new("00000000-0000-0000-0001-000000000013");
    static readonly DateTimeOffset At = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
    static int nextMessageId = 90_000;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static Money Rsd(decimal amount) => new(amount, CurrencyCode.Rsd);

    static Money Eur(decimal amount) => new(amount, CurrencyCode.Eur);

    static Guid AddWallet(LedgerDbContext db, string name, CurrencyCode currency)
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), Name = name, Currency = currency, CreatedAt = At };
        db.Wallets.Add(wallet);
        return wallet.Id;
    }

    static Transaction AddRecord(LedgerDbContext db, TransactionKind kind, Guid walletId)
    {
        var record = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            Kind = kind,
            RawText = "seeded",
            Status = TransactionStatus.Completed,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = At,
            OccurredOn = new DateOnly(2026, 9, 10),
            TelegramChatId = 1,
            TelegramMessageId = Interlocked.Increment(ref nextMessageId),
            CreatedAt = At,
        };
        db.Transactions.Add(record);
        return record;
    }

    static void AddLine(LedgerDbContext db, Transaction record, Money amount, EntryRole role, int ordinal) =>
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = record.Id,
            Description = role == EntryRole.Fee ? "Fee" : "line",
            Amount = amount,
            CategoryId = role == EntryRole.Fee ? FeesId : GroceriesId,
            CategorizedBy = role == EntryRole.Fee ? CategorizationAuthority.Rule : CategorizationAuthority.Model,
            Ordinal = ordinal,
            Role = role,
        });

    static async Task<IReadOnlyList<(Guid WalletId, Money Amount, EntryRole Role)>> EntriesOfAsync(
        LedgerDbContext db, Guid transactionId)
    {
        var entries = await db.Entries.AsNoTracking().Where(entry => entry.TransactionId == transactionId).ToListAsync(Ct);
        return
        [
            .. entries
                .OrderBy(entry => entry.Amount.Currency.Value)
                .ThenBy(entry => entry.Role)
                .ThenBy(entry => entry.Amount.Amount)
                .Select(entry => (entry.WalletId, entry.Amount, entry.Role)),
        ];
    }

    [Fact]
    public async Task An_expense_posts_its_fee_lines_as_fee_entries_beside_its_principal_entries()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var raiffeisen = AddWallet(db, "Raiffeisen RSD", CurrencyCode.Rsd);
        var record = AddRecord(db, TransactionKind.Expense, raiffeisen);
        AddLine(db, record, Rsd(1000m), EntryRole.Principal, 1);
        AddLine(db, record, Eur(3.50m), EntryRole.Principal, 2);
        AddLine(db, record, Rsd(10m), EntryRole.Fee, 3);
        await db.SaveChangesAsync(Ct);

        await LedgerPostings.RewriteAsync(db, record, null, null, Ct);

        (await EntriesOfAsync(db, record.Id)).Should().Equal(
            (raiffeisen, Eur(-3.50m), EntryRole.Principal),
            (raiffeisen, Rsd(-1000m), EntryRole.Principal),
            (raiffeisen, Rsd(-10m), EntryRole.Fee));
    }

    [Fact]
    public async Task A_transfer_posts_each_leg_on_its_own_wallet_and_a_source_fee_on_the_source()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var raiffeisen = AddWallet(db, "Raiffeisen RSD", CurrencyCode.Rsd);
        var cash = AddWallet(db, "Cash RSD", CurrencyCode.Rsd);
        var record = AddRecord(db, TransactionKind.Transfer, raiffeisen);
        await db.SaveChangesAsync(Ct);
        // "снял 10000 с райфа, комиссия 150": 10150 left the source, 10000 reached the cash (T-12).
        var withdrawal = new TransferFacts(raiffeisen, Rsd(10150m), cash, Rsd(10000m), Rsd(150m), TransferLeg.From, null);

        await LedgerPostings.RewriteAsync(db, record, null, withdrawal, Ct);
        await LedgerPostings.RewriteAsync(db, record, null, withdrawal, Ct);

        var entries = await EntriesOfAsync(db, record.Id);
        entries.Should().Equal(
            (raiffeisen, Rsd(-10000m), EntryRole.Principal),
            (cash, Rsd(10000m), EntryRole.Principal),
            (raiffeisen, Rsd(-150m), EntryRole.Fee));
        entries.Where(entry => entry.WalletId == raiffeisen).Sum(entry => entry.Amount.Amount)
            .Should().Be(-10150m, "the source's entries sum to all that left it");
        var stored = await db.Transfers.AsNoTracking().SingleAsync(row => row.TransactionId == record.Id, Ct);
        stored.FromWalletId.Should().Be(raiffeisen);
        stored.From.Should().Be(Rsd(10150m));
        stored.ToWalletId.Should().Be(cash);
        stored.To.Should().Be(Rsd(10000m));
        stored.FeeLeg.Should().Be(TransferLeg.From);
        stored.StatedRate.Should().BeNull();
        stored.StatedRateBase.Should().BeNull();
        stored.VenueMerchantId.Should().BeNull();
    }

    [Fact]
    public async Task A_fee_kept_by_the_receiving_side_is_posted_on_the_destination_and_the_stated_rate_is_kept()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var cashEur = AddWallet(db, "Cash EUR", CurrencyCode.Eur);
        var cashRsd = AddWallet(db, "Cash RSD", CurrencyCode.Rsd);
        var record = AddRecord(db, TransactionKind.Transfer, cashEur);
        await db.SaveChangesAsync(Ct);
        // 100 EUR at 117 is 11700 RSD, of which the office kept 200: 11500 reached the destination.
        var exchange = new TransferFacts(
            cashEur, Eur(100m), cashRsd, Rsd(11500m), Rsd(200m), TransferLeg.To,
            new ExchangeRate(CurrencyCode.Eur, 117m, CurrencyCode.Rsd));

        await LedgerPostings.RewriteAsync(db, record, null, exchange, Ct);

        var entries = await EntriesOfAsync(db, record.Id);
        entries.Should().Equal(
            (cashEur, Eur(-100m), EntryRole.Principal),
            (cashRsd, Rsd(11700m), EntryRole.Principal),
            (cashRsd, Rsd(-200m), EntryRole.Fee));
        entries.Where(entry => entry.WalletId == cashRsd).Sum(entry => entry.Amount.Amount)
            .Should().Be(11500m, "the destination's entries sum to all that reached it");
        var stored = await db.Transfers.AsNoTracking().SingleAsync(row => row.TransactionId == record.Id, Ct);
        stored.FeeLeg.Should().Be(TransferLeg.To);
        stored.StatedRate.Should().Be(117m);
        stored.StatedRateBase.Should().Be(CurrencyCode.Eur);
    }

    [Fact]
    public async Task A_record_that_is_no_longer_a_transfer_keeps_no_transfer_row()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var raiffeisen = AddWallet(db, "Raiffeisen RSD", CurrencyCode.Rsd);
        var cash = AddWallet(db, "Cash RSD", CurrencyCode.Rsd);
        var record = AddRecord(db, TransactionKind.Transfer, raiffeisen);
        await db.SaveChangesAsync(Ct);
        await LedgerPostings.RewriteAsync(
            db, record, null, new TransferFacts(raiffeisen, Rsd(500m), cash, Rsd(500m), null, null, null), Ct);

        record.Kind = TransactionKind.Expense;
        AddLine(db, record, Rsd(500m), EntryRole.Principal, 1);
        await db.SaveChangesAsync(Ct);
        await LedgerPostings.RewriteAsync(db, record, null, null, Ct);

        (await db.Transfers.CountAsync(row => row.TransactionId == record.Id, Ct)).Should().Be(0,
            "leaving a kind deletes its facts, as leaving a balance statement deletes its checkpoint");
        (await EntriesOfAsync(db, record.Id)).Should().Equal((raiffeisen, Rsd(-500m), EntryRole.Principal));
    }

    [Fact]
    public async Task A_kind_with_no_posting_of_its_own_is_refused_rather_than_posted_as_an_expense()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var raiffeisen = AddWallet(db, "Raiffeisen RSD", CurrencyCode.Rsd);
        var record = AddRecord(db, TransactionKind.Expense, raiffeisen);
        AddLine(db, record, Rsd(250m), EntryRole.Principal, 1);
        await db.SaveChangesAsync(Ct);
        await LedgerPostings.RewriteAsync(db, record, null, null, Ct);
        (await EntriesOfAsync(db, record.Id)).Should().NotBeEmpty("the record was posted once as an expense");
        record.Kind = (TransactionKind)99;

        var act = () => LedgerPostings.RewriteAsync(db, record, null, null, Ct);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await EntriesOfAsync(db, record.Id)).Should().BeEmpty("nothing posts a kind the ledger has no rule for");
    }
}
