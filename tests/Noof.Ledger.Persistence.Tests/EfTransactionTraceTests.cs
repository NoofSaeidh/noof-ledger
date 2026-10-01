using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Diagnostics;
using Noof.Ledger.Persistence.Receipts;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfTransactionTraceTests(PostgresFixture fixture)
{
    static readonly Guid TransactionId = new("00000000-0000-0000-0003-000000000001");

    static EfTransactionTrace Trace(LedgerDbContext db) => new(db, new EfReceiptStore(db, TimeProvider.System));

    static async Task SeedTransactionAsync(LedgerDbContext db) =>
        await SeedTransactionAsync(db, TransactionId);

    static async Task SeedTransactionAsync(LedgerDbContext db, Guid id)
    {
        db.Transactions.Add(new Transaction
        {
            Id = id,
            WalletId = new Guid("00000000-0000-0000-0000-000000000001"),
            Kind = TransactionKind.Expense,
            RawText = "кофе 250",
            Status = TransactionStatus.Completed,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
            OccurredOn = new DateOnly(2026, 9, 25),
            TelegramChatId = 1,
            TelegramMessageId = 1,
            CreatedAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    static readonly DateTimeOffset At = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);
    static readonly Guid FeesAndChargesId = new("00000000-0000-0000-0001-000000000013");
    static readonly Guid SubscriptionsId = new("00000000-0000-0000-0001-000000000011");

    static Wallet NewWallet(string name, CurrencyCode currency) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Currency = currency,
        CreatedAt = At,
    };

    static Transaction NewRecord(Guid walletId, TransactionKind kind, string rawText) => new()
    {
        Id = TransactionId,
        WalletId = walletId,
        Kind = kind,
        RawText = rawText,
        Status = TransactionStatus.Completed,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = At,
        OccurredOn = new DateOnly(2026, 9, 25),
        TelegramChatId = 1,
        TelegramMessageId = 1,
        CreatedAt = At,
    };

    static LineItem NewLine(string description, Money amount, Guid categoryId, int ordinal, EntryRole role) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = TransactionId,
        Description = description,
        Amount = amount,
        CategoryId = categoryId,
        CategorizedBy = role == EntryRole.Fee ? CategorizationAuthority.Rule : CategorizationAuthority.Model,
        MerchantId = null,
        Ordinal = ordinal,
        Role = role,
    };

    // 101 EUR left Wise, 1 EUR of it the fee; 11 700 RSD reached Cash RSD, at a menjačnica.
    static async Task<(Wallet Wise, Wallet Cash)> SeedExchangeAsync(
        LedgerDbContext db, Guid? venueId = null, decimal? statedEurRate = null)
    {
        var wise = NewWallet("Wise EUR", CurrencyCode.Eur);
        var cash = NewWallet("Cash RSD", CurrencyCode.Rsd);
        db.Wallets.AddRange(wise, cash);
        db.Transactions.Add(NewRecord(wise.Id, TransactionKind.Transfer, "поменял 100 евро на 11700, комиссия 1 евро"));
        db.Transfers.Add(new Transfer
        {
            TransactionId = TransactionId,
            FromWalletId = wise.Id,
            From = new Money(101m, CurrencyCode.Eur),
            ToWalletId = cash.Id,
            To = new Money(11700m, CurrencyCode.Rsd),
            FeeLeg = TransferLeg.From,
            StatedRate = statedEurRate,
            StatedRateBase = statedEurRate is null ? null : CurrencyCode.Eur,
            VenueMerchantId = venueId,
        });
        db.LineItems.Add(NewLine("Fee", new Money(1m, CurrencyCode.Eur), FeesAndChargesId, 1, EntryRole.Fee));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (wise, cash);
    }

    static AppLogEntry StageEvent(long id, DateTimeOffset at, string stage, int eventId, Guid transactionId) => new()
    {
        Id = id,
        LoggedAt = at,
        Level = LogSeverity.Information,
        Source = "Noof.Ledger.Telegram.TelegramPollingService",
        Message = stage,
        Template = stage,
        TransactionId = transactionId,
        PropertiesJson = $$"""{"Stage":"{{stage}}","EventId":{"Id":{{eventId}},"Name":"{{stage}}"},"TransactionId":"{{transactionId}}"}""",
    };

    static AppLogEntry StageFailedEvent(long id, DateTimeOffset at, string failedStage, Guid transactionId, string? exception = null) => new()
    {
        Id = id,
        LoggedAt = at,
        Level = LogSeverity.Error,
        Source = "Noof.Ledger.Host.Workers.CategorizationWorker",
        Message = $"{TransactionStages.StageFailed} at stage {failedStage}",
        Template = "{Stage} at stage {FailedStage}",
        Exception = exception,
        TransactionId = transactionId,
        PropertiesJson = $$"""
            {"Stage":"{{TransactionStages.StageFailed}}","FailedStage":"{{failedStage}}","EventId":{"Id":{{TransactionStages.StageFailedEventId}},"Name":"{{TransactionStages.StageFailed}}"},"TransactionId":"{{transactionId}}"}
            """,
    };

    static AppLogEntry NonStageEvent(long id, DateTimeOffset at, Guid transactionId) => new()
    {
        Id = id,
        LoggedAt = at,
        Level = LogSeverity.Debug,
        Source = "Noof.Ledger.Telegram.TelegramPollingService",
        Message = "unrelated line sharing the transaction id",
        Template = "unrelated line sharing the transaction id",
        TransactionId = transactionId,
        PropertiesJson = """{"TransactionId":"irrelevant"}""",
    };

    [Fact]
    public async Task Events_are_ordered_by_logged_at_then_id_with_stage_and_event_id_parsed()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);
        var t0 = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        db.AppLogs.AddRange(
            StageEvent(3, t0, TransactionStages.Received, TransactionStages.ReceivedEventId, TransactionId),
            StageEvent(1, t0, TransactionStages.Categorized, TransactionStages.CategorizedEventId, TransactionId), // same instant, lower id: sorts first
            // id 2 is lower than id 3 but occurs a second later: an id-only sort would place this
            // ahead of "Received" (id 3); the correct sort (by logged_at first) must not.
            StageEvent(2, t0.AddSeconds(1), TransactionStages.Persisted, TransactionStages.PersistedEventId, TransactionId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Exists.Should().BeTrue();
        trace.Events.Select(e => e.Stage).Should().Equal(
            TransactionStages.Categorized, TransactionStages.Received, TransactionStages.Persisted);
        trace.Events[0].EventId.Should().Be(TransactionStages.CategorizedEventId);
    }

    [Fact]
    public async Task Rows_without_a_transaction_id_or_without_a_stage_are_excluded()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);
        var t0 = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        db.AppLogs.AddRange(
            StageEvent(1, t0, TransactionStages.Received, TransactionStages.ReceivedEventId, TransactionId),
            NonStageEvent(2, t0.AddSeconds(1), TransactionId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Events.Should().ContainSingle().Which.Stage.Should().Be(TransactionStages.Received);
    }

    [Fact]
    public async Task Pruned_logs_leave_events_empty_but_history_still_present()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);
        db.TransactionRevisions.Add(new TransactionRevision
        {
            Id = Guid.NewGuid(),
            TransactionId = TransactionId,
            RevisionNumber = 1,
            Kind = RevisionKind.Initial,
            Instruction = null,
            StatusBefore = TransactionStatus.Captured,
            StatusAfter = TransactionStatus.Completed,
            Snapshot = "{}",
            CreatedAt = DateTimeOffset.Parse("2026-09-25T10:00:00Z"),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Exists.Should().BeTrue();
        trace.Events.Should().BeEmpty();
        trace.History.Should().ContainSingle().Which.ChangeKind.Should().Be(nameof(RevisionKind.Initial));
    }

    [Fact]
    public async Task History_uses_the_instruction_when_present_else_the_status_transition()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);
        db.TransactionRevisions.AddRange(
            new TransactionRevision
            {
                Id = Guid.NewGuid(), TransactionId = TransactionId, RevisionNumber = 1, Kind = RevisionKind.Initial,
                Instruction = null, StatusBefore = TransactionStatus.Captured, StatusAfter = TransactionStatus.Completed,
                Snapshot = "{}", CreatedAt = DateTimeOffset.Parse("2026-09-25T10:00:00Z"),
            },
            new TransactionRevision
            {
                Id = Guid.NewGuid(), TransactionId = TransactionId, RevisionNumber = 2, Kind = RevisionKind.Correction,
                Instruction = "нет, 1500", StatusBefore = TransactionStatus.Completed, StatusAfter = TransactionStatus.Completed,
                Snapshot = "{}", CreatedAt = DateTimeOffset.Parse("2026-09-25T10:01:00Z"),
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.History.Select(h => h.Details).Should().Equal("Captured → Completed", "нет, 1500");
        trace.History.Select(h => h.ChangeKind).Should().Equal(nameof(RevisionKind.Initial), nameof(RevisionKind.Correction));
    }

    [Fact]
    public async Task A_StageFailed_event_carries_the_failed_stage_parsed_from_properties()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);
        var t0 = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        db.AppLogs.Add(StageFailedEvent(1, t0, TransactionStages.Categorized, TransactionId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        var stageFailed = trace.Events.Should().ContainSingle().Subject;
        stageFailed.Stage.Should().Be(TransactionStages.StageFailed);
        stageFailed.FailedStage.Should().Be(TransactionStages.Categorized);
    }

    [Fact]
    public async Task A_non_failed_event_carries_no_failed_stage()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);
        var t0 = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        db.AppLogs.Add(StageEvent(1, t0, TransactionStages.Received, TransactionStages.ReceivedEventId, TransactionId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Events.Should().ContainSingle().Which.FailedStage.Should().BeNull();
    }

    [Fact]
    public async Task A_transaction_with_no_receipt_reports_a_null_Receipt()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Receipt.Should().BeNull();
    }

    [Fact]
    public async Task A_transaction_with_a_receipt_exposes_shop_lines_and_their_categories()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);

        var category = new Category { Id = Guid.NewGuid(), Slug = "trace-groceries", NameEn = "Groceries", NameRu = "Продукты", IsActive = true };
        db.Categories.Add(category);

        var receiptId = Guid.NewGuid();
        db.Receipts.Add(new Receipt
        {
            Id = receiptId,
            TransactionId = TransactionId,
            Source = ReceiptSource.Vision,
            SellerName = "Test Market",
            LocationName = "Test Market Nova 12",
            SellerAddress = "Bulevar 1",
            SellerTaxId = "123456789",
            FiscalNumber = "FN-1",
            IssuedAt = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero),
            Total = new Money(300m, CurrencyCode.Rsd),
            Kind = ReceiptKind.Sale,
            PaymentMethod = PaymentMethod.Card,
            QrTotal = 305m,
            CreatedAt = new DateTimeOffset(2026, 9, 25, 9, 0, 5, TimeSpan.Zero),
        });
        var lineId = Guid.NewGuid();
        db.ReceiptLines.Add(new ReceiptLine
        {
            Id = lineId,
            ReceiptId = receiptId,
            Ordinal = 1,
            Name = "Bread",
            Quantity = 1m,
            Unit = "kom",
            UnitPrice = 300m,
            Total = 300m,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = TransactionId,
            Description = "Bread",
            Amount = new Money(300m, CurrencyCode.Rsd),
            CategoryId = category.Id,
            CategorizedBy = CategorizationAuthority.Model,
            Ordinal = 1,
            ReceiptLineId = lineId,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Receipt.Should().NotBeNull();
        trace.Receipt!.Source.Should().Be(ReceiptSource.Vision);
        trace.Receipt.SellerName.Should().Be("Test Market");
        trace.Receipt.LocationName.Should().Be("Test Market Nova 12");
        trace.Receipt.SellerAddress.Should().Be("Bulevar 1");
        trace.Receipt.SellerTaxId.Should().Be("123456789");
        trace.Receipt.FiscalNumber.Should().Be("FN-1");
        trace.Receipt.PaymentMethod.Should().Be(PaymentMethod.Card);
        trace.Receipt.Total.Should().Be(300m);
        trace.Receipt.Currency.Should().Be(CurrencyCode.Rsd);
        trace.Receipt.QrTotal.Should().Be(305m);
        var line = trace.Receipt.Lines.Should().ContainSingle().Subject;
        line.Ordinal.Should().Be(1);
        line.Name.Should().Be("Bread");
        line.Quantity.Should().Be(1m);
        line.Unit.Should().Be("kom");
        line.UnitPrice.Should().Be(300m);
        line.Total.Should().Be(300m);
        line.CategoryNameEn.Should().Be("Groceries");
    }

    // 2026-09-27: AwaitingConfirmation is derived (vision receipt + no CategorizeReceipt job +
    // Captured), never stored - this is the trace page's own view of the same state the echo showed
    // right after extraction.
    [Fact]
    public async Task A_captured_vision_receipt_with_no_categorize_job_is_awaiting_confirmation_with_the_mismatch_named()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        db.Transactions.Add(new Transaction
        {
            Id = TransactionId,
            WalletId = new Guid("00000000-0000-0000-0000-000000000001"),
            Kind = TransactionKind.Expense,
            RawText = null,
            CaptureKind = CaptureKind.Photo,
            TelegramFileId = "photo-1",
            Status = TransactionStatus.Captured,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
            OccurredOn = new DateOnly(2026, 9, 25),
            TelegramChatId = 1,
            TelegramMessageId = 1,
            CreatedAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
        });
        var receiptId = Guid.NewGuid();
        db.Receipts.Add(new Receipt
        {
            Id = receiptId,
            TransactionId = TransactionId,
            Source = ReceiptSource.Vision,
            SellerName = "Test Market",
            IssuedAt = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero),
            Total = new Money(500m, CurrencyCode.Rsd),
            Kind = ReceiptKind.Sale,
            CreatedAt = new DateTimeOffset(2026, 9, 25, 9, 0, 5, TimeSpan.Zero),
        });
        db.ReceiptLines.Add(new ReceiptLine
        {
            Id = Guid.NewGuid(),
            ReceiptId = receiptId,
            Ordinal = 1,
            Name = "Bread",
            Quantity = 1m,
            UnitPrice = 400m,
            Total = 400m,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Receipt.Should().NotBeNull();
        trace.Receipt!.AwaitingConfirmation.Should().BeTrue();
        trace.Receipt.Problems.Should().ContainSingle().Which.Should().Be("Lines add up to 400.00 RSD, the receipt says 500.00 RSD");
    }

    [Fact]
    public async Task A_captured_vision_receipt_whose_categorize_job_already_exists_is_not_awaiting_confirmation()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        db.Transactions.Add(new Transaction
        {
            Id = TransactionId,
            WalletId = new Guid("00000000-0000-0000-0000-000000000001"),
            Kind = TransactionKind.Expense,
            RawText = null,
            CaptureKind = CaptureKind.Photo,
            TelegramFileId = "photo-1",
            Status = TransactionStatus.Captured,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
            OccurredOn = new DateOnly(2026, 9, 25),
            TelegramChatId = 1,
            TelegramMessageId = 1,
            CreatedAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
        });
        var receiptId = Guid.NewGuid();
        db.Receipts.Add(new Receipt
        {
            Id = receiptId,
            TransactionId = TransactionId,
            Source = ReceiptSource.Vision,
            SellerName = "Test Market",
            IssuedAt = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero),
            Total = new Money(400m, CurrencyCode.Rsd),
            Kind = ReceiptKind.Sale,
            CreatedAt = new DateTimeOffset(2026, 9, 25, 9, 0, 5, TimeSpan.Zero),
        });
        db.ReceiptLines.Add(new ReceiptLine
        {
            Id = Guid.NewGuid(),
            ReceiptId = receiptId,
            Ordinal = 1,
            Name = "Bread",
            Quantity = 1m,
            UnitPrice = 400m,
            Total = 400m,
        });
        db.CategorizationJobs.Add(new CategorizationJob
        {
            Id = Guid.NewGuid(),
            TransactionId = TransactionId,
            Kind = JobKind.CategorizeReceipt,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            RunAfter = DateTimeOffset.UnixEpoch,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Receipt!.AwaitingConfirmation.Should().BeFalse();
        trace.Receipt.Problems.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_transaction_id_reports_Exists_false_with_empty_events_and_history()
    {
        await using var db = await fixture.CreateMigratedContextAsync();

        var trace = await Trace(db).GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        trace.Exists.Should().BeFalse();
        trace.Events.Should().BeEmpty();
        trace.History.Should().BeEmpty();
        trace.Summary.Should().BeNull();
    }

    [Fact]
    public async Task The_summary_carries_the_raw_text_capture_kind_received_time_status_kind_wallet_and_occurred_on()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Summary.Should().NotBeNull();
        trace.Summary!.RawText.Should().Be("кофе 250");
        trace.Summary.CaptureKind.Should().Be(CaptureKind.Text);
        trace.Summary.ReceivedAt.Should().Be(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero));
        trace.Summary.Status.Should().Be(TransactionStatus.Completed);
        trace.Summary.Kind.Should().Be(TransactionKind.Expense);
        trace.Summary.WalletName.Should().Be("Main Wallet");
        trace.Summary.OccurredOn.Should().Be(new DateOnly(2026, 9, 25));
    }

    [Fact]
    public async Task The_summary_reports_no_wallet_name_when_the_transaction_has_no_wallet()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        db.Transactions.Add(new Transaction
        {
            Id = TransactionId,
            WalletId = null,
            Kind = TransactionKind.Expense,
            RawText = null,
            CaptureKind = CaptureKind.Voice,
            VoiceFileId = "voice-file-id",
            Status = TransactionStatus.Failed,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
            OccurredOn = new DateOnly(2026, 9, 25),
            TelegramChatId = 1,
            TelegramMessageId = 1,
            CreatedAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Summary.Should().NotBeNull();
        trace.Summary!.WalletName.Should().BeNull();
        trace.Summary.RawText.Should().BeNull();
        trace.Summary.CaptureKind.Should().Be(CaptureKind.Voice);
    }

    [Fact]
    public async Task The_summary_lists_line_items_with_description_amount_and_category_when_any_exist()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = TransactionId,
            Description = "Coffee",
            Amount = new Money(250m, CurrencyCode.Rsd),
            CategoryId = new Guid("00000000-0000-0000-0001-000000000001"),
            CategorizedBy = CategorizationAuthority.Model,
            MerchantId = null,
            Ordinal = 1,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Summary!.LineItems.Should().ContainSingle().Which.Should().Be(
            new TraceLineItem("Coffee", new Money(250m, CurrencyCode.Rsd), "Groceries"));
    }

    [Fact]
    public async Task The_summary_has_no_line_items_when_none_exist()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Summary!.LineItems.Should().BeEmpty();
    }

    [Fact]
    public async Task A_StageFailed_event_carries_a_one_line_reason_extracted_from_its_exception_text()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);
        var t0 = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        const string exceptionText = """
            Noof.Ledger.Application.Categorization.ModelCallException: model call failed ---> Noof.Ledger.Ai.Anthropic.AnthropicBadRequestException: {"type":"error","error":{"type":"invalid_request_error","message":"tools.1.custom: Invalid schema"},"request_id":"req_1"}
               at Noof.Ledger.Ai.Anthropic.AnthropicTranslatingChatClient.GetResponseAsync()
            """;
        db.AppLogs.Add(StageFailedEvent(1, t0, TransactionStages.Categorized, TransactionId, exceptionText));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Events.Should().ContainSingle().Which.Reason.Should().Be("tools.1.custom: Invalid schema");
    }

    [Fact]
    public async Task An_event_with_no_exception_carries_no_reason()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);
        var t0 = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        db.AppLogs.Add(StageEvent(1, t0, TransactionStages.Received, TransactionStages.ReceivedEventId, TransactionId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Events.Should().ContainSingle().Which.Reason.Should().BeNull();
    }

    [Fact]
    public async Task The_summary_of_a_transfer_shows_both_legs_the_fee_on_its_leg_the_rate_and_the_venue()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var venue = new Merchant { Id = Guid.NewGuid(), DisplayName = "Menjačnica Centar", Kind = MerchantKind.ExchangeVenue };
        db.Merchants.Add(venue);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await SeedExchangeAsync(db, venue.Id);

        var summary = (await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken)).Summary!;

        summary.Kind.Should().Be(TransactionKind.Transfer);
        summary.WalletName.Should().Be("Wise EUR", "transactions.wallet_id holds the source leg");
        summary.Transfer.Should().Be(new TransferTraceView(
            new TransferLine(
                "Wise EUR", new Money(101m, CurrencyCode.Eur), "Cash RSD", new Money(11700m, CurrencyCode.Rsd),
                new Money(1m, CurrencyCode.Eur), TransferLeg.From, new ExchangeRate(CurrencyCode.Eur, 117m, CurrencyCode.Rsd)),
            RateStated: false,
            VenueName: "Menjačnica Centar"));
        summary.LineItems.Should().ContainSingle().Which.Should().Be(
            new TraceLineItem("Fee", new Money(1m, CurrencyCode.Eur), "Fees & Charges", EntryRole.Fee));
        summary.Charges.Should().BeEmpty();
    }

    [Fact]
    public async Task The_summary_of_a_foreign_spending_shows_its_charge_with_its_source_and_terms()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = NewWallet("Kaspi KZT", CurrencyCode.Kzt);
        db.Wallets.Add(kaspi);
        db.Transactions.Add(NewRecord(kaspi.Id, TransactionKind.Expense, "30 долларов с каспи"));
        db.LineItems.AddRange(
            NewLine("App Store", new Money(30m, CurrencyCode.Usd), SubscriptionsId, 1, EntryRole.Principal),
            NewLine("Fee · USD purchase", new Money(156m, CurrencyCode.Kzt), FeesAndChargesId, 2, EntryRole.Fee));
        db.Charges.Add(new Charge
        {
            TransactionId = TransactionId,
            Currency = CurrencyCode.Usd,
            ChargedAmount = 15600m,
            FeeAmount = 156m,
            RateUsed = 520m,
            FeePercent = 1m,
            FeeFixed = null,
            FeeMinimum = null,
            Source = ChargeSource.WalletTerms,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var summary = (await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken)).Summary!;

        summary.Charges.Should().ContainSingle().Which.Should().Be(new ChargeView(
            CurrencyCode.Usd, 30m, new Money(15600m, CurrencyCode.Kzt), new Money(156m, CurrencyCode.Kzt), 520m,
            new FeeTerms(1m, null, null), ChargeSource.WalletTerms));
        summary.LineItems.Should().BeEquivalentTo(
        [
            new TraceLineItem("App Store", new Money(30m, CurrencyCode.Usd), "Subscriptions"),
            new TraceLineItem("Fee · USD purchase", new Money(156m, CurrencyCode.Kzt), "Fees & Charges", EntryRole.Fee),
        ]);
        summary.Transfer.Should().BeNull();
    }

    [Fact]
    public async Task The_summary_of_a_transfer_with_a_stated_rate_shows_that_rate_as_stated()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedExchangeAsync(db, statedEurRate: 117.35m);

        var transfer = (await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken)).Summary!.Transfer!;

        transfer.RateStated.Should().BeTrue();
        transfer.Line.Rate.Should().Be(new ExchangeRate(CurrencyCode.Eur, 117.35m, CurrencyCode.Rsd));
    }

    [Fact]
    public async Task The_summary_lists_a_records_charges_in_currency_order()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = NewWallet("Kaspi KZT", CurrencyCode.Kzt);
        db.Wallets.Add(kaspi);
        db.Transactions.Add(NewRecord(kaspi.Id, TransactionKind.Expense, "30 долларов и 10 евро с каспи"));
        db.LineItems.AddRange(
            NewLine("App Store", new Money(30m, CurrencyCode.Usd), SubscriptionsId, 1, EntryRole.Principal),
            NewLine("Spotify", new Money(10m, CurrencyCode.Eur), SubscriptionsId, 2, EntryRole.Principal));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        // Saved one at a time, USD first, so the rows' physical order is the reverse of the order expected.
        db.Charges.Add(NewCharge(CurrencyCode.Usd, 15600m, 520m));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.Charges.Add(NewCharge(CurrencyCode.Eur, 5600m, 560m));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var summary = (await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken)).Summary!;

        summary.Charges!.Select(charge => (charge.Currency, charge.ForeignSum)).Should().Equal(
            (CurrencyCode.Eur, 10m), (CurrencyCode.Usd, 30m));
    }

    static Charge NewCharge(CurrencyCode currency, decimal chargedAmount, decimal rateUsed) => new()
    {
        TransactionId = TransactionId,
        Currency = currency,
        ChargedAmount = chargedAmount,
        FeeAmount = 0m,
        RateUsed = rateUsed,
        Source = ChargeSource.WalletTerms,
    };

    [Fact]
    public async Task The_history_reads_back_the_transfer_block_the_revision_log_writes()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedExchangeAsync(db);
        var record = await db.Transactions.SingleAsync(t => t.Id == TransactionId, TestContext.Current.CancellationToken);
        await RevisionLog.AppendAsync(db, record, RevisionKind.Initial, null, TransactionStatus.Captured, At, TestContext.Current.CancellationToken);

        var history = (await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken)).History;

        var snapshot = history.Should().ContainSingle().Which.Snapshot;
        snapshot.Should().NotBeNull();
        snapshot!.Kind.Should().Be(TransactionKind.Transfer);
        snapshot.WalletName.Should().Be("Wise EUR");
        snapshot.Transfer.Should().Be(new TransferLine(
            "Wise EUR", new Money(101m, CurrencyCode.Eur), "Cash RSD", new Money(11700m, CurrencyCode.Rsd),
            new Money(1m, CurrencyCode.Eur), TransferLeg.From, new ExchangeRate(CurrencyCode.Eur, 117m, CurrencyCode.Rsd)));
        snapshot.Items.Should().ContainSingle().Which.Should().Be(
            new TraceLineItem("Fee", new Money(1m, CurrencyCode.Eur), "Fees & Charges", EntryRole.Fee));
        snapshot.Charges.Should().BeEmpty();
    }

    [Fact]
    public async Task The_history_reads_back_the_charges_the_revision_log_writes()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        var kaspi = NewWallet("Kaspi KZT", CurrencyCode.Kzt);
        db.Wallets.Add(kaspi);
        var record = NewRecord(kaspi.Id, TransactionKind.Expense, "30 долларов с каспи");
        db.Transactions.Add(record);
        db.LineItems.AddRange(
            NewLine("App Store", new Money(30m, CurrencyCode.Usd), SubscriptionsId, 1, EntryRole.Principal),
            NewLine("Fee · USD purchase", new Money(156m, CurrencyCode.Kzt), FeesAndChargesId, 2, EntryRole.Fee));
        var charge = NewCharge(CurrencyCode.Usd, 15600m, 520m);
        charge.FeeAmount = 156m;
        charge.FeePercent = 1m;
        db.Charges.Add(charge);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RevisionLog.AppendAsync(db, record, RevisionKind.Initial, null, TransactionStatus.Captured, At, TestContext.Current.CancellationToken);

        var snapshot = (await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken)).History.Should().ContainSingle().Which.Snapshot!;

        snapshot.WalletName.Should().Be("Kaspi KZT");
        snapshot.Transfer.Should().BeNull();
        snapshot.Charges.Should().ContainSingle().Which.Should().Be(new ChargeView(
            CurrencyCode.Usd, 30m, new Money(15600m, CurrencyCode.Kzt), new Money(156m, CurrencyCode.Kzt), 520m,
            new FeeTerms(1m, null, null), ChargeSource.WalletTerms));
    }

    [Fact]
    public async Task The_history_still_reads_a_snapshot_written_before_phase_7_and_an_empty_one()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);
        db.TransactionRevisions.AddRange(
            new TransactionRevision
            {
                Id = Guid.NewGuid(), TransactionId = TransactionId, RevisionNumber = 1, Kind = RevisionKind.Initial,
                Instruction = null, StatusBefore = TransactionStatus.Captured, StatusAfter = TransactionStatus.Completed,
                Snapshot = """
                    {"raw_text":"кофе 250","occurred_on":"2026-09-25","items":[{"description":"кофе","amount":"250.0000","currency":"RSD","category_slug":"groceries","merchant_id":null,"categorized_by":1}],"kind":"Expense","wallet_id":"00000000-0000-0000-0000-000000000001","stated_balance":null}
                    """,
                CreatedAt = DateTimeOffset.Parse("2026-09-25T10:00:00Z"),
            },
            new TransactionRevision
            {
                Id = Guid.NewGuid(), TransactionId = TransactionId, RevisionNumber = 2, Kind = RevisionKind.Correction,
                Instruction = "нет, 1500", StatusBefore = TransactionStatus.Completed, StatusAfter = TransactionStatus.Completed,
                Snapshot = "{}", CreatedAt = DateTimeOffset.Parse("2026-09-25T10:01:00Z"),
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var history = (await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken)).History;

        history.Should().HaveCount(2);
        history[0].Snapshot.Should().BeEquivalentTo(new RevisionSnapshotView(
            TransactionKind.Expense,
            "Main Wallet",
            [new TraceLineItem("кофе", new Money(250m, CurrencyCode.Rsd), "Groceries")],
            null,
            []));
        history[1].Snapshot.Should().BeNull();
        history[1].Details.Should().Be("нет, 1500");
    }

    [Fact]
    public async Task A_revision_whose_snapshot_cannot_be_read_shows_no_record_and_the_trace_still_renders()
    {
        await using var db = await fixture.CreateMigratedContextAsync();
        await SeedTransactionAsync(db);
        db.TransactionRevisions.AddRange(
            new TransactionRevision
            {
                Id = Guid.NewGuid(), TransactionId = TransactionId, RevisionNumber = 1, Kind = RevisionKind.Initial,
                Instruction = null, StatusBefore = TransactionStatus.Captured, StatusAfter = TransactionStatus.Completed,
                Snapshot = """{"kind":"Expense","items":[{"description":"кофе","currency":"RSD"}]}""",
                CreatedAt = DateTimeOffset.Parse("2026-09-25T10:00:00Z"),
            },
            new TransactionRevision
            {
                Id = Guid.NewGuid(), TransactionId = TransactionId, RevisionNumber = 2, Kind = RevisionKind.Correction,
                Instruction = "нет, 1500", StatusBefore = TransactionStatus.Completed, StatusAfter = TransactionStatus.Completed,
                Snapshot = """{"kind":"Expense","items":[{"description":"кофе","amount":"1500.0000","currency":"RSD","category_slug":"groceries","merchant_id":null,"categorized_by":1,"role":0}],"wallet_id":"00000000-0000-0000-0000-000000000001","transfer":null,"charges":[]}""",
                CreatedAt = DateTimeOffset.Parse("2026-09-25T10:01:00Z"),
            },
            // Parses, but its fee is the whole source amount, so the source principal is zero and no rate exists.
            new TransactionRevision
            {
                Id = Guid.NewGuid(), TransactionId = TransactionId, RevisionNumber = 3, Kind = RevisionKind.Correction,
                Instruction = "это был обмен", StatusBefore = TransactionStatus.Completed, StatusAfter = TransactionStatus.Completed,
                Snapshot = """{"kind":"Transfer","items":[{"description":"Fee","amount":"1.0000","currency":"EUR","category_slug":"fees-charges","merchant_id":null,"categorized_by":2,"role":1}],"wallet_id":"00000000-0000-0000-0000-000000000001","transfer":{"from_wallet_id":"00000000-0000-0000-0000-000000000001","from_amount":"1.0000","from_currency":"EUR","to_wallet_id":"7a1c0000-0000-4000-8000-000000000002","to_amount":"117.0000","to_currency":"RSD","fee_leg":0,"stated_rate":null,"stated_rate_base":null,"venue_merchant_id":null},"charges":[]}""",
                CreatedAt = DateTimeOffset.Parse("2026-09-25T10:02:00Z"),
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await Trace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Summary.Should().NotBeNull();
        trace.History.Select(h => h.ChangeKind).Should().Equal(
            nameof(RevisionKind.Initial), nameof(RevisionKind.Correction), nameof(RevisionKind.Correction));
        trace.History[0].Snapshot.Should().BeNull("one damaged snapshot must not take the trace page down");
        trace.History[1].Snapshot!.Items.Should().ContainSingle().Which.Amount.Should().Be(new Money(1500m, CurrencyCode.Rsd));
        trace.History[2].Snapshot.Should().BeNull("a snapshot that parses but cannot be shown is damage too");
    }
}
