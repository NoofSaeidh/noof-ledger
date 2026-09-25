using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
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

    static CategorizationJob Job() => new()
    {
        Id = JobId,
        TransactionId = TransactionId,
        Status = JobStatus.Claimed,
        AttemptCount = 1,
        Kind = JobKind.CategorizeReceipt,
        RunAfter = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    static readonly DateOnly SentOn = new(2026, 9, 25);

    static CategorizationSubject Subject(int? botMessageId = 42, TransactionStatus status = TransactionStatus.Captured) =>
        new(TransactionId, string.Empty, 111L, botMessageId, "Main RSD", status, SentOn, SentOn, [],
            CaptureKind.Photo, TransactionKind.Expense, CurrencyCode.Rsd, WalletId: RsdDefault.Id);

    static AppReceipts.ReceiptView Receipt(
        AppReceipts.ReceiptKind kind = AppReceipts.ReceiptKind.Sale,
        string? sellerTaxId = "SYN-100000001",
        string? sellerName = "Test Market",
        AppReceipts.PaymentMethod? paymentMethod = AppReceipts.PaymentMethod.Card,
        decimal total = 373.4567m,
        IReadOnlyList<AppReceipts.ReceiptLineView>? lines = null) =>
        new(ReceiptId, AppReceipts.ReceiptSource.FiscalQr, sellerTaxId, sellerName, "1 Test Street", null, "SYN-1",
            new DateTimeOffset(2026, 9, 25, 9, 30, 0, TimeSpan.Zero), total, CurrencyCode.Rsd, kind, paymentMethod, total, "https://suf.purs.gov.rs/v/?vl=synthetic",
            lines ?? [
                new AppReceipts.ReceiptLineView(Guid.NewGuid(), 1, "Bread", 1m, "kom", 123.4567m, 123.4567m, null),
                new AppReceipts.ReceiptLineView(Guid.NewGuid(), 2, "Milk", 2m, "kom", 125m, 250m, null),
            ]);

    static AppReceipts.ReceiptCategorization Categorization(
        Guid? walletId = null, string? merchantCanonicalName = null, IReadOnlyList<AppReceipts.ReceiptLineCategory>? lines = null) =>
        new(lines ?? [new AppReceipts.ReceiptLineCategory(1, "groceries"), new AppReceipts.ReceiptLineCategory(2, "groceries")],
            merchantCanonicalName, walletId);

    static IServiceScopeFactory ScopeFactoryFor(
        IJobQueue jobQueue, IModelProvider modelProvider, ICategorizationStore? store = null,
        AppReceipts.IReceiptStore? receiptStore = null, ICategoryCatalog? categoryCatalog = null,
        IMerchantDirectory? merchantDirectory = null, AppReceipts.IReceiptCategorizer? categorizer = null,
        IChatNotifier? notifier = null, IWalletDirectory? walletDirectory = null)
    {
        var resolvedStore = store ?? DefaultStore();
        var resolvedReceiptStore = receiptStore ?? DefaultReceiptStore();
        var resolvedCategoryCatalog = categoryCatalog ?? DefaultCategoryCatalog();
        var resolvedMerchantDirectory = merchantDirectory ?? DefaultMerchantDirectory();
        var resolvedCategorizer = categorizer ?? DefaultCategorizer();
        var resolvedNotifier = notifier ?? Substitute.For<IChatNotifier>();
        var resolvedWalletDirectory = walletDirectory ?? DefaultWalletDirectory();

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
        directory.DefaultForPaymentAsync(Arg.Any<AppReceipts.PaymentMethod>(), Arg.Any<CancellationToken>()).Returns((Guid?)null);
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

    static IDatabaseGate ReadyGate()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return gate;
    }

    static ReceiptCategorizationWorker CreateWorker(IServiceScopeFactory scopeFactory, FakeTimeProvider time) =>
        new(scopeFactory, time, new CategorizationWorkerOptions(), WorkerId, Echo, Utc, ReadyGate(),
            new CapturingLogger<ReceiptCategorizationWorker>());

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
        walletDirectory.DefaultForPaymentAsync(AppReceipts.PaymentMethod.Card, Arg.Any<CancellationToken>()).Returns(CashWallet.Id);
        var store = DefaultStore();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer, walletDirectory: walletDirectory), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == NamedInCaption.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_caption_wallet_the_directory_no_longer_offers_falls_through_to_the_payment_default()
    {
        var categorizer = DefaultCategorizer();
        categorizer.CategorizeAsync(Arg.Any<AppReceipts.ReceiptCategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Categorization(walletId: Guid.NewGuid(), merchantCanonicalName: "Test Market"));
        var walletDirectory = DefaultWalletDirectory();
        walletDirectory.DefaultForPaymentAsync(AppReceipts.PaymentMethod.Card, Arg.Any<CancellationToken>()).Returns(CashWallet.Id);
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
        walletDirectory.DefaultForPaymentAsync(AppReceipts.PaymentMethod.Card, Arg.Any<CancellationToken>()).Returns(CashWallet.Id);
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
        walletDirectory.DefaultForPaymentAsync(AppReceipts.PaymentMethod.Cash, Arg.Any<CancellationToken>()).Returns(CashWallet.Id);
        var receiptStore = DefaultReceiptStore();
        receiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Receipt(paymentMethod: AppReceipts.PaymentMethod.Cash));
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
            .Returns(Receipt(paymentMethod: AppReceipts.PaymentMethod.Transfer));
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
        receiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Receipt(kind: AppReceipts.ReceiptKind.Refund));
        var store = DefaultStore();
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, receiptStore: receiptStore), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.TransactionKind == TransactionKind.Income),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(AppReceipts.ReceiptKind.Copy, "copy")]
    [InlineData(AppReceipts.ReceiptKind.Training, "training")]
    [InlineData(AppReceipts.ReceiptKind.Proforma, "proforma")]
    [InlineData(AppReceipts.ReceiptKind.Advance, "advance")]
    public async Task A_non_money_receipt_kind_is_not_posted_and_the_transaction_is_marked_failed(
        AppReceipts.ReceiptKind kind, string word)
    {
        var receiptStore = DefaultReceiptStore();
        receiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Receipt(kind: kind));
        var store = DefaultStore();
        var notifier = Substitute.For<IChatNotifier>();
        var jobQueue = QueueWith(Job());
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, receiptStore: receiptStore, notifier: notifier), Time());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await store.DidNotReceive().ApplyAsync(Arg.Any<Guid>(), Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(
            111L, 42, Arg.Is<EchoMessage>(echo => echo.Text == $"This receipt is a {word} — not recorded"), Arg.Any<CancellationToken>());
        await jobQueue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
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
}
