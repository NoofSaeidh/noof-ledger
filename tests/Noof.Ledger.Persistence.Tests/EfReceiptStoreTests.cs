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

    static Transaction NewLinkTransaction() => new()
    {
        Id = Guid.NewGuid(),
        WalletId = DefaultWalletId,
        RawText = null,
        CaptureKind = CaptureKind.Photo,
        TelegramFileId = null,
        VerificationUrl = "https://suf.purs.gov.rs/v/?vl=synthetic",
        Status = TransactionStatus.Captured,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = Now,
        OccurredOn = new DateOnly(2026, 9, 25),
        TelegramChatId = 222,
        TelegramMessageId = Interlocked.Increment(ref nextMessageId),
        CreatedAt = Now,
    };

    static AppReceipts.ExtractedReceipt NewExtractedReceipt(string? sellerTaxId = "SYN-100000001", string? fiscalNumber = "SYN-1") => new(
        ReceiptSource.FiscalQr,
        VerificationUrl: "https://suf.purs.gov.rs/v/?vl=synthetic",
        SellerTaxId: sellerTaxId,
        SellerName: "Test Market",
        SellerAddress: "1 Test Street",
        LocationName: "Test Market - Centre",
        FiscalNumber: fiscalNumber,
        IssuedAt: Now,
        Total: 373.4567m,
        Currency: CurrencyCode.Rsd,
        Kind: ReceiptKind.Sale,
        PaymentMethod: PaymentMethod.Card,
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

        var result = await store.SaveExtractedAsync(transaction.Id, receipt, "photo-file-1", true, TestContext.Current.CancellationToken);

        result.ReceiptId.Should().NotBeNull();
        result.DuplicateOfTransactionId.Should().BeNull();

        var view = await store.GetByTransactionAsync(transaction.Id, TestContext.Current.CancellationToken);
        view.Should().NotBeNull();
        view!.Id.Should().Be(result.ReceiptId!.Value);
        view.Source.Should().Be(ReceiptSource.FiscalQr);
        view.SellerTaxId.Should().Be(receipt.SellerTaxId);
        view.SellerName.Should().Be(receipt.SellerName);
        view.Total.Should().Be(373.4567m);
        view.Currency.Should().Be(CurrencyCode.Rsd);
        view.Kind.Should().Be(ReceiptKind.Sale);
        view.PaymentMethod.Should().Be(PaymentMethod.Card);
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
    public async Task A_receipt_issued_at_a_Belgrade_offset_is_saved_and_read_back_as_the_same_instant()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        var issuedInBelgrade = new DateTimeOffset(2026, 9, 25, 12, 30, 0, TimeSpan.FromHours(2));

        await store.SaveExtractedAsync(
            transaction.Id, NewExtractedReceipt() with { IssuedAt = issuedInBelgrade }, "photo-file-1", true, TestContext.Current.CancellationToken);

        var view = await store.GetByTransactionAsync(transaction.Id, TestContext.Current.CancellationToken);
        view!.IssuedAt.Should().Be(issuedInBelgrade);
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

        var firstResult = await store.SaveExtractedAsync(first.Id, receipt, "photo-file-1", true, TestContext.Current.CancellationToken);
        var secondResult = await store.SaveExtractedAsync(second.Id, receipt, "photo-file-2", true, TestContext.Current.CancellationToken);

        firstResult.ReceiptId.Should().NotBeNull();
        secondResult.ReceiptId.Should().BeNull();
        secondResult.DuplicateOfTransactionId.Should().Be(first.Id);
        (await db.Receipts.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "the duplicate must write nothing, not a second receipt row");
    }

    // Copilot finding on PR #3: the QR-decoded-but-fetch-failed vision fallback now carries the QR's
    // own fiscal number into ExtractedReceipt.FiscalNumber (ExtractReceiptWorker), so two such vision
    // receipts for the same seller and QR fiscal number must collide on the duplicate index exactly
    // like two fiscal-QR receipts already do above. This guard is against EfReceiptStore directly and
    // did not change with that worker fix - the duplicate index and lookup already ignore Source - but
    // it is worth pinning now that a vision receipt can carry a QR-shaped fiscal number too.
    [Fact]
    public async Task A_second_QR_decoded_vision_receipt_with_the_same_seller_and_QR_fiscal_number_is_a_duplicate()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var first = NewPhotoTransaction();
        var second = NewPhotoTransaction();
        db.Transactions.AddRange(first, second);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        var receipt = NewExtractedReceipt(sellerTaxId: "SYN-300000003", fiscalNumber: "REQABCDE-SIG12345-42") with
        {
            Source = ReceiptSource.Vision,
        };

        var firstResult = await store.SaveExtractedAsync(first.Id, receipt, "photo-file-1", true, TestContext.Current.CancellationToken);
        var secondResult = await store.SaveExtractedAsync(second.Id, receipt, "photo-file-2", true, TestContext.Current.CancellationToken);

        firstResult.ReceiptId.Should().NotBeNull();
        secondResult.ReceiptId.Should().BeNull();
        secondResult.DuplicateOfTransactionId.Should().Be(first.Id);
        (await db.Receipts.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task A_replayed_save_for_the_same_transaction_with_a_fiscal_receipt_returns_the_existing_receipt_not_a_duplicate()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        var receipt = NewExtractedReceipt(sellerTaxId: "SYN-400000004", fiscalNumber: "SYN-REPLAY-1");

        var first = await store.SaveExtractedAsync(transaction.Id, receipt, "photo-file-1", true, TestContext.Current.CancellationToken);
        var replay = await store.SaveExtractedAsync(transaction.Id, receipt, "photo-file-1", true, TestContext.Current.CancellationToken);

        replay.ReceiptId.Should().Be(first.ReceiptId, "a job replayed after a committed save must recognise its own receipt");
        replay.DuplicateOfTransactionId.Should().BeNull("a transaction is never a duplicate of itself");
        (await db.Receipts.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await db.CategorizationJobs.CountAsync(j => j.TransactionId == transaction.Id, TestContext.Current.CancellationToken))
            .Should().Be(1, "the replay must not enqueue a second CategorizeReceipt job");
    }

    [Fact]
    public async Task A_replayed_save_for_the_same_transaction_with_a_vision_receipt_returns_the_existing_receipt()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        var receipt = NewExtractedReceipt(sellerTaxId: null, fiscalNumber: null) with { Source = ReceiptSource.Vision };

        var first = await store.SaveExtractedAsync(transaction.Id, receipt, "photo-file-1", true, TestContext.Current.CancellationToken);
        var replay = await store.SaveExtractedAsync(transaction.Id, receipt, "photo-file-1", true, TestContext.Current.CancellationToken);

        replay.ReceiptId.Should().Be(first.ReceiptId, "IX_receipts_transaction_id must be recognised, not surfaced as a generic failure");
        replay.DuplicateOfTransactionId.Should().BeNull();
        (await db.Receipts.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
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

    [Fact]
    public async Task GetVerificationUrlAsync_returns_the_captures_own_link()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewLinkTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        var url = await store.GetVerificationUrlAsync(transaction.Id, TestContext.Current.CancellationToken);

        url.Should().Be("https://suf.purs.gov.rs/v/?vl=synthetic");
    }

    [Fact]
    public async Task SaveExtractedAsync_enqueues_a_CategorizeReceipt_job_for_the_transaction()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        await store.SaveExtractedAsync(transaction.Id, NewExtractedReceipt(), "photo-file-1", true, TestContext.Current.CancellationToken);

        var job = await db.CategorizationJobs.SingleAsync(j => j.TransactionId == transaction.Id, TestContext.Current.CancellationToken);
        job.Kind.Should().Be(JobKind.CategorizeReceipt);
        job.Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public async Task A_duplicate_receipt_enqueues_no_CategorizeReceipt_job_for_the_new_transaction()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var first = NewPhotoTransaction();
        var second = NewPhotoTransaction();
        db.Transactions.AddRange(first, second);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        var receipt = NewExtractedReceipt(sellerTaxId: "SYN-300000003", fiscalNumber: "SYN-DUP-2");

        await store.SaveExtractedAsync(first.Id, receipt, "photo-file-1", true, TestContext.Current.CancellationToken);
        await store.SaveExtractedAsync(second.Id, receipt, "photo-file-2", true, TestContext.Current.CancellationToken);

        (await db.CategorizationJobs.CountAsync(j => j.TransactionId == second.Id, TestContext.Current.CancellationToken))
            .Should().Be(0, "a duplicate writes nothing, including no follow-up job for the transaction it never really extracted");
    }

    [Fact]
    public async Task SaveExtractedAsync_with_enqueueCategorization_false_saves_the_receipt_but_no_job()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        var result = await store.SaveExtractedAsync(
            transaction.Id, NewExtractedReceipt(), "photo-file-1", enqueueCategorization: false, TestContext.Current.CancellationToken);

        result.ReceiptId.Should().NotBeNull();
        (await store.GetByTransactionAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().NotBeNull();
        (await db.CategorizationJobs.CountAsync(j => j.TransactionId == transaction.Id, TestContext.Current.CancellationToken))
            .Should().Be(0, "a receipt that does not add up is saved for the echo to show, but categorisation waits for Record anyway");
    }

    [Fact]
    public async Task EnqueueCategorizationAsync_queues_a_CategorizeReceipt_job_with_the_echo_message_id()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        await store.SaveExtractedAsync(
            transaction.Id, NewExtractedReceipt(), "photo-file-1", enqueueCategorization: false, TestContext.Current.CancellationToken);

        var queued = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);

        queued.Should().BeTrue();
        var job = await db.CategorizationJobs.SingleAsync(j => j.TransactionId == transaction.Id, TestContext.Current.CancellationToken);
        job.Kind.Should().Be(JobKind.CategorizeReceipt);
        job.Status.Should().Be(JobStatus.Pending);
        job.SourceMessageId.Should().Be(999);
    }

    [Fact]
    public async Task EnqueueCategorizationAsync_pressed_twice_for_the_same_echo_message_is_a_no_op_the_second_time()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        await store.SaveExtractedAsync(
            transaction.Id, NewExtractedReceipt(), "photo-file-1", enqueueCategorization: false, TestContext.Current.CancellationToken);

        var first = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);
        var second = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);

        first.Should().BeTrue();
        second.Should().BeFalse();
        (await db.CategorizationJobs.CountAsync(j => j.TransactionId == transaction.Id, TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task IsAwaitingConfirmationAsync_is_true_for_a_vision_receipt_saved_without_a_job()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        await store.SaveExtractedAsync(
            transaction.Id, NewExtractedReceipt() with { Source = ReceiptSource.Vision }, "photo-file-1",
            enqueueCategorization: false, TestContext.Current.CancellationToken);

        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    // Independent of the transaction's own status (2026-09-27): RecordActionHandler needs the same
    // answer right after Cancel (Captured -> Cancelled, the job still never existed) as it gets before
    // either button is pressed.
    [Fact]
    public async Task IsAwaitingConfirmationAsync_stays_true_after_the_transaction_is_cancelled()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        await store.SaveExtractedAsync(
            transaction.Id, NewExtractedReceipt() with { Source = ReceiptSource.Vision }, "photo-file-1",
            enqueueCategorization: false, TestContext.Current.CancellationToken);
        var toCancel = await db.Transactions.SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken);
        toCancel.Status = TransactionStatus.Cancelled;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAwaitingConfirmationAsync_is_false_once_Record_anyway_has_queued_the_job()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        await store.SaveExtractedAsync(
            transaction.Id, NewExtractedReceipt() with { Source = ReceiptSource.Vision }, "photo-file-1",
            enqueueCategorization: false, TestContext.Current.CancellationToken);
        await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);

        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAwaitingConfirmationAsync_is_false_for_a_fiscal_QR_receipt_with_no_job()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        await store.SaveExtractedAsync(
            transaction.Id, NewExtractedReceipt(), "photo-file-1", enqueueCategorization: false, TestContext.Current.CancellationToken);

        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task IsAwaitingConfirmationAsync_is_false_when_no_receipt_exists_for_the_transaction()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeFalse();
    }
}
