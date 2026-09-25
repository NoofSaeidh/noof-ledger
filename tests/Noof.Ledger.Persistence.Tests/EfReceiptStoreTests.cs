using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Receipts;
using AppReceipts = Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfReceiptStoreTests(PostgresFixture fixture)
{
    static readonly Guid DefaultWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly DateTimeOffset Now = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
    static int nextMessageId = 4000;

    static Transaction NewPhotoTransaction() => new()
    {
        Id = Guid.NewGuid(),
        WalletId = DefaultWalletId,
        RawText = null,
        CaptureKind = CaptureKind.Photo,
        TelegramFileId = "photo-file-1",
        Status = TransactionStatus.Captured,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = Now,
        OccurredOn = new DateOnly(2026, 9, 25),
        TelegramChatId = 222,
        TelegramMessageId = Interlocked.Increment(ref nextMessageId),
        CreatedAt = Now,
    };

    static AppReceipts.ExtractedReceipt NewExtractedReceipt(string? sellerTaxId = "SYN-100000001", string? fiscalNumber = "SYN-1") => new(
        AppReceipts.ReceiptSource.FiscalQr,
        VerificationUrl: "https://suf.purs.gov.rs/v/?vl=synthetic",
        SellerTaxId: sellerTaxId,
        SellerName: "Test Market",
        SellerAddress: "1 Test Street",
        LocationName: "Test Market - Centre",
        FiscalNumber: fiscalNumber,
        IssuedAt: Now,
        Total: 373.4567m,
        Currency: CurrencyCode.Rsd,
        Kind: AppReceipts.ReceiptKind.Sale,
        PaymentMethod: AppReceipts.PaymentMethod.Card,
        QrTotal: 373.4567m,
        Lines:
        [
            new AppReceipts.ExtractedReceiptLine(1, "Bread", 1m, "kom", 123.4567m, 123.4567m, null),
            new AppReceipts.ExtractedReceiptLine(2, "Milk", 2m, "kom", 125m, 250m, "Ђ"),
        ]);

    [Fact]
    public async Task SaveExtractedAsync_round_trips_the_receipt_and_its_lines_with_exact_decimals()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        var receipt = NewExtractedReceipt();

        var result = await store.SaveExtractedAsync(transaction.Id, receipt, "photo-file-1", TestContext.Current.CancellationToken);

        result.ReceiptId.Should().NotBeNull();
        result.DuplicateOfTransactionId.Should().BeNull();

        var view = await store.GetByTransactionAsync(transaction.Id, TestContext.Current.CancellationToken);
        view.Should().NotBeNull();
        view!.Id.Should().Be(result.ReceiptId!.Value);
        view.Source.Should().Be(AppReceipts.ReceiptSource.FiscalQr);
        view.SellerTaxId.Should().Be(receipt.SellerTaxId);
        view.SellerName.Should().Be(receipt.SellerName);
        view.Total.Should().Be(373.4567m);
        view.Currency.Should().Be(CurrencyCode.Rsd);
        view.Kind.Should().Be(AppReceipts.ReceiptKind.Sale);
        view.PaymentMethod.Should().Be(AppReceipts.PaymentMethod.Card);
        view.QrTotal.Should().Be(373.4567m);
        view.VerificationUrl.Should().Be(receipt.VerificationUrl);
        view.Lines.Should().HaveCount(2);
        view.Lines[0].Ordinal.Should().Be(1);
        view.Lines[0].Name.Should().Be("Bread");
        view.Lines[0].UnitPrice.Should().Be(123.4567m);
        view.Lines[1].Ordinal.Should().Be(2);
        view.Lines[1].TaxLabel.Should().Be("Ђ");
    }

    [Fact]
    public async Task A_second_receipt_with_the_same_seller_and_fiscal_number_writes_nothing_and_names_the_first_transaction()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var first = NewPhotoTransaction();
        var second = NewPhotoTransaction();
        db.Transactions.AddRange(first, second);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        var receipt = NewExtractedReceipt(sellerTaxId: "SYN-200000002", fiscalNumber: "SYN-DUP-1");

        var firstResult = await store.SaveExtractedAsync(first.Id, receipt, "photo-file-1", TestContext.Current.CancellationToken);
        var secondResult = await store.SaveExtractedAsync(second.Id, receipt, "photo-file-2", TestContext.Current.CancellationToken);

        firstResult.ReceiptId.Should().NotBeNull();
        secondResult.ReceiptId.Should().BeNull();
        secondResult.DuplicateOfTransactionId.Should().Be(first.Id);
        (await db.Receipts.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "the duplicate must write nothing, not a second receipt row");
    }

    [Fact]
    public async Task GetByTransactionAsync_returns_null_when_the_transaction_has_no_receipt()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        var view = await store.GetByTransactionAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        view.Should().BeNull();
    }

    [Fact]
    public async Task GetTelegramFileIdAsync_returns_the_captures_own_file_id()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        var fileId = await store.GetTelegramFileIdAsync(transaction.Id, TestContext.Current.CancellationToken);

        fileId.Should().Be("photo-file-1");
    }
}
