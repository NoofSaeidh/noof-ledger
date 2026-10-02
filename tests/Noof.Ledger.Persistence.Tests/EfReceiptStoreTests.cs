using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;
using Noof.Ledger.Persistence.Editing;
using Noof.Ledger.Persistence.Jobs;
using Noof.Ledger.Persistence.Receipts;
using Noof.Ledger.Persistence.Revisions;
using Npgsql;
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        var view = await store.GetByTransactionAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        view.Should().BeNull();
    }

    [Fact]
    public async Task GetTelegramFileIdAsync_returns_the_captures_own_file_id()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        await store.SaveExtractedAsync(
            transaction.Id, NewExtractedReceipt() with { Source = ReceiptSource.Vision }, "photo-file-1",
            enqueueCategorization: false, TestContext.Current.CancellationToken);

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
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        await store.SaveExtractedAsync(
            transaction.Id, NewExtractedReceipt() with { Source = ReceiptSource.Vision }, "photo-file-1",
            enqueueCategorization: false, TestContext.Current.CancellationToken);

        var first = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);
        var second = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);

        first.Should().BeTrue();
        second.Should().BeFalse();
        (await db.CategorizationJobs.CountAsync(j => j.TransactionId == transaction.Id, TestContext.Current.CancellationToken)).Should().Be(1);
    }

    // The status/awaiting-confirmation check runs inside EnqueueCategorizationAsync itself, under the
    // same row lock the insert runs under (2026-09-27) - not as a separate read the caller does first,
    // which a concurrent Cancel could land between. This proves the sequential case: once Cancel has
    // already committed, the guard must still catch it and insert nothing.
    [Fact]
    public async Task EnqueueCategorizationAsync_returns_false_and_inserts_nothing_once_the_transaction_is_cancelled()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
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
        db.ChangeTracker.Clear();

        var queued = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);

        queued.Should().BeFalse();
        (await db.CategorizationJobs.CountAsync(j => j.TransactionId == transaction.Id, TestContext.Current.CancellationToken))
            .Should().Be(0, "a Cancelled transaction must never get a CategorizeReceipt job queued for it");
    }

    // Proves the row lock item 2 added actually does something: without EnqueueCategorizationAsync's
    // own SELECT ... FOR UPDATE as its first statement, storeB reads Captured under READ COMMITTED
    // (dbA's Cancel has not committed yet), its INSERT waits only on the transactions FK's key-share
    // lock, and it commits a job for a row that becomes Cancelled a moment later - the same race the
    // sequential "once cancelled" test above cannot exercise, because there both writes are already
    // committed before EnqueueCategorizationAsync ever runs. Same two-context, one-connection-string
    // pattern as EfCategorizationStoreTests' Concurrent_ApplyAsync test.
    [Fact]
    public async Task EnqueueCategorizationAsync_blocks_on_a_concurrent_Cancel_and_then_honours_its_result()
    {
        await using var dbA = await fixture.CreateMigratedContextAsync();
        var transaction = NewPhotoTransaction();
        dbA.Transactions.Add(transaction);
        await dbA.SaveChangesAsync(TestContext.Current.CancellationToken);
        dbA.ChangeTracker.Clear();
        var seedingStore = new EfReceiptStore(dbA, new FakeTimeProvider(Now));
        await seedingStore.SaveExtractedAsync(
            transaction.Id, NewExtractedReceipt() with { Source = ReceiptSource.Vision }, "photo-file-1",
            enqueueCategorization: false, TestContext.Current.CancellationToken);

        var connectionString = dbA.Database.GetConnectionString();
        await using var dbB = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options);
        var storeB = new EfReceiptStore(dbB, new FakeTimeProvider(Now));

        await using var lockingTx = await dbA.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await dbA.Database.SqlQueryRaw<Guid>(
            "SELECT id FROM transactions WHERE id = @transactionId FOR UPDATE",
            new NpgsqlParameter("transactionId", transaction.Id))
            .ToListAsync(TestContext.Current.CancellationToken);

        var enqueueTask = storeB.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        enqueueTask.IsCompleted.Should().BeFalse("storeB must block on dbA's row lock, not read the pre-cancel status");

        var toCancel = await dbA.Transactions.SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken);
        toCancel.Status = TransactionStatus.Cancelled;
        await dbA.SaveChangesAsync(TestContext.Current.CancellationToken);
        await lockingTx.CommitAsync(TestContext.Current.CancellationToken);

        var queued = await enqueueTask;

        queued.Should().BeFalse();
        (await dbA.CategorizationJobs.CountAsync(j => j.TransactionId == transaction.Id, TestContext.Current.CancellationToken))
            .Should().Be(0, "the Cancel that committed first must win - no job for a Cancelled transaction");
    }

    [Fact]
    public async Task IsAwaitingConfirmationAsync_is_true_for_a_vision_receipt_saved_without_a_job()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
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
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = NewPhotoTransaction();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    // 00:30 in Belgrade on 26 September is still the 25th in UTC: a caption's InstructionDay is the local day.
    static readonly DateTimeOffset SlipSentAt = new(2026, 9, 25, 22, 30, 0, TimeSpan.Zero);

    static Transaction NewSlipPhotoTransaction(string? caption) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = null,
        RawText = caption,
        CaptureKind = CaptureKind.Photo,
        TelegramFileId = "photo-file-1",
        Status = TransactionStatus.Captured,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = SlipSentAt,
        OccurredOn = new DateOnly(2026, 9, 26),
        TelegramChatId = 222,
        TelegramMessageId = Interlocked.Increment(ref nextMessageId),
        CreatedAt = SlipSentAt,
    };

    static async Task<Transaction> SeedSlipTransactionAsync(LedgerDbContext db, string? caption = null)
    {
        var transaction = NewSlipPhotoTransaction(caption);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        return transaction;
    }

    static AppReceipts.ExtractedReceipt NewSlipReceipt(string? sellerTaxId = "123456789") => new(
        ReceiptSource.Vision,
        VerificationUrl: null,
        SellerTaxId: sellerTaxId,
        SellerName: "Menjačnica Zlatnik",
        SellerAddress: null,
        LocationName: null,
        FiscalNumber: null,
        IssuedAt: new DateTimeOffset(2026, 9, 26, 0, 25, 0, TimeSpan.FromHours(2)),
        Total: 11700.00m,
        Currency: CurrencyCode.Rsd,
        Kind: ReceiptKind.Exchange,
        PaymentMethod: PaymentMethod.Cash,
        QrTotal: null,
        Lines: []);

    static AppReceipts.ExtractedExchange NewSlip(string? slipNumber = " pz-2026-0917 ", decimal? received = 11700.00m) => new(
        GivenAmount: 100.00m,
        GivenCurrency: "EUR",
        ReceivedAmount: received,
        ReceivedCurrency: "RSD",
        Rate: 117.123456789012m,
        CommissionAmount: null,
        CommissionCurrency: null,
        SlipNumber: slipNumber);

    static async Task<List<CategorizationJob>> JobsOfAsync(LedgerDbContext db, Guid transactionId) =>
        await db.CategorizationJobs.AsNoTracking()
            .Where(j => j.TransactionId == transactionId)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task SaveExchangeSlipAsync_writes_the_slip_as_evidence_with_its_number_normalised_and_every_figure_as_read()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db);
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        var result = await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt(), NewSlip(), "photo-file-1", AppReceipts.SlipDisposition.Record,
            TestContext.Current.CancellationToken);

        result.DuplicateOfTransactionId.Should().BeNull();
        var receipt = await db.Receipts.AsNoTracking().SingleAsync(r => r.TransactionId == transaction.Id, TestContext.Current.CancellationToken);
        receipt.Id.Should().Be(result.ReceiptId!.Value);
        receipt.Kind.Should().Be(ReceiptKind.Exchange);
        receipt.Source.Should().Be(ReceiptSource.Vision);
        receipt.SlipNumber.Should().Be("PZ-2026-0917");
        receipt.SellerTaxId.Should().Be("123456789");
        receipt.SellerName.Should().Be("Menjačnica Zlatnik");
        receipt.Total.Should().Be(new Money(11700.00m, CurrencyCode.Rsd));
        receipt.IssuedAt.Should().Be(new DateTimeOffset(2026, 9, 25, 22, 25, 0, TimeSpan.Zero));
        receipt.TelegramFileId.Should().Be("photo-file-1");
        (await db.ReceiptLines.CountAsync(l => l.ReceiptId == receipt.Id, TestContext.Current.CancellationToken))
            .Should().Be(0, "a slip has no lines");

        var evidence = await db.ReceiptExchanges.AsNoTracking().SingleAsync(e => e.ReceiptId == receipt.Id, TestContext.Current.CancellationToken);
        evidence.GivenAmount.Should().Be(100.00m);
        evidence.GivenCurrency.Should().Be("EUR");
        evidence.ReceivedAmount.Should().Be(11700.00m);
        evidence.ReceivedCurrency.Should().Be("RSD");
        evidence.Rate.Should().Be(117.123456789012m, "numeric(24,12) keeps a rate's twelve decimals");
        evidence.CommissionAmount.Should().BeNull();
        evidence.CommissionCurrency.Should().BeNull();
        evidence.SlipNumber.Should().Be(" pz-2026-0917 ", "the evidence keeps the slip number exactly as vision read it");
    }

    [Fact]
    public async Task GetExchangeSlipAsync_reads_the_slip_back_and_is_null_for_a_fiscal_receipt()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var slipTransaction = await SeedSlipTransactionAsync(db);
        var fiscalTransaction = NewPhotoTransaction();
        db.Transactions.Add(fiscalTransaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        var slip = NewSlip();
        var saved = await store.SaveExchangeSlipAsync(
            slipTransaction.Id, NewSlipReceipt(), slip, "photo-file-1", AppReceipts.SlipDisposition.Hold, TestContext.Current.CancellationToken);
        await store.SaveExtractedAsync(fiscalTransaction.Id, NewExtractedReceipt(), "photo-file-2", true, TestContext.Current.CancellationToken);

        var view = await store.GetExchangeSlipAsync(slipTransaction.Id, TestContext.Current.CancellationToken);
        var fiscal = await store.GetExchangeSlipAsync(fiscalTransaction.Id, TestContext.Current.CancellationToken);

        view.Should().Be(new AppReceipts.ExchangeSlipView(
            saved.ReceiptId!.Value, "123456789", "Menjačnica Zlatnik", new DateTimeOffset(2026, 9, 25, 22, 25, 0, TimeSpan.Zero),
            "PZ-2026-0917", slip));
        fiscal.Should().BeNull();
    }

    [Fact]
    public async Task A_clean_slip_queues_RecordExchange_and_nothing_more_when_the_photo_had_no_caption()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db);
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt(), NewSlip(), "photo-file-1", AppReceipts.SlipDisposition.Record, TestContext.Current.CancellationToken);

        var jobs = await JobsOfAsync(db, transaction.Id);
        jobs.Select(j => j.Kind).Should().Equal(JobKind.RecordExchange);
        jobs[0].Status.Should().Be(JobStatus.Pending);
        jobs[0].CreatedAt.Should().Be(Now);
        jobs[0].SourceMessageId.Should().BeNull();
    }

    [Fact]
    public async Task A_clean_slip_queues_its_caption_as_a_correction_one_microsecond_after_RecordExchange()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db, caption: "  получил 11650 ");
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt(), NewSlip(), "photo-file-1", AppReceipts.SlipDisposition.Record, TestContext.Current.CancellationToken);

        var jobs = await JobsOfAsync(db, transaction.Id);
        jobs.Select(j => j.Kind).Should().Equal(JobKind.RecordExchange, JobKind.Correct);
        var caption = jobs[1];
        caption.Instruction.Should().Be("получил 11650");
        caption.InstructionDay.Should().Be(new DateOnly(2026, 9, 26), "the caption was written with the photo, on its local day in Belgrade");
        caption.SourceMessageId.Should().BeNull("a caption is not a reply");
        caption.RunAfter.Should().Be(Now);
        caption.CreatedAt.Should().Be(jobs[0].CreatedAt.AddTicks(10));
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken))
            .Status.Should().Be(TransactionStatus.Captured, "a clean slip is recorded by RecordExchange, not by saving it");
    }

    // Review focus 3: a slip photo with the caption "получил 11650" - RecordExchange runs first, then the
    // caption's correction replaces the received amount; never both at once, never the other way round.
    [Fact]
    public async Task The_caption_job_of_a_clean_slip_is_claimable_only_after_its_record_exchange_job()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db, caption: "получил 11650");
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt(), NewSlip(), "photo-file-1", AppReceipts.SlipDisposition.Record, TestContext.Current.CancellationToken);
        var queue = new EfJobQueue(db, new FakeTimeProvider(Now.AddMinutes(1)), maxAttempts: 8);
        var lease = TimeSpan.FromMinutes(5);

        (await queue.ClaimAsync("caption-worker", [JobKind.Correct], lease, TestContext.Current.CancellationToken))
            .Should().BeNull("the caption must wait for the RecordExchange job created before it");

        var recordJob = await queue.ClaimAsync("record-worker", [JobKind.RecordExchange], lease, TestContext.Current.CancellationToken);
        recordJob.Should().NotBeNull();
        (await queue.ClaimAsync("caption-worker", [JobKind.Correct], lease, TestContext.Current.CancellationToken))
            .Should().BeNull("a claimed RecordExchange still holds the caption back");

        await queue.SucceedAsync(recordJob!.Id, "record-worker", TestContext.Current.CancellationToken);
        var captionJob = await queue.ClaimAsync("caption-worker", [JobKind.Correct], lease, TestContext.Current.CancellationToken);

        captionJob.Should().NotBeNull();
        captionJob!.CreatedAt.Should().BeAfter(recordJob.CreatedAt);
    }

    [Fact]
    public async Task A_blank_caption_queues_no_correction()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db, caption: "   ");
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt(), NewSlip(), "photo-file-1", AppReceipts.SlipDisposition.Record, TestContext.Current.CancellationToken);

        (await JobsOfAsync(db, transaction.Id)).Select(j => j.Kind).Should().Equal(JobKind.RecordExchange);
    }

    [Fact]
    public async Task A_held_slip_queues_nothing_not_even_its_caption()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db, caption: "получил 11650");
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        var result = await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt(), NewSlip(received: 11650.00m), "photo-file-1", AppReceipts.SlipDisposition.Hold,
            TestContext.Current.CancellationToken);

        result.ReceiptId.Should().NotBeNull();
        (await JobsOfAsync(db, transaction.Id)).Should().BeEmpty("a held slip's caption waits for Record anyway");
        var stored = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken);
        stored.Status.Should().Be(TransactionStatus.Captured);
        stored.FailureReason.Should().Be(RecordFailureReason.None);
    }

    [Fact]
    public async Task An_incomplete_slip_fails_the_record_with_SlipIncomplete_and_queues_its_caption_at_once()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db, caption: "получил 11650");
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt(), NewSlip(received: null) with { Rate = null }, "photo-file-1",
            AppReceipts.SlipDisposition.Incomplete, TestContext.Current.CancellationToken);

        var stored = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken);
        stored.Status.Should().Be(TransactionStatus.Failed);
        stored.FailureReason.Should().Be(RecordFailureReason.SlipIncomplete);
        var jobs = await JobsOfAsync(db, transaction.Id);
        jobs.Select(j => j.Kind).Should().Equal(JobKind.Correct);
        jobs[0].Instruction.Should().Be("получил 11650", "the caption may carry the missing figure");
    }

    [Fact]
    public async Task A_second_slip_with_the_same_PIB_and_slip_number_writes_nothing_and_names_the_first_transaction()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var first = await SeedSlipTransactionAsync(db);
        var second = await SeedSlipTransactionAsync(db, caption: "получил 11650");
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        await store.SaveExchangeSlipAsync(
            first.Id, NewSlipReceipt(), NewSlip(slipNumber: "PZ-2026-0917"), "photo-file-1", AppReceipts.SlipDisposition.Record,
            TestContext.Current.CancellationToken);

        var secondResult = await store.SaveExchangeSlipAsync(
            second.Id, NewSlipReceipt(), NewSlip(slipNumber: " pz-2026-0917"), "photo-file-2", AppReceipts.SlipDisposition.Record,
            TestContext.Current.CancellationToken);

        secondResult.Should().Be(new AppReceipts.ReceiptSaveResult(null, first.Id));
        (await db.Receipts.CountAsync(r => r.TransactionId == second.Id, TestContext.Current.CancellationToken)).Should().Be(0);
        (await db.ReceiptExchanges.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await JobsOfAsync(db, second.Id)).Should().BeEmpty("the duplicate's RecordExchange and caption roll back with its receipt");
    }

    [Fact]
    public async Task A_replayed_slip_save_for_the_same_transaction_returns_its_own_receipt_not_a_duplicate()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db);
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        var first = await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt(), NewSlip(), "photo-file-1", AppReceipts.SlipDisposition.Record, TestContext.Current.CancellationToken);

        var replay = await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt(), NewSlip(), "photo-file-1", AppReceipts.SlipDisposition.Record, TestContext.Current.CancellationToken);

        replay.Should().Be(new AppReceipts.ReceiptSaveResult(first.ReceiptId, null));
        (await db.Receipts.CountAsync(r => r.TransactionId == transaction.Id, TestContext.Current.CancellationToken)).Should().Be(1);
        (await JobsOfAsync(db, transaction.Id)).Select(j => j.Kind).Should().Equal(JobKind.RecordExchange);
    }

    [Fact]
    public async Task Slips_with_no_slip_number_are_never_duplicates_of_each_other()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var first = await SeedSlipTransactionAsync(db);
        var second = await SeedSlipTransactionAsync(db);
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        var firstResult = await store.SaveExchangeSlipAsync(
            first.Id, NewSlipReceipt(), NewSlip(slipNumber: null), "photo-file-1", AppReceipts.SlipDisposition.Hold,
            TestContext.Current.CancellationToken);
        var secondResult = await store.SaveExchangeSlipAsync(
            second.Id, NewSlipReceipt(), NewSlip(slipNumber: "  "), "photo-file-2", AppReceipts.SlipDisposition.Hold,
            TestContext.Current.CancellationToken);

        firstResult.ReceiptId.Should().NotBeNull();
        secondResult.ReceiptId.Should().NotBeNull();
        secondResult.DuplicateOfTransactionId.Should().BeNull();
    }

    // Amendment 26: a slip changed money the evening before its photo was sent; the record is dated by the slip.
    [Theory]
    [InlineData(AppReceipts.SlipDisposition.Record)]
    [InlineData(AppReceipts.SlipDisposition.Hold)]
    [InlineData(AppReceipts.SlipDisposition.Incomplete)]
    public async Task A_slip_dated_the_day_before_its_photo_dates_the_record_by_the_slip(AppReceipts.SlipDisposition disposition)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db);
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt() with { IssuedAt = new DateTimeOffset(2026, 9, 25, 20, 15, 0, TimeSpan.FromHours(2)) },
            NewSlip(), "photo-file-1", disposition, TestContext.Current.CancellationToken);

        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken))
            .OccurredOn.Should().Be(new DateOnly(2026, 9, 25), "the photo was sent on the 26th, the exchange happened on the 25th");
    }

    [Fact]
    public async Task A_slip_with_no_readable_date_keeps_the_day_its_photo_was_sent()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db);
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt() with { IssuedAt = null }, NewSlip(), "photo-file-1", AppReceipts.SlipDisposition.Hold,
            TestContext.Current.CancellationToken);

        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken))
            .OccurredOn.Should().Be(new DateOnly(2026, 9, 26));
    }

    // Amendment 26 and spec §3: a held slip a reply completes keeps the slip's day - the correction that names no day
    // keeps the record's (CategorizationWorker.DefaultDay), and the record's day is now the slip's. PR 7c's worker test
    // covers the worker half; this pins that the day a reply finds is the slip's, through the subject it reads.
    [Fact]
    public async Task A_held_slips_subject_carries_the_slips_day_for_the_reply_that_completes_it()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db);
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt() with { IssuedAt = new DateTimeOffset(2026, 9, 25, 20, 15, 0, TimeSpan.FromHours(2)) },
            NewSlip(received: 11650.00m), "photo-file-1", AppReceipts.SlipDisposition.Hold, TestContext.Current.CancellationToken);

        var subject = await new EfCategorizationStore(db, new FakeTimeProvider(Now))
            .GetSubjectAsync(transaction.Id, TestContext.Current.CancellationToken);

        subject!.OccurredOn.Should().Be(new DateOnly(2026, 9, 25));
        subject.SentOn.Should().Be(new DateOnly(2026, 9, 26));
    }

    static async Task<(EfReceiptStore Store, Transaction Transaction)> SeedSlipAsync(
        LedgerDbContext db, AppReceipts.SlipDisposition disposition, string? caption = null)
    {
        var transaction = await SeedSlipTransactionAsync(db, caption);
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt(), NewSlip(received: 11650.00m), "photo-file-1", disposition, TestContext.Current.CancellationToken);
        return (store, transaction);
    }

    [Fact]
    public async Task EnqueueCategorizationAsync_on_a_held_slip_queues_RecordExchange_with_the_echo_message_id_then_the_caption()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (store, transaction) = await SeedSlipAsync(db, AppReceipts.SlipDisposition.Hold, caption: "получил 11650");

        var queued = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);

        queued.Should().BeTrue();
        var jobs = await JobsOfAsync(db, transaction.Id);
        jobs.Select(j => j.Kind).Should().Equal(JobKind.RecordExchange, JobKind.Correct);
        jobs[0].SourceMessageId.Should().Be(999);
        jobs[1].Instruction.Should().Be("получил 11650");
        jobs[1].CreatedAt.Should().Be(jobs[0].CreatedAt.AddTicks(10));
    }

    [Fact]
    public async Task EnqueueCategorizationAsync_on_a_held_slip_pressed_twice_is_a_no_op_the_second_time()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (store, transaction) = await SeedSlipAsync(db, AppReceipts.SlipDisposition.Hold, caption: "получил 11650");

        var first = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);
        var second = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);

        first.Should().BeTrue();
        second.Should().BeFalse();
        (await JobsOfAsync(db, transaction.Id)).Should().HaveCount(2, "one RecordExchange and one caption, not two of each");
    }

    static async Task<Guid> ReplyToAsync(LedgerDbContext db, Guid transactionId, string reply, int replyMessageId)
    {
        await new EfRecordEditor(db, new FakeTimeProvider(Now)).RequestCorrectionAsync(
            transactionId, reply, replyMessageId, Now, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        return (await JobsOfAsync(db, transactionId)).Single(j => j.SourceMessageId == replyMessageId).Id;
    }

    // Amendment A-8: the reply records a held slip. Pressed while the reply's correction still waits, Record anyway
    // would queue the caption behind it, and the caption would re-apply its own figures over the operator's reply.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Record_anyway_is_refused_while_a_reply_to_the_held_slip_is_still_being_applied(bool claimed)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (store, transaction) = await SeedSlipAsync(db, AppReceipts.SlipDisposition.Hold, caption: "получил 11650");
        var replyJobId = await ReplyToAsync(db, transaction.Id, "получил 11700", replyMessageId: 1001);
        if (claimed)
        {
            (await new EfJobQueue(db, new FakeTimeProvider(Now), maxAttempts: 8)
                .ClaimAsync("worker", [JobKind.Correct], TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken))
                .Should().NotBeNull();
        }

        var queued = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);

        queued.Should().BeFalse("the reply records the slip; the caption must not be queued behind it");
        (await JobsOfAsync(db, transaction.Id)).Select(j => j.Id).Should().Equal(replyJobId);
    }

    [Fact]
    public async Task Record_anyway_is_refused_while_a_voice_reply_to_the_held_slip_is_still_being_transcribed()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (store, transaction) = await SeedSlipAsync(db, AppReceipts.SlipDisposition.Hold, caption: "получил 11650");
        await new EfRecordEditor(db, new FakeTimeProvider(Now)).RequestVoiceCorrectionAsync(
            transaction.Id, "voice-file-1", 1001, Now, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var queued = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);

        queued.Should().BeFalse();
        (await JobsOfAsync(db, transaction.Id)).Select(j => j.Kind).Should().Equal(JobKind.Transcribe);
    }

    // A reply that finished without recording the slip (nothing to correct, or out of attempts) leaves it held.
    [Theory]
    [InlineData(JobStatus.Succeeded)]
    [InlineData(JobStatus.Failed)]
    public async Task A_reply_that_finished_without_recording_the_held_slip_no_longer_holds_back_Record_anyway(JobStatus finished)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (store, transaction) = await SeedSlipAsync(db, AppReceipts.SlipDisposition.Hold, caption: "получил 11650");
        var replyJobId = await ReplyToAsync(db, transaction.Id, "спасибо", replyMessageId: 1001);
        await db.CategorizationJobs
            .Where(j => j.Id == replyJobId)
            .ExecuteUpdateAsync(set => set.SetProperty(j => j.Status, finished), TestContext.Current.CancellationToken);

        var queued = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);

        queued.Should().BeTrue();
        (await JobsOfAsync(db, transaction.Id)).Where(j => j.Id != replyJobId).Select(j => j.Kind)
            .Should().Equal(JobKind.RecordExchange, JobKind.Correct);
    }

    // An edited caption is not a reply: the caption Record anyway queues already carries the edited text.
    [Fact]
    public async Task An_edited_caption_still_being_reread_does_not_hold_back_Record_anyway()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (store, transaction) = await SeedSlipAsync(db, AppReceipts.SlipDisposition.Hold, caption: "получил 11650");
        (await new EfRecordEditor(db, new FakeTimeProvider(Now)).ReplaceRawTextAsync(
            transaction.Id, "получил 11700", TestContext.Current.CancellationToken)).Should().BeTrue();
        db.ChangeTracker.Clear();

        var queued = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);

        queued.Should().BeTrue();
        var jobs = await JobsOfAsync(db, transaction.Id);
        jobs.Select(j => j.Kind).Should().BeEquivalentTo([JobKind.Reinterpret, JobKind.RecordExchange, JobKind.Correct]);
        jobs.Single(j => j.Kind == JobKind.Correct).Instruction.Should().Be("получил 11700");
    }

    // ExtractReceiptWorker saves the slip while it still holds the capture's ExtractReceipt claim.
    static async Task<Guid> SeedClaimedExtractionAsync(LedgerDbContext db, Guid transactionId)
    {
        var extraction = new CategorizationJob
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Kind = JobKind.ExtractReceipt,
            Status = JobStatus.Claimed,
            AttemptCount = 1,
            ClaimedAt = Now,
            ClaimedBy = "extract-worker",
            RunAfter = Now.AddMinutes(5),
            CreatedAt = SlipSentAt,
            UpdatedAt = Now,
        };
        db.CategorizationJobs.Add(extraction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        return extraction.Id;
    }

    [Fact]
    public async Task A_slip_saved_under_its_own_extraction_claim_still_queues_its_caption()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db, caption: "получил 11650");
        var extractionJobId = await SeedClaimedExtractionAsync(db, transaction.Id);
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));

        await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt(), NewSlip(), "photo-file-1", AppReceipts.SlipDisposition.Record, TestContext.Current.CancellationToken);

        (await JobsOfAsync(db, transaction.Id)).Where(j => j.Id != extractionJobId).Select(j => j.Kind)
            .Should().Equal([JobKind.RecordExchange, JobKind.Correct], "the capture's own job is not a reply");
    }

    // A-22: a reply sent while the photo was still being read is the operator's later word. Queued behind it, the
    // caption would re-apply the older figures over the reply's, so the save queues no caption; RecordExchange is
    // still queued, after the reply.
    [Theory]
    [InlineData(AppReceipts.SlipDisposition.Record, new[] { JobKind.RecordExchange })]
    [InlineData(AppReceipts.SlipDisposition.Incomplete, new JobKind[0])]
    public async Task A_reply_sent_while_the_slip_was_being_read_wins_over_the_photos_caption(
        AppReceipts.SlipDisposition disposition, JobKind[] queuedBySave)
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db, caption: "получил 11650");
        var extractionJobId = await SeedClaimedExtractionAsync(db, transaction.Id);
        var replyJobId = await ReplyToAsync(db, transaction.Id, "получил 11700", replyMessageId: 1001);
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now.AddMinutes(1)));
        var slip = disposition == AppReceipts.SlipDisposition.Record ? NewSlip() : NewSlip(received: null) with { Rate = null };

        await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt(), slip, "photo-file-1", disposition, TestContext.Current.CancellationToken);

        (await JobsOfAsync(db, transaction.Id)).Where(j => j.Id != replyJobId && j.Id != extractionJobId).Select(j => j.Kind)
            .Should().Equal(queuedBySave, "the reply, not the caption, is the operator's last word");
    }

    [Fact]
    public async Task EnqueueCategorizationAsync_never_queues_anything_for_an_incomplete_slip()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (store, transaction) = await SeedSlipAsync(db, AppReceipts.SlipDisposition.Incomplete);

        var queued = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);

        queued.Should().BeFalse("confirmation cannot supply an amount; only a reply can");
        (await JobsOfAsync(db, transaction.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task IsAwaitingConfirmationAsync_is_true_for_a_held_slip()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (store, transaction) = await SeedSlipAsync(db, AppReceipts.SlipDisposition.Hold);

        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    // Review focus 2: Cancel, then Restore, must bring back the "Record anyway" prompt for a held slip.
    [Fact]
    public async Task A_cancelled_held_slip_is_still_awaiting_confirmation()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (store, transaction) = await SeedSlipAsync(db, AppReceipts.SlipDisposition.Hold);

        await new EfRecordEditor(db, new FakeTimeProvider(Now)).CancelAsync(transaction.Id, TestContext.Current.CancellationToken);

        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken))
            .Should().BeTrue("a Cancel is not an apply: Restore must return to the slip's confirmation prompt");
    }

    // Review focus 2: a slip a reply completed (amendment 9) and then cancelled offers Restore only.
    [Fact]
    public async Task A_slip_a_reply_completed_is_never_awaiting_even_when_cancelled()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (store, transaction) = await SeedSlipAsync(db, AppReceipts.SlipDisposition.Hold);
        var completed = await db.Transactions.SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken);
        completed.Status = TransactionStatus.Completed;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RevisionLog.AppendAsync(
            db, completed, RevisionKind.Correction, "получил 11650", TransactionStatus.Captured, Now, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeFalse();

        await new EfRecordEditor(db, new FakeTimeProvider(Now)).CancelAsync(transaction.Id, TestContext.Current.CancellationToken);

        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken))
            .Should().BeFalse("a reply already recorded it; Restore must never offer Record anyway again");
    }

    // Review focus 2: an incomplete slip is Failed with SlipIncomplete - only a reply completes it.
    [Fact]
    public async Task An_incomplete_slip_is_never_awaiting()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (store, transaction) = await SeedSlipAsync(db, AppReceipts.SlipDisposition.Incomplete);

        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeFalse();

        await new EfRecordEditor(db, new FakeTimeProvider(Now)).CancelAsync(transaction.Id, TestContext.Current.CancellationToken);

        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    // A photo cancelled while it was being read stays Cancelled when its incomplete slip is saved, but keeps the
    // outcome: Restore brings it back Failed with SlipIncomplete, so the echo asks for the missing figure. Restored
    // Captured instead, it had no job and no buttons - "Reading the receipt…" forever.
    [Fact]
    public async Task An_incomplete_slip_saved_after_a_Cancel_is_restored_incomplete_and_never_recorded_anyway()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db);
        var editor = new EfRecordEditor(db, new FakeTimeProvider(Now));
        var store = new EfReceiptStore(db, new FakeTimeProvider(Now));
        await editor.CancelAsync(transaction.Id, TestContext.Current.CancellationToken);
        await store.SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt(), NewSlip(received: null) with { Rate = null }, "photo-file-1",
            AppReceipts.SlipDisposition.Incomplete, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var whileCancelled = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken);
        whileCancelled.Status.Should().Be(TransactionStatus.Cancelled, "the operator's Cancel stands");
        whileCancelled.FailureReason.Should().Be(RecordFailureReason.SlipIncomplete);

        await editor.RestoreAsync(transaction.Id, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var queued = await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);

        var stored = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken);
        stored.Status.Should().Be(TransactionStatus.Failed, "nothing applied it while cancelled, so it is still incomplete");
        stored.FailureReason.Should().Be(RecordFailureReason.SlipIncomplete);
        queued.Should().BeFalse("the slip is missing its received amount; only a reply can complete it");
        (await JobsOfAsync(db, transaction.Id)).Should().BeEmpty();
        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    // Amendment 22 still wins: a reply that completed the incomplete slip while it was cancelled restores Completed.
    [Fact]
    public async Task An_incomplete_slip_saved_after_a_Cancel_and_completed_by_a_reply_while_cancelled_restores_Completed()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var transaction = await SeedSlipTransactionAsync(db);
        var clock = new FakeTimeProvider(Now);
        var editor = new EfRecordEditor(db, clock);
        var cashEur = await AddWalletAsync(db, "Cash EUR", CurrencyCode.Eur);
        var cashRsd = await AddWalletAsync(db, "Cash RSD", CurrencyCode.Rsd);
        await editor.CancelAsync(transaction.Id, TestContext.Current.CancellationToken);
        await new EfReceiptStore(db, clock).SaveExchangeSlipAsync(
            transaction.Id, NewSlipReceipt(), NewSlip(received: null) with { Rate = null }, "photo-file-1",
            AppReceipts.SlipDisposition.Incomplete, TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        await new EfCategorizationStore(db, clock).ApplyAsync(transaction.Id, new CategorizationOutcome(
            [], new DateOnly(2026, 9, 26), Kind: JobKind.Correct, Instruction: "получил 11700",
            TransactionKind: TransactionKind.Transfer,
            Transfer: new TransferFacts(
                cashEur, new Money(100.00m, CurrencyCode.Eur), cashRsd, new Money(11700.00m, CurrencyCode.Rsd), null, null, null)),
            TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        await editor.RestoreAsync(transaction.Id, TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var stored = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken);
        stored.Status.Should().Be(TransactionStatus.Completed);
        stored.FailureReason.Should().Be(RecordFailureReason.None);
    }

    static async Task<Guid> AddWalletAsync(LedgerDbContext db, string name, CurrencyCode currency)
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), Name = name, Currency = currency, CreatedAt = Now };
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        return wallet.Id;
    }

    [Fact]
    public async Task A_clean_slip_is_never_awaiting_confirmation()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (store, transaction) = await SeedSlipAsync(db, AppReceipts.SlipDisposition.Record);

        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task A_held_slip_is_no_longer_awaiting_once_Record_anyway_has_queued_RecordExchange()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var (store, transaction) = await SeedSlipAsync(db, AppReceipts.SlipDisposition.Hold);
        await store.EnqueueCategorizationAsync(transaction.Id, 999, TestContext.Current.CancellationToken);

        (await store.IsAwaitingConfirmationAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeFalse();
    }
}
