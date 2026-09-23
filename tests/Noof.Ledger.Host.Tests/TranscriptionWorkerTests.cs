using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Transcription;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

public class TranscriptionWorkerTests
{
    const string WorkerId = "worker-t";
    static readonly Guid TransactionId = Guid.NewGuid();
    static readonly Guid JobId = Guid.NewGuid();
    static readonly IRecordEcho Echo = new RecordEcho();
    static readonly DateOnly SentOn = new(2026, 9, 24);
    static readonly byte[] SyntheticAudio = "OggS-synthetic-voice-note"u8.ToArray();

    sealed record Harness(
        IJobQueue Queue, ISpeechProvider SpeechProvider, ICategorizationStore Store, ITranscriptionStore TranscriptionStore,
        IVoiceFileSource VoiceFiles, ITranscriber Transcriber, IChatNotifier Notifier)
    {
        public IServiceScopeFactory ScopeFactory()
        {
            var provider = Substitute.For<IServiceProvider>();
            provider.GetService(typeof(IJobQueue)).Returns(Queue);
            provider.GetService(typeof(ISpeechProvider)).Returns(SpeechProvider);
            provider.GetService(typeof(ICategorizationStore)).Returns(Store);
            provider.GetService(typeof(ITranscriptionStore)).Returns(TranscriptionStore);
            provider.GetService(typeof(IVoiceFileSource)).Returns(VoiceFiles);
            provider.GetService(typeof(ITranscriber)).Returns(Transcriber);
            provider.GetService(typeof(IChatNotifier)).Returns(Notifier);

            var scope = Substitute.For<IServiceScope>();
            scope.ServiceProvider.Returns(provider);
            var factory = Substitute.For<IServiceScopeFactory>();
            factory.CreateScope().Returns(scope);
            return factory;
        }
    }

    static CategorizationJob CaptureJob(int attemptCount = 1) => new()
    {
        Id = JobId, TransactionId = TransactionId, Kind = JobKind.Transcribe, VoiceFileId = "voice-file-1",
        Status = JobStatus.Claimed, AttemptCount = attemptCount,
        RunAfter = DateTimeOffset.UnixEpoch, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    static CategorizationJob CorrectionJob(int attemptCount = 1) => new()
    {
        Id = JobId, TransactionId = TransactionId, Kind = JobKind.Transcribe, VoiceFileId = "reply-voice",
        SourceMessageId = 900, InstructionDay = new DateOnly(2026, 9, 25),
        Status = JobStatus.Claimed, AttemptCount = attemptCount,
        RunAfter = DateTimeOffset.UnixEpoch, CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    static CategorizationSubject WaitingVoiceNote() =>
        new(TransactionId, "", 111L, 42, "Cash", TransactionStatus.Captured, SentOn, SentOn, [], CaptureKind.Voice);

    static CategorizationSubject RecordedCoffee() =>
        new(TransactionId, "кофе 250", 111L, 42, "Cash", TransactionStatus.Completed, SentOn, SentOn,
            [new RecordedLine("кофе", new Money(250m, CurrencyCode.Rsd), "food-drink", "Food & Drink", null)]);

    static Harness Setup(CategorizationJob job, string transcript = "купил вчера штуку евро", bool keyPresent = true,
        CategorizationSubject? record = null)
    {
        var queue = Substitute.For<IJobQueue>();
        queue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(job);
        queue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        queue.FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        queue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);

        var speechProvider = Substitute.For<ISpeechProvider>();
        speechProvider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(keyPresent);

        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(record ?? WaitingVoiceNote());

        var voiceFiles = Substitute.For<IVoiceFileSource>();
        voiceFiles.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(SyntheticAudio)));

        var transcriber = Substitute.For<ITranscriber>();
        transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(transcript);

