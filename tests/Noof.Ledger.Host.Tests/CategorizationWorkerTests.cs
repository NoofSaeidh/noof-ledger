using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

public class CategorizationWorkerTests
{
    const string WorkerId = "worker-a";
    static readonly Guid TransactionId = Guid.NewGuid();
    static readonly Guid JobId = Guid.NewGuid();
    static readonly CategoryEntry Groceries = new(Guid.NewGuid(), "groceries", "Groceries", "Продукты", null);

    static IServiceScopeFactory ScopeFactoryFor(
        IJobQueue jobQueue, ISecretStore secretStore, ICategorizationStore? store = null,
        ICategoryCatalog? categoryCatalog = null, IMerchantDirectory? merchantDirectory = null,
        ICategorizer? categorizer = null, IChatNotifier? notifier = null)
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

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IJobQueue)).Returns(jobQueue);
        provider.GetService(typeof(ISecretStore)).Returns(secretStore);
        provider.GetService(typeof(ICategorizationStore)).Returns(resolvedStore);
        provider.GetService(typeof(ICategoryCatalog)).Returns(resolvedCategoryCatalog);
        provider.GetService(typeof(IMerchantDirectory)).Returns(resolvedMerchantDirectory);
        provider.GetService(typeof(ICategorizer)).Returns(resolvedCategorizer);
        provider.GetService(typeof(IChatNotifier)).Returns(resolvedNotifier);

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

    static ISecretStore KeyPresent()
    {
        var store = Substitute.For<ISecretStore>();
        store.GetStatusAsync(SecretKeys.AnthropicApiKey, Arg.Any<CancellationToken>())
            .Returns(new SecretStatus(SecretState.Present, DateTimeOffset.UtcNow));
        return store;
    }

    static ISecretStore KeyMissing()
    {
        var store = Substitute.For<ISecretStore>();
        store.GetStatusAsync(SecretKeys.AnthropicApiKey, Arg.Any<CancellationToken>())
            .Returns(new SecretStatus(SecretState.Missing, null));
        return store;
    }

    static CategorizationJob Job(int attemptCount = 1) => new()
    {
        Id = JobId,
        TransactionId = TransactionId,
        Status = JobStatus.Claimed,
        AttemptCount = attemptCount,
        RunAfter = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    static CategorizationSubject Subject(int? botMessageId = 42, string rawText = "Bread 250 RSD") =>
        new(TransactionId, rawText, 111L, botMessageId, "Cash");

    static CategorizationProposal OneGroceryLine(string quote = "250", string currency = "RSD") =>
        new([new ProposedLineItem("Bread", quote, currency, "groceries", null, null)]);

    static CategorizationWorker CreateWorker(
        IServiceScopeFactory scopeFactory, FakeTimeProvider time, CategorizationWorkerOptions? options = null) =>
        new(scopeFactory, time, options ?? new CategorizationWorkerOptions(), WorkerId, NullLogger<CategorizationWorker>.Instance);

    [Fact]
    public async Task Idle_when_the_Anthropic_key_is_not_present()
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
        await jobQueue.DidNotReceive().ClaimAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
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
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns((CategorizationJob?)null);
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent()), new FakeTimeProvider(DateTimeOffset.UtcNow));

        var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(CategorizationTickResult.Idle);
    }

    [Fact]
    public async Task A_terminal_model_failure_calls_FailAsync_not_RetryAsync()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
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
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 1));
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
    public async Task A_verification_failure_is_terminal_and_never_retried()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        // amount_quote "999" never occurs in the raw text "Bread 250 RSD" - ProposalVerification.TryResolve
        // must reject this, which is real production logic (Task 1), not a stub.
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneGroceryLine(quote: "999"));
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer), new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().RetryAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_proposal_with_no_items_completes_the_job_honestly_instead_of_recording_a_loan_as_spending()
    {
        // The defect this guards against: "заняла у Маши 5000 рсд" is a loan received, not a
        // purchase. A model that (correctly, per the prompt) answers with zero items must succeed
        // the job with zero line items, not be forced to invent one and not be treated as a
        // failure either - both would misrepresent what actually happened.
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(rawText: "заняла у Маши 5000 рсд"));
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
            TransactionId, Arg.Is<IReadOnlyList<CategorizedLineItem>>(items => items.Count == 0), Arg.Any<CancellationToken>());
        await store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await jobQueue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().FailAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().RetryAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(
            111L, 42,
            Arg.Is<string>(text => text.Contains("nothing", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotOwned_from_SucceedAsync_does_not_trigger_a_fallback_retry_or_fail()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
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
        await store.Received(1).ApplyAsync(TransactionId, Arg.Any<IReadOnlyList<CategorizedLineItem>>(), Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(111L, 42, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().RetryAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().FailAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotOwned_from_RetryAsync_does_not_mark_the_transaction_failed_or_edit_telegram()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 8));
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
        await notifier.DidNotReceive().EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotOwned_from_FailAsync_does_not_mark_the_transaction_failed_or_edit_telegram()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
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
        await notifier.DidNotReceive().EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 3));
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
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
        var notifier = Substitute.For<IChatNotifier>();
        notifier.EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
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

        await notifier.DidNotReceive().EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobQueue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_known_alias_is_used_without_calling_the_model_to_canonicalize_it()
    {
        var merchantId = Guid.NewGuid();
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(rawText: "Starbucks coffee 250 RSD"));
        var merchantDirectory = Substitute.For<IMerchantDirectory>();
        merchantDirectory.AliasesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<MerchantAliasEntry> { new("STARBUCKS", merchantId, "Starbucks") });
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CategorizationProposal([new ProposedLineItem("Coffee", "250", "RSD", "groceries", null, "Starbucks")]));
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, merchantDirectory: merchantDirectory, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await categorizer.DidNotReceive().CanonicalizeMerchantAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<MerchantOption>>(), Arg.Any<CancellationToken>());
        await merchantDirectory.DidNotReceive().LinkAliasAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await store.Received(1).ApplyAsync(
            TransactionId,
            Arg.Is<IReadOnlyList<CategorizedLineItem>>(items => items.Single().MerchantId == merchantId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_merchant_is_canonicalized_and_linked_exactly_once()
    {
        var linkedId = Guid.NewGuid();
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
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
            .Returns(new CategorizationProposal([new ProposedLineItem("Coffee", "250", "RSD", "groceries", null, "Starbucks")]));
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
            Arg.Is<IReadOnlyList<CategorizedLineItem>>(items => items.Single().MerchantId == linkedId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_subject_fails_the_job_without_ever_calling_ApplyAsync()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
        jobQueue.FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns((CategorizationSubject?)null);
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store), new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ApplyAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyList<CategorizedLineItem>>(), Arg.Any<CancellationToken>());
        await store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Last_attempt_exhausted_by_a_transient_failure_marks_the_transaction_failed_and_notifies()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 8));
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
        await notifier.Received(1).EditAsync(111L, 42, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_retry_with_attempts_remaining_does_not_notify_or_mark_the_transaction_failed()
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 1));
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
        await notifier.DidNotReceive().EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_transient_model_failure_is_retried_and_the_captured_transaction_survives_until_it_recovers()
    {
        // Stands in for "pull the network cable": ModelCallException(Transient, ...) is exactly
        // what AnthropicCategorizer is contractually required to throw whether the cable is out
        // or the API is briefly unreachable - from the worker's point of view they are the same
        // "the call did not complete."
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 1));
        jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
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
            Arg.Any<Guid>(), Arg.Any<IReadOnlyList<CategorizedLineItem>>(), Arg.Any<CancellationToken>());
        await notifier.DidNotReceive().EditAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
            Arg.Is<IReadOnlyList<CategorizedLineItem>>(items => items.Single().Description == "Bread"),
            Arg.Any<CancellationToken>());
        await jobQueue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(
            111L, 42, Arg.Is<string>(text => text.Contains("250") && text.Contains("RSD")), Arg.Any<CancellationToken>());
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
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 8));
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
            TransactionId, Arg.Any<IReadOnlyList<CategorizedLineItem>>(), Arg.Any<CancellationToken>());
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
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 1));
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
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 1));
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
        await jobQueue.DidNotReceive().ClaimAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await jobQueue.Received(1).ReleaseExpiredLeasesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Claiming_resumes_once_the_account_cooldown_elapses()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job(attemptCount: 1));
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
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns((CategorizationJob?)null);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
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

    sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new ThrowingScope();

        sealed class ThrowingScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new ThrowingProvider();
            public void Dispose() { }
        }

        sealed class ThrowingProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(IJobQueue)
                    ? throw new InvalidOperationException("the container cannot resolve IJobQueue")
                    : null;
        }
    }
}
