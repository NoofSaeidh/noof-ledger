using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Configurations;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class TransferSchemaTests(PostgresFixture fixture)
{
    static readonly Guid MainWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly Guid FeesAndChargesId = new("00000000-0000-0000-0001-000000000013");
    static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    static int nextMessageId = 4000;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static Transaction Record(Guid walletId, TransactionKind kind = TransactionKind.Transfer) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = walletId,
        Kind = kind,
        RawText = "поменял 100 евро на 11700 динар",
        Status = TransactionStatus.Completed,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = Now,
        OccurredOn = new DateOnly(2026, 10, 1),
        TelegramChatId = 111,
        TelegramMessageId = Interlocked.Increment(ref nextMessageId),
        CreatedAt = Now,
    };

    static Transaction Photo() => new()
    {
        Id = Guid.NewGuid(),
        WalletId = MainWalletId,
        RawText = null,
        CaptureKind = CaptureKind.Photo,
        TelegramFileId = "photo-1",
        Status = TransactionStatus.Captured,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = Now,
        OccurredOn = new DateOnly(2026, 10, 1),
        TelegramChatId = 111,
        TelegramMessageId = Interlocked.Increment(ref nextMessageId),
        CreatedAt = Now,
    };

    static Wallet NewWallet(string name, CurrencyCode currency) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Currency = currency,
        CreatedAt = Now,
    };

    static Transfer Exchange(Guid transactionId, Guid fromWalletId, Guid toWalletId, decimal from = 100m, decimal to = 11700m) => new()
    {
        TransactionId = transactionId,
        FromWalletId = fromWalletId,
        From = new Money(from, CurrencyCode.Eur),
        ToWalletId = toWalletId,
        To = new Money(to, CurrencyCode.Rsd),
    };

    static Charge UsdCharge(Guid transactionId, decimal charged = 15600m, decimal fee = 156m, decimal rate = 520m) => new()
    {
        TransactionId = transactionId,
        Currency = CurrencyCode.Usd,
        ChargedAmount = charged,
        FeeAmount = fee,
        RateUsed = rate,
        FeePercent = 1m,
        Source = ChargeSource.WalletTerms,
    };

    static Receipt Slip(Guid transactionId, ReceiptKind kind = ReceiptKind.Exchange, string? slipNumber = "POT-000123") => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        Source = ReceiptSource.Vision,
        SellerTaxId = "SYN-100000001",
        SellerName = "Menjačnica Test",
        IssuedAt = Now,
        Total = new Money(11700m, CurrencyCode.Rsd),
        Kind = kind,
        SlipNumber = slipNumber,
        CreatedAt = Now,
    };

    // A transfer record whose source is a Cash EUR wallet; the destination is the seeded Main Wallet (RSD).
    static async Task<(Guid TransactionId, Guid CashEur)> SeedTransferRecordAsync(LedgerDbContext db)
    {
        var cashEur = NewWallet("Cash EUR", CurrencyCode.Eur);
        var record = Record(cashEur.Id);
        db.Wallets.Add(cashEur);
        db.Transactions.Add(record);
        await db.SaveChangesAsync(Ct);
        return (record.Id, cashEur.Id);
    }

    static async Task<string?> ViolatedConstraintAsync(LedgerDbContext db)
    {
        try
        {
            await db.SaveChangesAsync(Ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException postgres)
        {
            return postgres.ConstraintName;
        }
    }

    [Fact]
    public async Task A_transfer_keeps_both_legs_its_fee_leg_and_its_stated_rate_with_every_digit()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (transactionId, cashEur) = await SeedTransferRecordAsync(db);
        var transfer = Exchange(transactionId, cashEur, MainWalletId, from: 102m, to: 11700m);
        transfer.FeeLeg = TransferLeg.From;
        transfer.StatedRate = 117.123456789012m;
        transfer.StatedRateBase = CurrencyCode.Eur;
        db.Transfers.Add(transfer);
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        var stored = await db.Transfers.AsNoTracking().SingleAsync(t => t.TransactionId == transactionId, Ct);

        stored.Should().BeEquivalentTo(transfer, "stated_rate is numeric(24,12); the (19,4) default would keep 117.1235");
    }

    [Theory]
    [InlineData(0, 11700)]
    [InlineData(100, 0)]
    [InlineData(-100, 11700)]
    public async Task A_transfer_leg_that_is_not_positive_is_refused(int from, int to)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (transactionId, cashEur) = await SeedTransferRecordAsync(db);
        db.Transfers.Add(Exchange(transactionId, cashEur, MainWalletId, from, to));

        (await ViolatedConstraintAsync(db)).Should().Be("ck_transfers_amounts_positive");
    }

    [Fact]
    public async Task A_transfer_from_a_wallet_to_itself_is_refused()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (transactionId, cashEur) = await SeedTransferRecordAsync(db);
        var transfer = Exchange(transactionId, cashEur, cashEur);
        transfer.To = new Money(100m, CurrencyCode.Eur);
        db.Transfers.Add(transfer);

        (await ViolatedConstraintAsync(db)).Should().Be("ck_transfers_wallets_differ");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_stated_rate_and_its_base_currency_are_stored_together_or_not_at_all(bool withRate, bool withBase)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (transactionId, cashEur) = await SeedTransferRecordAsync(db);
        var transfer = Exchange(transactionId, cashEur, MainWalletId);
        transfer.StatedRate = withRate ? 117m : null;
        transfer.StatedRateBase = withBase ? CurrencyCode.Eur : null;
        db.Transfers.Add(transfer);

        (await ViolatedConstraintAsync(db)).Should().Be("ck_transfers_stated_rate_has_base");
    }

    [Fact]
    public async Task A_wallet_a_transfer_pays_into_cannot_be_deleted()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (transactionId, cashEur) = await SeedTransferRecordAsync(db);
        var cashRsd = NewWallet("Cash RSD", CurrencyCode.Rsd);
        db.Wallets.Add(cashRsd);
        db.Transfers.Add(Exchange(transactionId, cashEur, cashRsd.Id));
        await db.SaveChangesAsync(Ct);

        var act = () => db.Database.ExecuteSqlAsync($"DELETE FROM public.wallets WHERE id = {cashRsd.Id}", Ct);

        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("FK_transfers_wallets_to_wallet_id");
    }

    [Fact]
    public async Task Both_legs_of_a_transfer_are_indexed()
    {
        await using var db = await fixture.CreateMigratedContextAsync();

        var indexes = await db.Database.SqlQueryRaw<string>(
            """SELECT indexname AS "Value" FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'transfers'""")
            .ToListAsync(Ct);

        indexes.Should().Contain(["ix_transfers_from_wallet_id", "ix_transfers_to_wallet_id"]);
    }

    [Fact]
    public async Task Deleting_a_record_deletes_its_transfer_and_its_charges()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (transferRecordId, cashEur) = await SeedTransferRecordAsync(db);
        var spending = Record(MainWalletId, TransactionKind.Expense);
        db.Transactions.Add(spending);
        db.Transfers.Add(Exchange(transferRecordId, cashEur, MainWalletId));
        db.Charges.Add(UsdCharge(spending.Id));
        await db.SaveChangesAsync(Ct);

        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM public.transactions WHERE id IN ({transferRecordId}, {spending.Id})", Ct);

        (await db.Transfers.CountAsync(Ct)).Should().Be(0);
        (await db.Charges.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Wallet_terms_keep_every_digit_of_the_rate_and_the_fees()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = NewWallet("Kaspi KZT", CurrencyCode.Kzt);
        var terms = new WalletFxTerms
        {
            WalletId = kaspi.Id,
            Currency = CurrencyCode.Usd,
            Rate = 520.123456789012m,
            FeePercent = 1.2345m,
            FeeFixed = 150.5m,
            FeeMinimum = 1000m,
        };
        db.Wallets.Add(kaspi);
        db.WalletFxTerms.Add(terms);
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        (await db.WalletFxTerms.AsNoTracking().SingleAsync(t => t.WalletId == kaspi.Id, Ct)).Should().BeEquivalentTo(terms);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-520)]
    public async Task A_wallet_term_whose_rate_is_not_positive_is_refused(int rate)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = NewWallet("Kaspi KZT", CurrencyCode.Kzt);
        db.Wallets.Add(kaspi);
        db.WalletFxTerms.Add(new WalletFxTerms { WalletId = kaspi.Id, Currency = CurrencyCode.Usd, Rate = rate });

        (await ViolatedConstraintAsync(db)).Should().Be("ck_wallet_fx_terms_rate_positive");
    }

    [Fact]
    public async Task Deleting_a_wallet_deletes_its_terms()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = NewWallet("Kaspi KZT", CurrencyCode.Kzt);
        db.Wallets.Add(kaspi);
        db.WalletFxTerms.Add(new WalletFxTerms { WalletId = kaspi.Id, Currency = CurrencyCode.Usd, Rate = 520m });
        await db.SaveChangesAsync(Ct);

        await db.Database.ExecuteSqlAsync($"DELETE FROM public.wallets WHERE id = {kaspi.Id}", Ct);

        (await db.WalletFxTerms.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task A_charge_keeps_its_rate_and_its_terms_snapshot_with_every_digit()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var spending = Record(MainWalletId, TransactionKind.Expense);
        var charge = UsdCharge(spending.Id, charged: 15400m, fee: 154m, rate: 513.333333333333m);
        charge.FeePercent = 1.2345m;
        charge.FeeFixed = 30.5m;
        charge.FeeMinimum = 200m;
        charge.Source = ChargeSource.Stated;
        db.Transactions.Add(spending);
        db.Charges.Add(charge);
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        (await db.Charges.AsNoTracking().SingleAsync(c => c.TransactionId == spending.Id, Ct)).Should().BeEquivalentTo(charge);
    }

    [Fact]
    public async Task A_charge_with_no_fee_is_accepted()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var spending = Record(MainWalletId, TransactionKind.Expense);
        db.Transactions.Add(spending);
        db.Charges.Add(UsdCharge(spending.Id, fee: 0m));

        (await ViolatedConstraintAsync(db)).Should().BeNull();
    }

    [Theory]
    [InlineData(0, 0, 520)]
    [InlineData(15600, -1, 520)]
    [InlineData(15600, 156, 0)]
    public async Task A_charge_that_is_not_positive_or_has_a_negative_fee_is_refused(int charged, int fee, int rate)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var spending = Record(MainWalletId, TransactionKind.Expense);
        db.Transactions.Add(spending);
        db.Charges.Add(UsdCharge(spending.Id, charged, fee, rate));

        (await ViolatedConstraintAsync(db)).Should().Be("ck_charges_amounts");
    }

    [Fact]
    public async Task A_slip_reading_keeps_every_field_exactly_as_read()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var photo = Photo();
        var slip = Slip(photo.Id);
        var reading = new ReceiptExchange
        {
            ReceiptId = slip.Id,
            GivenAmount = 100m,
            GivenCurrency = "EUR",
            ReceivedAmount = 11700m,
            ReceivedCurrency = "RSD",
            Rate = 117.123456789012m,
            CommissionAmount = 1.5m,
            CommissionCurrency = "EUR",
            SlipNumber = "pot-000123 ",
        };
        db.Transactions.Add(photo);
        db.Receipts.Add(slip);
        db.ReceiptExchanges.Add(reading);
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        (await db.ReceiptExchanges.AsNoTracking().SingleAsync(e => e.ReceiptId == slip.Id, Ct)).Should().BeEquivalentTo(reading);
    }

    [Fact]
    public async Task A_slip_reading_may_leave_every_field_unread()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var photo = Photo();
        var slip = Slip(photo.Id, slipNumber: null);
        var reading = new ReceiptExchange { ReceiptId = slip.Id };
        db.Transactions.Add(photo);
        db.Receipts.Add(slip);
        db.ReceiptExchanges.Add(reading);
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        (await db.ReceiptExchanges.AsNoTracking().SingleAsync(e => e.ReceiptId == slip.Id, Ct)).Should().BeEquivalentTo(reading);
    }

    [Fact]
    public async Task A_second_slip_from_the_same_office_with_the_same_number_is_refused()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var first = Photo();
        var second = Photo();
        db.Transactions.AddRange(first, second);
        db.Receipts.Add(Slip(first.Id));
        await db.SaveChangesAsync(Ct);
        db.Receipts.Add(Slip(second.Id));

        (await ViolatedConstraintAsync(db)).Should().Be(ReceiptConfiguration.SlipDuplicateIndex);
    }

    [Fact]
    public async Task The_slip_duplicate_guard_ignores_other_receipt_kinds_and_unread_slip_numbers()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        Transaction[] photos = [Photo(), Photo(), Photo(), Photo()];
        db.Transactions.AddRange(photos);
        db.Receipts.AddRange(
            Slip(photos[0].Id, ReceiptKind.Sale),
            Slip(photos[1].Id, ReceiptKind.Sale),
            Slip(photos[2].Id, slipNumber: null),
            Slip(photos[3].Id, slipNumber: null));

        (await ViolatedConstraintAsync(db)).Should().BeNull();
    }

    [Fact]
    public async Task A_line_item_written_without_a_role_is_a_principal_line()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var spending = Record(MainWalletId, TransactionKind.Expense);
        db.Transactions.Add(spending);
        await db.SaveChangesAsync(Ct);
        var lineId = Guid.NewGuid();

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO public.line_items (id, transaction_id, description, categorized_by, ordinal, amount, currency)
            VALUES ({lineId}, {spending.Id}, 'coffee', 1, 1, 250, 'RSD')
            """,
            Ct);

        (await db.LineItems.AsNoTracking().SingleAsync(l => l.Id == lineId, Ct)).Role.Should().Be(EntryRole.Principal);
    }

    [Fact]
    public async Task A_fee_line_keeps_its_role()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var spending = Record(MainWalletId, TransactionKind.Expense);
        var fee = new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = spending.Id,
            Description = "Fee",
            Amount = new Money(150m, CurrencyCode.Rsd),
            CategoryId = FeesAndChargesId,
            CategorizedBy = CategorizationAuthority.Rule,
            Ordinal = 1,
            Role = EntryRole.Fee,
        };
        db.Transactions.Add(spending);
        db.LineItems.Add(fee);
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        (await db.LineItems.AsNoTracking().SingleAsync(l => l.Id == fee.Id, Ct)).Role.Should().Be(EntryRole.Fee);
    }

    [Fact]
    public async Task A_failed_record_keeps_its_failure_reason_and_any_other_record_has_none()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var failed = Record(MainWalletId);
        failed.Status = TransactionStatus.Failed;
        failed.FailureReason = RecordFailureReason.MissingReceivedAmount;
        var recorded = Record(MainWalletId, TransactionKind.Expense);
        db.Transactions.AddRange(failed, recorded);
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();

        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == failed.Id, Ct))
            .FailureReason.Should().Be(RecordFailureReason.MissingReceivedAmount);
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == recorded.Id, Ct)).FailureReason.Should().BeNull();
    }
}
