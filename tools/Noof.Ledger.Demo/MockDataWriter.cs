using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Backup;
using Noof.Ledger.Persistence.Diagnostics;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Demo;

internal static class MockDataWriter
{
    public static async Task WriteAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<LedgerDbContext>();
        var wallets = await WriteWalletsAsync(services.GetRequiredService<IWalletAdmin>(), db, cancellationToken);
        var categories = await db.Categories.ToDictionaryAsync(category => category.Slug, category => category.Id, cancellationToken);
        var merchants = await WriteMerchantsAsync(db, cancellationToken);

        for (var index = 0; index < MockData.Records.Count; index++)
            await WriteRecordAsync(db, MockData.Records[index], index + 1, wallets, categories, merchants, cancellationToken);

        await WriteTraceAsync(db, cancellationToken);
        await WriteReceiptTracesAsync(db, cancellationToken);
        await WriteLogRowsAsync(db, cancellationToken);
        await services.GetRequiredService<ILogRetentionSettings>().SaveAsync(MockData.LogRetention, cancellationToken);
        await WriteUserAsync(services, cancellationToken);
        await WriteSecretsAsync(services.GetRequiredService<ISecretStore>(), cancellationToken);
        await WriteBackupRunAsync(db, cancellationToken);
    }

    static async Task<Dictionary<string, Guid>> WriteWalletsAsync(IWalletAdmin admin, LedgerDbContext db, CancellationToken cancellationToken)
    {
        var ids = new Dictionary<string, Guid>();
        foreach (var wallet in MockData.Wallets)
        {
            var id = await admin.CreateAsync(
                new NewWallet(wallet.Name, wallet.Currency, wallet.Opening, MockData.OpeningDate, wallet.Aliases, wallet.Default),
                cancellationToken);

            // Every opening lands at the same start of day and the lists order ties arbitrarily; a minute
            // apart, they come out in the same order on every run, and so do the screenshots.
            var openedAt = ZonedClock.StartOfDay(MockData.OpeningDate, MockData.TimeZoneId).AddMinutes(ids.Count);
            await db.Transactions
                .Where(transaction => transaction.WalletId == id && transaction.Kind == TransactionKind.BalanceCheck)
                .ExecuteUpdateAsync(set => set.SetProperty(transaction => transaction.OccurredAt, openedAt), cancellationToken);

            if (wallet.PaymentDefault is { } payment)
                await admin.SetPaymentDefaultAsync(id, payment, cancellationToken);

            if (wallet.Archived)
                await admin.ArchiveAsync(id, cancellationToken);

            ids[wallet.Name] = id;
        }

        return ids;
    }

    static async Task<Dictionary<string, Guid>> WriteMerchantsAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        var names = MockData.Records.SelectMany(record => record.Lines)
            .Select(line => line.Merchant).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();

        var ids = names.Select((name, index) => (name, id: MockData.Id(800 + index))).ToDictionary(pair => pair.name, pair => pair.id);
        foreach (var (name, id) in ids)
        {
            db.Merchants.Add(new Merchant { Id = id, DisplayName = name, Kind = MerchantKind.Retail, TaxId = RecordedTaxIdOf(name) });
            db.MerchantAliases.Add(new MerchantAlias { Folded = MerchantName.Fold(name), MerchantId = id, CreatedAt = MockData.Now });
        }

        await db.SaveChangesAsync(cancellationToken);
        return ids;
    }

    // The app learns a merchant's tax id from the first receipt categorised under it.
    static string? RecordedTaxIdOf(string merchant) =>
        MockData.Records
            .Where(record => record is { Status: TransactionStatus.Completed, Receipt: not null })
            .Select(record => record.Receipt!)
            .FirstOrDefault(receipt => receipt.SellerName == merchant)?.SellerTaxId;

    static async Task WriteRecordAsync(
        LedgerDbContext db, MockRecord record, int messageId, Dictionary<string, Guid> wallets,
        Dictionary<string, Guid> categories, Dictionary<string, Guid> merchants, CancellationToken cancellationToken)
    {
        Guid? walletId = record.Wallet is { } wallet ? wallets[wallet] : null;
        var currency = record.Receipt is null ? MockData.Wallets.Single(candidate => candidate.Name == record.Wallet).Currency : CurrencyCode.Rsd;
        var occurredAt = ZonedClock.StartOfDay(record.Day, MockData.TimeZoneId).AddHours(12).AddMinutes(messageId);
        var transaction = new Transaction
        {
            Id = record.Id ?? MockData.Id(messageId),
            WalletId = walletId,
            Kind = record.Kind,
            RawText = record.RawText,
            CaptureKind = record.Receipt is null ? CaptureKind.Text : CaptureKind.Photo,
            TelegramFileId = record.Receipt is null ? null : $"demo-photo-{messageId}",
            Status = record.Status,
            TimeZoneId = MockData.TimeZoneId,
            OccurredAt = occurredAt,
            OccurredOn = record.Day,
            TelegramChatId = MockData.TelegramChatId,
            TelegramMessageId = messageId,
            BotMessageId = 10_000 + messageId,
            CreatedAt = occurredAt,
        };
        db.Transactions.Add(transaction);
        var receiptLineIds = record.Receipt is { } receipt ? AddReceipt(db, transaction, receipt, messageId) : [];

        if (record is { Kind: TransactionKind.BalanceCheck, Stated: { } stated, ComputedBefore: { } computedBefore })
        {
            db.BalanceChecks.Add(new BalanceCheck
            {
                TransactionId = transaction.Id,
                WalletId = wallets[record.Wallet!],
                Stated = new Money(stated, currency),
                ComputedBefore = computedBefore,
            });
        }
        else if (record.Status == TransactionStatus.Completed)
        {
            var total = record.Lines.Sum(line => line.Amount);
            db.Entries.Add(new Entry
            {
                Id = MockData.Id(1000 + messageId),
                TransactionId = transaction.Id,
                WalletId = wallets[record.Wallet!],
                Amount = new Money(record.Kind == TransactionKind.Income ? total : -total, currency),
                Role = EntryRole.Principal,
            });

            for (var line = 0; line < record.Lines.Count; line++)
            {
                db.LineItems.Add(new LineItem
                {
                    Id = MockData.Id(2000 + messageId * 10 + line),
                    TransactionId = transaction.Id,
                    Description = record.Lines[line].Description,
                    Amount = new Money(record.Lines[line].Amount, currency),
                    CategoryId = categories[record.Lines[line].CategorySlug],
                    CategorizedBy = CategorizationAuthority.Model,
                    MerchantId = record.Lines[line].Merchant is { } merchant ? merchants[merchant] : null,
                    Ordinal = line + 1,
                    ReceiptLineId = receiptLineIds.Count > 0 ? receiptLineIds[line] : null,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        if (record.Status == TransactionStatus.Completed)
        {
            await RevisionLog.AppendAsync(
                db, transaction, RevisionKind.Initial, null, TransactionStatus.Captured, occurredAt.AddSeconds(2), cancellationToken);
        }

        if (transaction.Id == MockData.TracedTransactionId)
        {
            await RevisionLog.AppendAsync(
                db, transaction, RevisionKind.Correction, "wine was 12.50, not 15", TransactionStatus.Completed,
                occurredAt.AddMinutes(2), cancellationToken);
        }
    }

    static List<Guid> AddReceipt(LedgerDbContext db, Transaction transaction, MockReceipt receipt, int messageId)
    {
        var receiptId = MockData.Id(4000 + messageId);
        db.Receipts.Add(new Receipt
        {
            Id = receiptId,
            TransactionId = transaction.Id,
            Source = receipt.Source,
            SellerTaxId = receipt.SellerTaxId,
            SellerName = receipt.SellerName,
            SellerAddress = receipt.SellerAddress,
            LocationName = receipt.LocationName,
            FiscalNumber = receipt.FiscalNumber,
            IssuedAt = receipt.IssuedAt,
            Total = new Money(receipt.Total, CurrencyCode.Rsd),
            Kind = ReceiptKind.Sale,
            PaymentMethod = receipt.Payment,
            QrTotal = receipt.QrTotal,
            TelegramFileId = transaction.TelegramFileId,
            CreatedAt = transaction.OccurredAt.AddSeconds(3),
        });

        var lineIds = receipt.Lines.Select((_, index) => MockData.Id(3000 + messageId * 10 + index)).ToList();
        db.ReceiptLines.AddRange(receipt.Lines.Select((line, index) => new ReceiptLine
        {
            Id = lineIds[index],
            ReceiptId = receiptId,
            Ordinal = index + 1,
            Name = line.Name,
            Quantity = line.Quantity,
            Unit = line.Unit,
            UnitPrice = line.UnitPrice,
            Total = line.Total,
        }));

        // A receipt read from the photo counts as confirmed only once its CategorizeReceipt job exists.
        AddSucceededJob(db, transaction, JobKind.ExtractReceipt, MockData.Id(5000 + messageId * 10));
        if (transaction.Status == TransactionStatus.Completed)
            AddSucceededJob(db, transaction, JobKind.CategorizeReceipt, MockData.Id(5000 + messageId * 10 + 1));

        return lineIds;
    }

    static void AddSucceededJob(LedgerDbContext db, Transaction transaction, JobKind kind, Guid id) =>
        db.CategorizationJobs.Add(new CategorizationJob
        {
            Id = id,
            TransactionId = transaction.Id,
            Kind = kind,
            Status = JobStatus.Succeeded,
            AttemptCount = 1,
            RunAfter = transaction.OccurredAt,
            CreatedAt = transaction.OccurredAt,
            UpdatedAt = transaction.OccurredAt.AddSeconds(5),
        });

    static async Task WriteTraceAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        var traced = await db.Transactions.SingleAsync(transaction => transaction.Id == MockData.TracedTransactionId, cancellationToken);
        var failed = await db.Transactions.SingleAsync(transaction => transaction.Id == MockData.FailedTransactionId, cancellationToken);

        db.AppLogs.AddRange(
            Stage(traced.Id, traced.OccurredAt, "Noof.Ledger.Telegram.TelegramUpdateRouter", TransactionStages.Received, TransactionStages.ReceivedEventId),
            Stage(traced.Id, traced.OccurredAt.AddMilliseconds(1450), "Noof.Ledger.Host.Workers.CategorizationWorker", TransactionStages.Categorized, TransactionStages.CategorizedEventId),
            Stage(traced.Id, traced.OccurredAt.AddMilliseconds(1520), "Noof.Ledger.Host.Workers.CategorizationWorker", TransactionStages.Persisted, TransactionStages.PersistedEventId),
            Stage(traced.Id, traced.OccurredAt.AddMilliseconds(1690), "Noof.Ledger.Telegram.TelegramChatNotifier", TransactionStages.Replied, TransactionStages.RepliedEventId),
            Stage(failed.Id, failed.OccurredAt, "Noof.Ledger.Telegram.TelegramUpdateRouter", TransactionStages.Received, TransactionStages.ReceivedEventId),
            StageFailed(failed.Id, failed.OccurredAt.AddMilliseconds(2300), TransactionStages.Categorized));

        await db.SaveChangesAsync(cancellationToken);
    }

    static async Task WriteReceiptTracesAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        const string Router = "Noof.Ledger.Telegram.TelegramUpdateRouter";
        const string Extractor = "Noof.Ledger.Host.Workers.ExtractReceiptWorker";
        const string Categorizer = "Noof.Ledger.Host.Workers.CategorizationWorker";
        const string Notifier = "Noof.Ledger.Telegram.TelegramChatNotifier";

        var qr = await db.Transactions.SingleAsync(transaction => transaction.Id == MockData.ReceiptTransactionId, cancellationToken);
        var vision = await db.Transactions.SingleAsync(transaction => transaction.Id == MockData.VisionReceiptTransactionId, cancellationToken);
        var unconfirmed = await db.Transactions.SingleAsync(transaction => transaction.Id == MockData.UnconfirmedReceiptTransactionId, cancellationToken);

        db.AppLogs.AddRange(
            Stage(qr.Id, qr.OccurredAt, Router, TransactionStages.Received, TransactionStages.ReceivedEventId),
            Stage(qr.Id, qr.OccurredAt.AddMilliseconds(2140), Extractor, TransactionStages.Extracted, TransactionStages.ExtractedEventId),
            Stage(qr.Id, qr.OccurredAt.AddMilliseconds(4310), Categorizer, TransactionStages.Categorized, TransactionStages.CategorizedEventId),
            Stage(qr.Id, qr.OccurredAt.AddMilliseconds(4380), Categorizer, TransactionStages.Persisted, TransactionStages.PersistedEventId),
            Stage(qr.Id, qr.OccurredAt.AddMilliseconds(4560), Notifier, TransactionStages.Replied, TransactionStages.RepliedEventId),
            Stage(vision.Id, vision.OccurredAt, Router, TransactionStages.Received, TransactionStages.ReceivedEventId),
            ReceiptFetchFailed(vision.Id, vision.OccurredAt.AddMilliseconds(10200)),
            Stage(vision.Id, vision.OccurredAt.AddMilliseconds(17850), Extractor, TransactionStages.Extracted, TransactionStages.ExtractedEventId),
            Stage(vision.Id, vision.OccurredAt.AddMilliseconds(20120), Categorizer, TransactionStages.Categorized, TransactionStages.CategorizedEventId),
            Stage(vision.Id, vision.OccurredAt.AddMilliseconds(20190), Categorizer, TransactionStages.Persisted, TransactionStages.PersistedEventId),
            Stage(vision.Id, vision.OccurredAt.AddMilliseconds(20400), Notifier, TransactionStages.Replied, TransactionStages.RepliedEventId),
            Stage(unconfirmed.Id, unconfirmed.OccurredAt, Router, TransactionStages.Received, TransactionStages.ReceivedEventId),
            Stage(unconfirmed.Id, unconfirmed.OccurredAt.AddMilliseconds(6480), Extractor, TransactionStages.Extracted, TransactionStages.ExtractedEventId));

        await db.SaveChangesAsync(cancellationToken);
    }

    static async Task WriteLogRowsAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        db.AppLogs.AddRange(MockData.LogRows.Select(row => Row(row.At, row.Level, row.Source, row.Message, row.Exception)));
        await db.SaveChangesAsync(cancellationToken);
    }

    static AppLogEntry Stage(Guid transactionId, DateTimeOffset at, string source, string stage, int eventId) => new()
    {
        Id = 0,
        LoggedAt = at,
        Level = LogSeverity.Information,
        Source = source,
        Message = stage,
        Template = "{Stage}",
        TransactionId = transactionId,
        PropertiesJson = $$"""{"Stage":"{{stage}}","EventId":{"Id":{{eventId}},"Name":"{{stage}}"},"TransactionId":"{{transactionId}}"}""",
    };

    static AppLogEntry StageFailed(Guid transactionId, DateTimeOffset at, string failedStage) => new()
    {
        Id = 0,
        LoggedAt = at,
        Level = LogSeverity.Error,
        Source = "Noof.Ledger.Host.Workers.CategorizationWorker",
        Message = $"{TransactionStages.StageFailed} at stage {failedStage}",
        Template = "{Stage} at stage {FailedStage}",
        Exception = "Noof.Ledger.Application.Categorization.ModelCallException: the message could not be read as an amount",
        TransactionId = transactionId,
        PropertiesJson = $$"""{"Stage":"{{TransactionStages.StageFailed}}","FailedStage":"{{failedStage}}","EventId":{"Id":{{TransactionStages.StageFailedEventId}},"Name":"{{TransactionStages.StageFailed}}"},"TransactionId":"{{transactionId}}"}""",
    };

    static AppLogEntry ReceiptFetchFailed(Guid transactionId, DateTimeOffset at) => new()
    {
        Id = 0,
        LoggedAt = at,
        Level = LogSeverity.Warning,
        Source = "Noof.Ledger.Host.Workers.ExtractReceiptWorker",
        Message = $"{TransactionStages.ReceiptFetchFailed}: the Tax Administration site did not answer (status 503)",
        Template = "{Stage}: {Reason} (status {StatusCode})",
        TransactionId = transactionId,
        PropertiesJson = $$"""{"Stage":"{{TransactionStages.ReceiptFetchFailed}}","Reason":"the Tax Administration site did not answer","StatusCode":503,"EventId":{"Id":{{TransactionStages.ReceiptFetchFailedEventId}},"Name":"{{TransactionStages.ReceiptFetchFailed}}"},"TransactionId":"{{transactionId}}"}""",
    };

    static AppLogEntry Row(DateTimeOffset at, LogSeverity level, string source, string message, string? exception = null) => new()
    {
        Id = 0,
        LoggedAt = at,
        Level = level,
        Source = source,
        Message = message,
        Template = "{Message}",
        Exception = exception,
    };

    static async Task WriteUserAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var user = new AppUser
        {
            Id = MockData.UserId,
            Username = MockData.Username,
            PasswordHash = string.Empty,
            CreatedAt = MockData.Now,
        };
        user.PasswordHash = services.GetRequiredService<IPasswordHasher>().Hash(user, MockData.Password);

        await services.GetRequiredService<IUserStore>().UpsertAsync(user, cancellationToken);
    }

    static async Task WriteSecretsAsync(ISecretStore secrets, CancellationToken cancellationToken)
    {
        await secrets.SetAsync(MockData.AnthropicKeySecret, MockData.FakeKey, cancellationToken);
        await secrets.SetAsync(MockData.GroqKeySecret, MockData.FakeKey, cancellationToken);
    }

    static async Task WriteBackupRunAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        // The real clock, not MockData.Now: the Backups health check measures a backup's age against the
        // host's own clock, and anything older than 26 hours reads as stale.
        var finishedAt = DateTimeOffset.UtcNow.AddHours(-2);

        db.BackupRuns.Add(new BackupRun
        {
            Id = Guid.Parse("7a1c0000-0000-4000-8000-0000000000bb"),
            StartedAt = finishedAt.AddMinutes(-1),
            FinishedAt = finishedAt,
            Succeeded = true,
            FileName = "noof_ledger_demo-mock.dump",
            SizeBytes = 1_048_576,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
