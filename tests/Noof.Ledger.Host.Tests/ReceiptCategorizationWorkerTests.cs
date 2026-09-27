using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Workers;
using Noof.Ledger.TestKit;
using AppReceipts = Noof.Ledger.Application.Receipts;

namespace Noof.Ledger.Host.Tests;

public class ReceiptCategorizationWorkerTests
{
    const string WorkerId = "worker-a";
    static readonly Guid TransactionId = Guid.NewGuid();
    static readonly Guid JobId = Guid.NewGuid();
    static readonly Guid ReceiptId = Guid.NewGuid();
    static readonly Guid GroceriesId = Guid.NewGuid();
    static readonly Guid OtherId = Guid.NewGuid();
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly IRecordEcho Echo = new RecordEcho();

    static readonly WalletOption RsdDefault = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"), "Main RSD", CurrencyCode.Rsd, [], IsDefaultForCurrency: true);
    static readonly WalletOption EurDefault = new(
        Guid.Parse("00000000-0000-0000-0000-000000000002"), "Main EUR", CurrencyCode.Eur, [], IsDefaultForCurrency: true);
    static readonly WalletOption CashWallet = new(
        Guid.Parse("00000000-0000-0000-0000-000000000003"), "Cash", CurrencyCode.Rsd, [], IsDefaultForCurrency: false);
    static readonly WalletOption NamedInCaption = new(
        Guid.Parse("00000000-0000-0000-0000-000000000004"), "Wise", CurrencyCode.Rsd, [], IsDefaultForCurrency: false);

    static CategorizationJob Job(
        string? instruction = null, DateTimeOffset? createdAt = null, DateTimeOffset? claimedAt = null, int attemptCount = 1) => new()
    {
        Id = JobId,
        TransactionId = TransactionId,
        Status = JobStatus.Claimed,
        AttemptCount = attemptCount,
        Kind = JobKind.CategorizeReceipt,
        Instruction = instruction,
        RunAfter = DateTimeOffset.UtcNow,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        ClaimedAt = claimedAt,
    };

    static readonly DateOnly SentOn = new(2026, 9, 25);

    static CategorizationSubject Subject(
        int? botMessageId = 42, TransactionStatus status = TransactionStatus.Captured, string rawText = "") =>
        new(TransactionId, rawText, 111L, botMessageId, "Main RSD", status, SentOn, SentOn, [],
            CaptureKind.Photo, TransactionKind.Expense, CurrencyCode.Rsd, WalletId: RsdDefault.Id);

    static AppReceipts.ReceiptView Receipt(
        ReceiptKind kind = ReceiptKind.Sale,
        string? sellerTaxId = "SYN-100000001",
        string? sellerName = "Test Market",
        PaymentMethod? paymentMethod = PaymentMethod.Card,
        decimal total = 373.4567m,
        IReadOnlyList<AppReceipts.ReceiptLineView>? lines = null) =>
        new(ReceiptId, ReceiptSource.FiscalQr, sellerTaxId, sellerName, "1 Test Street", null, "SYN-1",
            new DateTimeOffset(2026, 9, 25, 9, 30, 0, TimeSpan.Zero), total, CurrencyCode.Rsd, kind, paymentMethod, total, "https://suf.purs.gov.rs/v/?vl=synthetic",
            lines ?? [
                new AppReceipts.ReceiptLineView(Guid.NewGuid(), 1, "Bread", 1m, "kom", 123.4567m, 123.4567m, null),
                new AppReceipts.ReceiptLineView(Guid.NewGuid(), 2, "Milk", 2m, "kom", 125m, 250m, null),
            ]);

    static AppReceipts.ReceiptCategorization Categorization(
        Guid? walletId = null, string? merchantCanonicalName = null, IReadOnlyList<AppReceipts.ReceiptLineCategory>? lines = null,
        AppReceipts.UnsupportedChangeKind unsupportedChange = AppReceipts.UnsupportedChangeKind.None) =>
        new(lines ?? [new AppReceipts.ReceiptLineCategory(1, "groceries"), new AppReceipts.ReceiptLineCategory(2, "groceries")],
            merchantCanonicalName, walletId, unsupportedChange);

