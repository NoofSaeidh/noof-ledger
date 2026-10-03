using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Editing;
using Noof.Ledger.Persistence.Receipts;
using AppReceipts = Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Persistence.Tests;

// Records as the capture pipeline leaves them, for the checks that read a record's clock (P-4): every created_at, job
// updated_at and revision created_at is the instant the test names, never the real clock. Each helper saves and clears
// the change tracker, so whatever the test calls next reads the rows back as they are stored.
internal static class PipelineSeed
{
    public static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly Day = new(2026, 9, 27);

    // Apart from IntegritySeed's 70 000 range: EfIntegrityChecksTests uses both seeds.
    static int nextMessageId = 170_000;
    static int nextSlipNumber;

    static readonly AppReceipts.ExtractedReceipt SaleReceipt = new(
        ReceiptSource.Vision, VerificationUrl: null, SellerTaxId: "100000001", SellerName: "Synthetic Market",
        SellerAddress: null, LocationName: null, FiscalNumber: null, IssuedAt: null, Total: 1250.00m,
        Currency: CurrencyCode.Rsd, Kind: ReceiptKind.Sale, PaymentMethod: null, QrTotal: null, Lines: []);

    static readonly AppReceipts.ExtractedReceipt SlipReceipt = SaleReceipt with
    {
        SellerTaxId = "123456789",
        SellerName = "Synthetic Exchange",
        Total = 11650.00m,
        Kind = ReceiptKind.Exchange,
    };