        var transcriptionStore = Substitute.For<ITranscriptionStore>();
        transcriptionStore.CompleteCaptureAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        transcriptionStore.CompleteCorrectionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<DateOnly?>(),
            Arg.Any<CancellationToken>()).Returns(true);

        return new Harness(queue, speechProvider, store, transcriptionStore, voiceFiles, transcriber, Substitute.For<IChatNotifier>());
    }

    static TranscriptionWorker CreateWorker(IServiceScopeFactory scopeFactory, FakeTimeProvider? time = null) =>
        new(scopeFactory, time ?? new FakeTimeProvider(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero)),
            new CategorizationWorkerOptions(), WorkerId, Echo, NullLogger<TranscriptionWorker>.Instance);

    static Task<CategorizationTickResult> TickAsync(Harness harness) =>
        CreateWorker(harness.ScopeFactory()).RunTickAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Without_a_speech_key_it_claims_nothing()
    {
        var harness = Setup(CaptureJob(), keyPresent: false);

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Idle);

        await harness.Queue.Received(1).ReleaseExpiredLeasesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await harness.Queue.DidNotReceiveWithAnyArgs().ClaimAsync(default!, default!, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Claims_only_transcriptions()
    {
        var harness = Setup(CaptureJob());

        await TickAsync(harness);

        await harness.Queue.Received(1).ClaimAsync(
            WorkerId, Arg.Is<IReadOnlyCollection<JobKind>>(kinds => kinds.SequenceEqual(new[] { JobKind.Transcribe })),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Transcribes_the_jobs_voice_note_and_hands_the_text_to_the_pipeline()
    {
        var harness = Setup(CaptureJob());

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.VoiceFiles.Received(1).DownloadAsync("voice-file-1", Arg.Any<CancellationToken>());
        await harness.TranscriptionStore.Received(1).CompleteCaptureAsync(TransactionId, "купил вчера штуку евро", Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
        await harness.TranscriptionStore.DidNotReceiveWithAnyArgs().CompleteCorrectionAsync(default, default!, default, default, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_spoken_correction_becomes_a_correction_with_the_transcript_as_its_instruction()
    {
        var harness = Setup(CorrectionJob(), transcript: "нет, полторы тысячи", record: RecordedCoffee());

        await TickAsync(harness);

        await harness.VoiceFiles.Received(1).DownloadAsync("reply-voice", Arg.Any<CancellationToken>());
        await harness.TranscriptionStore.Received(1).CompleteCorrectionAsync(
            TransactionId, "нет, полторы тысячи", 900, new DateOnly(2026, 9, 25), Arg.Any<CancellationToken>());
        await harness.TranscriptionStore.DidNotReceiveWithAnyArgs().CompleteCaptureAsync(default, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_heard_in_a_voice_note_fails_the_record_and_says_so()
    {
        var harness = Setup(CaptureJob(), transcript: "");

        await TickAsync(harness);

        await harness.Queue.Received(1).FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m => m.Text == Echo.HeardNothing.Text),
            Arg.Any<CancellationToken>());
        await harness.TranscriptionStore.DidNotReceiveWithAnyArgs().CompleteCaptureAsync(default, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_heard_in_a_spoken_correction_leaves_the_record_and_says_so()
    {
        var harness = Setup(CorrectionJob(), transcript: "", record: RecordedCoffee());

        await TickAsync(harness);

        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text.StartsWith("Heard nothing in that voice note.\n\nRecorded — Cash", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await harness.TranscriptionStore.DidNotReceiveWithAnyArgs().CompleteCorrectionAsync(default, default!, default, default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_transient_failure_retries_and_tells_nobody_yet()
    {
        var harness = Setup(CaptureJob());
        harness.Transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "Groq transcription failed with status 429."));

        await TickAsync(harness);

        await harness.Queue.Received(1).RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Queue.DidNotReceiveWithAnyArgs().FailAsync(default, default!, default!, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, Arg.Any<CancellationToken>());
        await harness.Notifier.DidNotReceiveWithAnyArgs().EditAsync(default, default, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_terminal_failure_on_a_voice_note_fails_the_record_and_says_it_could_not_be_transcribed()
    {
        var harness = Setup(CaptureJob());
        harness.Transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "Groq transcription failed with status 400."));

        await TickAsync(harness);

        await harness.Queue.Received(1).FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m => m.Text == Echo.TranscriptionFailure.Text),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_terminal_failure_on_a_spoken_correction_leaves_the_record_unchanged_and_says_so()
    {
        var harness = Setup(CorrectionJob(), record: RecordedCoffee());
        harness.Transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "Groq transcription failed with status 400."));

        await TickAsync(harness);

        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text.StartsWith("Could not apply that correction", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_last_transient_attempt_fails_the_record()
    {
        var harness = Setup(CaptureJob(attemptCount: new CategorizationWorkerOptions().MaxAttempts));
        harness.Transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "Groq transcription failed with status 503."));

        await TickAsync(harness);

        await harness.Queue.Received(1).RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await harness.Notifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m => m.Text == Echo.TranscriptionFailure.Text),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_rejected_key_retries_the_job_and_pauses_claiming()
    {
        var harness = Setup(CaptureJob());
        harness.Transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "Groq transcription failed with status 401.").AsAccountLevel());
        var worker = CreateWorker(harness.ScopeFactory());

        await worker.RunTickAsync(TestContext.Current.CancellationToken);
        var second = await worker.RunTickAsync(TestContext.Current.CancellationToken);

        second.Should().Be(CategorizationTickResult.Idle);
        await harness.Queue.Received(1).RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Queue.Received(1).ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_download_is_retried()
    {
        var harness = Setup(CaptureJob());
        harness.VoiceFiles.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("telegram unreachable"));

        await TickAsync(harness);

        await harness.Queue.Received(1).RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Transcriber.DidNotReceiveWithAnyArgs().TranscribeAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_rerun_whose_transcript_was_already_stored_still_succeeds_the_job()
    {
        var harness = Setup(CaptureJob());
        harness.TranscriptionStore.CompleteCaptureAsync(TransactionId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, Arg.Any<CancellationToken>());
        await harness.Notifier.DidNotReceiveWithAnyArgs().EditAsync(default, default, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_SucceedAsync_failure_after_the_hand_off_never_fails_the_record()
    {
        var harness = Setup(CaptureJob());
        harness.Queue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("connection reset"));

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, Arg.Any<CancellationToken>());
        await harness.Queue.DidNotReceiveWithAnyArgs().RetryAsync(default, default!, default, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_broken_container_is_reported_as_failed_without_throwing()
    {
        var worker = CreateWorker(new ThrowingScopeFactory());

        (await worker.RunTickAsync(TestContext.Current.CancellationToken)).Should().Be(CategorizationTickResult.Failed);
    }
}