    static IServiceScopeFactory ScopeFactoryFor(
        IJobQueue jobQueue, IModelProvider modelProvider, ICategorizationStore? store = null,
        AppReceipts.IReceiptStore? receiptStore = null, ICategoryCatalog? categoryCatalog = null,
        IMerchantDirectory? merchantDirectory = null, AppReceipts.IReceiptCategorizer? categorizer = null,
        IChatNotifier? notifier = null, IWalletDirectory? walletDirectory = null, IRecordEditor? recordEditor = null)
    {
        var resolvedStore = store ?? DefaultStore();
        var resolvedReceiptStore = receiptStore ?? DefaultReceiptStore();
        var resolvedCategoryCatalog = categoryCatalog ?? DefaultCategoryCatalog();
        var resolvedMerchantDirectory = merchantDirectory ?? DefaultMerchantDirectory();
        var resolvedCategorizer = categorizer ?? DefaultCategorizer();
        var resolvedNotifier = notifier ?? Substitute.For<IChatNotifier>();
        var resolvedWalletDirectory = walletDirectory ?? DefaultWalletDirectory();
        var resolvedRecordEditor = recordEditor ?? Substitute.For<IRecordEditor>();

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IJobQueue)).Returns(jobQueue);
        provider.GetService(typeof(IModelProvider)).Returns(modelProvider);
        provider.GetService(typeof(ICategorizationStore)).Returns(resolvedStore);
        provider.GetService(typeof(AppReceipts.IReceiptStore)).Returns(resolvedReceiptStore);
        provider.GetService(typeof(ICategoryCatalog)).Returns(resolvedCategoryCatalog);
        provider.GetService(typeof(IMerchantDirectory)).Returns(resolvedMerchantDirectory);
        provider.GetService(typeof(AppReceipts.IReceiptCategorizer)).Returns(resolvedCategorizer);
        provider.GetService(typeof(IChatNotifier)).Returns(resolvedNotifier);
        provider.GetService(typeof(IWalletDirectory)).Returns(resolvedWalletDirectory);
        provider.GetService(typeof(IRecordEditor)).Returns(resolvedRecordEditor);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);

        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    static ICategorizationStore DefaultStore()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        return store;
    }

    static AppReceipts.IReceiptStore DefaultReceiptStore()
    {
        var receiptStore = Substitute.For<AppReceipts.IReceiptStore>();
        receiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Receipt());
        return receiptStore;
    }

    static ICategoryCatalog DefaultCategoryCatalog()
    {
        var catalog = Substitute.For<ICategoryCatalog>();
        catalog.ActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<CategoryEntry>
        {
            new(GroceriesId, "groceries", "Groceries", "Продукты", null),
            new(OtherId, "other", "Other", "Другое", null),
        });
        return catalog;
    }

    static IMerchantDirectory DefaultMerchantDirectory()
    {
        var directory = Substitute.For<IMerchantDirectory>();
        directory.AliasesAsync(Arg.Any<CancellationToken>()).Returns(new List<MerchantAliasEntry>());
        directory.FindByTaxIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Guid?)null);
        directory.LinkAliasAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        return directory;
    }

    static AppReceipts.IReceiptCategorizer DefaultCategorizer()
    {
        var categorizer = Substitute.For<AppReceipts.IReceiptCategorizer>();
        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Categorization(merchantCanonicalName: "Test Market"));
        return categorizer;
    }

    static IWalletDirectory DefaultWalletDirectory()
    {
        var directory = Substitute.For<IWalletDirectory>();
        IReadOnlyList<WalletOption> active = [RsdDefault, EurDefault, CashWallet, NamedInCaption];
        directory.ActiveAsync(Arg.Any<CancellationToken>()).Returns(active);
        directory.DefaultForPaymentAsync(Arg.Any<PaymentMethod>(), Arg.Any<CancellationToken>()).Returns((Guid?)null);
        return directory;
    }

    static IModelProvider KeyPresent()
    {
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(true);
        return modelProvider;
    }

    static IJobQueue QueueWith(CategorizationJob job)
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(job);
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        jobQueue.FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);
        return jobQueue;
    }

    // A non-money job is claimed through ClaimNonMoneyReceiptAsync instead - QueueWith leaves that
    // unconfigured (defaults to null), so a test that wants one claimed there must set it up itself.
    static IJobQueue QueueWithNonMoneyJob(CategorizationJob job)
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimNonMoneyReceiptAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(job);
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        jobQueue.FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        return jobQueue;
    }

    static IDatabaseGate ReadyGate()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return gate;
    }

    static readonly AppReceipts.FiscalVerificationUrl DefaultVerificationUrl =
        new(new AppReceipts.FiscalVerificationUrlOptions { VerificationUrlPrefix = "https://suf.purs.gov.rs/v/?vl=" });
    static readonly TimeZoneInfo Belgrade = TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade");

    static ReceiptCategorizationWorker CreateWorker(
        IServiceScopeFactory scopeFactory, FakeTimeProvider time, CapturingLogger<ReceiptCategorizationWorker>? logger = null,
        IOperationTimer? timer = null, TimeZoneInfo? captureTimeZone = null) =>
        new(scopeFactory, time, new CategorizationWorkerOptions(), WorkerId, Echo, captureTimeZone ?? Utc, ReadyGate(),
            timer ?? new OperationTimer(time, new SlowOperationOptions()),
            DefaultVerificationUrl,
            logger ?? new CapturingLogger<ReceiptCategorizationWorker>());

    static string? OperationOf(CapturedLogEntry entry) => entry.Properties.GetValueOrDefault("Operation") as string;

    static FakeTimeProvider Time() => new(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_merchant_known_by_pib_is_used_without_creating_an_alias()
    {
        var merchantId = Guid.NewGuid();
        var merchantDirectory = DefaultMerchantDirectory();
        merchantDirectory.FindByTaxIdAsync("SYN-100000001", Arg.Any<CancellationToken>()).Returns(merchantId);
        var store = DefaultStore();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, merchantDirectory: merchantDirectory), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await merchantDirectory.DidNotReceive().LinkAliasAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await merchantDirectory.Received(1).LinkTaxIdAsync(merchantId, "SYN-100000001", Arg.Any<CancellationToken>());
        await store.Received(1).ApplyAsync(
            TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.Items.All(item => item.MerchantId == merchantId)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_merchant_known_by_an_existing_alias_is_used_and_the_categorizer_is_told_the_merchant_is_known()
    {
        var merchantId = Guid.NewGuid();
        var merchantDirectory = DefaultMerchantDirectory();
        merchantDirectory.AliasesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<MerchantAliasEntry> { new(MerchantName.Fold("Test Market"), merchantId, "Test Market") });
        var categorizer = DefaultCategorizer();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), merchantDirectory: merchantDirectory, categorizer: categorizer), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await categorizer.Received(1).CategorizeAsync(
            Arg.Is<AppReceipts.ReceiptCategorizationRequest>(request => request.MerchantKnown), Arg.Any<CancellationToken>());
        await merchantDirectory.DidNotReceive().LinkAliasAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_merchant_is_created_from_the_model_s_canonical_name_and_given_the_receipt_s_tax_id()
    {
        var newMerchantId = Guid.NewGuid();
        var merchantDirectory = DefaultMerchantDirectory();
        merchantDirectory.LinkAliasAsync(MerchantName.Fold("Test Market"), "Fancy Market Inc.", Arg.Any<CancellationToken>())
            .Returns(newMerchantId);
        var categorizer = DefaultCategorizer();
        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Categorization(merchantCanonicalName: "Fancy Market Inc."));
        var store = DefaultStore();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, merchantDirectory: merchantDirectory, categorizer: categorizer), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await merchantDirectory.Received(1).LinkAliasAsync(MerchantName.Fold("Test Market"), "Fancy Market Inc.", Arg.Any<CancellationToken>());
        await merchantDirectory.Received(1).LinkTaxIdAsync(newMerchantId, "SYN-100000001", Arg.Any<CancellationToken>());
        await store.Received(1).ApplyAsync(
            TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.Items.All(item => item.MerchantId == newMerchantId)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_wallet_named_in_the_caption_wins_over_the_payment_default()
    {
        var categorizer = DefaultCategorizer();
        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Categorization(walletId: NamedInCaption.Id, merchantCanonicalName: "Test Market"));
        var walletDirectory = DefaultWalletDirectory();
        walletDirectory.DefaultForPaymentAsync(PaymentMethod.Card, Arg.Any<CancellationToken>()).Returns(CashWallet.Id);
        var store = DefaultStore();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer, walletDirectory: walletDirectory), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == NamedInCaption.Id), Arg.Any<CancellationToken>());
    }

    // Item A (Copilot, Phase 6 review): for a text-link capture, sub.RawText is the original message
    // and carries the whole fiscal verification URL - it must never reach the categorisation model,
    // only the free words alongside it.
    [Fact]
    public async Task The_caption_sent_to_the_model_never_contains_the_verification_url()
    {
        var store = DefaultStore();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(rawText: "lunch https://suf.purs.gov.rs/v/?vl=AbCdEf123 card"));
        var categorizer = DefaultCategorizer();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await categorizer.Received(1).CategorizeAsync(
            Arg.Is<AppReceipts.ReceiptCategorizationRequest>(r =>
                r.Caption == "lunch card" && !r.Caption.Contains("suf.purs.gov.rs", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_caption_that_is_only_the_verification_url_becomes_null()
    {
        var store = DefaultStore();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(rawText: "https://suf.purs.gov.rs/v/?vl=AbCdEf123"));
        var categorizer = DefaultCategorizer();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await categorizer.Received(1).CategorizeAsync(
            Arg.Is<AppReceipts.ReceiptCategorizationRequest>(r => r.Caption == null), Arg.Any<CancellationToken>());
    }

    // A correction that resends the same fiscal link (CorrectionHandler lets that through as an
    // ordinary edit when it matches the receipt's own link) still carries the URL in job.Instruction.
    [Fact]
    public async Task A_correction_that_resends_the_same_link_strips_it_before_reaching_the_model()
    {
        var store = DefaultStore();
        var categorizer = DefaultCategorizer();
        var jobQueue = QueueWith(Job(instruction: "https://suf.purs.gov.rs/v/?vl=AbCdEf123 wrong wallet"));
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await categorizer.Received(1).CategorizeAsync(
            Arg.Is<AppReceipts.ReceiptCategorizationRequest>(r => r.Correction == "wrong wallet"), Arg.Any<CancellationToken>());
    }

    // N-3 (Phase 6 re-review): CategorizationWorker guards exactly this for a plain-text Correct job
    // (KeepingTheRecordsWallet) - re-resolving the payment/currency default on every correction is only
    // right the first time a receipt is categorized. A later correction (or a changed wallet default)
    // that names no wallet must leave the record where the operator already put it, not move it back.
    [Fact]
    public async Task A_correction_that_names_no_wallet_keeps_the_records_current_wallet()
    {
        var store = DefaultStore();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed) with { WalletId = NamedInCaption.Id });
        var walletDirectory = DefaultWalletDirectory();
        walletDirectory.DefaultForPaymentAsync(PaymentMethod.Card, Arg.Any<CancellationToken>()).Returns(CashWallet.Id);
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job("bread is groceries, not other")), KeyPresent(), store, walletDirectory: walletDirectory), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == NamedInCaption.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_correction_that_names_a_wallet_still_moves_the_record_there()
    {
        var categorizer = DefaultCategorizer();
        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Categorization(walletId: CashWallet.Id, merchantCanonicalName: "Test Market"));
        var store = DefaultStore();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed) with { WalletId = NamedInCaption.Id });
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job("that was from cash")), KeyPresent(), store, categorizer: categorizer), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == CashWallet.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_first_categorization_that_names_no_wallet_still_falls_through_to_the_payment_default()
    {
        var walletDirectory = DefaultWalletDirectory();
        walletDirectory.DefaultForPaymentAsync(PaymentMethod.Card, Arg.Any<CancellationToken>()).Returns(CashWallet.Id);
        var store = DefaultStore();
        // No Instruction (Job() with no argument): this is the first, Initial categorization, so the
        // payment/currency default still applies even though the subject's own WalletId happens to be
        // set (a fresh Captured transaction has none in practice, but the guard is on Instruction, not
        // on WalletId, precisely so a first reading is never mistaken for a correction).
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, walletDirectory: walletDirectory), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == CashWallet.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_caption_wallet_the_directory_no_longer_offers_falls_through_to_the_payment_default()
    {
        var categorizer = DefaultCategorizer();
        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Categorization(walletId: Guid.NewGuid(), merchantCanonicalName: "Test Market"));
        var walletDirectory = DefaultWalletDirectory();
        walletDirectory.DefaultForPaymentAsync(PaymentMethod.Card, Arg.Any<CancellationToken>()).Returns(CashWallet.Id);
        var store = DefaultStore();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer, walletDirectory: walletDirectory), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == CashWallet.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_no_caption_wallet_a_card_payment_uses_the_wallet_marked_default_for_card()
    {
        var walletDirectory = DefaultWalletDirectory();
        walletDirectory.DefaultForPaymentAsync(PaymentMethod.Card, Arg.Any<CancellationToken>()).Returns(CashWallet.Id);
        var store = DefaultStore();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, walletDirectory: walletDirectory), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == CashWallet.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_no_caption_wallet_a_cash_payment_uses_the_wallet_marked_default_for_cash()
    {
        var walletDirectory = DefaultWalletDirectory();
        walletDirectory.DefaultForPaymentAsync(PaymentMethod.Cash, Arg.Any<CancellationToken>()).Returns(CashWallet.Id);
        var receiptStore = DefaultReceiptStore();
        receiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Receipt(paymentMethod: PaymentMethod.Cash));
        var store = DefaultStore();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, receiptStore: receiptStore, walletDirectory: walletDirectory), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == CashWallet.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_no_caption_wallet_and_no_payment_default_the_wallet_defaults_to_the_receipt_currency_s_default_wallet()
    {
        var receiptStore = DefaultReceiptStore();
        receiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Receipt(paymentMethod: PaymentMethod.Transfer));
        var store = DefaultStore();
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, receiptStore: receiptStore), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == RsdDefault.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refund_is_recorded_as_income()
    {
        var receiptStore = DefaultReceiptStore();
        receiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Receipt(kind: ReceiptKind.Refund));
        var store = DefaultStore();
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, receiptStore: receiptStore), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.TransactionKind == TransactionKind.Income),
            Arg.Any<CancellationToken>());
    }

    // M-4 (Phase 6 final review): a copy/training/proforma/advance slip is a recognised, deliberate
    // non-post - the same kind of decision a duplicate receipt already gets (Cancelled via
    // IRecordEditor.CancelAsync, never MarkFailedAsync) - not a processing error. Cancelled keeps
    // TransactionStatus.Failed meaning "something broke, worth investigating"; Cancelled also writes
    // a revision, and StageFailed at Categorized (with a FailedStage) gives the trace page a row to
    // explain the gap instead of showing Extracted-then-nothing.
    [Theory]
    [InlineData(ReceiptKind.Copy, "copy")]
    [InlineData(ReceiptKind.Training, "training")]
    [InlineData(ReceiptKind.Proforma, "proforma")]
    [InlineData(ReceiptKind.Advance, "advance")]
    public async Task A_non_money_receipt_kind_is_not_posted_and_the_transaction_is_cancelled_with_a_traceable_reason(
        ReceiptKind kind, string word)
    {
        var receiptStore = DefaultReceiptStore();
        receiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Receipt(kind: kind));
        var store = DefaultStore();
        var notifier = Substitute.For<IChatNotifier>();
        var recordEditor = Substitute.For<IRecordEditor>();
        var jobQueue = QueueWith(Job());
        var logger = new CapturingLogger<ReceiptCategorizationWorker>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, receiptStore: receiptStore, notifier: notifier, recordEditor: recordEditor),
            Time(), logger);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await recordEditor.Received(1).CancelAsync(TransactionId, Arg.Any<CancellationToken>());
        await store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ApplyAsync(Arg.Any<Guid>(), Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(
            111L, 42, Arg.Is<EchoMessage>(echo => echo.Text == $"This receipt is a {word} — not recorded"), Arg.Any<CancellationToken>());
        await jobQueue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());

        var stageFailed = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.StageFailedEventId).Subject;
        stageFailed.Stage.Should().Be(TransactionStages.StageFailed);
        stageFailed.Properties["FailedStage"].Should().Be(TransactionStages.Categorized);
        stageFailed.Exception.Should().NotBeNull();
    }

    [Fact]
    public async Task A_missing_ordinal_from_the_categorizer_falls_back_to_the_catalogue_s_other_category()
    {
        var categorizer = DefaultCategorizer();
        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Categorization(merchantCanonicalName: "Test Market", lines: [new AppReceipts.ReceiptLineCategory(1, "groceries")]));
        var store = DefaultStore();
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId,
            Arg.Is<CategorizationOutcome>(outcome =>
                outcome.Items.Single(item => item.Description == "Bread").CategoryId == GroceriesId
                && outcome.Items.Single(item => item.Description == "Milk").CategoryId == OtherId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Amounts_and_ordinals_come_from_the_receipt_lines_never_the_categorizer()
    {
        var store = DefaultStore();
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId,
            Arg.Is<CategorizationOutcome>(outcome =>
                outcome.Items.Count == 2
                && outcome.Items.Any(item => item.Ordinal == 1 && item.Amount == new Money(123.4567m, CurrencyCode.Rsd))
                && outcome.Items.Any(item => item.Ordinal == 2 && item.Amount == new Money(250m, CurrencyCode.Rsd))
                && outcome.Items.All(item => item.ReceiptLineId != null)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_correction_instruction_reaches_the_categorizer_and_amounts_and_ordinals_still_come_from_the_receipt()
    {
        var categorizer = DefaultCategorizer();
        var store = DefaultStore();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job("that was cash, not card")), KeyPresent(), store, categorizer: categorizer), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await categorizer.Received(1).CategorizeAsync(
            Arg.Is<AppReceipts.ReceiptCategorizationRequest>(request => request.Correction == "that was cash, not card"),
            Arg.Any<CancellationToken>());
        await store.Received(1).ApplyAsync(
            TransactionId,
            Arg.Is<CategorizationOutcome>(outcome =>
                outcome.Kind == JobKind.CategorizeReceipt
                && outcome.Instruction == "that was cash, not card"
                && outcome.Items.All(item => item.ReceiptLineId != null)
                && outcome.Items.Any(item => item.Ordinal == 1 && item.Amount == new Money(123.4567m, CurrencyCode.Rsd))
                && outcome.Items.Any(item => item.Ordinal == 2 && item.Amount == new Money(250m, CurrencyCode.Rsd))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_categorizers_declined_amount_change_reaches_the_echo()
    {
        var categorizer = DefaultCategorizer();
        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Categorization(merchantCanonicalName: "Test Market", unsupportedChange: AppReceipts.UnsupportedChangeKind.Amount));
        // N-5 (Phase 6 re-review): ComposeReceipt now renders Captured (DefaultStore's own default
        // status) as the waiting text, not "Recorded" - a correction's own echo reads the record back
        // as Completed, exactly as ApplyAsync would have already left it by the time EchoAsync runs.
        var store = DefaultStore();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(status: TransactionStatus.Completed));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job("make it 1000")), KeyPresent(), store, categorizer: categorizer, notifier: notifier), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await notifier.Received(1).EditAsync(
            111L, 42, Arg.Is<EchoMessage>(echo => echo.Text.StartsWith("Amounts come from the receipt")), Arg.Any<CancellationToken>());
    }

    // N-8 (Phase 6 re-review): a date request gets its own, distinct warning, decided by the model's
    // unsupported_change answer rather than C# text matching.
    [Fact]
    public async Task A_categorizers_declined_date_change_reaches_the_echo()
    {
        var categorizer = DefaultCategorizer();
        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Categorization(merchantCanonicalName: "Test Market", unsupportedChange: AppReceipts.UnsupportedChangeKind.Date));
        var store = DefaultStore();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(status: TransactionStatus.Completed));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job("that was yesterday")), KeyPresent(), store, categorizer: categorizer, notifier: notifier), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await notifier.Received(1).EditAsync(
            111L, 42, Arg.Is<EchoMessage>(echo => echo.Text.StartsWith("The date comes from the receipt")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_receipt_correction_always_recomputes_OccurredOn_from_the_receipts_own_issued_at()
    {
        // N-8: the date always comes from the receipt (ExtractReceiptWorker's IssuedAt), whatever a
        // correction asked for - categorize_receipt has no date field to answer with either way.
        var categorizer = DefaultCategorizer();
        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Categorization(merchantCanonicalName: "Test Market", unsupportedChange: AppReceipts.UnsupportedChangeKind.Date));
        var store = DefaultStore();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job("that was yesterday")), KeyPresent(), store, categorizer: categorizer), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.OccurredOn == new DateOnly(2026, 9, 25)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_receipt_for_the_transaction_fails_the_job_terminally()
    {
        var jobQueue = QueueWith(Job());
        var receiptStore = Substitute.For<AppReceipts.IReceiptStore>();
        receiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns((AppReceipts.ReceiptView?)null);
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), receiptStore: receiptStore), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task With_a_missing_key_the_worker_stays_idle_and_claims_nothing()
    {
        var jobQueue = QueueWith(Job());
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(false);
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, modelProvider), Time());

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(CategorizationTickResult.Idle);
        await jobQueue.DidNotReceive().ClaimAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // The Copilot finding this guards: the model-key gate ran in front of every CategorizeReceipt
    // claim, so a Copy/Training/Proforma/Advance receipt - which this worker never calls the model
    // for (ReportNotRecordedAsync, above) - stayed at "Categorising..." forever with no key
    // configured, instead of being cancelled. ClaimNonMoneyReceiptAsync lets that drain independently
    // of IModelProvider, while a money receipt (the test above) still claims nothing without a key.
    [Fact]
    public async Task A_non_money_receipt_is_cancelled_even_with_no_model_key_configured()
    {
        var receiptStore = DefaultReceiptStore();
        receiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Receipt(kind: ReceiptKind.Copy));
        var store = DefaultStore();
        var notifier = Substitute.For<IChatNotifier>();
        var recordEditor = Substitute.For<IRecordEditor>();
        var jobQueue = QueueWithNonMoneyJob(Job());
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(false);
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, modelProvider, store, receiptStore: receiptStore, notifier: notifier, recordEditor: recordEditor),
            Time());

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(CategorizationTickResult.Processed);
        await recordEditor.Received(1).CancelAsync(TransactionId, Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(
            111L, 42, Arg.Is<EchoMessage>(echo => echo.Text == "This receipt is a copy — not recorded"), Arg.Any<CancellationToken>());
        await jobQueue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().ClaimAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // The Fable review finding this guards: the non-money claim used to run on every tick, even when
    // the ordinary claim would take the job - an extra UPDATE per idle poll, and a non-money receipt
    // could jump ahead of a money receipt regardless of run_after. With a key configured and no
    // cooldown, the ordinary claim (which covers non-money receipts too, via IsNonMoneyKind inside
    // ProcessClaimedJobAsync) is tried instead, and the non-money claim is never called.
    [Fact]
    public async Task With_a_configured_key_the_ordinary_claim_is_used_and_the_non_money_claim_is_skipped()
    {
        var jobQueue = QueueWith(Job());
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent()), Time());

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(CategorizationTickResult.Processed);
        await jobQueue.Received(1).ClaimAsync(
            WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().ClaimNonMoneyReceiptAsync(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_claimed_job_logs_claim_queueWait_and_job_categorizeReceipt()
    {
        var claimedAt = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var job = Job(createdAt: claimedAt - TimeSpan.FromSeconds(3), claimedAt: claimedAt);
        var store = DefaultStore();
        var logger = new CapturingLogger<ReceiptCategorizationWorker>();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(job), KeyPresent(), store), new FakeTimeProvider(claimedAt), logger);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        var operations = logger.Entries.Select(OperationOf).Where(operation => operation is not null).ToList();
        operations.Should().Contain("db.claimJob");
        operations.Should().Contain("job.queueWait");
        operations.Should().Contain(TimedOperations.JobCategorizeReceipt);

        var queueWait = logger.Entries.Should().ContainSingle(entry => OperationOf(entry) == "job.queueWait").Subject;
        queueWait.Properties["ElapsedMs"].Should().Be(3000L);
    }

    [Fact]
    public async Task An_idle_tick_logs_no_timing_event()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((CategorizationJob?)null);
        var logger = new CapturingLogger<ReceiptCategorizationWorker>();
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent()), Time(), logger);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_transient_model_failure_retries_and_edits_the_echo_with_a_retry_notice()
    {
        var jobQueue = QueueWith(Job());
        var categorizer = Substitute.For<AppReceipts.IReceiptCategorizer>();
        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "categorize_receipt did not answer"));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), categorizer: categorizer, notifier: notifier), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m =>
            m.Text.Contains("Categorising the receipt", StringComparison.Ordinal)
            && m.Text.Contains("the model did not answer", StringComparison.Ordinal)
            && !m.Text.Contains("categorize_receipt did not answer", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_last_transient_attempt_fails_the_transaction_instead_of_sending_a_retry_notice()
    {
        var jobQueue = QueueWith(Job(attemptCount: new CategorizationWorkerOptions().MaxAttempts));
        var categorizer = Substitute.For<AppReceipts.IReceiptCategorizer>();
        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "categorize_receipt did not answer"));
        var store = DefaultStore();
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer, notifier: notifier), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m => m.Text == Echo.Failure.Text), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_retry_notice_names_roughly_when_the_next_attempt_runs_in_the_capture_time_zone()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero));
        var jobQueue = QueueWith(Job());
        var categorizer = Substitute.For<AppReceipts.IReceiptCategorizer>();
        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "categorize_receipt did not answer"));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), categorizer: categorizer, notifier: notifier), time, captureTimeZone: Belgrade);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text.Contains("11:", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_success_after_a_retried_failure_replaces_the_retry_notice_with_the_ordinary_echo()
    {
        var jobQueue = QueueWith(Job());
        var categorizer = Substitute.For<AppReceipts.IReceiptCategorizer>();
        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "categorize_receipt did not answer"));
        var store = DefaultStore();
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer, notifier: notifier), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);
        notifier.ClearReceivedCalls();

        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Categorization(merchantCanonicalName: "Test Market"));
        // EchoAsync reads the record back after ApplyAsync commits it - exactly as ApplyAsync would
        // have already left it by the time EchoAsync runs (the same pattern this file's other
        // EchoAsync-reads-back tests use).
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(status: TransactionStatus.Completed));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text.StartsWith("Recorded", StringComparison.Ordinal) && !m.Text.Contains("retrying", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }
}
