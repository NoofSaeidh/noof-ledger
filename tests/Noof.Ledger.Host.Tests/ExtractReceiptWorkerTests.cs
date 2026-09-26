using AwesomeAssertions;
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
using PaymentMethod = Noof.Ledger.Application.Receipts.PaymentMethod;
using ReceiptKind = Noof.Ledger.Application.Receipts.ReceiptKind;
using ReceiptSource = Noof.Ledger.Application.Receipts.ReceiptSource;

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
        IFiscalReceiptClient FetchClient, IReceiptVision Vision, IReceiptFetchStatus FetchStatus, IModelProvider ModelProvider)
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

            var scope = Substitute.For<IServiceScope>();
            scope.ServiceProvider.Returns(provider);
            var factory = Substitute.For<IServiceScopeFactory>();
            factory.CreateScope().Returns(scope);
            return factory;
        }
    }

    static CategorizationJob ExtractJob(int attemptCount = 1) => new()
    {
        Id = JobId, TransactionId = TransactionId, Kind = JobKind.ExtractReceipt,
        Status = JobStatus.Claimed, AttemptCount = attemptCount,
        RunAfter = DateTimeOffset.UnixEpoch, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
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
        receiptStore.SaveExtractedAsync(Arg.Any<Guid>(), Arg.Any<ExtractedReceipt>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
            .Returns(Extracted(ReceiptSource.Vision));

        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(true);

        return new Harness(queue, store, receiptStore, recordEditor, Substitute.For<IChatNotifier>(), photoSource, qrReader, decoder,
            fetchClient, vision, Substitute.For<IReceiptFetchStatus>(), modelProvider);
    }

    static readonly TimeZoneInfo Belgrade = TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade");

    static ExtractReceiptWorker CreateWorker(
        IServiceScopeFactory scopeFactory, FakeTimeProvider? time = null, IDatabaseGate? gate = null,
        CapturingLogger<ExtractReceiptWorker>? logger = null, TimeZoneInfo? captureTimeZone = null) =>
        new(scopeFactory, time ?? new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero)),
            new CategorizationWorkerOptions(), WorkerId, Echo, captureTimeZone ?? Belgrade, gate ?? ReadyGate(),
            logger ?? new CapturingLogger<ExtractReceiptWorker>());

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
            TransactionId, Arg.Is<ExtractedReceipt>(r => r.Source == ReceiptSource.FiscalQr), null, Arg.Any<CancellationToken>());
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
        await harness.ReceiptStore.DidNotReceiveWithAnyArgs().SaveExtractedAsync(default, default!, default, Arg.Any<CancellationToken>());
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
            TransactionId, Arg.Is<ExtractedReceipt>(r => r.Source == ReceiptSource.FiscalQr), null, Arg.Any<CancellationToken>());
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
            TransactionId, Arg.Is<ExtractedReceipt>(r => r.Source == ReceiptSource.Vision), Arg.Any<string?>(), Arg.Any<CancellationToken>());
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
        harness.ReceiptStore.SaveExtractedAsync(Arg.Any<Guid>(), Arg.Any<ExtractedReceipt>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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

    [Fact]
    public async Task The_duplicate_echo_computes_the_date_in_the_capture_time_zone_not_UTC()
    {
        var harness = Setup(ExtractJob());
        var duplicateId = Guid.NewGuid();
        harness.ReceiptStore.SaveExtractedAsync(Arg.Any<Guid>(), Arg.Any<ExtractedReceipt>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
        await harness.ReceiptStore.DidNotReceiveWithAnyArgs().SaveExtractedAsync(default, default!, default, Arg.Any<CancellationToken>());
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
        await harness.ReceiptStore.DidNotReceiveWithAnyArgs().SaveExtractedAsync(default, default!, default, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.ComposeCategorisingReceipt(1)), Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Vision_returning_zero_lines_is_a_failed_extraction_not_an_empty_record()
    {
        // M-8 (2026-09-25 final review): ReceiptVisionSchema allows an empty lines array (the model's
        // honest "I could not read this"), but applying zero items used to succeed the job and render
        // "Total: " with a mismatch warning instead of a clear failure.
        var harness = Setup(ExtractJob());
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .Returns(Extracted(ReceiptSource.Vision) with { Lines = [] });

        await TickAsync(harness);

        await harness.ReceiptStore.DidNotReceiveWithAnyArgs().SaveExtractedAsync(default, default!, default, Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text == Echo.ReceiptReadFailure.Text), Arg.Any<CancellationToken>());
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
    public async Task A_transient_vision_failure_retries_and_tells_nobody_yet()
    {
        var harness = Setup(ExtractJob());
        harness.Vision.ReadAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<string>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "read_receipt produced no tool call"));

        await TickAsync(harness);

        await harness.Queue.Received(1).RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Queue.DidNotReceiveWithAnyArgs().FailAsync(default, default!, default!, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, Arg.Any<CancellationToken>());
        await harness.Notifier.DidNotReceiveWithAnyArgs().EditAsync(default, default, default!, Arg.Any<CancellationToken>());
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
}
