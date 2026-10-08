using AwesomeAssertions;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.BugReports;
using Noof.Ledger.Persistence.Revisions;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class RecordSummaryComposerTests(PostgresFixture fixture)
{
    const string FiscalLink = "https://suf.purs.gov.rs/v/?vl=QUJDREVGR0hJSktMTU5PUFFSU1RVVldY";
    static readonly Guid GroceriesId = new("00000000-0000-0000-0001-000000000001");
    static readonly Guid FeesId = new("00000000-0000-0000-0001-000000000013");
    static readonly DateTimeOffset At = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
    static readonly IFiscalVerificationUrl VerificationUrl =
        new FiscalVerificationUrl(new FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" });
    static int nextMessageId = 85_000;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static Task<string?> ComposeAsync(LedgerDbContext db, Guid transactionId) =>
        new RecordSummaryComposer(db, VerificationUrl).ComposeAsync(transactionId, Ct);

    static string Lines(string text) => text.ReplaceLineEndings("\n");

    static Wallet AddWallet(LedgerDbContext db, string name, CurrencyCode currency)
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), Name = name, Currency = currency, CreatedAt = At };
        db.Wallets.Add(wallet);
        return wallet;
    }

    static Transaction AddRecord(
        LedgerDbContext db, TransactionKind kind, Guid? walletId, string rawText,
        TransactionStatus status = TransactionStatus.Completed, RecordFailureReason reason = RecordFailureReason.None)
    {
        var record = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            Kind = kind,
            RawText = rawText,
            Status = status,
            FailureReason = reason,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = At,
            OccurredOn = new DateOnly(2026, 9, 10),
            TelegramChatId = 111,
            TelegramMessageId = Interlocked.Increment(ref nextMessageId),
            CreatedAt = At,
        };
        db.Transactions.Add(record);
        return record;
    }

    static void AddLine(
        LedgerDbContext db, Transaction record, int ordinal, string description, Money amount, Guid? categoryId,
        EntryRole role = EntryRole.Principal) =>
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = record.Id,
            Description = description,
            Amount = amount,
            CategoryId = categoryId,
            CategorizedBy = role == EntryRole.Fee ? CategorizationAuthority.Rule : CategorizationAuthority.Model,
            Ordinal = ordinal,
            Role = role,
        });

    static void AddRevision(
        LedgerDbContext db, Transaction record, int number, RevisionKind kind, string? instruction,
        TransactionStatus before, DateTimeOffset at) =>
        db.TransactionRevisions.Add(new TransactionRevision
        {
            Id = Guid.NewGuid(),
            TransactionId = record.Id,
            RevisionNumber = number,
            Kind = kind,
            Instruction = instruction,
            StatusBefore = before,
            StatusAfter = record.Status,
            Snapshot = "{}",
            CreatedAt = at,
        });

    // fi-FI writes "1000,00" and "10.01": every figure must still read invariant.
    [Fact]
    public async Task An_expense_lists_its_lines_by_ordinal_its_charges_and_its_revisions_without_fiscal_links()
    {
        using var culture = new CultureScope("fi-FI");
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = AddWallet(db, "Kaspi KZT", CurrencyCode.Kzt);
        var record = AddRecord(db, TransactionKind.Expense, kaspi.Id, $"обед 1000 тенге и 30 долларов {FiscalLink}");
        AddLine(db, record, 3, "Fee", new Money(154m, CurrencyCode.Kzt), FeesId, EntryRole.Fee);
        AddLine(db, record, 1, "обед", new Money(1000m, CurrencyCode.Kzt), GroceriesId);
        AddLine(db, record, 4, "без категории", new Money(5m, CurrencyCode.Kzt), categoryId: null);
        AddLine(db, record, 2, $"ужин {FiscalLink}", new Money(30m, CurrencyCode.Usd), GroceriesId);
        db.Charges.Add(new Charge
        {
            TransactionId = record.Id,
            Currency = CurrencyCode.Usd,
            ChargedAmount = 15_600m,
            FeeAmount = 154m,
            RateUsed = 520m,
            FeePercent = 1m,
            Source = ChargeSource.WalletTerms,
        });
        AddRevision(db, record, 1, RevisionKind.Initial, null, TransactionStatus.Captured,
            new DateTimeOffset(2026, 9, 10, 10, 1, 0, TimeSpan.Zero));
        AddRevision(db, record, 2, RevisionKind.Correction, $"ужин был 30 {FiscalLink}", TransactionStatus.Completed,
            new DateTimeOffset(2026, 9, 11, 8, 30, 0, TimeSpan.Zero));
        AddRevision(db, record, 3, RevisionKind.Edit, FiscalLink, TransactionStatus.Completed,
            new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync(Ct);

        (await ComposeAsync(db, record.Id)).Should().Be(Lines("""
            Record: Expense · Completed · captured from Text
            Date: 2026-09-10
            Wallet: Kaspi KZT
            Text: обед 1000 тенге и 30 долларов
            Lines:
            - обед · 1000.00 KZT · Groceries
            - ужин · 30.00 USD · Groceries
            - Fee · 154.00 KZT · Fees & Charges · fee
            - без категории · 5.00 KZT · uncategorised
            Charges:
            - 30.00 USD charged 15600.00 KZT at 520 · fee 154.00 KZT · WalletTerms
            Revisions:
            - 2026-09-10 10:01 UTC · Initial · Captured → Completed
            - 2026-09-11 08:30 UTC · Correction · ужин был 30
            - 2026-09-12 09:00 UTC · Edit · Completed → Completed
            """));
    }

    [Fact]
    public async Task A_transfer_names_both_legs_its_fee_and_its_stated_rate_instead_of_a_wallet()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var cashEur = AddWallet(db, "Cash EUR", CurrencyCode.Eur);
        var cashRsd = AddWallet(db, "Cash RSD", CurrencyCode.Rsd);
        var record = AddRecord(db, TransactionKind.Transfer, cashEur.Id, "поменял 100 евро на 11500 динар, комиссия 200");
        AddLine(db, record, 1, "Fee", new Money(200m, CurrencyCode.Rsd), FeesId, EntryRole.Fee);
        db.Transfers.Add(new Transfer
        {
            TransactionId = record.Id,
            FromWalletId = cashEur.Id,
            From = new Money(100m, CurrencyCode.Eur),
            ToWalletId = cashRsd.Id,
            To = new Money(11_500m, CurrencyCode.Rsd),
            FeeLeg = TransferLeg.To,
            StatedRate = 117m,
            StatedRateBase = CurrencyCode.Eur,
        });
        await db.SaveChangesAsync(Ct);

        (await ComposeAsync(db, record.Id)).Should().Be(Lines("""
            Record: Transfer · Completed · captured from Text
            Date: 2026-09-10
            From: Cash EUR · 100.00 EUR
            To: Cash RSD · 11500.00 RSD
            Fee: 200.00 RSD on the To leg
            Stated rate: 1 EUR = 117.0000 RSD
            Text: поменял 100 евро на 11500 динар, комиссия 200
            Lines:
            - Fee · 200.00 RSD · Fees & Charges · fee
            Revisions: (none)
            """));
    }

    [Fact]
    public async Task A_statement_shows_the_balance_it_states()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var raiffeisen = AddWallet(db, "Raiffeisen RSD", CurrencyCode.Rsd);
        var record = AddRecord(db, TransactionKind.BalanceCheck, raiffeisen.Id, "на счету 45000");
        db.BalanceChecks.Add(new BalanceCheck
        {
            TransactionId = record.Id,
            WalletId = raiffeisen.Id,
            Stated = new Money(45_000m, CurrencyCode.Rsd),
            ComputedBefore = 44_800m,
        });
        await db.SaveChangesAsync(Ct);

        (await ComposeAsync(db, record.Id)).Should().Be(Lines("""
            Record: BalanceCheck · Completed · captured from Text
            Date: 2026-09-10
            Wallet: Raiffeisen RSD
            Statement: 45000.00 RSD
            Text: на счету 45000
            Lines: (none)
            Revisions: (none)
            """));
    }

    [Fact]
    public async Task A_failed_record_names_its_reason_and_what_it_does_not_have()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var record = AddRecord(db, TransactionKind.Expense, walletId: null, FiscalLink,
            TransactionStatus.Failed, RecordFailureReason.MissingReceivedAmount);
        await db.SaveChangesAsync(Ct);

        (await ComposeAsync(db, record.Id)).Should().Be(Lines("""
            Record: Expense · Failed · failure reason MissingReceivedAmount · captured from Text
            Date: 2026-09-10
            Wallet: (none)
            Text: (none)
            Lines: (none)
            Revisions: (none)
            """));
    }

    [Fact]
    public async Task A_record_that_does_not_exist_has_no_summary()
    {
        await using var db = await fixture.CreateMigratedContextAsync();

        (await ComposeAsync(db, Guid.NewGuid())).Should().BeNull();
    }

    // Wallet and category names are the operator's own words too, so a pasted fiscal link there loses it like the raw
    // text does - each name on its own, so the summary's lines stay apart (spec P-10).
    [Fact]
    public async Task Wallet_and_category_names_lose_their_fiscal_links()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var cashEur = AddWallet(db, $"Cash {FiscalLink} EUR", CurrencyCode.Eur);
        var cashRsd = AddWallet(db, FiscalLink, CurrencyCode.Rsd);
        var categoryId = Guid.NewGuid();
        db.Categories.Add(new Category
        {
            Id = categoryId, Slug = $"synthetic-{categoryId:N}", NameEn = $"Office {FiscalLink}", NameRu = "Офис", IsActive = true,
        });
        var record = AddRecord(db, TransactionKind.Transfer, cashEur.Id, "поменял 100 евро");
        AddLine(db, record, 1, "Fee", new Money(200m, CurrencyCode.Rsd), categoryId, EntryRole.Fee);
        db.Transfers.Add(new Transfer
        {
            TransactionId = record.Id,
            FromWalletId = cashEur.Id,
            From = new Money(100m, CurrencyCode.Eur),
            ToWalletId = cashRsd.Id,
            To = new Money(11_500m, CurrencyCode.Rsd),
            FeeLeg = TransferLeg.To,
        });
        await db.SaveChangesAsync(Ct);

        (await ComposeAsync(db, record.Id)).Should().Be(Lines("""
            Record: Transfer · Completed · captured from Text
            Date: 2026-09-10
            From: Cash EUR · 100.00 EUR
            To:  · 11500.00 RSD
            Fee: 200.00 RSD on the To leg
            Text: поменял 100 евро
            Lines:
            - Fee · 200.00 RSD · Office · fee
            Revisions: (none)
            """));
    }
}
