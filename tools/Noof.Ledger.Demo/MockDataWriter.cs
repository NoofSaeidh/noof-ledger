using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Auth;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Fx;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Backup;
using Noof.Ledger.Persistence.Balances;
using Noof.Ledger.Persistence.Diagnostics;
using Noof.Ledger.Persistence.Receipts;
using Noof.Ledger.Persistence.Revisions;
using Npgsql;
using NpgsqlTypes;

namespace Noof.Ledger.Demo;

internal static class MockDataWriter
{
    public static async Task WriteAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<LedgerDbContext>();
        var wallets = await WriteWalletsAsync(
            services.GetRequiredService<IWalletAdmin>(), services.GetRequiredService<IWalletFxTerms>(), db, cancellationToken);
        var categories = await db.Categories.ToDictionaryAsync(category => category.Slug, category => category.Id, cancellationToken);
        var merchants = await WriteMerchantsAsync(db, cancellationToken);

        for (var index = 0; index < MockData.Records.Count; index++)
            await WriteRecordAsync(db, MockData.Records[index], index + 1, wallets, categories, merchants, cancellationToken);

        var messageId = MockData.Records.Count;
        foreach (var transfer in MockData.Transfers)
            await WriteTransferAsync(db, transfer, ++messageId, wallets, categories, cancellationToken);
        await WriteForeignSpendingAsync(db, MockData.ForeignSpending, ++messageId, wallets, categories, cancellationToken);
        await WriteWaitingRecordAsync(db, MockData.Waiting, ++messageId, cancellationToken);

