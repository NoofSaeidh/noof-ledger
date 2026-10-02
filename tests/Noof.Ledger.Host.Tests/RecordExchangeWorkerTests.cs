using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Workers;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Host.Tests;

public class RecordExchangeWorkerTests
{
    const string WorkerId = "worker-x";
    static readonly Guid TransactionId = Guid.Parse("00000000-0000-0000-0007-000000000001");
    static readonly Guid JobId = Guid.Parse("00000000-0000-0000-0007-000000000002");
    static readonly Guid VenueId = Guid.Parse("00000000-0000-0000-0007-000000000003");
    static readonly IRecordEcho Echo = new RecordEcho();
    static readonly DateOnly SentOn = new(2026, 9, 26);

    // The slip was issued at 00:30 on the 27th in Belgrade and photographed on the 26th by UTC; saving the slip
    // already dated the record by the slip's local day (spec A-23), so that is the day the transfer keeps.
    static readonly DateOnly SlipDay = new(2026, 9, 27);

    static readonly WalletOption CashRsd = new(
        Guid.Parse("00000000-0000-0000-0007-0000000000d1"), "Cash RSD", CurrencyCode.Rsd, [], IsDefaultForCurrency: false,
        DefaultForPayment: WalletPaymentDefault.Cash);
    static readonly WalletOption CashEur = new(
        Guid.Parse("00000000-0000-0000-0007-0000000000e1"), "Cash EUR", CurrencyCode.Eur, [], IsDefaultForCurrency: false,
        DefaultForPayment: WalletPaymentDefault.Cash);
    static readonly WalletOption RaiffeisenRsd = new(
        Guid.Parse("00000000-0000-0000-0007-0000000000d2"), "Raiffeisen RSD", CurrencyCode.Rsd, [], IsDefaultForCurrency: true);
    static readonly WalletOption WiseEur = new(
        Guid.Parse("00000000-0000-0000-0007-0000000000e2"), "Wise EUR", CurrencyCode.Eur, [], IsDefaultForCurrency: true);

    static readonly ExtractedExchange CleanSale = new(100.00m, "EUR", 11700.00m, "RSD", 117.0000m, null, null, "PZ-2026-0917");

    sealed record Harness(
        IJobQueue Queue, ICategorizationStore Store, IReceiptStore ReceiptStore, IWalletDirectory Wallets,
        IMerchantDirectory Merchants, IChatNotifier Notifier)
    {
        public IServiceScopeFactory ScopeFactory()
        {
            var provider = Substitute.For<IServiceProvider>();
            provider.GetService(typeof(IJobQueue)).Returns(Queue);
            provider.GetService(typeof(ICategorizationStore)).Returns(Store);
            provider.GetService(typeof(IReceiptStore)).Returns(ReceiptStore);
            provider.GetService(typeof(IWalletDirectory)).Returns(Wallets);
            provider.GetService(typeof(IMerchantDirectory)).Returns(Merchants);
            provider.GetService(typeof(IChatNotifier)).Returns(Notifier);

            var scope = Substitute.For<IServiceScope>();
            scope.ServiceProvider.Returns(provider);
            var factory = Substitute.For<IServiceScopeFactory>();
            factory.CreateScope().Returns(scope);
            return factory;
        }
    }

    static CategorizationJob RecordJob(int attemptCount) => new()
    {
        Id = JobId,
        TransactionId = TransactionId,
        Kind = JobKind.RecordExchange,
        Status = JobStatus.Claimed,
        AttemptCount = attemptCount,
        RunAfter = DateTimeOffset.UnixEpoch,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    static CategorizationSubject SlipCapture(TransactionStatus status = TransactionStatus.Captured) =>
        new(TransactionId, string.Empty, 111L, 42, string.Empty, status, SentOn, SlipDay, [], CaptureKind.Photo);

    static ExchangeSlipView Slip(ExtractedExchange? evidence = null, string? sellerTaxId = "123456789") => new(
        Guid.Parse("00000000-0000-0000-0007-000000000004"), sellerTaxId, "Menjačnica Zlatnik",
        new DateTimeOffset(2026, 9, 26, 22, 30, 0, TimeSpan.Zero), "PZ-2026-0917", evidence ?? CleanSale);

    static Harness Setup(
        ExchangeSlipView? slip = null, CategorizationSubject? record = null, int attemptCount = 1,
        IReadOnlyList<WalletOption>? wallets = null)
    {
        var queue = Substitute.For<IJobQueue>();
        queue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(RecordJob(attemptCount));
        queue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        queue.FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        queue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);

        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(record ?? SlipCapture());

        var receiptStore = Substitute.For<IReceiptStore>();
        receiptStore.GetExchangeSlipAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(slip ?? Slip());

        IReadOnlyList<WalletOption> offered = wallets ?? [CashRsd, CashEur, RaiffeisenRsd, WiseEur];
        var walletDirectory = Substitute.For<IWalletDirectory>();
        walletDirectory.ActiveAsync(Arg.Any<CancellationToken>()).Returns(offered);

        var merchants = Substitute.For<IMerchantDirectory>();
        merchants.VenueForTaxIdAsync("123456789", "Menjačnica Zlatnik", Arg.Any<CancellationToken>()).Returns(VenueId);

        return new Harness(queue, store, receiptStore, walletDirectory, merchants, Substitute.For<IChatNotifier>());
    }