    // 100 EUR at the printed rate is 11712.35 RSD, not the 11650 received: the amounts disagree, so the slip holds.
    static readonly AppReceipts.ExtractedExchange HeldSlip = new(
        GivenAmount: 100.00m, GivenCurrency: "EUR", ReceivedAmount: 11650.00m, ReceivedCurrency: "RSD",
        Rate: 117.123456789012m, CommissionAmount: null, CommissionCurrency: null, SlipNumber: "SYN-0");

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Guid> WalletAsync(LedgerDbContext db, string name, CurrencyCode currency)
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), Name = name, Currency = currency, CreatedAt = Now.AddDays(-30) };
        db.Wallets.Add(wallet);
        await SaveAsync(db);
        return wallet.Id;
    }

    public static Task<Guid> TextRecordAsync(
        LedgerDbContext db, DateTimeOffset createdAt, TransactionStatus status = TransactionStatus.Captured,
        RecordFailureReason reason = RecordFailureReason.None, TransactionKind kind = TransactionKind.Expense,
        Guid? walletId = null, string rawText = "coffee 250") =>
        AddAsync(db, new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            Kind = kind,
            RawText = rawText,
            CaptureKind = CaptureKind.Text,
            Status = status,
            FailureReason = reason,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = createdAt,
            OccurredOn = Day,
            TelegramChatId = 1,
            TelegramMessageId = Interlocked.Increment(ref nextMessageId),
            CreatedAt = createdAt,
        });

    public static async Task<Guid> JobAsync(
        LedgerDbContext db, Guid transactionId, JobKind kind, JobStatus status, DateTimeOffset createdAt,
        DateTimeOffset? updatedAt = null, string? lastError = null)
    {
        var job = new CategorizationJob
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Kind = kind,
            Instruction = kind == JobKind.Correct ? "it was 250" : null,
            VoiceFileId = kind == JobKind.Transcribe ? "voice-file-1" : null,
            Status = status,
            AttemptCount = status == JobStatus.Pending ? 0 : 1,
            RunAfter = createdAt,
            ClaimedAt = status == JobStatus.Claimed ? createdAt : null,
            ClaimedBy = status == JobStatus.Claimed ? "worker-1" : null,
            LastError = lastError,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt ?? createdAt,
        };
        db.CategorizationJobs.Add(job);
        await SaveAsync(db);
        return job.Id;
    }

    // A receipt saved without its CategorizeReceipt job, its ExtractReceipt job done a minute after capture: a Vision
    // one is held for Record anyway; a fiscal one is a hole in the write path.
    public static async Task<Guid> ReceiptWithoutCategorizationAsync(
        LedgerDbContext db, DateTimeOffset createdAt, ReceiptSource source)
    {
        var id = await PhotoRecordAsync(db, createdAt);
        await JobAsync(db, id, JobKind.ExtractReceipt, JobStatus.Succeeded, createdAt, createdAt.AddMinutes(1));
        await new EfReceiptStore(db, new FakeTimeProvider(createdAt)).SaveExtractedAsync(
            id, SaleReceipt with { Source = source }, "photo-file-1", enqueueCategorization: false, Ct);
        db.ChangeTracker.Clear();
        return id;
    }

    // An exchange slip as SaveExchangeSlipAsync leaves it: Hold queues nothing, Record queues RecordExchange,
    // Incomplete fails the record with SlipIncomplete. No caption, so no Correct job. With cancelledBeforeExtractionAt
    // the photo is cancelled while it is read: the record stays Cancelled, and an incomplete slip still stores its reason.
    public static async Task<Guid> SlipAsync(
        LedgerDbContext db, DateTimeOffset createdAt, AppReceipts.SlipDisposition disposition,
        DateTimeOffset? cancelledBeforeExtractionAt = null)
    {
        var id = await PhotoRecordAsync(db, createdAt);
        if (cancelledBeforeExtractionAt is { } cancelledAt)
            await CancelAsync(db, id, cancelledAt);
        await JobAsync(db, id, JobKind.ExtractReceipt, JobStatus.Succeeded, createdAt, createdAt.AddMinutes(1));
        var numbered = HeldSlip with { SlipNumber = $"SYN-{Interlocked.Increment(ref nextSlipNumber)}" };
        var slip = disposition == AppReceipts.SlipDisposition.Incomplete
            ? numbered with { ReceivedAmount = null, Rate = null }
            : numbered;
        await new EfReceiptStore(db, new FakeTimeProvider(createdAt)).SaveExchangeSlipAsync(
            id, SlipReceipt, slip, "photo-file-1", disposition, Ct);
        db.ChangeTracker.Clear();
        return id;
    }

    // What the RecordExchange worker leaves behind, as far as the waiting checks read it: the job's outcome (its
    // updated_at left alone, so the record's clock does not move) and, for an applied slip, the record completed.
    public static async Task SettleRecordExchangeAsync(
        LedgerDbContext db, Guid transactionId, JobStatus outcome, TransactionStatus? recordStatus = null)
    {
        string? lastError = outcome == JobStatus.Failed ? "The exchange could not be recorded." : null;
        await db.CategorizationJobs
            .Where(job => job.TransactionId == transactionId && job.Kind == JobKind.RecordExchange)
            .ExecuteUpdateAsync(set => set
                .SetProperty(job => job.Status, outcome)
                .SetProperty(job => job.LastError, lastError), Ct);
        if (recordStatus is { } status)
            await db.Transactions
                .Where(t => t.Id == transactionId)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.Status, status), Ct);
    }

    public static async Task CancelAsync(LedgerDbContext db, Guid transactionId, DateTimeOffset at)
    {
        await new EfRecordEditor(db, new FakeTimeProvider(at)).CancelAsync(transactionId, Ct);
        db.ChangeTracker.Clear();
    }

    public static async Task RestoreAsync(LedgerDbContext db, Guid transactionId, DateTimeOffset at)
    {
        await new EfRecordEditor(db, new FakeTimeProvider(at)).RestoreAsync(transactionId, Ct);
        db.ChangeTracker.Clear();
    }

    public static async Task LineAsync(
        LedgerDbContext db, Guid transactionId, Money amount, int ordinal, EntryRole role = EntryRole.Principal)
    {
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Description = role == EntryRole.Fee ? "Fee" : "line",
            Amount = amount,
            CategorizedBy = role == EntryRole.Fee ? CategorizationAuthority.Rule : CategorizationAuthority.Model,
            Ordinal = ordinal,
            Role = role,
        });
        await SaveAsync(db);
    }

    public static async Task<Guid> EntryAsync(LedgerDbContext db, Guid transactionId, Guid walletId, Money amount)
    {
        var entry = new Entry
        {
            Id = Guid.NewGuid(), TransactionId = transactionId, WalletId = walletId, Amount = amount, Role = EntryRole.Principal,
        };
        db.Entries.Add(entry);
        await SaveAsync(db);
        return entry.Id;
    }

    public static async Task TransferAsync(
        LedgerDbContext db, Guid transactionId, Guid fromWalletId, Money from, Guid toWalletId, Money to)
    {
        db.Transfers.Add(new Transfer
        {
            TransactionId = transactionId, FromWalletId = fromWalletId, From = from, ToWalletId = toWalletId, To = to,
        });
        await SaveAsync(db);
    }

    static Task<Guid> PhotoRecordAsync(LedgerDbContext db, DateTimeOffset createdAt) =>
        AddAsync(db, new Transaction
        {
            Id = Guid.NewGuid(),
            RawText = null,
            CaptureKind = CaptureKind.Photo,
            TelegramFileId = "photo-file-1",
            Status = TransactionStatus.Captured,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = createdAt,
            OccurredOn = Day,
            TelegramChatId = 1,
            TelegramMessageId = Interlocked.Increment(ref nextMessageId),
            CreatedAt = createdAt,
        });

    static async Task<Guid> AddAsync(LedgerDbContext db, Transaction record)
    {
        db.Transactions.Add(record);
        await SaveAsync(db);
        return record.Id;
    }

    static async Task SaveAsync(LedgerDbContext db)
    {
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();
    }
}
