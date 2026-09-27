using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Receipts;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Workers;
using Noof.Ledger.TestKit;

namespace Noof.Ledger.Host.Tests;

public class ExtractReceiptWorkerTests
{
    const string WorkerId = "worker-r";
    static readonly Guid TransactionId = Guid.NewGuid();
    static readonly Guid JobId = Guid.NewGuid();
    static readonly IRecordEcho Echo = new RecordEcho();
    static readonly DateOnly SentOn = new(2026, 9, 25);
    static readonly byte[] SyntheticPhoto = "JFIF-synthetic-receipt"u8.ToArray();

    sealed record Harness(
        IJobQueue Queue, ICategorizationStore Store, IReceiptStore ReceiptStore, IRecordEditor RecordEditor,
        IChatNotifier Notifier, IReceiptPhotoSource PhotoSource, IQrReader QrReader, IFiscalQrDecoder Decoder,
        IFiscalReceiptClient FetchClient, IReceiptVision Vision, IReceiptFetchStatus FetchStatus, IModelProvider ModelProvider,
        IReceiptImageScaler Scaler)
    {
        public IServiceScopeFactory ScopeFactory()
        {
            var provider = Substitute.For<IServiceProvider>();
            provider.GetService(typeof(IJobQueue)).Returns(Queue);
            provider.GetService(typeof(ICategorizationStore)).Returns(Store);
            provider.GetService(typeof(IReceiptStore)).Returns(ReceiptStore);
            provider.GetService(typeof(IRecordEditor)).Returns(RecordEditor);
            provider.GetService(typeof(IChatNotifier)).Returns(Notifier);
            provider.GetService(typeof(IReceiptPhotoSource)).Returns(PhotoSource);
            provider.GetService(typeof(IQrReader)).Returns(QrReader);
            provider.GetService(typeof(IFiscalQrDecoder)).Returns(Decoder);
            provider.GetService(typeof(IFiscalReceiptClient)).Returns(FetchClient);
            provider.GetService(typeof(IReceiptVision)).Returns(Vision);
            provider.GetService(typeof(IReceiptFetchStatus)).Returns(FetchStatus);
            provider.GetService(typeof(IModelProvider)).Returns(ModelProvider);
            provider.GetService(typeof(IReceiptImageScaler)).Returns(Scaler);

            var scope = Substitute.For<IServiceScope>();
            scope.ServiceProvider.Returns(provider);
            var factory = Substitute.For<IServiceScopeFactory>();
            factory.CreateScope().Returns(scope);
            return factory;
        }
    }

    static CategorizationJob ExtractJob(int attemptCount = 1, DateTimeOffset? createdAt = null, DateTimeOffset? claimedAt = null) => new()
    {
        Id = JobId, TransactionId = TransactionId, Kind = JobKind.ExtractReceipt,
        Status = JobStatus.Claimed, AttemptCount = attemptCount,
        RunAfter = DateTimeOffset.UnixEpoch, CreatedAt = createdAt ?? DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
        ClaimedAt = claimedAt,
    };

    static CategorizationSubject WaitingReceipt() =>
        new(TransactionId, "", 111L, 42, "Cash", TransactionStatus.Captured, SentOn, SentOn, [], CaptureKind.Photo);

    static ExtractedReceipt Extracted(ReceiptSource source = ReceiptSource.FiscalQr, decimal? qrTotal = 500m, decimal total = 500m) => new(
        source, "https://suf.purs.gov.rs/v/?vl=abc", "SYN-1", "Test Market", null, null, "SYN-F1",
        DateTimeOffset.Parse("2026-09-25T09:00:00Z"), total, CurrencyCode.Rsd, ReceiptKind.Sale, PaymentMethod.Card, qrTotal,
        [new ExtractedReceiptLine(1, "Bread", 1m, "kom", total, total, null)]);

    static FiscalQrPayload Payload() => new(
        "https://suf.purs.gov.rs/v/?vl=abc", 500m, DateTimeOffset.Parse("2026-09-25T09:00:00Z"), "REQ", "SIG", ReceiptKind.Sale, 1, 1);

    static Harness Setup(CategorizationJob job, CategorizationSubject? record = null, string? telegramFileId = "photo-1", string? verificationUrl = null)
    {
        var queue = Substitute.For<IJobQueue>();
        queue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(job);
        queue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        queue.FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        queue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);

        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(record ?? WaitingReceipt());

        var receiptStore = Substitute.For<IReceiptStore>();
        receiptStore.GetTelegramFileIdAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(telegramFileId);
        receiptStore.GetVerificationUrlAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(verificationUrl);
        receiptStore.SaveExtractedAsync(Arg.Any<Guid>(), Arg.Any<ExtractedReceipt>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiptSaveResult(Guid.NewGuid(), null));

        var recordEditor = Substitute.For<IRecordEditor>();