    static IDatabaseGate ReadyGate()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return gate;
    }

    static RecordExchangeWorker CreateWorker(
        IServiceScopeFactory scopeFactory, IDatabaseGate? gate = null, CapturingLogger<RecordExchangeWorker>? logger = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero));
        return new(scopeFactory, time, new CategorizationWorkerOptions(), WorkerId, Echo, gate ?? ReadyGate(),
            new OperationTimer(time, new SlowOperationOptions()), logger ?? new CapturingLogger<RecordExchangeWorker>());
    }

    static Task<CategorizationTickResult> TickAsync(Harness harness, CapturingLogger<RecordExchangeWorker>? logger = null) =>
        CreateWorker(harness.ScopeFactory(), logger: logger).RunTickAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Claims_only_record_exchange_jobs_and_never_asks_for_a_model_key()
    {
        var harness = Setup();

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.Queue.Received(1).ClaimAsync(
            WorkerId, Arg.Is<IReadOnlyCollection<JobKind>>(kinds => kinds.SequenceEqual(new[] { JobKind.RecordExchange })),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        // The scope offers no IModelProvider, so asking for one anywhere would either fail the tick or retry the job.
        await harness.Store.Received(1).ApplyAsync(TransactionId, Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>());
        await harness.Queue.DidNotReceiveWithAnyArgs().RetryAsync(default, default!, default, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_clean_slip_is_applied_as_a_transfer_between_the_cash_wallets_of_its_two_currencies()
    {
        var harness = Setup();

        await TickAsync(harness);

        await harness.Store.Received(1).ApplyAsync(TransactionId, Arg.Is<CategorizationOutcome>(outcome =>
                outcome.Kind == JobKind.RecordExchange
                && outcome.Instruction == null
                && outcome.TransactionKind == TransactionKind.Transfer
                && outcome.Items.Count == 0
                && outcome.WalletId == CashEur.Id
                && outcome.OccurredOn == SlipDay
                && outcome.Transfer == new TransferFacts(
                    CashEur.Id, new Money(100.00m, CurrencyCode.Eur), CashRsd.Id, new Money(11700.00m, CurrencyCode.Rsd),
                    null, null, new ExchangeRate(CurrencyCode.Eur, 117.0000m, CurrencyCode.Rsd), VenueId)),
            Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_received_amount_is_filled_from_the_printed_rate()
    {
        var harness = Setup(Slip(CleanSale with { ReceivedAmount = null }));

        await TickAsync(harness);

        await harness.Store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.Transfer!.To == new Money(11700.00m, CurrencyCode.Rsd)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_printed_commission_is_the_fee_on_the_leg_it_was_taken_on()
    {
        var harness = Setup(Slip(new ExtractedExchange(11850.00m, "RSD", 100.00m, "EUR", 117.0000m, 150.00m, "RSD", "PZ-2026-0917")));

        await TickAsync(harness);

        await harness.Store.Received(1).ApplyAsync(TransactionId, Arg.Is<CategorizationOutcome>(outcome =>
                outcome.Transfer!.FromWalletId == CashRsd.Id
                && outcome.Transfer.From == new Money(11850.00m, CurrencyCode.Rsd)
                && outcome.Transfer.ToWalletId == CashEur.Id
                && outcome.Transfer.To == new Money(100.00m, CurrencyCode.Eur)
                && outcome.Transfer.Fee == new Money(150.00m, CurrencyCode.Rsd)
                && outcome.Transfer.FeeLeg == TransferLeg.From),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_currency_with_no_cash_default_falls_back_to_its_default_wallet()
    {
        var harness = Setup(wallets: [CashRsd, RaiffeisenRsd, WiseEur]);

        await TickAsync(harness);

        await harness.Store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.Transfer!.FromWalletId == WiseEur.Id && outcome.Transfer.ToWalletId == CashRsd.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_slip_whose_currency_has_no_wallet_fails_the_record_with_LegCurrencyMismatch()
    {
        var harness = Setup(wallets: [CashRsd, RaiffeisenRsd]);

        await TickAsync(harness);

        await harness.Queue.Received(1).FailAsync(JobId, WorkerId, "LegCurrencyMismatch", Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkFailedAsync(TransactionId, RecordFailureReason.LegCurrencyMismatch, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().ApplyAsync(default, default!, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42, Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_slip_with_both_legs_in_one_currency_fails_the_record_with_SameWallet()
    {
        var harness = Setup(Slip(new ExtractedExchange(100.00m, "EUR", 100.00m, "EUR", null, null, null, "PZ-2026-0917")));

        await TickAsync(harness);

        await harness.Queue.Received(1).FailAsync(JobId, WorkerId, "SameWallet", Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkFailedAsync(TransactionId, RecordFailureReason.SameWallet, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().ApplyAsync(default, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_slip_that_cannot_be_settled_fails_the_record_with_the_reason()
    {
        var harness = Setup(Slip(CleanSale with { CommissionAmount = 1.30m, CommissionCurrency = "USD" }));

        await TickAsync(harness);

        await harness.Store.Received(1).MarkFailedAsync(TransactionId, RecordFailureReason.InvalidFee, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().ApplyAsync(default, default!, Arg.Any<CancellationToken>());
    }

    // The store fails only a still-Captured record, so a Cancel pressed after "Record anyway" stays a Cancel (spec A-22);
    // the worker's part is to fail its job and echo the record as the database holds it.
    [Fact]
    public async Task A_record_cancelled_after_record_anyway_fails_only_its_job()
    {
        var cancelled = SlipCapture(TransactionStatus.Cancelled);
        var harness = Setup(Slip(CleanSale with { CommissionAmount = 1.30m, CommissionCurrency = "USD" }), record: cancelled);

        await TickAsync(harness);

        await harness.Queue.Received(1).FailAsync(JobId, WorkerId, "InvalidFee", Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().ApplyAsync(default, default!, Arg.Any<CancellationToken>());
        var expected = Echo.Compose(cancelled);
        await harness.Notifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m => m.Text == expected.Text), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_slip_with_no_PIB_has_no_venue()
    {
        var harness = Setup(Slip(sellerTaxId: null));

        await TickAsync(harness);

        await harness.Merchants.DidNotReceiveWithAnyArgs().VenueForTaxIdAsync(default!, default!, Arg.Any<CancellationToken>());
        await harness.Store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.Transfer!.VenueMerchantId == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_echo_is_composed_from_the_record_read_back_after_apply()
    {
        var recorded = SlipCapture(TransactionStatus.Completed) with
        {
            Kind = TransactionKind.Transfer,
            WalletName = "Cash EUR",
            WalletCurrency = CurrencyCode.Eur,
            WalletId = CashEur.Id,
            Transfer = new TransferView(
                CashEur.Id, "Cash EUR", new Money(100.00m, CurrencyCode.Eur), CashRsd.Id, "Cash RSD", new Money(11700.00m, CurrencyCode.Rsd),
                null, null, new ExchangeRate(CurrencyCode.Eur, 117.0000m, CurrencyCode.Rsd), "Menjačnica Zlatnik",
                [new Money(0.00m, CurrencyCode.Eur)], [new Money(11700.00m, CurrencyCode.Rsd)]),
            Slip = new SlipFacts("Menjačnica Zlatnik", "PZ-2026-0917", CleanSale),
        };
        var harness = Setup();
        harness.Store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(SlipCapture(), recorded);

        await TickAsync(harness);

        var expected = Echo.Compose(recorded);
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == expected.Text && m.Actions.SequenceEqual(expected.Actions)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_transient_failure_retries_and_only_the_last_attempt_fails_the_record()
    {
        var first = Setup(attemptCount: 1);
        first.Store.ApplyAsync(TransactionId, Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the database went away"));
        var last = Setup(attemptCount: 8);
        last.Store.ApplyAsync(TransactionId, Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the database went away"));

        await TickAsync(first);
        await TickAsync(last);

        await first.Queue.Received(1).RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), "the database went away", Arg.Any<CancellationToken>());
        await first.Store.DidNotReceive().MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<RecordFailureReason>(), Arg.Any<CancellationToken>());
        await last.Store.Received(1).MarkFailedAsync(TransactionId, RecordFailureReason.None, Arg.Any<CancellationToken>());
    }

    static CategorizationSubject RepliedRecord(TransactionStatus status) =>
        SlipCapture(status) with
        {
            Kind = TransactionKind.Transfer,
            WalletName = "Cash EUR",
            WalletCurrency = CurrencyCode.Eur,
            WalletId = CashEur.Id,
            Transfer = new TransferView(
                CashEur.Id, "Cash EUR", new Money(100.00m, CurrencyCode.Eur), CashRsd.Id, "Cash RSD", new Money(11650.00m, CurrencyCode.Rsd),
                null, null, new ExchangeRate(CurrencyCode.Eur, 117.0000m, CurrencyCode.Rsd), "Menjačnica Zlatnik",
                [new Money(0.00m, CurrencyCode.Eur)], [new Money(11650.00m, CurrencyCode.Rsd)]),
            Slip = new SlipFacts("Menjačnica Zlatnik", "PZ-2026-0917", CleanSale),
        };

    // Spec A-22: the photo was being read when the operator replied "получил 11650"; that reply was queued before
    // RecordExchange existed, so it was claimed first and recorded 11650 RSD. The slip (11700 RSD) never overwrites it.
    [Fact]
    public async Task A_reply_applied_before_the_slip_was_recorded_keeps_its_amount()
    {
        var replied = RepliedRecord(TransactionStatus.Completed);
        var harness = Setup(record: replied);
        var logger = new CapturingLogger<RecordExchangeWorker>();

        (await TickAsync(harness, logger)).Should().Be(CategorizationTickResult.Processed);

        await harness.Store.DidNotReceiveWithAnyArgs().ApplyAsync(default, default!, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, default, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
        var expected = Echo.Compose(replied);
        await harness.Notifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m => m.Text == expected.Text), Arg.Any<CancellationToken>());
        logger.Entries.Should().Contain(entry => entry.EventId.Id == 1806);
    }

    [Fact]
    public async Task A_record_a_reply_applied_and_the_operator_then_cancelled_is_left_alone()
    {
        var harness = Setup(record: RepliedRecord(TransactionStatus.Cancelled));

        await TickAsync(harness);

        await harness.Store.DidNotReceiveWithAnyArgs().ApplyAsync(default, default!, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
    }

    // A reply can make the record a balance statement, which has neither lines nor a transfer, only the statement.
    [Fact]
    public async Task A_record_a_reply_made_a_balance_statement_and_the_operator_then_cancelled_is_left_alone()
    {
        var statement = SlipCapture(TransactionStatus.Cancelled) with
        {
            Kind = TransactionKind.BalanceCheck,
            WalletName = "Cash RSD",
            WalletCurrency = CurrencyCode.Rsd,
            WalletId = CashRsd.Id,
            Statement = new BalanceStatement(new Money(11650.00m, CurrencyCode.Rsd), 0.00m),
        };
        var harness = Setup(record: statement);

        await TickAsync(harness);

        await harness.Store.DidNotReceiveWithAnyArgs().ApplyAsync(default, default!, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_slip_fails_the_job_without_touching_the_record()
    {
        var harness = Setup();
        harness.ReceiptStore.GetExchangeSlipAsync(TransactionId, Arg.Any<CancellationToken>()).Returns((ExchangeSlipView?)null);

        await TickAsync(harness);

        await harness.Queue.Received(1).FailAsync(
            JobId, WorkerId, "the transaction or its exchange slip no longer exists", Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_broken_container_is_reported_as_a_failed_tick_without_throwing()
    {
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Throws(new InvalidOperationException("no container"));
        var logger = new CapturingLogger<RecordExchangeWorker>();

        var result = await CreateWorker(factory, logger: logger).RunTickAsync(TestContext.Current.CancellationToken);

        result.Should().Be(CategorizationTickResult.Failed);
        logger.Entries.Should().Contain(entry => entry.EventId.Id == 1801);
    }

    [Fact]
    public async Task The_loop_waits_for_the_database_gate_before_its_first_claim()
    {
        var harness = Setup();
        harness.Queue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((CategorizationJob?)null);
        var ready = new TaskCompletionSource();
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(ready.Task);
        var worker = CreateWorker(harness.ScopeFactory(), gate: gate);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        harness.Queue.ReceivedCalls().Should().BeEmpty("nothing may touch the queue before the database is ready");

        ready.SetResult();
        for (var waited = 0; waited < 50 && !harness.Queue.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IJobQueue.ClaimAsync)); waited++)
            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        await harness.Queue.Received().ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }
}
