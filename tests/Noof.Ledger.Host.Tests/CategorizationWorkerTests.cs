using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Workers;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Host.Tests;

public class CategorizationWorkerTests
{
    const string WorkerId = "worker-a";
    static readonly Guid TransactionId = Guid.NewGuid();
    static readonly Guid JobId = Guid.NewGuid();
    static readonly CategoryEntry Groceries = new(Guid.NewGuid(), "groceries", "Groceries", "Продукты", null);
    static readonly IProposalMapper Mapper = new ProposalMapper();
    static readonly IMerchantScan Scan = new MerchantScan();
    static readonly IRecordEcho Echo = new RecordEcho();
    static readonly WalletOption MainWallet = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"), "Main Wallet", CurrencyCode.Rsd, [], IsDefaultForCurrency: true);
    static readonly WalletOption CashRsd = new(
        Guid.Parse("33333333-3333-3333-3333-333333333333"), "Cash", CurrencyCode.Rsd, ["налик", "наличка"], IsDefaultForCurrency: false);
    static readonly WalletOption WiseEur = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "Wise EUR", CurrencyCode.Eur, ["wise", "вайз"], IsDefaultForCurrency: true);

    static IServiceScopeFactory ScopeFactoryFor(
        IJobQueue jobQueue, IModelProvider modelProvider, ICategorizationStore? store = null,
        ICategoryCatalog? categoryCatalog = null, IMerchantDirectory? merchantDirectory = null,
        ICategorizer? categorizer = null, IChatNotifier? notifier = null, IWalletDirectory? walletDirectory = null)
    {
        // The fallback substitutes are resolved into locals BEFORE any .Returns() call below.
        // Calling Substitute.For<T>() (or a helper that itself configures a substitute, like
        // DefaultCategoryCatalog) inline as a .Returns() argument creates/configures a second
        // substitute while the first substitute's "last call" is still pending on NSubstitute's
        // ThreadLocalContext - exactly the "mySub.SomeMethod().Returns(ConfigOtherSub())"
        // anti-pattern NSubstitute's own CouldNotSetReturnDueToNoLastCallException message warns
        // against - and clobbers it, so the outer .Returns() throws that exception at runtime.
        var resolvedStore = store ?? Substitute.For<ICategorizationStore>();
        var resolvedCategoryCatalog = categoryCatalog ?? DefaultCategoryCatalog();
        var resolvedMerchantDirectory = merchantDirectory ?? DefaultMerchantDirectory();
        var resolvedCategorizer = categorizer ?? Substitute.For<ICategorizer>();
        var resolvedNotifier = notifier ?? Substitute.For<IChatNotifier>();
        var resolvedWalletDirectory = walletDirectory ?? WalletDirectoryOf(MainWallet, CashRsd, WiseEur);

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IJobQueue)).Returns(jobQueue);
        provider.GetService(typeof(IModelProvider)).Returns(modelProvider);
        provider.GetService(typeof(ICategorizationStore)).Returns(resolvedStore);
        provider.GetService(typeof(ICategoryCatalog)).Returns(resolvedCategoryCatalog);
        provider.GetService(typeof(IMerchantDirectory)).Returns(resolvedMerchantDirectory);
        provider.GetService(typeof(ICategorizer)).Returns(resolvedCategorizer);
        provider.GetService(typeof(IChatNotifier)).Returns(resolvedNotifier);
        provider.GetService(typeof(IWalletDirectory)).Returns(resolvedWalletDirectory);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);

        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    static ICategoryCatalog DefaultCategoryCatalog()
    {
        var catalog = Substitute.For<ICategoryCatalog>();
        catalog.ActiveAsync(Arg.Any<CancellationToken>()).Returns(new List<CategoryEntry> { Groceries });
        return catalog;
    }

    static IMerchantDirectory DefaultMerchantDirectory()
    {
        var directory = Substitute.For<IMerchantDirectory>();
        directory.AliasesAsync(Arg.Any<CancellationToken>()).Returns(new List<MerchantAliasEntry>());
        return directory;
    }

    static IWalletDirectory WalletDirectoryOf(params WalletOption[] wallets)
    {
        var directory = Substitute.For<IWalletDirectory>();
        IReadOnlyList<WalletOption> active = wallets;
        directory.ActiveAsync(Arg.Any<CancellationToken>()).Returns(active);
        return directory;
    }

    static IModelProvider KeyPresent() => ModelProvider(configured: true);

    static IModelProvider KeyMissing() => ModelProvider(configured: false);

    static IModelProvider ModelProvider(bool configured)
    {
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(configured);
        return modelProvider;
    }

    static CategorizationJob Job(
        int attemptCount = 1, JobKind kind = JobKind.Categorize, string? instruction = null, DateOnly? instructionDay = null) => new()
    {
        Id = JobId,
        TransactionId = TransactionId,
        Status = JobStatus.Claimed,
        AttemptCount = attemptCount,
        Kind = kind,
        Instruction = instruction,
        InstructionDay = instructionDay,
        RunAfter = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    static readonly DateOnly SentOn = new(2026, 9, 21);

    static CategorizationSubject Subject(
        int? botMessageId = 42, string rawText = "Bread 250 RSD", DateOnly? occurredOn = null,
        TransactionStatus status = TransactionStatus.Captured, IReadOnlyList<RecordedLine>? lines = null, Guid? walletId = null) =>
        new(TransactionId, rawText, 111L, botMessageId, "Cash", status, SentOn, occurredOn ?? SentOn, lines ?? [], WalletId: walletId);

    static CategorizationProposal OneGroceryLine(decimal amount = 250m, string currency = "RSD") =>
        new([new ProposedLineItem("Bread", amount, currency, "groceries", null, null)]);

    static CategorizationWorker CreateWorker(
        IServiceScopeFactory scopeFactory, FakeTimeProvider time, CategorizationWorkerOptions? options = null,
        IDatabaseGate? gate = null, CapturingLogger<CategorizationWorker>? logger = null) =>
        new(scopeFactory, time, options ?? new CategorizationWorkerOptions(), WorkerId,
            Mapper, Scan, Echo, gate ?? ReadyGate(), logger ?? new CapturingLogger<CategorizationWorker>());

    static IDatabaseGate ReadyGate()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return gate;
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

    [Fact]
    public async Task The_model_is_told_the_day_the_message_was_sent_not_the_day_the_job_runs()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
        // The job runs the morning after the message was sent - the offline queue (D2).
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero));
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer), time);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await categorizer.Received(1).ProposeAsync(
            Arg.Is<CategorizationRequest>(request => request.Today == SentOn), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_proposal_that_names_no_day_is_recorded_on_the_send_day()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(occurredOn: new DateOnly(2026, 9, 1)));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.OccurredOn == SentOn), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_proposal_that_names_a_day_is_recorded_on_that_day()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneGroceryLine() with { OccurredOn = "2026-09-20" });
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.OccurredOn == new DateOnly(2026, 9, 20)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_day_that_does_not_parse_fails_the_job_terminally()
    {
        var jobQueue = QueueWith(Job());
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneGroceryLine() with { OccurredOn = "вчера" });
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).FailAsync(JobId, WorkerId, Arg.Is<string>(error => error.Contains("occurred_on")), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ApplyAsync(Arg.Any<Guid>(), Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>());
    }

    static readonly RecordedLine StoredBread = new("Bread", new Money(250m, CurrencyCode.Rsd), "groceries", "Groceries", null);

    [Fact]
    public async Task A_correction_hands_the_model_the_current_record_and_the_instruction()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 20), lines: [StoredBread]));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine(1500m));
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job(kind: JobKind.Correct, instruction: "нет, 1500")), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await categorizer.Received(1).ProposeAsync(
            Arg.Is<CategorizationRequest>(request =>
                request.Correction != null
                && request.Correction.Instruction == "нет, 1500"
                && request.Correction.CurrentOccurredOn == new DateOnly(2026, 9, 20)
                && request.Correction.CurrentLines.Single() == StoredBread),
            Arg.Any<CancellationToken>());
        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.Kind == JobKind.Correct && outcome.Instruction == "нет, 1500"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_correction_that_names_no_day_keeps_the_records_day()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 20), lines: [StoredBread]));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine(1500m));
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job(kind: JobKind.Correct, instruction: "нет, 1500")), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.OccurredOn == new DateOnly(2026, 9, 20)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_correction_sent_on_a_later_day_tells_the_model_that_day()
    {
        // The defect this guards against: a purchase captured Monday (SentOn) corrected on
        // Wednesday with "это было позавчера" must resolve позавчера from Wednesday, not from the
        // original Monday capture - otherwise every relative word in a correction is off by
        // however long the correction waited (docs/OPEN-QUESTIONS.md P2-2).
        var instructionDay = new DateOnly(2026, 9, 23);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 20), lines: [StoredBread]));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine(1500m));
        var worker = CreateWorker(
            ScopeFactoryFor(
                QueueWith(Job(kind: JobKind.Correct, instruction: "нет, 1500", instructionDay: instructionDay)),
                KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await categorizer.Received(1).ProposeAsync(
            Arg.Is<CategorizationRequest>(request => request.Today == instructionDay), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reinterpretation_reads_from_scratch_and_falls_back_to_the_send_day()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 20), lines: [StoredBread]));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job(kind: JobKind.Reinterpret)), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await categorizer.Received(1).ProposeAsync(Arg.Is<CategorizationRequest>(request => request.Correction == null), Arg.Any<CancellationToken>());
        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.OccurredOn == SentOn && outcome.Kind == JobKind.Reinterpret),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_correction_leaves_the_record_as_it_was()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed, lines: [StoredBread]));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "bad request"));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job(kind: JobKind.Correct, instruction: "нет, 1500")), KeyPresent(), store,
                categorizer: categorizer, notifier: notifier),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(echo => echo.Text.StartsWith("Could not apply that correction") && echo.Text.Contains("250.00 RSD")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_model_is_offered_the_active_wallets()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await categorizer.Received(1).ProposeAsync(
            Arg.Is<CategorizationRequest>(request =>
                request.Wallets != null && request.Wallets.SequenceEqual(new[] { MainWallet, CashRsd, WiseEur })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_spending_that_names_no_wallet_goes_to_the_default_wallet_of_its_currency()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine(3.50m, "EUR"));
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome =>
                outcome.TransactionKind == TransactionKind.Expense
                && outcome.WalletId == WiseEur.Id
                && outcome.StatedBalance == null
                && outcome.Items.Single().Amount == new Money(3.50m, CurrencyCode.Eur)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_income_is_recorded_as_income_in_the_wallet_the_model_named()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(rawText: "пришла зарплата 2000 евро на Wise"));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneGroceryLine(2000m, "EUR") with { Kind = ProposedKind.Income, WalletId = WiseEur.Id });
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome =>
                outcome.TransactionKind == TransactionKind.Income && outcome.WalletId == WiseEur.Id && outcome.Items.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_balance_statement_reaches_the_store_with_its_stated_balance_and_no_lines()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(rawText: "на главном 45 тысяч"));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CategorizationProposal([], Kind: ProposedKind.Balance, BalanceAmount: 45000m));
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome =>
                outcome.TransactionKind == TransactionKind.BalanceCheck
                && outcome.WalletId == MainWallet.Id
                && outcome.StatedBalance == new Money(45000m, CurrencyCode.Rsd)
                && outcome.Items.Count == 0
                && outcome.Kind == JobKind.Categorize),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Categorized_is_logged_with_kind_wallet_and_a_computed_summary()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine(250m, "RSD"));
        var logger = new CapturingLogger<CategorizationWorker>();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow), logger: logger);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.CategorizedEventId).Subject;
        entry.Stage.Should().Be(TransactionStages.Categorized);
        entry.Properties["Kind"].Should().Be(TransactionKind.Expense);
        entry.Properties["WalletId"].Should().Be(MainWallet.Id);
        entry.Properties["Summary"].Should().Be("250 RSD groceries");
        entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(TransactionId);
    }

    [Fact]
    public async Task Categorized_summarises_a_balance_statement_by_its_stated_amount()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(rawText: "на главном 45 тысяч"));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CategorizationProposal([], Kind: ProposedKind.Balance, BalanceAmount: 45000m));
        var logger = new CapturingLogger<CategorizationWorker>();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow), logger: logger);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.CategorizedEventId).Subject;
        entry.Properties["Kind"].Should().Be(TransactionKind.BalanceCheck);
        entry.Properties["Summary"].Should().Be("balance 45000 RSD");
    }

    [Fact]
    public async Task Persisted_is_logged_after_the_outcome_is_applied()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
        var logger = new CapturingLogger<CategorizationWorker>();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow), logger: logger);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.PersistedEventId).Subject;
        entry.Stage.Should().Be(TransactionStages.Persisted);
        entry.Properties["Kind"].Should().Be(TransactionKind.Expense);
        entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(TransactionId);
    }

    [Fact]
    public async Task Replied_is_logged_after_the_echo_edit_succeeds()
    {
        var store = StoreThatRemembersWhatItApplies(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
        var logger = new CapturingLogger<CategorizationWorker>();
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow), logger: logger);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.RepliedEventId).Subject;
        entry.Stage.Should().Be(TransactionStages.Replied);
        entry.Properties["BotMessageId"].Should().Be(42);
        entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(TransactionId);
    }

    [Fact]
    public async Task A_terminal_failure_logs_StageFailed_for_Categorized()
    {
        var jobQueue = QueueWith(Job());
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "no credit left on this key"));
        var logger = new CapturingLogger<CategorizationWorker>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow), logger: logger);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.StageFailedEventId).Subject;
        entry.Properties["FailedStage"].Should().Be(TransactionStages.Categorized);
        entry.Exception!.Message.Should().Be("no credit left on this key");
        entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(TransactionId);
    }

    [Fact]
    public async Task A_transient_failure_logs_StageFailed_for_Categorized()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "rate limited"));
        var logger = new CapturingLogger<CategorizationWorker>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow), logger: logger);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.StageFailedEventId).Subject;
        entry.Properties["FailedStage"].Should().Be(TransactionStages.Categorized);
    }

    [Fact]
    public async Task A_missing_transaction_logs_StageFailed_for_Categorized()
    {
        var jobQueue = QueueWith(Job());
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns((CategorizationSubject?)null);
        var logger = new CapturingLogger<CategorizationWorker>();
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store), new FakeTimeProvider(DateTimeOffset.UtcNow), logger: logger);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.StageFailedEventId).Subject;
        entry.Properties["FailedStage"].Should().Be(TransactionStages.Categorized);
    }

    [Fact]
    public async Task A_wallet_the_model_was_not_offered_fails_the_job_terminally()
    {
        var stranger = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var jobQueue = QueueWith(Job());
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneGroceryLine() with { WalletId = stranger });
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).FailAsync(JobId, WorkerId, $"wallet {stranger} was not offered", Arg.Any<CancellationToken>());
        await store.DidNotReceive().ApplyAsync(Arg.Any<Guid>(), Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_correction_that_names_no_wallet_keeps_the_record_in_its_wallet()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed, lines: [StoredBread], walletId: CashRsd.Id));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine(1500m));
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job(kind: JobKind.Correct, instruction: "нет, 1500")), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == CashRsd.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_correction_that_names_a_wallet_moves_the_record_there()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed, lines: [StoredBread], walletId: MainWallet.Id));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneGroceryLine() with { WalletId = CashRsd.Id });
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job(kind: JobKind.Correct, instruction: "это было с налички")), KeyPresent(), store,
                categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == CashRsd.Id && outcome.Kind == JobKind.Correct),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_correction_whose_wallet_was_archived_falls_back_to_the_default()
    {
        var archived = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed, lines: [StoredBread], walletId: archived));
        CategorizationOutcome? applied = null;
        store.ApplyAsync(TransactionId, Arg.Do<CategorizationOutcome>(outcome => applied = outcome), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine(1500m, "RSD"));
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job(kind: JobKind.Correct, instruction: "нет, 1500")), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        (applied?.WalletId).Should().Be(MainWallet.Id,
            "the line is stated in RSD and Main Wallet is the RSD default; the archived wallet is not offered, so the correction cannot keep it");
    }

    [Fact]
    public async Task A_message_with_no_default_wallet_fails_terminally_naming_the_cause()
    {
        var jobQueue = QueueWith(Job());
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(rawText: "кофе 250"));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine(250m, "RSD"));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer, notifier: notifier,
                walletDirectory: WalletDirectoryOf(CashRsd)),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).FailAsync(JobId, WorkerId, "no wallet to record into", Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().RetryAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ApplyAsync(Arg.Any<Guid>(), Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>());
        await store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(111L, 42, Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reinterpretation_that_names_no_wallet_resolves_the_wallet_afresh()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed, lines: [StoredBread], walletId: CashRsd.Id));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job(kind: JobKind.Reinterpret)), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == MainWallet.Id),
            Arg.Any<CancellationToken>());
    }

    // Behaves like the real store for the one property the echo depends on: after ApplyAsync, reading the
    // record back returns what was applied.
    static ICategorizationStore StoreThatRemembersWhatItApplies(CategorizationSubject before)
    {
        var store = Substitute.For<ICategorizationStore>();
        var current = before;
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(_ => current);
        store.When(s => s.ApplyAsync(TransactionId, Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var outcome = call.Arg<CategorizationOutcome>();
                current = current with
                {
                    Status = TransactionStatus.Completed,
                    OccurredOn = outcome.OccurredOn,
                    Lines = [.. outcome.Items.Select(item => new RecordedLine(item.Description, item.Amount, Groceries.Slug, Groceries.NameEn, null))],
                };
            });
        return store;
    }

    [Fact]
    public async Task Idle_when_the_model_provider_is_not_configured()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyMissing()), new FakeTimeProvider(DateTimeOffset.UtcNow));

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(CategorizationTickResult.Idle);
    }

    [Fact]
    public async Task A_missing_key_does_not_burn_the_attempt_budget()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyMissing()), new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        // ClaimAsync is the only IJobQueue member that increments attempt_count (see EfJobQueue). Proving
        // it was never called is proof the count is unchanged, without needing a real database here.
        await jobQueue.DidNotReceive().ClaimAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Releases_expired_leases_even_while_the_key_is_missing()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyMissing()), new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).ReleaseExpiredLeasesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Idle_when_nothing_is_pending_to_claim()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns((CategorizationJob?)null);
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent()), new FakeTimeProvider(DateTimeOffset.UtcNow));

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(CategorizationTickResult.Idle);
    }

    [Fact]
    public async Task A_terminal_model_failure_calls_FailAsync_not_RetryAsync()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "no credit left on this key"));
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer), new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).FailAsync(JobId, WorkerId, "no credit left on this key", Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().RetryAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_transient_model_failure_calls_RetryAsync_with_the_documented_backoff()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 1));
        jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "rate limited"));
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer), time);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).RetryAsync(JobId, WorkerId, now + TimeSpan.FromSeconds(30), "rate limited", Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().FailAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_answer_that_does_not_map_is_terminal_and_never_retried()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        // Amount is a JSON number now, so the model cannot hand back an unparsable amount - the
        // schema rules that out. "GBP" is not a supported currency, and ProposalMapper.TryMap refuses
        // it, which is real production logic, not a stub.
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneGroceryLine(currency: "GBP"));
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer), new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().RetryAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_proposal_with_no_items_completes_the_job_honestly_instead_of_recording_a_loan_as_spending()
    {
        // A message can still come back with zero items (the prompt no longer asks for this on a
        // loan specifically - see CategorizationPromptTests - but a purely conversational message
        // with a figure and nothing to record against it can). A model that answers with zero
        // items must succeed the job with zero line items, not be forced to invent one and not be
        // treated as a failure either - both would misrepresent what actually happened.
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = StoreThatRemembersWhatItApplies(Subject(rawText: "заняла у Маши 5000 рсд"));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CategorizationProposal([]));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer, notifier: notifier),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(CategorizationTickResult.Processed);
        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Is<CategorizationOutcome>(outcome => outcome.Items.Count == 0), Arg.Any<CancellationToken>());
        await store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await jobQueue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().FailAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().RetryAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(
            111L, 42,
            Arg.Is<EchoMessage>(echo => echo.Text.Contains("nothing recorded")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotOwned_from_SucceedAsync_does_not_trigger_a_fallback_retry_or_fail()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.NotOwned);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer, notifier: notifier),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(CategorizationTickResult.Processed);
        await store.Received(1).ApplyAsync(TransactionId, Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(111L, 42, Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().RetryAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().FailAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotOwned_from_RetryAsync_does_not_mark_the_transaction_failed_or_edit_telegram()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 8));
        jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.NotOwned);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "rate limited"));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer, notifier: notifier),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await notifier.DidNotReceive().EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotOwned_from_FailAsync_does_not_mark_the_transaction_failed_or_edit_telegram()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.NotOwned);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "bad key"));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer, notifier: notifier),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await notifier.DidNotReceive().EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Every_dependency_throwing_is_reported_as_failed_without_throwing_or_stopping_the_host()
    {
        var worker = CreateWorker(new ThrowingScopeFactory(), new FakeTimeProvider(DateTimeOffset.UtcNow));

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(CategorizationTickResult.Failed);
    }

    [Fact]
    public async Task An_unexpected_exception_mid_job_is_treated_as_transient_and_retried_with_backoff()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 3));
        jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("db blip"));
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store), time);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).RetryAsync(JobId, WorkerId, now + TimeSpan.FromMinutes(2), "db blip", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_Telegram_edit_failure_still_succeeds_the_job()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
        var notifier = Substitute.For<IChatNotifier>();
        notifier.EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("message was deleted"));
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer, notifier: notifier),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(CategorizationTickResult.Processed);
        await jobQueue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_null_BotMessageId_skips_the_edit_but_still_succeeds_the_job()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(botMessageId: null));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer, notifier: notifier),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await notifier.DidNotReceive().EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
        await jobQueue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_known_alias_is_used_without_calling_the_model_to_canonicalize_it()
    {
        var merchantId = Guid.NewGuid();
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(rawText: "Starbucks coffee 250 RSD"));
        var merchantDirectory = Substitute.For<IMerchantDirectory>();
        merchantDirectory.AliasesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<MerchantAliasEntry> { new("STARBUCKS", merchantId, "Starbucks") });
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CategorizationProposal([new ProposedLineItem("Coffee", 250m, "RSD", "groceries", null, "Starbucks")]));
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, merchantDirectory: merchantDirectory, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await categorizer.DidNotReceive().CanonicalizeMerchantAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<MerchantOption>>(), Arg.Any<CancellationToken>());
        await merchantDirectory.DidNotReceive().LinkAliasAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await store.Received(1).ApplyAsync(
            TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.Items.Single().MerchantId == merchantId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_merchant_is_canonicalized_and_linked_exactly_once()
    {
        var linkedId = Guid.NewGuid();
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(rawText: "Starbucks coffee 250 RSD"));
        var merchantDirectory = Substitute.For<IMerchantDirectory>();
        merchantDirectory.AliasesAsync(Arg.Any<CancellationToken>()).Returns(new List<MerchantAliasEntry>());
        merchantDirectory.MerchantsAsync(Arg.Any<CancellationToken>()).Returns(new List<MerchantOption>());
        merchantDirectory.LinkAliasAsync("STARBUCKS", "Starbucks", Arg.Any<CancellationToken>()).Returns(linkedId);
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CategorizationProposal([new ProposedLineItem("Coffee", 250m, "RSD", "groceries", null, "Starbucks")]));
        categorizer.CanonicalizeMerchantAsync("Starbucks", Arg.Any<IReadOnlyList<MerchantOption>>(), Arg.Any<CancellationToken>())
            .Returns("Starbucks");
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, merchantDirectory: merchantDirectory, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await categorizer.Received(1).CanonicalizeMerchantAsync(
            "Starbucks", Arg.Any<IReadOnlyList<MerchantOption>>(), Arg.Any<CancellationToken>());
        await merchantDirectory.Received(1).LinkAliasAsync("STARBUCKS", "Starbucks", Arg.Any<CancellationToken>());
        await store.Received(1).ApplyAsync(
            TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.Items.Single().MerchantId == linkedId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_subject_fails_the_job_without_ever_calling_ApplyAsync()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns((CategorizationSubject?)null);
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store), new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ApplyAsync(
            Arg.Any<Guid>(), Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>());
        await store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Last_attempt_exhausted_by_a_transient_failure_marks_the_transaction_failed_and_notifies()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 8));
        jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "rate limited"));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer, notifier: notifier),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(111L, 42, Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_retry_with_attempts_remaining_does_not_notify_or_mark_the_transaction_failed()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 1));
        jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "rate limited"));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer, notifier: notifier),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await notifier.DidNotReceive().EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_transient_model_failure_is_retried_and_the_captured_transaction_survives_until_it_recovers()
    {
        // Stands in for "pull the network cable": ModelCallException(Transient, ...) is exactly
        // what ICategorizer is contractually required to throw whether the cable is out
        // or the API is briefly unreachable - from the worker's point of view they are the same
        // "the call did not complete."
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 1));
        jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = StoreThatRemembersWhatItApplies(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "simulated network outage"));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer, notifier: notifier),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        // Still saves: nothing was marked failed and nothing was applied to the transaction - the
        // raw capture alone survives - and the job was retried, not abandoned.
        await store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ApplyAsync(
            Arg.Any<Guid>(), Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>());
        await notifier.DidNotReceive().EditAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
        await jobQueue.Received(1).RetryAsync(
            JobId, WorkerId, Arg.Any<DateTimeOffset>(), "simulated network outage", Arg.Any<CancellationToken>());

        // Reconnect: same job, same worker, the model now answers.
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneGroceryLine());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        // It categorizes itself, and the Telegram message updates - faked, per the acceptance
        // table's decision: EditAsync being called with the computed figures is the automated
        // half of that proof.
        await store.Received(1).ApplyAsync(
            TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.Items.Single().Description == "Bread"),
            Arg.Any<CancellationToken>());
        await jobQueue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(
            111L, 42,
            Arg.Is<EchoMessage>(echo => echo.Text.Contains("250.00 RSD") && echo.Actions.Contains(RecordAction.Cancel)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_SucceedAsync_failure_after_the_write_already_committed_never_marks_the_transaction_failed()
    {
        // Reproduces the reviewer's scenario: ApplyAsync has already committed the line items and
        // flipped the transaction to Completed. If SucceedAsync (or the chat notifier) throws after
        // that - here on the job's last attempt - the old catch-all treated it as an ordinary
        // transient failure and, on the last attempt, called MarkFailedAsync, flipping a Completed
        // transaction back to Failed while its line items stayed in the table. The dashboard would
        // then say nothing was recorded for that message while the month totals still counted it.
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 8));
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db blip right after commit"));
        jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer, notifier: notifier),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(CategorizationTickResult.Processed);
        await store.Received(1).ApplyAsync(
            TransactionId, Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().FailAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().RetryAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_account_level_failure_is_retried_not_terminally_failed()
    {
        // 401/402/403 are properties of the account (a bad key, no credit, a revoked permission),
        // not of this job's request - one occurrence must not permanently fail the job it happened
        // to land on.
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 1));
        jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "Anthropic call failed with status 401.").AsAccountLevel());
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer), new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).RetryAsync(
            JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().FailAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_account_level_failure_pauses_claiming_new_work_for_a_cooldown()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 1));
        jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "Anthropic call failed with status 401.").AsAccountLevel());
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer), time);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);
        jobQueue.ClearReceivedCalls();

        // A second pending job exists, but the whole backlog must not be burned in seconds behind
        // one bad key - the worker must not even attempt to claim while the cooldown is in effect.
        var secondTickResult = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        secondTickResult.Should().Be(CategorizationTickResult.Idle);
        await jobQueue.DidNotReceive().ClaimAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await jobQueue.Received(1).ReleaseExpiredLeasesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Claiming_resumes_once_the_account_cooldown_elapses()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 1));
        jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "Anthropic call failed with status 401.").AsAccountLevel());
        var options = new CategorizationWorkerOptions { AccountCooldown = TimeSpan.FromMinutes(1) };
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer), time, options);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        jobQueue.ClearReceivedCalls();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns((CategorizationJob?)null);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_echo_is_rendered_from_the_stored_record_not_from_the_proposal()
    {
        // The store answers with 300 whatever was applied: if the echo said 250 it would be quoting the
        // model, and D4 says it must show what the database holds.
        var store = Substitute.For<ICategorizationStore>();
        var stored = Subject(status: TransactionStatus.Completed,
            lines: [new RecordedLine("Bread", new Money(300m, CurrencyCode.Rsd), "groceries", "Groceries", null)]);
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(), stored);
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine(250m));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer, notifier: notifier),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(echo => echo.Text.Contains("300.00 RSD") && !echo.Text.Contains("250")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_first_reading_shows_the_failure_echo()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "bad request"));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer, notifier: notifier),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await notifier.Received(1).EditAsync(111L, 42, Echo.Failure, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void CreateWorkerId_fits_the_128_character_claimed_by_column()
    {
        var id = CategorizationWorker.CreateWorkerId();

        id.Length.Should().BeLessThanOrEqualTo(128);
    }

    [Fact]
    public void CreateWorkerId_is_different_on_every_call()
    {
        var first = CategorizationWorker.CreateWorkerId();
        var second = CategorizationWorker.CreateWorkerId();

        first.Should().NotBe(second);
    }

    [Fact]
    public async Task Claims_every_kind_but_transcription()
    {
        var jobQueue = QueueWith(Job());
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store), new FakeTimeProvider());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).ClaimAsync(
            WorkerId,
            Arg.Is<IReadOnlyCollection<JobKind>>(kinds =>
                kinds.Order().SequenceEqual(new[] { JobKind.Categorize, JobKind.Correct, JobKind.Reinterpret })),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_loop_waits_for_the_database_gate_before_its_first_claim()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        var gateSource = new TaskCompletionSource();
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(gateSource.Task);
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent()), new FakeTimeProvider(), gate: gate);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await jobQueue.DidNotReceive().ClaimAsync(
            WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());

        gateSource.SetResult();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await jobQueue.Received().ReleaseExpiredLeasesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        await worker.StopAsync(TestContext.Current.CancellationToken);
    }
}