        var photoSource = Substitute.For<IReceiptPhotoSource>();
        photoSource.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiptPhoto(SyntheticPhoto, "image/jpeg"));

        var qrReader = Substitute.For<IQrReader>();
        qrReader.Read(Arg.Any<Stream>()).Returns((string?)null);

        var decoder = Substitute.For<IFiscalQrDecoder>();
        decoder.Decode(Arg.Any<string>()).Returns(new FiscalQrDecodeResult(null, "not a fiscal link"));

        var fetchClient = Substitute.For<IFiscalReceiptClient>();
        fetchClient.FetchAsync(Arg.Any<FiscalQrPayload>(), Arg.Any<CancellationToken>())
            .Returns(new FiscalFetchResult(Extracted(), null));

        var vision = Substitute.For<IReceiptVision>();
        vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiptVisionResult(Extracted(ReceiptSource.Vision), null));

        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(true);

        var scaler = Substitute.For<IReceiptImageScaler>();
        scaler.ScaleForVision(Arg.Any<ReceiptPhoto>()).Returns(callInfo => callInfo.Arg<ReceiptPhoto>());

        return new Harness(queue, store, receiptStore, recordEditor, Substitute.For<IChatNotifier>(), photoSource, qrReader, decoder,
            fetchClient, vision, Substitute.For<IReceiptFetchStatus>(), modelProvider, scaler);
    }

    static readonly TimeZoneInfo Belgrade = TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade");

    static ExtractReceiptWorker CreateWorker(
        IServiceScopeFactory scopeFactory, FakeTimeProvider? time = null, IDatabaseGate? gate = null,
        CapturingLogger<ExtractReceiptWorker>? logger = null, TimeZoneInfo? captureTimeZone = null, IOperationTimer? timer = null)
    {
        var resolvedTime = time ?? new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero));
        return new(scopeFactory, resolvedTime,
            new CategorizationWorkerOptions(), WorkerId, Echo, captureTimeZone ?? Belgrade, gate ?? ReadyGate(),
            timer ?? new OperationTimer(resolvedTime, new SlowOperationOptions()),
            logger ?? new CapturingLogger<ExtractReceiptWorker>());
    }

    static string? OperationOf(CapturedLogEntry entry) => entry.Properties.GetValueOrDefault("Operation") as string;

    static IDatabaseGate ReadyGate()
    {
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return gate;
    }

    static Task<CategorizationTickResult> TickAsync(Harness harness) =>
        CreateWorker(harness.ScopeFactory()).RunTickAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Claims_only_extract_receipt_jobs()
    {
        var harness = Setup(ExtractJob(), verificationUrl: "https://suf.purs.gov.rs/v/?vl=abc", telegramFileId: null);
        harness.Decoder.Decode(Arg.Any<string>()).Returns(new FiscalQrDecodeResult(Payload(), null));

        await TickAsync(harness);

        await harness.Queue.Received(1).ClaimAsync(
            WorkerId, Arg.Is<IReadOnlyCollection<JobKind>>(kinds => kinds.SequenceEqual(new[] { JobKind.ExtractReceipt })),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_QR_that_decodes_and_fetches_succeeds_without_touching_vision()
    {
        var harness = Setup(ExtractJob(), verificationUrl: "https://suf.purs.gov.rs/v/?vl=abc", telegramFileId: null);
        harness.Decoder.Decode(Arg.Any<string>()).Returns(new FiscalQrDecodeResult(Payload(), null));

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.FetchClient.Received(1).FetchAsync(Arg.Any<FiscalQrPayload>(), Arg.Any<CancellationToken>());
        await harness.Vision.DidNotReceiveWithAnyArgs().ReadAsync(default, default!, default, Arg.Any<CancellationToken>());
        await harness.ReceiptStore.Received(1).SaveExtractedAsync(
            TransactionId, Arg.Is<ExtractedReceipt>(r => r.Source == ReceiptSource.FiscalQr), null, true, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Extracted_is_logged_with_source_lines_total_and_mismatch()
    {
        var harness = Setup(ExtractJob(), verificationUrl: "https://suf.purs.gov.rs/v/?vl=abc", telegramFileId: null);
        harness.Decoder.Decode(Arg.Any<string>()).Returns(new FiscalQrDecodeResult(Payload(), null));
        harness.FetchClient.FetchAsync(Arg.Any<FiscalQrPayload>(), Arg.Any<CancellationToken>())
            .Returns(new FiscalFetchResult(Extracted(qrTotal: 999m, total: 500m), null));
        var logger = new CapturingLogger<ExtractReceiptWorker>();

        await CreateWorker(harness.ScopeFactory(), logger: logger).RunTickAsync(TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.ExtractedEventId).Subject;
        entry.Stage.Should().Be(TransactionStages.Extracted);
        entry.Properties["Source"].Should().Be(ReceiptSource.FiscalQr);
        entry.Properties["Lines"].Should().Be(1);
        entry.Properties["Total"].Should().Be(500m);
        entry.Properties["QrTotal"].Should().Be(999m);
        entry.Properties["Mismatch"].Should().Be(true);
        entry.Properties["FetchFailed"].Should().Be(false);
        entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(TransactionId);
    }

    [Fact]
    public async Task No_QR_on_the_photo_falls_back_to_vision()
    {
        var harness = Setup(ExtractJob());
        var logger = new CapturingLogger<ExtractReceiptWorker>();

        await CreateWorker(harness.ScopeFactory(), logger: logger).RunTickAsync(TestContext.Current.CancellationToken);

        await harness.Vision.Received(1).ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), "image/jpeg", null, Arg.Any<CancellationToken>());
        await harness.FetchClient.DidNotReceiveWithAnyArgs().FetchAsync(default!, Arg.Any<CancellationToken>());
        var entry = logger.Entries.Should().ContainSingle(e => e.Properties.ContainsKey("Reason") && (string)e.Properties["Reason"] == "no QR").Subject;
        entry.Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public async Task No_QR_and_no_model_key_fails_with_a_clear_echo_instead_of_burning_every_attempt_on_vision()
    {
        // M-10 (2026-09-25 final review): ReceiptCategorizationWorker idles rather than spend a model
        // call it cannot make; ExtractReceiptWorker had no such gate at all, so a QR-less photo with no
        // key configured burned all MaxAttempts retries - each re-downloading the photo - before
        // finally failing. Only the vision branch is gated: the QR+Tax-Administration path (tested
        // below) must still run with no key at all.
        var harness = Setup(ExtractJob());
        harness.ModelProvider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(false);

        await TickAsync(harness);

        await harness.Vision.DidNotReceiveWithAnyArgs().ReadAsync(default, default!, default, Arg.Any<CancellationToken>());
        await harness.ReceiptStore.DidNotReceiveWithAnyArgs().SaveExtractedAsync(default, default!, default, default, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Queue.DidNotReceiveWithAnyArgs().RetryAsync(default, default!, default, default!, Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.ReceiptVisionNotConfigured.Text), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_QR_and_tax_administration_path_still_runs_with_no_model_key_configured()
    {
        var harness = Setup(ExtractJob(), verificationUrl: "https://suf.purs.gov.rs/v/?vl=abc", telegramFileId: null);
        harness.Decoder.Decode(Arg.Any<string>()).Returns(new FiscalQrDecodeResult(Payload(), null));
        harness.ModelProvider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(false);

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.FetchClient.Received(1).FetchAsync(Arg.Any<FiscalQrPayload>(), Arg.Any<CancellationToken>());
        await harness.Vision.DidNotReceiveWithAnyArgs().ReadAsync(default, default!, default, Arg.Any<CancellationToken>());
        await harness.ReceiptStore.Received(1).SaveExtractedAsync(
            TransactionId, Arg.Is<ExtractedReceipt>(r => r.Source == ReceiptSource.FiscalQr), null, true, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_decode_error_with_a_photo_falls_back_to_vision()
    {
        var harness = Setup(ExtractJob());
        harness.QrReader.Read(Arg.Any<Stream>()).Returns("https://suf.purs.gov.rs/v/?vl=garbage");
        harness.Decoder.Decode("https://suf.purs.gov.rs/v/?vl=garbage").Returns(new FiscalQrDecodeResult(null, "bad payload"));

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.Vision.Received(1).ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), "image/jpeg", null, Arg.Any<CancellationToken>());
        await harness.ReceiptStore.Received(1).SaveExtractedAsync(
            TransactionId, Arg.Is<ExtractedReceipt>(r => r.Source == ReceiptSource.Vision), Arg.Any<string?>(), true, Arg.Any<CancellationToken>());
    }

    // 2026-09-27: DO NOT RECORD WHEN IT DOES NOT ADD UP - vision receipts only. The receipt and its
    // lines are still saved (so the echo can show exactly what was read) but CategorizeReceipt waits
    // for the operator's own "Record anyway".
    [Fact]
    public async Task A_vision_receipt_that_does_not_add_up_is_saved_without_categorising_and_asks_to_confirm()
    {
        var harness = Setup(ExtractJob());
        var mismatched = Extracted(ReceiptSource.Vision, qrTotal: null, total: 500m) with
        {
            Lines = [new ExtractedReceiptLine(1, "Bread", 1m, "kom", 400m, 400m, null)],
        };
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiptVisionResult(mismatched, null));

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.ReceiptStore.Received(1).SaveExtractedAsync(
            TransactionId, Arg.Any<ExtractedReceipt>(), Arg.Any<string?>(), enqueueCategorization: false, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text.Contains("Record it anyway", StringComparison.Ordinal)
                && m.Text.Contains("Lines add up to 400.00 RSD, the receipt says 500.00 RSD", StringComparison.Ordinal)
                && m.Actions.SequenceEqual(new[] { RecordAction.RecordAnyway, RecordAction.Cancel })),
            Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_vision_receipt_with_a_malformed_tax_id_is_saved_without_categorising_and_names_the_reason()
    {
        var harness = Setup(ExtractJob());
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiptVisionResult(Extracted(ReceiptSource.Vision), null, SellerTaxIdMalformed: true));

        await TickAsync(harness);

        await harness.ReceiptStore.Received(1).SaveExtractedAsync(
            TransactionId, Arg.Any<ExtractedReceipt>(), Arg.Any<string?>(), enqueueCategorization: false, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text.Contains("does not look like a valid PIB", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    // A fiscal QR/Tax-Administration receipt is never held back for confirmation, even when its own
    // lines happen not to sum to its total - only a vision read is ever second-guessed this way.
    [Fact]
    public async Task A_QR_fiscal_receipt_that_does_not_add_up_is_never_held_back_for_confirmation()
    {
        var harness = Setup(ExtractJob(), verificationUrl: "https://suf.purs.gov.rs/v/?vl=abc", telegramFileId: null);
        harness.Decoder.Decode(Arg.Any<string>()).Returns(new FiscalQrDecodeResult(Payload(), null));
        harness.FetchClient.FetchAsync(Arg.Any<FiscalQrPayload>(), Arg.Any<CancellationToken>())
            .Returns(new FiscalFetchResult(Extracted(qrTotal: 999m, total: 500m), null));

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.ReceiptStore.Received(1).SaveExtractedAsync(
            TransactionId, Arg.Any<ExtractedReceipt>(), Arg.Any<string?>(), enqueueCategorization: true, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.ComposeCategorisingReceipt(1)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_replay_of_a_still_unconfirmed_vision_receipt_shows_the_confirmation_echo_again_not_categorising()
    {
        var harness = Setup(ExtractJob(), record: WaitingReceipt() with { Status = TransactionStatus.Captured });
        harness.ReceiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(new ReceiptView(
            Guid.NewGuid(), ReceiptSource.Vision, null, "Test Market", null, null, null,
            DateTimeOffset.Parse("2026-09-25T09:00:00Z"), 500m, CurrencyCode.Rsd, ReceiptKind.Sale, PaymentMethod.Card, null,
            null, [new ReceiptLineView(Guid.NewGuid(), 1, "Bread", 1m, "kom", 400m, 400m, null)]));
        harness.ReceiptStore.IsAwaitingConfirmationAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(true);

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text.Contains("Record it anyway", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
    }

    // 2026-09-27 finding: a sum-vs-total recomputation could not tell a malformed-PIB-only pause apart
    // from one already confirmed (the lines add up fine; the PIB was the only reason). Job existence -
    // IsAwaitingConfirmationAsync - is the derivation instead, so this replay still shows the
    // confirmation prompt rather than falsely claiming "Categorising…" with nothing running.
    [Fact]
    public async Task A_replay_of_a_receipt_paused_only_for_a_malformed_PIB_still_shows_the_confirmation_echo()
    {
        var harness = Setup(ExtractJob(), record: WaitingReceipt() with { Status = TransactionStatus.Captured });
        harness.ReceiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(new ReceiptView(
            Guid.NewGuid(), ReceiptSource.Vision, null, "Test Market", null, null, null,
            DateTimeOffset.Parse("2026-09-25T09:00:00Z"), 500m, CurrencyCode.Rsd, ReceiptKind.Sale, PaymentMethod.Card, null,
            null, [new ReceiptLineView(Guid.NewGuid(), 1, "Bread", 1m, "kom", 500m, 500m, null)]));
        harness.ReceiptStore.IsAwaitingConfirmationAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(true);

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text.Contains("Record it anyway", StringComparison.Ordinal)
                && !m.Text.Contains("Lines add up to", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_decode_error_on_a_link_only_capture_fails_with_an_echo_and_no_vision_call()
    {
        var harness = Setup(ExtractJob(), verificationUrl: "https://suf.purs.gov.rs/v/?vl=abc", telegramFileId: null);
        harness.Decoder.Decode(Arg.Any<string>()).Returns(new FiscalQrDecodeResult(null, "not a fiscal link"));

        await TickAsync(harness);

        await harness.Vision.DidNotReceiveWithAnyArgs().ReadAsync(default, default!, default, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.NotAFiscalReceiptLink.Text), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_fetch_failure_with_a_photo_logs_a_warning_records_the_status_and_falls_back_to_vision_with_the_qr_total()
    {
        var harness = Setup(ExtractJob(), verificationUrl: null, telegramFileId: "photo-1");
        harness.QrReader.Read(Arg.Any<Stream>()).Returns("https://suf.purs.gov.rs/v/?vl=abc");
        harness.Decoder.Decode(Arg.Any<string>()).Returns(new FiscalQrDecodeResult(Payload(), null));
        harness.FetchClient.FetchAsync(Arg.Any<FiscalQrPayload>(), Arg.Any<CancellationToken>())
            .Returns(new FiscalFetchResult(null, new FiscalFetchFailure("504 gateway timeout", 504)));
        var logger = new CapturingLogger<ExtractReceiptWorker>();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));

        await CreateWorker(harness.ScopeFactory(), time, logger: logger).RunTickAsync(TestContext.Current.CancellationToken);

        var warning = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.ReceiptFetchFailedEventId).Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Properties["Reason"].Should().Be("504 gateway timeout");
        warning.Properties["StatusCode"].Should().Be(504);
        warning.Properties.Values.OfType<string>().Should().NotContain(value => value.Contains("suf.purs.gov.rs"),
            "the full verification URL/vl is never logged, only the reason and status code");

        harness.FetchStatus.Received(1).RecordFailure(time.GetUtcNow(), "504 gateway timeout");
        await harness.Vision.Received(1).ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), "image/jpeg", 500m, Arg.Any<CancellationToken>());
    }

    // Copilot finding on PR #3, ExtractReceiptWorker.cs:262: when the QR decodes but the Tax
    // Administration fetch fails, extracted.QrTotal is the verified offline amount while
    // extracted.Total still comes from the vision model - a model total that disagrees with the QR
    // total must never survive into Receipt.Total. Here the lines happen to sum to the verified QR
    // total, so the mismatch check (line sum vs. the verified total) passes and the receipt is
    // recorded normally - but with the QR's own total, never the model's 500.
    [Fact]
    public async Task A_vision_total_that_disagrees_with_the_verified_QR_total_is_normalized_to_it_when_the_lines_still_add_up()
    {
        var harness = Setup(ExtractJob(), verificationUrl: null, telegramFileId: "photo-1");
        harness.QrReader.Read(Arg.Any<Stream>()).Returns("https://suf.purs.gov.rs/v/?vl=abc");
        harness.Decoder.Decode(Arg.Any<string>()).Returns(new FiscalQrDecodeResult(Payload() with { Total = 600m }, null));
        harness.FetchClient.FetchAsync(Arg.Any<FiscalQrPayload>(), Arg.Any<CancellationToken>())
            .Returns(new FiscalFetchResult(null, new FiscalFetchFailure("504 gateway timeout", 504)));
        var modelDisagrees = Extracted(ReceiptSource.Vision, qrTotal: 600m, total: 500m) with
        {
            Lines = [new ExtractedReceiptLine(1, "Bread", 1m, "kom", 600m, 600m, null)],
        };
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), 600m, Arg.Any<CancellationToken>())
            .Returns(new ReceiptVisionResult(modelDisagrees, null));

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.ReceiptStore.Received(1).SaveExtractedAsync(
            TransactionId,
            Arg.Is<ExtractedReceipt>(r => r.Total == 600m),
            Arg.Any<string?>(), enqueueCategorization: true, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.ComposeCategorisingReceipt(1)), Arg.Any<CancellationToken>());
    }

    // Copilot finding on PR #3, ExtractReceiptWorker.cs:261: the QR decoder and the SUF client both
    // deal only in RSD, so a verified QrTotal is a Serbian fiscal amount - but the normalization above
    // only replaced Total, so a vision answer of currency EUR persisted the verified RSD number under
    // the model's own currency. Normalize Currency to RSD alongside Total.
    [Fact]
    public async Task A_vision_currency_that_disagrees_with_the_RSD_only_QR_total_is_normalized_to_RSD()
    {
        var harness = Setup(ExtractJob(), verificationUrl: null, telegramFileId: "photo-1");
        harness.QrReader.Read(Arg.Any<Stream>()).Returns("https://suf.purs.gov.rs/v/?vl=abc");
        harness.Decoder.Decode(Arg.Any<string>()).Returns(new FiscalQrDecodeResult(Payload() with { Total = 600m }, null));
        harness.FetchClient.FetchAsync(Arg.Any<FiscalQrPayload>(), Arg.Any<CancellationToken>())
            .Returns(new FiscalFetchResult(null, new FiscalFetchFailure("504 gateway timeout", 504)));
        var modelDisagrees = Extracted(ReceiptSource.Vision, qrTotal: 600m, total: 600m) with
        {
            Currency = CurrencyCode.Eur,
            Lines = [new ExtractedReceiptLine(1, "Bread", 1m, "kom", 600m, 600m, null)],
        };
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), 600m, Arg.Any<CancellationToken>())
            .Returns(new ReceiptVisionResult(modelDisagrees, null));

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.ReceiptStore.Received(1).SaveExtractedAsync(
            TransactionId,
            Arg.Is<ExtractedReceipt>(r => r.Total == 600m && r.Currency == CurrencyCode.Rsd),
            Arg.Any<string?>(), enqueueCategorization: true, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.ComposeCategorisingReceipt(1)), Arg.Any<CancellationToken>());
    }

    // Same finding, the other outcome: the lines do NOT add up to the verified QR total, so the
    // receipt is held behind Record anyway exactly like the other vision mismatches - and the
    // problem text names the verified 600, never the model's own 600-vs-500 confusion.
    [Fact]
    public async Task A_vision_total_that_disagrees_with_the_verified_QR_total_holds_the_receipt_when_the_lines_do_not_add_up_to_it()
    {
        var harness = Setup(ExtractJob(), verificationUrl: null, telegramFileId: "photo-1");
        harness.QrReader.Read(Arg.Any<Stream>()).Returns("https://suf.purs.gov.rs/v/?vl=abc");
        harness.Decoder.Decode(Arg.Any<string>()).Returns(new FiscalQrDecodeResult(Payload() with { Total = 600m }, null));
        harness.FetchClient.FetchAsync(Arg.Any<FiscalQrPayload>(), Arg.Any<CancellationToken>())
            .Returns(new FiscalFetchResult(null, new FiscalFetchFailure("504 gateway timeout", 504)));
        var modelDisagrees = Extracted(ReceiptSource.Vision, qrTotal: 600m, total: 500m) with
        {
            Lines = [new ExtractedReceiptLine(1, "Bread", 1m, "kom", 500m, 500m, null)],
        };
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), 600m, Arg.Any<CancellationToken>())
            .Returns(new ReceiptVisionResult(modelDisagrees, null));

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.ReceiptStore.Received(1).SaveExtractedAsync(
            TransactionId,
            Arg.Is<ExtractedReceipt>(r => r.Total == 600m),
            Arg.Any<string?>(), enqueueCategorization: false, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text.Contains("Record it anyway", StringComparison.Ordinal)
                && m.Text.Contains("Lines add up to 500.00 RSD, the receipt says 600.00 RSD", StringComparison.Ordinal)
                && m.Text.Contains("Total: 600.00 RSD", StringComparison.Ordinal)
                && m.Actions.SequenceEqual(new[] { RecordAction.RecordAnyway, RecordAction.Cancel })),
            Arg.Any<CancellationToken>());
    }

    // N-6 (2026-09-25 re-review of the final review): the QR decoded fine here, so the fiscal-QR-only
    // operator (R-1/R-5, no key by design) has done nothing wrong - the Tax Administration outage may
    // last minutes. M-10's own gate treated this exactly like "no readable QR at all" and failed the
    // job terminally under a "no readable fiscal QR code" message that misdescribes what happened and
    // loses the receipt for good. Retry like any other transient failure instead.
    [Fact]
    public async Task A_fetch_failure_with_a_photo_and_no_model_key_retries_instead_of_failing_terminally()
    {
        var harness = Setup(ExtractJob(), verificationUrl: null, telegramFileId: "photo-1");
        harness.QrReader.Read(Arg.Any<Stream>()).Returns("https://suf.purs.gov.rs/v/?vl=abc");
        harness.Decoder.Decode(Arg.Any<string>()).Returns(new FiscalQrDecodeResult(Payload(), null));
        harness.FetchClient.FetchAsync(Arg.Any<FiscalQrPayload>(), Arg.Any<CancellationToken>())
            .Returns(new FiscalFetchResult(null, new FiscalFetchFailure("504 gateway timeout", 504)));
        harness.ModelProvider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(false);

        await TickAsync(harness);

        await harness.Vision.DidNotReceiveWithAnyArgs().ReadAsync(default, default!, default, Arg.Any<CancellationToken>());
        await harness.ReceiptStore.DidNotReceiveWithAnyArgs().SaveExtractedAsync(default, default!, default, default, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Queue.DidNotReceiveWithAnyArgs().FailAsync(default, default!, default!, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, Arg.Any<CancellationToken>());
        // The 504 is wrapped into a ModelCallException naming the Tax Administration specifically
        // (there is a photo but no key to fall back to vision) - the safe reason says so, never "504
        // gateway timeout" itself.
        await harness.Notifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m =>
            m.Text.Contains("Reading the receipt", StringComparison.Ordinal)
            && m.Text.Contains("the Tax Administration is unreachable", StringComparison.Ordinal)
            && !m.Text.Contains("504", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_fetch_failure_on_a_link_only_capture_fails_with_an_unreachable_echo()
    {
        var harness = Setup(ExtractJob(), verificationUrl: "https://suf.purs.gov.rs/v/?vl=abc", telegramFileId: null);
        harness.Decoder.Decode(Arg.Any<string>()).Returns(new FiscalQrDecodeResult(Payload(), null));
        harness.FetchClient.FetchAsync(Arg.Any<FiscalQrPayload>(), Arg.Any<CancellationToken>())
            .Returns(new FiscalFetchResult(null, new FiscalFetchFailure("timeout", null)));

        await TickAsync(harness);

        await harness.Vision.DidNotReceiveWithAnyArgs().ReadAsync(default, default!, default, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.ReceiptFetchUnreachableLinkOnly.Text), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_duplicate_receipt_cancels_the_new_transaction_and_edits_the_placeholder()
    {
        var harness = Setup(ExtractJob());
        var duplicateId = Guid.NewGuid();
        harness.ReceiptStore.SaveExtractedAsync(Arg.Any<Guid>(), Arg.Any<ExtractedReceipt>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiptSaveResult(null, duplicateId));
        harness.ReceiptStore.GetByTransactionAsync(duplicateId, Arg.Any<CancellationToken>()).Returns(new ReceiptView(
            Guid.NewGuid(), ReceiptSource.FiscalQr, "SYN-1", "Test Market", null, null, "SYN-F1",
            DateTimeOffset.Parse("2026-09-20T09:00:00Z"), 500m, CurrencyCode.Rsd, ReceiptKind.Sale, PaymentMethod.Card, 500m,
            "https://suf.purs.gov.rs/v/?vl=abc", []));
        var logger = new CapturingLogger<ExtractReceiptWorker>();

        await CreateWorker(harness.ScopeFactory(), logger: logger).RunTickAsync(TestContext.Current.CancellationToken);

        await harness.RecordEditor.Received(1).CancelAsync(TransactionId, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text.StartsWith("Already recorded", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
        logger.Entries.Should().Contain(e => e.EventId.Id == 5014);
    }

    // M-11 (Phase 6 final review): the duplicate index does not care about status, so a receipt whose
    // earlier transaction was Cancelled is rejected forever unless the operator knows to press
    // Restore on that earlier message - say so in the echo instead of leaving it to be discovered.
    [Fact]
    public async Task A_duplicate_of_a_cancelled_receipt_says_so_and_points_at_Restore()
    {
        var harness = Setup(ExtractJob());
        var duplicateId = Guid.NewGuid();
        harness.ReceiptStore.SaveExtractedAsync(Arg.Any<Guid>(), Arg.Any<ExtractedReceipt>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiptSaveResult(null, duplicateId));
        harness.ReceiptStore.GetByTransactionAsync(duplicateId, Arg.Any<CancellationToken>()).Returns(new ReceiptView(
            Guid.NewGuid(), ReceiptSource.FiscalQr, "SYN-1", "Test Market", null, null, "SYN-F1",
            DateTimeOffset.Parse("2026-09-20T09:00:00Z"), 500m, CurrencyCode.Rsd, ReceiptKind.Sale, PaymentMethod.Card, 500m,
            "https://suf.purs.gov.rs/v/?vl=abc", []));
        harness.Store.GetSubjectAsync(duplicateId, Arg.Any<CancellationToken>())
            .Returns(WaitingReceipt() with { Status = TransactionStatus.Cancelled });

        await CreateWorker(harness.ScopeFactory()).RunTickAsync(TestContext.Current.CancellationToken);

        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m =>
                m.Text.StartsWith("Already recorded", StringComparison.Ordinal)
                && m.Text.Contains("cancelled", StringComparison.Ordinal)
                && m.Text.Contains("Restore", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_duplicate_echo_computes_the_date_in_the_capture_time_zone_not_UTC()
    {
        var harness = Setup(ExtractJob());
        var duplicateId = Guid.NewGuid();
        harness.ReceiptStore.SaveExtractedAsync(Arg.Any<Guid>(), Arg.Any<ExtractedReceipt>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiptSaveResult(null, duplicateId));
        // 00:30 Belgrade (+02:00 in September) is 22:30 the PREVIOUS day in UTC - M-6 (final review).
        var issuedAt = new DateTimeOffset(2026, 9, 25, 0, 30, 0, TimeSpan.FromHours(2));
        harness.ReceiptStore.GetByTransactionAsync(duplicateId, Arg.Any<CancellationToken>()).Returns(new ReceiptView(
            Guid.NewGuid(), ReceiptSource.FiscalQr, "SYN-1", "Test Market", null, null, "SYN-F1",
            issuedAt, 500m, CurrencyCode.Rsd, ReceiptKind.Sale, PaymentMethod.Card, 500m,
            "https://suf.purs.gov.rs/v/?vl=abc", []));

        await CreateWorker(harness.ScopeFactory(), captureTimeZone: Belgrade).RunTickAsync(TestContext.Current.CancellationToken);

        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text.Contains("25.09.2026", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_replayed_job_for_a_transaction_that_already_has_a_receipt_re_extracts_nothing()
    {
        var harness = Setup(ExtractJob(), record: WaitingReceipt() with { Status = TransactionStatus.Completed });
        harness.ReceiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(new ReceiptView(
            Guid.NewGuid(), ReceiptSource.FiscalQr, "SYN-1", "Test Market", null, null, "SYN-F1",
            DateTimeOffset.Parse("2026-09-25T09:00:00Z"), 500m, CurrencyCode.Rsd, ReceiptKind.Sale, PaymentMethod.Card, 500m,
            "https://suf.purs.gov.rs/v/?vl=abc", [new ReceiptLineView(Guid.NewGuid(), 1, "Bread", 1m, "kom", 500m, 500m, null)]));

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.PhotoSource.DidNotReceiveWithAnyArgs().DownloadAsync(default!, Arg.Any<CancellationToken>());
        harness.QrReader.DidNotReceiveWithAnyArgs().Read(default!);
        await harness.FetchClient.DidNotReceiveWithAnyArgs().FetchAsync(default!, Arg.Any<CancellationToken>());
        await harness.Vision.DidNotReceiveWithAnyArgs().ReadAsync(default, default!, default, Arg.Any<CancellationToken>());
        await harness.ReceiptStore.DidNotReceiveWithAnyArgs().SaveExtractedAsync(default, default!, default, default, Arg.Any<CancellationToken>());
        await harness.RecordEditor.DidNotReceiveWithAnyArgs().CancelAsync(default, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, Arg.Any<CancellationToken>());
        await harness.Notifier.DidNotReceiveWithAnyArgs().EditAsync(default, default, default!, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_replayed_job_for_a_transaction_still_pending_edits_the_placeholder_and_succeeds()
    {
        var harness = Setup(ExtractJob(), record: WaitingReceipt() with { Status = TransactionStatus.Captured });
        harness.ReceiptStore.GetByTransactionAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(new ReceiptView(
            Guid.NewGuid(), ReceiptSource.Vision, null, "Test Market", null, null, null,
            DateTimeOffset.Parse("2026-09-25T09:00:00Z"), 500m, CurrencyCode.Rsd, ReceiptKind.Sale, PaymentMethod.Card, null,
            null, [new ReceiptLineView(Guid.NewGuid(), 1, "Bread", 1m, "kom", 500m, 500m, null)]));

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.Vision.DidNotReceiveWithAnyArgs().ReadAsync(default, default!, default, Arg.Any<CancellationToken>());
        await harness.ReceiptStore.DidNotReceiveWithAnyArgs().SaveExtractedAsync(default, default!, default, default, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.ComposeCategorisingReceipt(1)), Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Vision_reporting_the_photo_unreadable_fails_with_the_unreadable_echo_not_ReceiptReadFailure()
    {
        // 2026-09-27: the model's own honest "I could not read this" (readable: false) is a distinct
        // outcome from a ModelCallException, and gets its own echo naming the fix (the QR link) rather
        // than the generic ReceiptReadFailure.
        var harness = Setup(ExtractJob());
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiptVisionResult(null, ReceiptUnreadableReason.Blurry));

        await TickAsync(harness);

        await harness.ReceiptStore.DidNotReceiveWithAnyArgs().SaveExtractedAsync(default, default!, default, default, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.ReceiptUnreadable.Text), Arg.Any<CancellationToken>());
    }

    // M-8 (2026-09-25 final review), superseded 2026-09-27: ReceiptVisionSchema allows an empty lines
    // array (the model's honest "I could not read this"), but applying zero items used to succeed the
    // job and render "Total: " with a mismatch warning instead of a clear failure. ChatReceiptVision
    // itself now maps a readable-but-empty answer to ReceiptVisionResult.Unreadable, so this exercises
    // the worker's own handling of that mapped result rather than a raw ExtractedReceipt.
    [Fact]
    public async Task Vision_returning_zero_lines_is_reported_as_unreadable_not_an_empty_record()
    {
        var harness = Setup(ExtractJob());
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiptVisionResult(null, ReceiptUnreadableReason.Other));

        await TickAsync(harness);

        await harness.ReceiptStore.DidNotReceiveWithAnyArgs().SaveExtractedAsync(default, default!, default, default, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.ReceiptUnreadable.Text), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_photo_is_scaled_before_it_reaches_vision()
    {
        var harness = Setup(ExtractJob());
        var scaledPhoto = new ReceiptPhoto(new byte[] { 9, 9, 9 }, "image/jpeg");
        harness.Scaler.ScaleForVision(Arg.Any<ReceiptPhoto>()).Returns(scaledPhoto);
        byte[]? qrReaderSawBytes = null;
        harness.QrReader.Read(Arg.Any<Stream>()).Returns(callInfo =>
        {
            qrReaderSawBytes = ReadAllBytes(callInfo.Arg<Stream>());
            return null;
        });

        await TickAsync(harness);

        await harness.Vision.Received(1).ReadAsync(scaledPhoto.Bytes, "image/jpeg", null, Arg.Any<CancellationToken>());
        qrReaderSawBytes.Should().Equal(SyntheticPhoto, "the QR reader must keep reading the original, unscaled bytes");
    }

    static byte[] ReadAllBytes(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task A_terminal_vision_failure_fails_the_record_and_reports_it()
    {
        var harness = Setup(ExtractJob());
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "read_receipt produced no tool call"));

        await TickAsync(harness);

        await harness.Queue.Received(1).FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.ReceiptReadFailure.Text), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_transient_vision_failure_retries_and_tells_the_operator()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero));
        var harness = Setup(ExtractJob());
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "read_receipt produced no tool call"));

        await CreateWorker(harness.ScopeFactory(), time).RunTickAsync(TestContext.Current.CancellationToken);

        await harness.Queue.Received(1).RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Queue.DidNotReceiveWithAnyArgs().FailAsync(default, default!, default!, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m =>
            m.Text.Contains("Reading the receipt", StringComparison.Ordinal)
            && m.Text.Contains("the model did not answer", StringComparison.Ordinal)
            && !m.Text.Contains("read_receipt produced no tool call", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_photo_download_names_the_reason_in_the_retry_notice()
    {
        var harness = Setup(ExtractJob());
        harness.PhotoSource.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("telegram unreachable"));

        await TickAsync(harness);

        await harness.Notifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m =>
            m.Text.Contains("the receipt photo could not be read", StringComparison.Ordinal)
            && !m.Text.Contains("telegram unreachable", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_database_failure_saving_the_extracted_receipt_names_a_database_error_in_the_retry_notice()
    {
        var harness = Setup(ExtractJob());
        harness.ReceiptStore.SaveExtractedAsync(Arg.Any<Guid>(), Arg.Any<ExtractedReceipt>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("Cannot write DateTimeOffset with Offset=02:00:00"));

        await TickAsync(harness);

        await harness.Notifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m =>
            m.Text.Contains("Reading the receipt", StringComparison.Ordinal)
            && m.Text.Contains("a database error", StringComparison.Ordinal)
            && !m.Text.Contains("DateTimeOffset", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_retry_notice_names_roughly_when_the_next_attempt_runs_in_the_capture_time_zone()
    {
        // 09:00 UTC is 11:00 in Belgrade (+02:00 in September) - the retry notice must show the
        // capture time zone's clock, not UTC's, the same rule ExtractReceiptWorker already applies
        // to a duplicate receipt's date.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero));
        var harness = Setup(ExtractJob());
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "read_receipt produced no tool call"));

        await CreateWorker(harness.ScopeFactory(), time, captureTimeZone: Belgrade).RunTickAsync(TestContext.Current.CancellationToken);

        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text.Contains("11:", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_success_after_a_retried_failure_replaces_the_retry_notice_with_the_ordinary_echo()
    {
        var harness = Setup(ExtractJob());
        var worker = CreateWorker(harness.ScopeFactory());
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "read_receipt produced no tool call"));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);
        harness.Notifier.ClearReceivedCalls();

        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .Returns(new ReceiptVisionResult(Extracted(ReceiptSource.Vision), null));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.ComposeCategorisingReceipt(1) && !m.Text.Contains("retrying", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_last_transient_attempt_fails_the_record()
    {
        var harness = Setup(ExtractJob(attemptCount: new CategorizationWorkerOptions().MaxAttempts));
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "read_receipt produced no tool call"));

        await TickAsync(harness);

        await harness.Store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.ReceiptReadFailure.Text), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_account_level_failure_retries_the_job_and_pauses_claiming()
    {
        var harness = Setup(ExtractJob());
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "status 401").AsAccountLevel());
        var worker = CreateWorker(harness.ScopeFactory());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);
        var second = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        second.Should().Be(CategorizationTickResult.Idle);
        await harness.Queue.Received(1).RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_photo_download_is_retried()
    {
        var harness = Setup(ExtractJob());
        harness.PhotoSource.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("telegram unreachable"));

        await TickAsync(harness);

        await harness.Queue.Received(1).RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Vision.DidNotReceiveWithAnyArgs().ReadAsync(default, default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_transaction_logs_StageFailed_for_Extracted()
    {
        var harness = Setup(ExtractJob());
        harness.Store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns((CategorizationSubject?)null);
        var logger = new CapturingLogger<ExtractReceiptWorker>();

        await CreateWorker(harness.ScopeFactory(), logger: logger).RunTickAsync(TestContext.Current.CancellationToken);

        var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.StageFailedEventId).Subject;
        entry.Properties["FailedStage"].Should().Be(TransactionStages.Extracted);
    }

    [Fact]
    public async Task A_broken_container_is_reported_as_failed_without_throwing()
    {
        var worker = CreateWorker(new ThrowingScopeFactory());

        (await worker.RunTickAsync(TestContext.Current.CancellationToken)).Should().Be(CategorizationTickResult.Failed);
    }

    [Fact]
    public async Task The_loop_waits_for_the_database_gate_before_its_first_claim()
    {
        var queue = Substitute.For<IJobQueue>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IJobQueue)).Returns(queue);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);

        var gateSource = new TaskCompletionSource();
        var gate = Substitute.For<IDatabaseGate>();
        gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(gateSource.Task);
        var worker = CreateWorker(factory, gate: gate);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await queue.DidNotReceive().ReleaseExpiredLeasesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        gateSource.SetResult();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await queue.Received().ReleaseExpiredLeasesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_claimed_job_logs_claim_queueWait_downloadFile_and_job_extractReceipt()
    {
        var claimedAt = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
        var job = ExtractJob(createdAt: claimedAt - TimeSpan.FromSeconds(4), claimedAt: claimedAt);
        var harness = Setup(job);
        var logger = new CapturingLogger<ExtractReceiptWorker>();
        var time = new FakeTimeProvider(claimedAt);
        var worker = CreateWorker(harness.ScopeFactory(), time, logger: logger);

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        var operations = logger.Entries.Select(OperationOf).Where(operation => operation is not null).ToList();
        operations.Should().Contain("db.claimJob");
        operations.Should().Contain("job.queueWait");
        operations.Should().Contain(TimedOperations.TelegramDownloadFile);
        operations.Should().Contain(TimedOperations.JobExtractReceipt);

        var queueWait = logger.Entries.Should().ContainSingle(entry => OperationOf(entry) == "job.queueWait").Subject;
        queueWait.Properties["ElapsedMs"].Should().Be(4000L);
    }

    [Fact]
    public async Task An_idle_tick_logs_no_timing_event()
    {
        var queue = Substitute.For<IJobQueue>();
        queue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((CategorizationJob?)null);
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IJobQueue)).Returns(queue);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        var logger = new CapturingLogger<ExtractReceiptWorker>();

        await CreateWorker(factory, logger: logger).RunTickAsync(TestContext.Current.CancellationToken);

        logger.Entries.Should().BeEmpty();
    }
}