        await WriteTraceAsync(db, cancellationToken);
        await WriteReceiptTracesAsync(db, cancellationToken);
        await WriteLogRowsAsync(db, cancellationToken);
        await services.GetRequiredService<ILogRetentionSettings>().SaveAsync(MockData.LogRetention, cancellationToken);
        await WriteUserAsync(services, cancellationToken);
        await WriteSecretsAsync(services.GetRequiredService<ISecretStore>(), cancellationToken);
        await WriteBackupRunAsync(db, cancellationToken);
        await WriteJobStampsAsync(db, cancellationToken);
        await WriteBugReportsAsync(db, wallets, cancellationToken);
        await WriteFxRatesAsync(services.GetRequiredService<IFxRateStore>(), cancellationToken);
    }

    static async Task WriteFxRatesAsync(IFxRateStore rates, CancellationToken cancellationToken)
    {
        foreach (var snapshot in MockData.FxRates(DateOnly.FromDateTime(DateTime.UtcNow)))
            await rates.AppendAsync(snapshot, cancellationToken);
    }

    static async Task<Dictionary<string, Guid>> WriteWalletsAsync(
        IWalletAdmin admin, IWalletFxTerms fxTerms, LedgerDbContext db, CancellationToken cancellationToken)
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

            foreach (var terms in wallet.Terms ?? [])
                await fxTerms.SetAsync(id, terms, cancellationToken);

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

    // Only the facts are written here; LedgerPostings posts the legs, the fee and the transfers row, as the app does.
    static async Task WriteTransferAsync(
        LedgerDbContext db, MockTransfer transfer, int messageId, Dictionary<string, Guid> wallets,
        Dictionary<string, Guid> categories, CancellationToken cancellationToken)
    {
        var from = new Money(transfer.FromAmount, CurrencyOf(transfer.From));
        var to = new Money(transfer.ToAmount, CurrencyOf(transfer.To));
        Money? fee = transfer.Fee is { } amount
            ? new Money(amount, transfer.FeeLeg == TransferLeg.To ? to.Currency : from.Currency)
            : null;

        var transaction = NewCompletedRecord(
            transfer.Id, wallets[transfer.From], TransactionKind.Transfer, transfer.RawText, transfer.Day, messageId);
        db.Transactions.Add(transaction);
        if (fee is { } taken)
            db.LineItems.Add(NewFeeLine(MockData.Id(2000 + messageId * 10), transaction.Id, "Fee", taken, categories, ordinal: 1));
        await db.SaveChangesAsync(cancellationToken);

        await LedgerPostings.RewriteAsync(
            db, transaction, stated: null,
            new TransferFacts(wallets[transfer.From], from, wallets[transfer.To], to, fee, transfer.FeeLeg, StatedRate: null),
            cancellationToken);
        await RevisionLog.AppendAsync(
            db, transaction, RevisionKind.Initial, null, TransactionStatus.Captured, transaction.OccurredAt.AddSeconds(2), cancellationToken);
    }

    // The USD line counts as Kaspi's KZT charge, for This month as for LedgerPostings; the fee is a Fee line of its own.
    static async Task WriteForeignSpendingAsync(
        LedgerDbContext db, MockForeignSpending spending, int messageId, Dictionary<string, Guid> wallets,
        Dictionary<string, Guid> categories, CancellationToken cancellationToken)
    {
        var transaction = NewCompletedRecord(
            spending.Id, wallets[spending.Wallet], TransactionKind.Expense, spending.RawText, spending.Day, messageId);
        db.Transactions.Add(transaction);
        db.LineItems.Add(new LineItem
        {
            Id = MockData.Id(2000 + messageId * 10),
            TransactionId = transaction.Id,
            Description = spending.Line.Description,
            Amount = new Money(spending.Line.Amount, spending.LineCurrency),
            CategoryId = categories[spending.Line.CategorySlug],
            CategorizedBy = CategorizationAuthority.Model,
            MerchantId = null,
            Ordinal = 1,
        });
        db.LineItems.Add(NewFeeLine(
            MockData.Id(2000 + messageId * 10 + 1), transaction.Id, $"Fee · {spending.LineCurrency.Value} purchase",
            new Money(spending.Fee, CurrencyOf(spending.Wallet)), categories, ordinal: 2));
        db.Charges.Add(new Charge
        {
            TransactionId = transaction.Id,
            Currency = spending.LineCurrency,
            ChargedAmount = spending.Charged,
            FeeAmount = spending.Fee,
            RateUsed = spending.Rate,
            FeePercent = spending.FeePercent,
            Source = ChargeSource.WalletTerms,
        });
        await db.SaveChangesAsync(cancellationToken);

        await LedgerPostings.RewriteAsync(db, transaction, stated: null, transfer: null, cancellationToken);
        await RevisionLog.AppendAsync(
            db, transaction, RevisionKind.Initial, null, TransactionStatus.Captured, transaction.OccurredAt.AddSeconds(2), cancellationToken);
    }

    // Created two days before the real now: the integrity checks measure idle time against the host's own clock, as the
    // Backups check measures a backup's age (WriteBackupRunAsync), so a record dated in the mock month could not wait
    // "over a day" on every day the pictures are taken. Kept an Expense, as a failed capture keeps its kind.
    static async Task WriteWaitingRecordAsync(LedgerDbContext db, MockWaitingRecord record, int messageId, CancellationToken cancellationToken)
    {
        db.Transactions.Add(new Transaction
        {
            Id = record.Id,
            WalletId = null,
            Kind = TransactionKind.Expense,
            RawText = record.RawText,
            CaptureKind = CaptureKind.Text,
            Status = TransactionStatus.Failed,
            FailureReason = record.Reason,
            TimeZoneId = MockData.TimeZoneId,
            OccurredAt = record.OccurredAt,
            OccurredOn = record.Day,
            TelegramChatId = MockData.TelegramChatId,
            TelegramMessageId = messageId,
            BotMessageId = 10_000 + messageId,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    // The failed record, the held receipt and the held slip are dated in the mock month and would read as Waiting on you
    // or not depending on the day the pictures are taken. A job touched at the real now keeps each of them active.
    static async Task WriteJobStampsAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        db.CategorizationJobs.Add(new CategorizationJob
        {
            Id = MockData.Id(5900),
            TransactionId = MockData.FailedTransactionId,
            Kind = JobKind.Categorize,
            Status = JobStatus.Failed,
            AttemptCount = 8,
            RunAfter = now,
            LastError = "The model call failed.",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        Guid[] held = [MockData.UnconfirmedReceiptTransactionId, MockData.HeldSlipTransactionId];
        await db.CategorizationJobs
            .Where(job => held.Contains(job.TransactionId) && job.Kind == JobKind.ExtractReceipt)
            .ExecuteUpdateAsync(set => set.SetProperty(job => job.UpdatedAt, now), cancellationToken);
    }

    // By column name: the table and its JSON are the phase's fixed shape, the entity's property names are not. Number is
    // left to the identity column, which numbers the reports #1-#3 in this order.
    const string InsertBugReport =
        """
        INSERT INTO bug_reports (id, created_at, source, text, transaction_id, reply_to, status,
            closed_at, snapshot_at, record_summary, findings, log_lines, collection_failures, explanation_state,
            explanation_attempts, explanation_next_at, explanation, looks_like_bug, delivered_as)
        VALUES (@id, @createdAt, @source, @text, @transactionId, @replyTo, @status,
            @closedAt, @snapshotAt, @recordSummary, @findings, @logLines, @collectionFailures, @explanationState,
            @attempts, @createdAt, @explanation, @looksLikeBug, @deliveredAs)
        """;

    static async Task WriteBugReportsAsync(LedgerDbContext db, Dictionary<string, Guid> wallets, CancellationToken cancellationToken)
    {
        var waitingSince = await db.Transactions
            .Where(transaction => transaction.Id == MockData.WaitingTransactionId)
            .Select(transaction => transaction.CreatedAt)
            .SingleAsync(cancellationToken);
        var tracedAt = await db.Transactions
            .Where(transaction => transaction.Id == MockData.TracedTransactionId)
            .Select(transaction => transaction.OccurredAt)
            .SingleAsync(cancellationToken);

        foreach (var report in MockBugReports.Build(waitingSince, wallets["Wise"], tracedAt))
            await db.Database.ExecuteSqlRawAsync(InsertBugReport, BugReportParameters(report), cancellationToken);
    }

    static NpgsqlParameter[] BugReportParameters(MockBugReport report) =>
    [
        Parameter("id", NpgsqlDbType.Uuid, report.Id),
        Parameter("createdAt", NpgsqlDbType.TimestampTz, report.CreatedAt),
        Parameter("source", NpgsqlDbType.Integer, (int)report.Source),
        Parameter("text", NpgsqlDbType.Text, report.Text),
        Parameter("transactionId", NpgsqlDbType.Uuid, report.TransactionId),
        Parameter("replyTo", NpgsqlDbType.Text, report.ReplyTo),
        Parameter("status", NpgsqlDbType.Integer, (int)report.Status),
        Parameter("closedAt", NpgsqlDbType.TimestampTz, report.ClosedAt),
        Parameter("snapshotAt", NpgsqlDbType.TimestampTz, report.SnapshotAt),
        Parameter("recordSummary", NpgsqlDbType.Text, report.RecordSummary),
        Parameter("findings", NpgsqlDbType.Jsonb, report.FindingsJson),
        Parameter("logLines", NpgsqlDbType.Jsonb, report.LogLinesJson),
        Parameter("collectionFailures", NpgsqlDbType.Text, report.CollectionFailures),
        Parameter("explanationState", NpgsqlDbType.Integer, (int)report.ExplanationState),
        Parameter("attempts", NpgsqlDbType.Integer, report.ExplanationAttempts),
        Parameter("explanation", NpgsqlDbType.Text, report.Explanation),
        Parameter("looksLikeBug", NpgsqlDbType.Boolean, report.LooksLikeBug),
        Parameter("deliveredAs", NpgsqlDbType.Text, report.DeliveredAs),
    ];

    static NpgsqlParameter Parameter(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    static Transaction NewCompletedRecord(Guid id, Guid walletId, TransactionKind kind, string rawText, DateOnly day, int messageId)
    {
        var occurredAt = ZonedClock.StartOfDay(day, MockData.TimeZoneId).AddHours(12).AddMinutes(messageId);
        return new Transaction
        {
            Id = id,
            WalletId = walletId,
            Kind = kind,
            RawText = rawText,
            CaptureKind = CaptureKind.Text,
            Status = TransactionStatus.Completed,
            TimeZoneId = MockData.TimeZoneId,
            OccurredAt = occurredAt,
            OccurredOn = day,
            TelegramChatId = MockData.TelegramChatId,
            TelegramMessageId = messageId,
            BotMessageId = 10_000 + messageId,
            CreatedAt = occurredAt,
        };
    }

    // As the app writes one: by C# (Rule), in Fees & Charges, after every principal line.
    static LineItem NewFeeLine(
        Guid id, Guid transactionId, string description, Money amount, Dictionary<string, Guid> categories, int ordinal) => new()
    {
        Id = id,
        TransactionId = transactionId,
        Description = description,
        Amount = amount,
        CategoryId = categories["fees-charges"],
        CategorizedBy = CategorizationAuthority.Rule,
        MerchantId = null,
        Ordinal = ordinal,
        Role = EntryRole.Fee,
    };

    static CurrencyCode CurrencyOf(string wallet) => MockData.Wallets.Single(candidate => candidate.Name == wallet).Currency;

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
            Kind = receipt.Kind,
            PaymentMethod = receipt.Payment,
            QrTotal = receipt.QrTotal,
            TelegramFileId = transaction.TelegramFileId,
            SlipNumber = EfReceiptStore.NormalisedSlipNumber(receipt.Exchange?.SlipNumber),
            CreatedAt = transaction.OccurredAt.AddSeconds(3),
        });

        if (receipt.Exchange is { } exchange)
        {
            db.ReceiptExchanges.Add(new ReceiptExchange
            {
                ReceiptId = receiptId,
                GivenAmount = exchange.GivenAmount,
                GivenCurrency = exchange.GivenCurrency,
                ReceivedAmount = exchange.ReceivedAmount,
                ReceivedCurrency = exchange.ReceivedCurrency,
                Rate = exchange.Rate,
                CommissionAmount = exchange.CommissionAmount,
                CommissionCurrency = exchange.CommissionCurrency,
                SlipNumber = exchange.SlipNumber,
            });
        }

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

        // A receipt read from the photo counts as confirmed only once its CategorizeReceipt job exists, a slip once
        // its RecordExchange job does: a Captured one gets neither, so it waits for Record anyway.
        AddSucceededJob(db, transaction, JobKind.ExtractReceipt, MockData.Id(5000 + messageId * 10));
        if (transaction.Status == TransactionStatus.Completed)
        {
            var recordedBy = receipt.Kind == ReceiptKind.Exchange ? JobKind.RecordExchange : JobKind.CategorizeReceipt;
            AddSucceededJob(db, transaction, recordedBy, MockData.Id(5000 + messageId * 10 + 1));
        }

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
        var failed = await db.Transactions.SingleAsync(transaction => transaction.Id == MockData.FailedTransactionId, cancellationToken);
        Guid[] completed = [MockData.TracedTransactionId, MockData.ExchangeTransactionId, MockData.ForeignSpendingTransactionId];
        var recorded = await db.Transactions.Where(transaction => completed.Contains(transaction.Id)).ToListAsync(cancellationToken);

        db.AppLogs.AddRange(recorded.SelectMany(TextCaptureStages));
        db.AppLogs.AddRange(
            Stage(failed.Id, failed.OccurredAt, "Noof.Ledger.Telegram.TelegramUpdateRouter", TransactionStages.Received, TransactionStages.ReceivedEventId),
            StageFailed(failed.Id, failed.OccurredAt.AddMilliseconds(2300), TransactionStages.Categorized));

        await db.SaveChangesAsync(cancellationToken);
    }

    static IEnumerable<AppLogEntry> TextCaptureStages(Transaction transaction) =>
    [
        Stage(transaction.Id, transaction.OccurredAt, "Noof.Ledger.Telegram.TelegramUpdateRouter", TransactionStages.Received, TransactionStages.ReceivedEventId),
        Stage(transaction.Id, transaction.OccurredAt.AddMilliseconds(1450), "Noof.Ledger.Host.Workers.CategorizationWorker", TransactionStages.Categorized, TransactionStages.CategorizedEventId),
        Stage(transaction.Id, transaction.OccurredAt.AddMilliseconds(1520), "Noof.Ledger.Host.Workers.CategorizationWorker", TransactionStages.Persisted, TransactionStages.PersistedEventId),
        Stage(transaction.Id, transaction.OccurredAt.AddMilliseconds(1690), "Noof.Ledger.Telegram.TelegramChatNotifier", TransactionStages.Replied, TransactionStages.RepliedEventId),
    ];

    static async Task WriteReceiptTracesAsync(LedgerDbContext db, CancellationToken cancellationToken)
    {
        const string Router = "Noof.Ledger.Telegram.TelegramUpdateRouter";
        const string Extractor = "Noof.Ledger.Host.Workers.ExtractReceiptWorker";
        const string Categorizer = "Noof.Ledger.Host.Workers.CategorizationWorker";
        const string Notifier = "Noof.Ledger.Telegram.TelegramChatNotifier";

        var qr = await db.Transactions.SingleAsync(transaction => transaction.Id == MockData.ReceiptTransactionId, cancellationToken);
        var vision = await db.Transactions.SingleAsync(transaction => transaction.Id == MockData.VisionReceiptTransactionId, cancellationToken);
        var unconfirmed = await db.Transactions.SingleAsync(transaction => transaction.Id == MockData.UnconfirmedReceiptTransactionId, cancellationToken);
        var heldSlip = await db.Transactions.SingleAsync(transaction => transaction.Id == MockData.HeldSlipTransactionId, cancellationToken);

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
            Stage(unconfirmed.Id, unconfirmed.OccurredAt.AddMilliseconds(6480), Extractor, TransactionStages.Extracted, TransactionStages.ExtractedEventId),
            Stage(heldSlip.Id, heldSlip.OccurredAt, Router, TransactionStages.Received, TransactionStages.ReceivedEventId),
            Stage(heldSlip.Id, heldSlip.OccurredAt.AddMilliseconds(5920), Extractor, TransactionStages.Extracted, TransactionStages.ExtractedEventId));

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
