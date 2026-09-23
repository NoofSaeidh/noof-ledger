# Phase 3: Voice Capture, Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A voice note the owner sends to the bot becomes the same record a typed message would — transcribed by Groq's `whisper-large-v3`, echoed with what was heard, cancellable and correctable — and a spoken reply corrects a record the way a typed one does.

**Architecture:** The router stores a voice capture at once (capture kind `Voice`, Telegram's `file_id`, no text) together with a `Transcribe` job, in one database transaction, and acknowledges with `🎤 Transcribing…`. A new `TranscriptionWorker` claims only `Transcribe` jobs, downloads the note from Telegram, and sends it through a provider-neutral `ITranscriber`. That transcriber runs over Microsoft.Extensions.AI's `ISpeechToTextClient`, and only `src/Noof.Ledger.Ai/Groq/` knows it is Groq. In one database transaction the transcript becomes the record's `raw_text` and an ordinary `Categorize` job is queued. For a spoken correction, an ordinary `Correct` job is queued with the transcript as its instruction. From there Phase 2's pipeline runs unchanged. The echo gains one first line, `🎤 "<transcript>"`.

**Tech Stack:** .NET 10 · C# · EF Core 10 + Npgsql · PostgreSQL · Microsoft.Extensions.AI 10.5.1 `ISpeechToTextClient` (`[Experimental("MEAI001")]`) · Groq `POST https://api.groq.com/openai/v1/audio/transcriptions` over our own `HttpClient` (no SDK) · Telegram.Bot 22.10.3.1 · Blazor Server + MudBlazor · xUnit v3 on Microsoft Testing Platform · AwesomeAssertions · NSubstitute

**Spec:** `docs/superpowers/specs/2026-09-23-voice-capture.md` (decisions V1–V11). It builds on `docs/superpowers/specs/2026-09-22-natural-language-capture.md` (D1–D8). The predecessor plan is `docs/superpowers/plans/2026-09-22-phase2-natural-language-capture.md`.

> ### ⚠ Decisions in this plan that are expensive to reverse. Read these before Task 1.
>
> Each schema change here migrates `noof_ledger` the next time the operator starts the host. All four follow from the spec (V8, V10); this plan fixes only the names.
>
> 1. **`transactions.raw_text` becomes nullable.** Three columns are added: `capture_kind int NOT NULL DEFAULT 0` (`Text = 0`, `Voice = 1`), `voice_file_id text NULL` and `voice_duration_seconds int NULL`. Check constraint `ck_transactions_capture_has_content`: `(capture_kind = 0 AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL)`. Every existing row is a text capture with text, so the constraint holds on migration.
> 2. **`categorization_jobs` gains `voice_file_id text NULL`**, with check constraint `ck_categorization_jobs_transcription_has_voice_file`: `kind <> 3 OR voice_file_id IS NOT NULL`. The unique index `IX_categorization_jobs_transaction_id_source_message_id` is dropped and replaced by `IX_categorization_jobs_transaction_id_source_message_id_kind` on `(transaction_id, source_message_id, kind) WHERE source_message_id IS NOT NULL`. The index name is one constant, `CategorizationJobConfiguration.SourceMessageIndex`. The configuration and every `catch` that matches the constraint name read that constant, so the name cannot drift the way a literal could.
> 3. **`JobKind.Transcribe = 3`**, stored as its integer like the other kinds. Existing values are never renumbered.
> 4. **`IJobQueue.ClaimAsync(string workerId, IReadOnlyCollection<JobKind> kinds, TimeSpan lease, CancellationToken)`.** This breaks every caller, and there is exactly one: `CategorizationWorker`.

## Global Constraints

- **Bot text is English (P2-5).** Every string the bot sends, and every button label. The transcript inside `🎤 "…"` is the operator's own words, shown as-is.
- **Capture has no validation layer (P2-1).** Only an empty or whitespace-only transcript stops a record (V6), and that is because there is nothing to interpret. Do not add a check on what the transcript says, a length bound, a language check or a "looks like a hallucination" filter.
- **Only `src/Noof.Ledger.Ai/Groq/` knows Groq (V2, D-A).** The word "Groq", the host `api.groq.com`, the secret key `groq-api-key`, the model id and the `.ogg` file name live only there. Nothing outside `src/Noof.Ledger.Ai/` may contain the text "groq" in any case, and `AiBoundaryTests` asserts both rules. Tests may name Groq.
- **The secret key string is exactly `groq-api-key`** and must never change once the operator has stored a key under it, because Data Protection's purpose is derived from it. It is pinned by a test the way `anthropic-api-key` is.
- **The Groq client never retries and never logs.** `MaxRetries`-style behaviour belongs to the job queue. The named `HttpClient` `"groq"` is registered with `.RemoveAllLoggers()`, because the key rides in the `Authorization` header. No exception message and no probe result may contain the key. A test proves it with a marker value.
- **`MEAI001` is suppressed only in `Noof.Ledger.Ai` and `Noof.Ledger.Ai.Tests`,** each with the reason written next to the `<NoWarn>`. No other project references `ISpeechToTextClient`.
- **`noof_ledger` holds real credentials.** Never run a test, a manual check, `dotnet ef database update` or the published host against it. `dotnet ef database update` without `--connection` resolves to `noof_ledger`. Always pass `--connection` naming `noof_ledger_test_template` (see "Updating the test template").
- **Never call a live model or a live speech service.** `NOOF_LEDGER_LIVE_ANTHROPIC_KEY`, `NOOF_LEDGER_LIVE_GROQ_KEY` and `NOOF_LEDGER_LIVE_VOICE_FILE` stay unset. The live tests Task 9 adds must compile and skip. Running them is the operator's decision.
- **No real voice ever enters the repo.** Audio in tests is a synthetic byte array such as `[0x4F, 0x67, 0x67, 0x53, 1, 2, 3]`. Nothing in this codebase decodes audio.
- **After `dotnet ef migrations add`, convert the new migration `.cs` (not the `.Designer.cs`, not the snapshot) to a file-scoped namespace.** `IDE0161` fails the build otherwise.
- **Never edit an applied migration. Never call `EnsureCreated()`.** Tests run against real PostgreSQL clones, never the InMemory provider.
- **Never seed a test with `DateTimeOffset.UtcNow` and then assert exact equality against a value read back from PostgreSQL.** Seed from a fixed literal, or use `BeCloseTo`.
- **Watch every new test fail before making it pass.** For an architecture rule, break the rule deliberately, see the failure name it, and revert.
- **Minimum accessibility.** A type is `internal` unless another assembly names it. Every newly public type is added to `PublicSurfaceTests.Allowed` in the task that creates it. Parallel tasks will conflict on that array: resolve by keeping every name from both sides.
- **Public services go through an interface (CLAUDE.md §3).** Every new public service is an interface registered by its assembly's own `AddNoofXxx`.
- **Modern C#:** file-scoped namespaces, primary constructors, records, collection expressions, pattern matching and `is null`. Comments only explain *why*, and there are no XML doc blocks.
- **TDD:** a failing test comes first for all behaviour. Migrations, DTOs and `Program.cs` wiring are exempt.
- **Test commands.** `dotnet test --project <csproj>` or `dotnet test --solution NoofLedger.slnx`, and `--filter <ClassName>` narrows a run. The full solution run includes the Playwright E2E suite, so it needs Chromium and PostgreSQL. **Serialise full-suite runs across worktrees.** Before `dotnet test --solution`, create the directory `C:\Users\noofs\AppData\Local\Temp\noof-suite.lock` (`mkdir` fails if it exists: wait and retry), and remove it afterwards. Two concurrent full suites exhaust PostgreSQL's connections.
- **Commit trailer.** Every commit message ends with exactly these two lines:
  ```
  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

### Updating the test template (Task 1 only: it is the only migration)

`noof_ledger_test_template` is cloned by `MoneyStorageTests` and the whole E2E suite. Most Persistence tests migrate a fresh empty database themselves. Run this in PowerShell from the repo root after the migration builds:

```powershell
$admin = if ($env:NOOF_TEST_PG) { $env:NOOF_TEST_PG } else { (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection").Trim() }
$template = $admin -replace 'Database=postgres', 'Database=noof_ledger_test_template'
if ($template -notmatch 'Database=noof_ledger_test_template') { throw "Refusing: the connection string does not name the test template." }
dotnet ef database update --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --connection $template
```

The output's last line must name `AddVoiceCapture`. Do **not** run it without `--connection`. The new schema is additive and nullable, so code from before Task 1 still runs against the updated template. A parallel worktree's E2E run is unaffected.

## Verified facts this plan is built on

| Fact | How it was established | Consequence |
|---|---|---|
| `ISpeechToTextClient`, `SpeechToTextOptions` (`SpeechLanguage`, `ModelId`), `SpeechToTextResponse(string)` and `SpeechToTextResponseUpdate` ship in `Microsoft.Extensions.AI.Abstractions` 10.5.1, marked `MEAI001` | XML docs and string scan of the package DLL | No new package. `ISpeechToTextClient` members are `GetTextAsync(Stream, SpeechToTextOptions?, CancellationToken)`, `GetStreamingTextAsync(Stream, SpeechToTextOptions?, CancellationToken)`, `GetService(Type, object?)` and `Dispose()`. If the compiler disagrees on a parameter's nullability, match the compiler |
| Groq accepts OGG/Opus as uploaded, but rejects the name `.oga` with 400. Its error lists `flac mp3 mp4 mpeg mpga m4a ogg opus wav webm` | console.groq.com/docs/speech-to-text, plus github.com/chigwell/telegram-mcp issue #219 | The multipart file part is always named `voice.ogg`, content type `audio/ogg` |
| Groq response for `response_format=json` is `{"text":"…", …}`. Errors are 4xx/5xx with `{"error":{…}}`. The free tier allows 20 requests/min, so 429 is a real, ordinary answer | Groq docs, rate-limits page | 429 is transient, never account-level. 401/403 are account-level |
| `GET https://api.groq.com/openai/v1/models` with the key costs nothing | Groq OpenAI-compatible API | This is the Test-button probe |
| `ITelegramBotClient.DownloadFile(string filePath, Stream destination, CancellationToken)` is an interface member. `GetInfoAndDownloadFile(this ITelegramBotClient, string fileId, Stream, CancellationToken)` is an extension that sends a `GetFileRequest` and then calls `DownloadFile` | Telegram.Bot 22.10.3.1 XML docs | A test substitutes `SendRequest(GetFileRequest)` and `DownloadFile(...)` on `ITelegramBotClient` |
| `Voice : FileBase` exposes `FileId`, `FileUniqueId`, `FileSize` and `Duration` (int seconds). A voice message has `Text == null`. `UpdateType.Message` already delivers voice notes | Same | `allowedUpdates` needs no change. `CorrectionHandler.HandleEditAsync` already ignores a voice edit, because its `edited.Text` guard returns early |
| `EfJobQueue.ClaimAsync`'s `NOT EXISTS (… earlier.created_at < j.created_at …)` guard does not filter on `kind` | `src/Noof.Ledger.Persistence/Jobs/EfJobQueue.cs` | Adding `AND j.kind = ANY(@kinds)` to the *outer* predicate only keeps the ordering rule spanning kinds (V8) |
| `SchemaSnapshotTests` compares `GenerateCreateScript()` with `tests/Noof.Ledger.Persistence.Tests/schema.expected.sql` | Read the test | Task 1 regenerates the snapshot with `dotnet ef dbcontext script`, which opens no connection |
| `Secrets.razor` hard-codes its rows and attaches a Test button to a row whose key matches a registered `ISecretProbe` | Read the page | Task 7 adds one row from an injected `ISpeechProvider`, and the Groq probe gives it a Test button |
| `AiRegistrationTests` asserts that exactly one `ISecretProbe` is registered | Read the test | Task 4 changes that assertion to two probes: the model's and the speech provider's |

## Review Focus

The inputs and conditions below are implied by the spec but easy to leave untested. Each one has a test in the task named.

1. **A `Transcribe` job that runs twice.** Its commit landed, `SucceedAsync` then failed, and the lease expired. The re-run must not queue a second `Categorize` job or overwrite the transcript: one record, one reading. *Tests:* Task 3 `Completing_a_capture_twice_queues_one_reading_and_keeps_the_first_transcript`. Task 8 `A_rerun_whose_transcript_was_already_stored_still_succeeds_the_job`.
2. **A typed correction to a voice record that has no transcript** (a reply to *Heard nothing in that voice note.*). The `Correct` job runs with an empty `RawText`, and `ApplyAsync` writes a revision whose snapshot `raw_text` is JSON `null`. Neither may crash. *Tests:* Task 1 `A_record_with_no_transcript_still_gets_a_revision`. Task 5 `A_voice_record_with_no_transcript_shows_no_heard_line`.
3. **Groq's free-tier 429 when several notes arrive at once.** It must retry with backoff, never pause the worker as a bad key would, and never fail the record. *Tests:* Task 4 `Too_many_requests_is_transient_and_not_account_level`. Task 8 `A_transient_failure_retries_and_tells_nobody_yet`.
4. **A voice note that replies to a bot message which is not a record's echo or prompt.** It is a new voice capture, not a correction. *Test:* Task 6 `A_voice_reply_to_anything_but_an_echo_is_captured_as_a_new_voice_note`.
5. **A stranger's voice note.** It is rejected before `Voice` is read: no capture, no reply, no download. *Test:* Task 6 `Rejects_a_strangers_voice_note_before_reading_it`.

## Task graph and parallel waves

| Wave | Tasks (parallel within a wave, each in its own worktree) | Depends on |
|---|---|---|
| 1 | **Task 1** schema + domain · **Task 4** Groq transcription in `Ai` | — |
| 2 | **Task 2** queue claims by kind · **Task 3** voice capture storage · **Task 5** echo texts · **Task 7** settings row | T2, T3, T5 need T1; T7 needs T4 |
| 3 | **Task 6** Telegram: voice capture, voice reply, download | T3, T5 |
| 4 | **Task 8** `TranscriptionWorker` | T2, T3, T4, T5, T6 |
| 5 | **Task 9** live test, docs, status | all |

Merge each wave into `phase-3-voice` with `--no-ff` before the next wave starts. The integration merge resolves `PublicSurfaceTests.Allowed` conflicts by keeping both sides.

## File structure

| File | Responsibility | Task |
|---|---|---|
| `src/Noof.Ledger.Domain/CaptureKind.cs` | `Text`/`Voice` | 1 |
| `src/Noof.Ledger.Persistence/Migrations/*_AddVoiceCapture.cs` | the one migration | 1 |
| `src/Noof.Ledger.Application/Transcription/ITranscriber.cs` | audio stream → transcript text | 4 |
| `src/Noof.Ledger.Application/Transcription/ISpeechProvider.cs` | "is a speech key configured", label, probe | 4 |
| `src/Noof.Ledger.Ai/ISpeechToTextClientFactory.cs` | internal seam, mirror of `IChatClientFactory` | 4 |
| `src/Noof.Ledger.Ai/SpeechTranscriber.cs` | provider-neutral `ITranscriber` | 4 |
| `src/Noof.Ledger.Ai/Groq/GroqOptions.cs` · `GroqSpeechToTextClient.cs` · `GroqSpeechToTextClientFactory.cs` · `GroqRegistration.cs` | everything Groq | 4 |
| `src/Noof.Ledger.Application/Capture/CapturedVoice.cs` | a voice capture's facts | 3 |
| `src/Noof.Ledger.Application/Transcription/ITranscriptionStore.cs` | hand a transcript to the pipeline | 3 |
| `src/Noof.Ledger.Persistence/Transcription/EfTranscriptionStore.cs` | its implementation | 3 |
| `src/Noof.Ledger.Application/Transcription/IVoiceFileSource.cs` | download a voice note by `file_id` | 6 |
| `src/Noof.Ledger.Telegram/TelegramVoiceFileSource.cs` | its implementation | 6 |
| `src/Noof.Ledger.Host/Workers/TranscriptionWorker.cs` | claims `Transcribe` jobs | 8 |
| `tests/Noof.Ledger.Ai.Tests/Groq/LiveTranscriptionGate.cs` · `LiveTranscriptionTests.cs` | opt-in live test | 9 |

---
### Task 1: Voice capture schema, and a raw text that may be missing

**Files:**
- Create: `src/Noof.Ledger.Domain/CaptureKind.cs`
- Modify: `src/Noof.Ledger.Domain/Transaction.cs`, `src/Noof.Ledger.Domain/CategorizationJob.cs`, `src/Noof.Ledger.Domain/JobKind.cs`
- Modify: `src/Noof.Ledger.Persistence/Configurations/TransactionConfiguration.cs`, `src/Noof.Ledger.Persistence/Configurations/CategorizationJobConfiguration.cs`
- Create: `src/Noof.Ledger.Persistence/Migrations/<timestamp>_AddVoiceCapture.cs` (+ generated `.Designer.cs`, regenerated `LedgerDbContextModelSnapshot.cs`)
- Modify: `src/Noof.Ledger.Persistence/Editing/EfRecordEditor.cs` (the index-name constant)
- Modify: `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs` (`CategorizationSubject` gains `CaptureKind`)
- Modify: `src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs`, `src/Noof.Ledger.Persistence/Revisions/RevisionLog.cs`, `src/Noof.Ledger.Persistence/Reporting/EfSpendingReadModel.cs`
- Modify: `tests/Noof.Ledger.Persistence.Tests/schema.expected.sql`, `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/VoiceSchemaTests.cs`
- Modify: `tests/Noof.Ledger.Persistence.Tests/EfCategorizationStoreTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `public enum CaptureKind { Text = 0, Voice = 1 }` (Domain).
  - `Transaction`: `required string? RawText { get; set; }`, `CaptureKind CaptureKind { get; init; }`, `string? VoiceFileId { get; init; }`, `int? VoiceDurationSeconds { get; init; }`.
  - `CategorizationJob.VoiceFileId` (`string?`, init).
  - `JobKind.Transcribe = 3`.
  - `CategorizationJobConfiguration.SourceMessageIndex` (`internal const string`), whose value is `"IX_categorization_jobs_transaction_id_source_message_id_kind"`.
  - `CategorizationSubject(..., IReadOnlyList<RecordedLine> Lines, CaptureKind CaptureKind = CaptureKind.Text)`. It is a new *last, optional* positional parameter, so every existing `new CategorizationSubject(...)` still compiles. `RawText` stays `string`: a voice capture awaiting its transcript reads back as `""`.

- [ ] **Step 1: Write the failing schema tests**

Create `tests/Noof.Ledger.Persistence.Tests/VoiceSchemaTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Configurations;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class VoiceSchemaTests(PostgresFixture fixture)
{
    static readonly Guid DefaultWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
    static int nextMessageId = 1000;

    static Transaction NewTransaction(CaptureKind kind, string? rawText, string? voiceFileId) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = DefaultWalletId,
        RawText = rawText,
        CaptureKind = kind,
        VoiceFileId = voiceFileId,
        VoiceDurationSeconds = voiceFileId is null ? null : 4,
        Status = TransactionStatus.Captured,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = Now,
        OccurredOn = new DateOnly(2026, 9, 24),
        TelegramChatId = 111,
        TelegramMessageId = Interlocked.Increment(ref nextMessageId),
        CreatedAt = Now,
    };

    static CategorizationJob NewJob(Guid transactionId, JobKind kind, int? sourceMessageId = null, string? voiceFileId = null,
        string? instruction = null) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        Kind = kind,
        SourceMessageId = sourceMessageId,
        VoiceFileId = voiceFileId,
        Instruction = instruction,
        Status = JobStatus.Pending,
        AttemptCount = 0,
        RunAfter = Now,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    static async Task<string?> ViolatedConstraintAsync(LedgerDbContext db)
    {
        try
        {
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException postgres)
        {
            return postgres.ConstraintName;
        }
    }

    [Fact]
    public async Task A_text_capture_without_text_is_refused()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Transactions.Add(NewTransaction(CaptureKind.Text, rawText: null, voiceFileId: null));

        (await ViolatedConstraintAsync(db)).Should().Be("ck_transactions_capture_has_content");
    }

    [Fact]
    public async Task A_voice_capture_without_a_voice_file_is_refused()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Transactions.Add(NewTransaction(CaptureKind.Voice, rawText: null, voiceFileId: null));

        (await ViolatedConstraintAsync(db)).Should().Be("ck_transactions_capture_has_content");
    }

    [Fact]
    public async Task A_voice_capture_awaiting_its_transcript_is_accepted()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewTransaction(CaptureKind.Voice, rawText: null, voiceFileId: "voice-file-1");
        db.Transactions.Add(transaction);

        (await ViolatedConstraintAsync(db)).Should().BeNull();

        db.ChangeTracker.Clear();
        var stored = await db.Transactions.SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken);
        stored.CaptureKind.Should().Be(CaptureKind.Voice);
        stored.RawText.Should().BeNull();
        stored.VoiceFileId.Should().Be("voice-file-1");
        stored.VoiceDurationSeconds.Should().Be(4);
    }

    [Fact]
    public async Task A_transcription_job_without_a_voice_file_is_refused()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewTransaction(CaptureKind.Voice, rawText: null, voiceFileId: "voice-file-1");
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.CategorizationJobs.Add(NewJob(transaction.Id, JobKind.Transcribe, voiceFileId: null));

        (await ViolatedConstraintAsync(db)).Should().Be("ck_categorization_jobs_transcription_has_voice_file");
    }

    [Fact]
    public async Task One_reply_may_queue_a_transcription_and_the_correction_it_produces()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewTransaction(CaptureKind.Text, rawText: "кофе 250", voiceFileId: null);
        db.Transactions.Add(transaction);
        db.CategorizationJobs.Add(NewJob(transaction.Id, JobKind.Transcribe, sourceMessageId: 900, voiceFileId: "reply-voice"));
        db.CategorizationJobs.Add(NewJob(transaction.Id, JobKind.Correct, sourceMessageId: 900, instruction: "нет, 1500"));

        (await ViolatedConstraintAsync(db)).Should().BeNull();
    }

    [Fact]
    public async Task The_same_reply_cannot_queue_two_transcriptions()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewTransaction(CaptureKind.Text, rawText: "кофе 250", voiceFileId: null);
        db.Transactions.Add(transaction);
        db.CategorizationJobs.Add(NewJob(transaction.Id, JobKind.Transcribe, sourceMessageId: 900, voiceFileId: "reply-voice"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.CategorizationJobs.Add(NewJob(transaction.Id, JobKind.Transcribe, sourceMessageId: 900, voiceFileId: "reply-voice"));

        (await ViolatedConstraintAsync(db)).Should().Be(CategorizationJobConfiguration.SourceMessageIndex);
    }
}
```

Append these two tests to `tests/Noof.Ledger.Persistence.Tests/EfCategorizationStoreTests.cs` (inside the class; add `using System.Text.Json;` at the top):

```csharp
    static readonly Guid SeededDefaultWalletId = new("00000000-0000-0000-0000-000000000001");

    static Transaction VoiceAwaitingTranscript() => new()
    {
        Id = Guid.NewGuid(),
        WalletId = SeededDefaultWalletId,
        RawText = null,
        CaptureKind = CaptureKind.Voice,
        VoiceFileId = "voice-file-1",
        VoiceDurationSeconds = 4,
        Status = TransactionStatus.Failed,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
        OccurredOn = new DateOnly(2026, 9, 21),
        TelegramChatId = 111,
        TelegramMessageId = 77,
        BotMessageId = 78,
        CreatedAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task A_voice_capture_with_no_transcript_reads_back_as_voice_with_empty_text()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = VoiceAwaitingTranscript();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db, Clock);

        var subject = await store.GetSubjectAsync(transaction.Id, TestContext.Current.CancellationToken);

        subject!.CaptureKind.Should().Be(CaptureKind.Voice);
        subject.RawText.Should().BeEmpty();
    }

    [Fact]
    public async Task A_record_with_no_transcript_still_gets_a_revision()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = VoiceAwaitingTranscript();
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var store = new EfCategorizationStore(db, Clock);

        await store.ApplyAsync(
            transaction.Id,
            new CategorizationOutcome([], new DateOnly(2026, 9, 21), JobKind.Correct, "это было 250 динаров за кофе"),
            TestContext.Current.CancellationToken);

        var revision = await db.TransactionRevisions.SingleAsync(TestContext.Current.CancellationToken);
        JsonDocument.Parse(revision.Snapshot).RootElement.GetProperty("raw_text").ValueKind.Should().Be(JsonValueKind.Null);
    }
```

In `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`, add `"CaptureKind"` to the `["Noof.Ledger.Domain"]` array.

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL to compile. `CaptureKind`, `VoiceFileId`, `JobKind.Transcribe`, `CategorizationJobConfiguration.SourceMessageIndex` and `CategorizationSubject.CaptureKind` do not exist. The compile errors are the red here, because the schema test cannot exist without the model.

- [ ] **Step 3: Add the domain types**

`src/Noof.Ledger.Domain/CaptureKind.cs`:

```csharp
namespace Noof.Ledger.Domain;

public enum CaptureKind
{
    Text = 0,
    Voice = 1,
}
```

In `src/Noof.Ledger.Domain/Transaction.cs`, replace `public required string RawText { get; set; }` with:

```csharp
    // Null only while a voice capture waits for its transcript (or when none ever came); a check
    // constraint requires it for every text capture.
    public required string? RawText { get; set; }

    public CaptureKind CaptureKind { get; init; }

    // Telegram's id for the voice note. It is enough to download the audio again, so the audio itself is never stored.
    public string? VoiceFileId { get; init; }

    public int? VoiceDurationSeconds { get; init; }
```

In `src/Noof.Ledger.Domain/CategorizationJob.cs`, after `InstructionDay`, add:

```csharp
    // The voice note to transcribe, set for a Transcribe job only, which a check constraint holds. For a spoken
    // correction it is the reply's own voice, not the record's.
    public string? VoiceFileId { get; init; }
```

In `src/Noof.Ledger.Domain/JobKind.cs`, append `Transcribe = 3,` after `Reinterpret = 2,`.

- [ ] **Step 4: Map the columns and constraints**

`src/Noof.Ledger.Persistence/Configurations/TransactionConfiguration.cs`: replace `builder.ToTable("transactions");` with

```csharp
        builder.ToTable("transactions", table => table.HasCheckConstraint(
            "ck_transactions_capture_has_content",
            "(capture_kind = 0 AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL)"));
```

Next, replace `builder.Property(t => t.RawText).HasColumnName("raw_text").IsRequired();` with the lines below:

```csharp
        builder.Property(t => t.RawText).HasColumnName("raw_text");
        builder.Property(t => t.CaptureKind).HasColumnName("capture_kind");
        builder.Property(t => t.VoiceFileId).HasColumnName("voice_file_id");
        builder.Property(t => t.VoiceDurationSeconds).HasColumnName("voice_duration_seconds");
```

`src/Noof.Ledger.Persistence/Configurations/CategorizationJobConfiguration.cs`: this step makes four changes.

1. Add the constant as the first member of the class, with its comment:

```csharp
    // One name for the index and for every catch that recognises a redelivered reply by it.
    internal const string SourceMessageIndex = "IX_categorization_jobs_transaction_id_source_message_id_kind";
```

2. Replace the `ToTable` call with:

```csharp
        builder.ToTable("categorization_jobs", table =>
        {
            table.HasCheckConstraint(
                "ck_categorization_jobs_correction_has_instruction", "kind <> 1 OR instruction IS NOT NULL");
            table.HasCheckConstraint(
                "ck_categorization_jobs_transcription_has_voice_file", "kind <> 3 OR voice_file_id IS NOT NULL");
        });
```

3. Add `builder.Property(j => j.VoiceFileId).HasColumnName("voice_file_id");` after the `InstructionDay` mapping.

4. Replace the `(TransactionId, SourceMessageId)` index with this one. The key gains `kind` because a spoken correction's `Transcribe` job and the `Correct` job it produces share the reply's message id (V7):

```csharp
        builder.HasIndex(j => new { j.TransactionId, j.SourceMessageId, j.Kind })
            .IsUnique()
            .HasFilter("source_message_id IS NOT NULL")
            .HasDatabaseName(SourceMessageIndex);
```

In `src/Noof.Ledger.Persistence/Editing/EfRecordEditor.cs`, delete `const string CorrectionSourceIndex = "IX_categorization_jobs_transaction_id_source_message_id";`. Replace its one use in the `catch` filter with `CategorizationJobConfiguration.SourceMessageIndex`, and add `using Noof.Ledger.Persistence.Configurations;`.

- [ ] **Step 5: Carry `CaptureKind` into the subject, and let a missing text through**

`src/Noof.Ledger.Application/Categorization/CategorizationContract.cs`: change the `CategorizationSubject` record to

```csharp
public sealed record CategorizationSubject(
    Guid TransactionId, string RawText, long TelegramChatId, int? BotMessageId, string WalletName,
    TransactionStatus Status, DateOnly SentOn, DateOnly OccurredOn, IReadOnlyList<RecordedLine> Lines,
    CaptureKind CaptureKind = CaptureKind.Text);
```

`src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs`, `GetSubjectAsync`: add `t.CaptureKind` to the anonymous header projection. Then change the constructor call to

```csharp
        // A voice capture has no text until its transcript arrives, and none at all when nothing was heard;
        // the pipeline and the echo read that as empty, which is what it is.
        return new CategorizationSubject(
            header.Id, header.RawText ?? string.Empty, header.TelegramChatId, header.BotMessageId, header.WalletName,
            header.Status, ZonedClock.LocalDate(header.OccurredAt, header.TimeZoneId), header.OccurredOn, lines,
            header.CaptureKind);
```

`src/Noof.Ledger.Persistence/Revisions/RevisionLog.cs`: in `sealed record Snapshot`, change `string RawText` to `string? RawText`. No other change is needed there.

`src/Noof.Ledger.Persistence/Reporting/EfSpendingReadModel.cs`: in the headers projection, change `t.RawText,` to `RawText = t.RawText ?? string.Empty,`. The read model's `RecentTransaction.RawText` stays `string`.

Build: `dotnet build NoofLedger.slnx`. Expected: success. If another nullable warning names `RawText`, map it the same way (`?? string.Empty`) at the point where the value leaves Persistence. Do not widen an Application record to `string?` for it.

- [ ] **Step 6: Add the migration and convert it to a file-scoped namespace**

Run: `dotnet ef migrations add AddVoiceCapture --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence`

This opens no database connection. Convert only the new `…_AddVoiceCapture.cs` to `namespace Noof.Ledger.Persistence.Migrations;`, matching `20260923155254_AddInstructionDay.cs`. Leave `.Designer.cs` and `LedgerDbContextModelSnapshot.cs` exactly as generated. Read the generated `Up`. It must contain all of these and nothing else:
- `AlterColumn<string>` making `raw_text` nullable;
- `AddColumn<int>` `capture_kind`, `nullable: false`, `defaultValue: 0`;
- `AddColumn<string>` `voice_file_id` on `transactions`;
- `AddColumn<int>` `voice_duration_seconds`, nullable;
- `AddColumn<string>` `voice_file_id` on `categorization_jobs`;
- `DropIndex` `IX_categorization_jobs_transaction_id_source_message_id`;
- `CreateIndex` `IX_categorization_jobs_transaction_id_source_message_id_kind` on `transaction_id, source_message_id, kind`, `unique: true`, `filter: "source_message_id IS NOT NULL"`;
- two `AddCheckConstraint` calls.

`Down` must reverse each of them. If `defaultValue: 0` is missing on `capture_kind`, add it by hand: existing rows must become text captures.

Regenerate the schema snapshot (also connection-free):
`dotnet ef dbcontext script --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --output tests/Noof.Ledger.Persistence.Tests/schema.expected.sql`

Diff it with `git diff tests/Noof.Ledger.Persistence.Tests/schema.expected.sql`. The only changes must be the ones listed above. If the file's previous form differs from `dbcontext script` output in anything else (a header line, for example), keep the file's previous form and hand-apply only the schema delta.

- [ ] **Step 7: Run the tests and see them pass**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: PASS, including `VoiceSchemaTests` (6), the two new `EfCategorizationStoreTests`, `SchemaSnapshotTests` and `MigrationContractTests.The_model_has_no_pending_changes`.

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Update the test template, then run the whole solution**

Run the "Updating the test template" command. The last line must name `AddVoiceCapture`.
Run (holding the suite lock): `dotnet test --solution NoofLedger.slnx`
Expected: every test passes. The count is Phase 2's 577 plus the 8 added here.

- [ ] **Step 9: Commit**

```bash
git add src/Noof.Ledger.Domain src/Noof.Ledger.Application/Categorization/CategorizationContract.cs src/Noof.Ledger.Persistence tests/Noof.Ledger.Persistence.Tests tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs
git commit -m "feat(persistence): voice capture schema - capture kind, voice file ids, Transcribe jobs (V10)"
```

### Task 2: The queue claims by kind

**Files:**
- Modify: `src/Noof.Ledger.Application/Jobs/IJobQueue.cs`
- Modify: `src/Noof.Ledger.Persistence/Jobs/EfJobQueue.cs`
- Modify: `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`
- Test: `tests/Noof.Ledger.Persistence.Tests/EfJobQueueTests.cs`, `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`

**Interfaces:**
- Consumes (Task 1): `JobKind.Transcribe`, `CategorizationJob.VoiceFileId`, the `voice_file_id` column.
- Produces: `Task<CategorizationJob?> ClaimAsync(string workerId, IReadOnlyCollection<JobKind> kinds, TimeSpan lease, CancellationToken cancellationToken)`. `CategorizationWorker` claims `[Categorize, Correct, Reinterpret]`. Task 8's `TranscriptionWorker` claims `[Transcribe]`.

- [ ] **Step 1: Write the failing queue tests**

In `tests/Noof.Ledger.Persistence.Tests/EfJobQueueTests.cs`:

1. Give the `NewJob` helper one more optional parameter, `string? voiceFileId = null`, and set `VoiceFileId = voiceFileId` in its initialiser.
2. Add the field below and pass it as the new second argument of **every existing** `queue.ClaimAsync(...)` call in the file. The existing tests keep their meaning: claim anything.

```csharp
    static readonly JobKind[] AnyKind = Enum.GetValues<JobKind>();
```

3. Append:

```csharp
    [Fact]
    public async Task Claims_only_the_kinds_it_asks_for()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero));
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var voiceTransaction = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        var textTransaction = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        var transcription = NewJob(voiceTransaction, time.GetUtcNow().AddMinutes(-2), kind: JobKind.Transcribe, voiceFileId: "voice-file-1");
        var reading = NewJob(textTransaction, time.GetUtcNow().AddMinutes(-1));
        db.CategorizationJobs.AddRange(transcription, reading);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var first = await queue.ClaimAsync("worker-a", [JobKind.Categorize, JobKind.Correct, JobKind.Reinterpret],
            TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        var second = await queue.ClaimAsync("worker-a", [JobKind.Categorize, JobKind.Correct, JobKind.Reinterpret],
            TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        first!.Id.Should().Be(reading.Id, "the transcription is older and due, but not a kind this worker claims");
        second.Should().BeNull();
    }

    [Fact]
    public async Task A_pending_transcription_holds_back_the_reading_queued_after_it_even_for_a_worker_that_cannot_claim_it()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero));
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var transactionId = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        db.CategorizationJobs.AddRange(
            NewJob(transactionId, time.GetUtcNow().AddMinutes(-2), kind: JobKind.Transcribe, voiceFileId: "voice-file-1"),
            NewJob(transactionId, time.GetUtcNow().AddMinutes(-1)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var claimed = await queue.ClaimAsync("worker-a", [JobKind.Categorize], TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        claimed.Should().BeNull("the ordering rule spans kinds: nothing overtakes an earlier job for the same record");
    }

    [Fact]
    public async Task A_claimed_transcription_carries_its_voice_file()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero));
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var transactionId = await SeedTransactionAsync(db, time.GetUtcNow(), TestContext.Current.CancellationToken);
        db.CategorizationJobs.Add(NewJob(transactionId, time.GetUtcNow().AddMinutes(-1), kind: JobKind.Transcribe, voiceFileId: "voice-file-1"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var claimed = await queue.ClaimAsync("worker-a", [JobKind.Transcribe], TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        claimed!.Kind.Should().Be(JobKind.Transcribe);
        claimed.VoiceFileId.Should().Be("voice-file-1");
    }
```

In `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`:

1. Replace every `ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())` with `ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())`.
2. Replace the one `DidNotReceive().ClaimAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())` with `DidNotReceive().ClaimAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())`.
3. Append:

```csharp
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
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL. `ClaimAsync` has no overload taking a kinds collection.

- [ ] **Step 3: Change the interface, the SQL and the caller**

`src/Noof.Ledger.Application/Jobs/IJobQueue.cs`: replace the `ClaimAsync` declaration. Extend the comment above it with the kinds sentence:

```csharp
    // Claims one pending, due job of one of the given kinds for workerId and marks it Claimed. lease is how long the
    // claim is held before ReleaseExpiredLeasesAsync may release it back to Pending - there is no separate
    // lease-expiry column, so claiming sets RunAfter to now + lease. Returns null when nothing claimable is pending
    // and due. The kind filter never weakens the ordering rule: a job still waits for every earlier job of the same
    // transaction, whatever that job's kind.
    Task<CategorizationJob?> ClaimAsync(
        string workerId, IReadOnlyCollection<JobKind> kinds, TimeSpan lease, CancellationToken cancellationToken);
```

`src/Noof.Ledger.Persistence/Jobs/EfJobQueue.cs`, `ClaimAsync`:
- signature to match the interface;
- in the inner `SELECT`, change `WHERE j.status = 0 AND j.run_after <= @now` to `WHERE j.status = 0 AND j.run_after <= @now AND j.kind = ANY(@kinds)`. Leave the `NOT EXISTS` subquery exactly as it is;
- append `, voice_file_id` to the `RETURNING` list after `instruction_day`;
- add the parameter `new NpgsqlParameter("kinds", kinds.Select(kind => (int)kind).ToArray())` after the other three.

`src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`: add the field, then change the claim call to `var job = await jobQueue.ClaimAsync(workerId, ClaimableKinds, options.Lease, cancellationToken);`

```csharp
    // Every kind but Transcribe. A transcription needs the speech provider, which is TranscriptionWorker's to check.
    static readonly JobKind[] ClaimableKinds = [JobKind.Categorize, JobKind.Correct, JobKind.Reinterpret];
```

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter EfJobQueueTests`
Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: PASS.

To see the guard work, temporarily delete `AND j.kind = ANY(@kinds)` from the SQL and run `Claims_only_the_kinds_it_asks_for`. It must fail, claiming the transcription. Put the line back.

- [ ] **Step 5: Commit**

```bash
git add src/Noof.Ledger.Application/Jobs/IJobQueue.cs src/Noof.Ledger.Persistence/Jobs/EfJobQueue.cs src/Noof.Ledger.Host/Workers/CategorizationWorker.cs tests/Noof.Ledger.Persistence.Tests/EfJobQueueTests.cs tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs
git commit -m "feat(queue): claim by kind so transcription and categorisation have their own workers (V8)"
```

---

### Task 3: Storing a voice capture, a spoken correction, and a finished transcript

**Files:**
- Create: `src/Noof.Ledger.Application/Capture/CapturedVoice.cs`
- Modify: `src/Noof.Ledger.Application/Capture/ICaptureStore.cs`, `src/Noof.Ledger.Application/Editing/IRecordEditor.cs`
- Create: `src/Noof.Ledger.Application/Transcription/ITranscriptionStore.cs`
- Modify: `src/Noof.Ledger.Persistence/Capture/EfCaptureStore.cs`, `src/Noof.Ledger.Persistence/Editing/EfRecordEditor.cs`
- Create: `src/Noof.Ledger.Persistence/Transcription/EfTranscriptionStore.cs`
- Modify: `src/Noof.Ledger.Persistence/PersistenceRegistration.cs`, `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`
- Test: `tests/Noof.Ledger.Persistence.Tests/CapturedVoiceTests.cs` (new), `EfCaptureStoreTests.cs`, `EfRecordEditorTests.cs`, `EfTranscriptionStoreTests.cs` (new)

**Interfaces:**
- Consumes (Task 1): `CaptureKind`, `Transaction.VoiceFileId`/`VoiceDurationSeconds`, `CategorizationJob.VoiceFileId`, `JobKind.Transcribe`, `CategorizationJobConfiguration.SourceMessageIndex`.
- Produces:
  - `public sealed record CapturedVoice(long ChatId, int MessageId, string VoiceFileId, int DurationSeconds, DateTimeOffset SentAt)` (namespace `Noof.Ledger.Application.Capture`).
  - `ICaptureStore.CaptureVoiceAsync(CapturedVoice voice, string timeZoneId, CancellationToken) → Task<Guid>`.
  - `IRecordEditor.RequestVoiceCorrectionAsync(Guid transactionId, string voiceFileId, int sourceMessageId, DateTimeOffset sentAt, CancellationToken) → Task<bool>`.
  - `public interface ITranscriptionStore` (namespace `Noof.Ledger.Application.Transcription`) with:
    - `Task<bool> CompleteCaptureAsync(Guid transactionId, string transcript, CancellationToken)`;
    - `Task<bool> CompleteCorrectionAsync(Guid transactionId, string transcript, int sourceMessageId, DateOnly? instructionDay, CancellationToken)`.
  - It is registered scoped in `AddNoofPersistence`.

- [ ] **Step 1: Write the failing tests**

`tests/Noof.Ledger.Persistence.Tests/CapturedVoiceTests.cs`:

```csharp
using AwesomeAssertions;
using Noof.Ledger.Application.Capture;

namespace Noof.Ledger.Persistence.Tests;

public class CapturedVoiceTests
{
    [Fact]
    public void Refuses_a_send_time_that_is_not_utc()
    {
        var act = () => new CapturedVoice(111, 5, "voice-file-1", 4, new DateTimeOffset(2026, 9, 24, 11, 0, 0, TimeSpan.FromHours(2)));

        act.Should().Throw<ArgumentException>().WithParameterName("SentAt");
    }
}
```

Append to `tests/Noof.Ledger.Persistence.Tests/EfCaptureStoreTests.cs`:

```csharp
    [Fact]
    public async Task Captures_a_voice_note_with_no_text_and_a_transcription_job()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 24, 21, 30, 0, TimeSpan.Zero);
        var store = new EfCaptureStore(db, new FakeTimeProvider(now));

        var transactionId = await store.CaptureVoiceAsync(
            new CapturedVoice(111, 5, "voice-file-1", 4, now), "Europe/Belgrade", TestContext.Current.CancellationToken);

        var transaction = await db.Transactions.SingleAsync(TestContext.Current.CancellationToken);
        transaction.Id.Should().Be(transactionId);
        transaction.CaptureKind.Should().Be(CaptureKind.Voice);
        transaction.RawText.Should().BeNull();
        transaction.VoiceFileId.Should().Be("voice-file-1");
        transaction.VoiceDurationSeconds.Should().Be(4);
        transaction.Status.Should().Be(TransactionStatus.Captured);
        transaction.OccurredOn.Should().Be(new DateOnly(2026, 9, 24), "21:30 UTC is 23:30 in Belgrade, still the 24th");

        var job = await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.TransactionId.Should().Be(transactionId);
        job.Kind.Should().Be(JobKind.Transcribe);
        job.VoiceFileId.Should().Be("voice-file-1");
        job.SourceMessageId.Should().BeNull("a capture's transcription is not a correction");
        job.Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public async Task A_redelivered_voice_note_is_captured_once()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
        var store = new EfCaptureStore(db, new FakeTimeProvider(now));
        var voice = new CapturedVoice(111, 5, "voice-file-1", 4, now);

        var first = await store.CaptureVoiceAsync(voice, "Europe/Belgrade", TestContext.Current.CancellationToken);
        var second = await store.CaptureVoiceAsync(voice, "Europe/Belgrade", TestContext.Current.CancellationToken);

        second.Should().Be(first);
        (await db.Transactions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await db.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }
```

These rely on the default wallet the migrations seed. Add any missing `using` lines (`Noof.Ledger.Application.Capture`, `Noof.Ledger.Domain`, `Microsoft.Extensions.Time.Testing`).

Append to `tests/Noof.Ledger.Persistence.Tests/EfRecordEditorTests.cs`:

```csharp
    [Fact]
    public async Task A_spoken_correction_queues_its_voice_for_transcription_once()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);
        var editor = new EfRecordEditor(db, Clock);
        var sentAt = new DateTimeOffset(2026, 9, 23, 22, 30, 0, TimeSpan.Zero);

        var first = await editor.RequestVoiceCorrectionAsync(transaction.Id, "reply-voice", 900, sentAt, TestContext.Current.CancellationToken);
        var second = await editor.RequestVoiceCorrectionAsync(transaction.Id, "reply-voice", 900, sentAt, TestContext.Current.CancellationToken);

        first.Should().BeTrue();
        second.Should().BeFalse("Telegram redelivered the same reply");
        var job = await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.Kind.Should().Be(JobKind.Transcribe);
        job.VoiceFileId.Should().Be("reply-voice");
        job.SourceMessageId.Should().Be(900);
        job.Instruction.Should().BeNull("the instruction is whatever the transcript turns out to be");
        job.InstructionDay.Should().Be(new DateOnly(2026, 9, 24), "22:30 UTC on the 23rd is the 24th in Belgrade");
    }
```

`tests/Noof.Ledger.Persistence.Tests/EfTranscriptionStoreTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Transcription;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfTranscriptionStoreTests(PostgresFixture fixture)
{
    static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero));
    static readonly Guid DefaultWalletId = new("00000000-0000-0000-0000-000000000001");

    static async Task<Transaction> SeedAsync(LedgerDbContext db, CaptureKind kind)
    {
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = DefaultWalletId,
            RawText = kind == CaptureKind.Text ? "кофе 250" : null,
            CaptureKind = kind,
            VoiceFileId = kind == CaptureKind.Voice ? "voice-file-1" : null,
            VoiceDurationSeconds = kind == CaptureKind.Voice ? 4 : null,
            Status = TransactionStatus.Captured,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero),
            OccurredOn = new DateOnly(2026, 9, 24),
            TelegramChatId = 111,
            TelegramMessageId = 5,
            BotMessageId = 6,
            CreatedAt = new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero),
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        return transaction;
    }

    [Fact]
    public async Task Completing_a_capture_stores_the_transcript_and_queues_its_first_reading()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db, CaptureKind.Voice);
        var store = new EfTranscriptionStore(db, Clock);

        var completed = await store.CompleteCaptureAsync(transaction.Id, "купил вчера штуку евро", TestContext.Current.CancellationToken);

        completed.Should().BeTrue();
        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(TestContext.Current.CancellationToken)).RawText.Should().Be("купил вчера штуку евро");
        var job = await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.Kind.Should().Be(JobKind.Categorize);
        job.Status.Should().Be(JobStatus.Pending);
        job.SourceMessageId.Should().BeNull();
    }

    [Fact]
    public async Task Completing_a_capture_twice_queues_one_reading_and_keeps_the_first_transcript()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db, CaptureKind.Voice);
        var store = new EfTranscriptionStore(db, Clock);

        await store.CompleteCaptureAsync(transaction.Id, "купил вчера штуку евро", TestContext.Current.CancellationToken);
        var again = await store.CompleteCaptureAsync(transaction.Id, "что-то другое", TestContext.Current.CancellationToken);

        again.Should().BeFalse("a Transcribe job re-run after its commit must not read the note twice");
        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(TestContext.Current.CancellationToken)).RawText.Should().Be("купил вчера штуку евро");
        (await db.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Completing_a_spoken_correction_queues_a_correction_with_the_transcript_as_its_instruction()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db, CaptureKind.Text);
        var store = new EfTranscriptionStore(db, Clock);

        var completed = await store.CompleteCorrectionAsync(
            transaction.Id, "нет, полторы тысячи", 900, new DateOnly(2026, 9, 25), TestContext.Current.CancellationToken);

        completed.Should().BeTrue();
        var job = await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.Kind.Should().Be(JobKind.Correct);
        job.Instruction.Should().Be("нет, полторы тысячи");
        job.SourceMessageId.Should().Be(900);
        job.InstructionDay.Should().Be(new DateOnly(2026, 9, 25));
    }

    [Fact]
    public async Task Completing_the_same_spoken_correction_twice_queues_it_once()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db, CaptureKind.Text);
        var store = new EfTranscriptionStore(db, Clock);

        await store.CompleteCorrectionAsync(transaction.Id, "нет, 1500", 900, null, TestContext.Current.CancellationToken);
        var again = await store.CompleteCorrectionAsync(transaction.Id, "нет, 1500", 900, null, TestContext.Current.CancellationToken);

        again.Should().BeFalse();
        (await db.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task A_spoken_correction_may_share_its_reply_with_its_own_transcription_job()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db, CaptureKind.Text);
        db.CategorizationJobs.Add(new CategorizationJob
        {
            Id = Guid.NewGuid(), TransactionId = transaction.Id, Kind = JobKind.Transcribe, VoiceFileId = "reply-voice",
            SourceMessageId = 900, Status = JobStatus.Claimed, AttemptCount = 1, RunAfter = Clock.GetUtcNow(),
            CreatedAt = Clock.GetUtcNow().AddMinutes(-1), UpdatedAt = Clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfTranscriptionStore(db, Clock);

        var completed = await store.CompleteCorrectionAsync(transaction.Id, "нет, 1500", 900, null, TestContext.Current.CancellationToken);

        completed.Should().BeTrue("the unique key includes the kind");
    }
}
```

In `PublicSurfaceTests.Allowed["Noof.Ledger.Application"]`, add `"CapturedVoice"` and `"ITranscriptionStore"`.

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL. `CapturedVoice`, `CaptureVoiceAsync`, `RequestVoiceCorrectionAsync`, `EfTranscriptionStore` and `ITranscriptionStore` do not exist.

- [ ] **Step 3: Add the Application contracts**

`src/Noof.Ledger.Application/Capture/CapturedVoice.cs`:

```csharp
namespace Noof.Ledger.Application.Capture;

public sealed record CapturedVoice(long ChatId, int MessageId, string VoiceFileId, int DurationSeconds, DateTimeOffset SentAt)
{
    // For CapturedMessage.SentAt's reason: a non-UTC instant fails here, where it is built, not deep inside
    // SaveChangesAsync with an Npgsql message that names neither this type nor the property.
    public DateTimeOffset SentAt { get; init; } = SentAt.Offset == TimeSpan.Zero
        ? SentAt
        : throw new ArgumentException($"must be UTC (Offset == TimeSpan.Zero), but was {SentAt.Offset}.", nameof(SentAt));
}
```

`src/Noof.Ledger.Application/Capture/ICaptureStore.cs`: add after `CaptureAsync`:

```csharp
    // CaptureAsync for a voice note: the same wallet rule, the same single database transaction and the same
    // idempotency on (ChatId, MessageId). The record has no text yet, and its job is a Transcribe job (V3).
    Task<Guid> CaptureVoiceAsync(CapturedVoice voice, string timeZoneId, CancellationToken cancellationToken);
```

`src/Noof.Ledger.Application/Editing/IRecordEditor.cs`: add after `RequestCorrectionAsync`:

```csharp
    // The spoken form of RequestCorrectionAsync: queues the reply's voice for transcription, and the transcript becomes
    // the correction's instruction (V7). Same idempotency, same instruction day.
    Task<bool> RequestVoiceCorrectionAsync(
        Guid transactionId, string voiceFileId, int sourceMessageId, DateTimeOffset sentAt, CancellationToken cancellationToken);
```

`src/Noof.Ledger.Application/Transcription/ITranscriptionStore.cs`:

```csharp
namespace Noof.Ledger.Application.Transcription;

public interface ITranscriptionStore
{
    // Writes the transcript as the record's raw_text and queues its first reading, in ONE database transaction.
    // False when the record already has text: a Transcribe job re-run after its commit must not read the note twice.
    Task<bool> CompleteCaptureAsync(Guid transactionId, string transcript, CancellationToken cancellationToken);

    // Queues the correction a spoken reply asked for, with the transcript as its instruction. False when this reply's
    // correction is already queued.
    Task<bool> CompleteCorrectionAsync(
        Guid transactionId, string transcript, int sourceMessageId, DateOnly? instructionDay, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement them in Persistence**

`src/Noof.Ledger.Persistence/Capture/EfCaptureStore.cs`: make both public capture methods one shared path. Replace the body of the class above `AttachBotMessageAsync` with:

```csharp
    public Task<Guid> CaptureAsync(CapturedMessage message, string timeZoneId, CancellationToken cancellationToken) =>
        StoreAsync(message.ChatId, message.MessageId, message.SentAt, timeZoneId, message.Text, voice: null, cancellationToken);

    public Task<Guid> CaptureVoiceAsync(CapturedVoice voice, string timeZoneId, CancellationToken cancellationToken) =>
        StoreAsync(voice.ChatId, voice.MessageId, voice.SentAt, timeZoneId, rawText: null, voice, cancellationToken);

    async Task<Guid> StoreAsync(
        long chatId, int messageId, DateTimeOffset sentAt, string timeZoneId, string? rawText, CapturedVoice? voice,
        CancellationToken cancellationToken)
    {
        var existing = await FindExistingAsync(chatId, messageId, cancellationToken);

        if (existing is not null)
            return existing.Id;

        // Checked before the wallet lookup: a replay of an already captured message must still
        // succeed even after the default wallet has been unmarked.
        var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.IsDefault, cancellationToken)
            ?? throw new InvalidOperationException(
                "No wallet is marked as the default. Capture cannot proceed without one.");

        var now = timeProvider.GetUtcNow();
        var transactionId = Guid.NewGuid();

        var transaction = new Transaction
        {
            Id = transactionId,
            WalletId = wallet.Id,
            RawText = rawText,
            CaptureKind = voice is null ? CaptureKind.Text : CaptureKind.Voice,
            VoiceFileId = voice?.VoiceFileId,
            VoiceDurationSeconds = voice?.DurationSeconds,
            Status = TransactionStatus.Captured,
            TimeZoneId = timeZoneId,
            OccurredAt = sentAt,
            OccurredOn = ZonedClock.LocalDate(sentAt, timeZoneId),
            TelegramChatId = chatId,
            TelegramMessageId = messageId,
            CreatedAt = now,
        };
        var job = new CategorizationJob
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Kind = voice is null ? JobKind.Categorize : JobKind.Transcribe,
            VoiceFileId = voice?.VoiceFileId,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            RunAfter = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Transactions.Add(transaction);
        db.CategorizationJobs.Add(job);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return transactionId;
        }
        catch (DbUpdateException ex) when (IsDuplicateCaptureViolation(ex))
        {
            // Another concurrent call for the same (ChatId, MessageId) committed first. Ours never
            // did; detach both rows so the re-read below goes back to the database instead of
            // returning these uncommitted, never-persisted entities from the identity map.
            db.Entry(transaction).State = EntityState.Detached;
            db.Entry(job).State = EntityState.Detached;

            var winner = await FindExistingAsync(chatId, messageId, cancellationToken)
                ?? throw new InvalidOperationException(
                    "A unique-constraint violation on capture reported a winner that cannot be found.");
            return winner.Id;
        }
    }

    Task<Transaction?> FindExistingAsync(long chatId, int messageId, CancellationToken cancellationToken) =>
        db.Transactions.SingleOrDefaultAsync(
            t => t.TelegramChatId == chatId && t.TelegramMessageId == messageId,
            cancellationToken);
```

Keep `IsDuplicateCaptureViolation` and `AttachBotMessageAsync` as they are.

`src/Noof.Ledger.Persistence/Editing/EfRecordEditor.cs`:
- give `NewJob` a last optional parameter, `string? voiceFileId = null`, and set `VoiceFileId = voiceFileId`;
- replace `RequestCorrectionAsync` with the three members below, plus the new method. The existing behaviour of `RequestCorrectionAsync` is unchanged.

```csharp
    public async Task<bool> RequestCorrectionAsync(
        Guid transactionId, string instruction, int sourceMessageId, DateTimeOffset sentAt, CancellationToken cancellationToken) =>
        await TryQueueAsync(
            NewJob(transactionId, JobKind.Correct, instruction, sourceMessageId,
                await InstructionDayAsync(transactionId, sentAt, cancellationToken)),
            cancellationToken);

    public async Task<bool> RequestVoiceCorrectionAsync(
        Guid transactionId, string voiceFileId, int sourceMessageId, DateTimeOffset sentAt, CancellationToken cancellationToken) =>
        await TryQueueAsync(
            NewJob(transactionId, JobKind.Transcribe, instruction: null, sourceMessageId,
                await InstructionDayAsync(transactionId, sentAt, cancellationToken), voiceFileId),
            cancellationToken);

    async Task<DateOnly?> InstructionDayAsync(Guid transactionId, DateTimeOffset sentAt, CancellationToken cancellationToken)
    {
        var timeZoneId = await db.Transactions.AsNoTracking()
            .Where(t => t.Id == transactionId)
            .Select(t => (string?)t.TimeZoneId)
            .SingleOrDefaultAsync(cancellationToken);

        return timeZoneId is null ? null : ZonedClock.LocalDate(sentAt, timeZoneId);
    }

    // False when this exact reply is already queued: Telegram redelivers an update whose handling failed partway.
    async Task<bool> TryQueueAsync(CategorizationJob job, CancellationToken cancellationToken)
    {
        db.CategorizationJobs.Add(job);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: CategorizationJobConfiguration.SourceMessageIndex,
        })
        {
            db.Entry(job).State = EntityState.Detached;
            return false;
        }
    }
```

If the original `RequestCorrectionAsync` carried comments explaining the instruction day (P2-2), move them onto `InstructionDayAsync`.

`src/Noof.Ledger.Persistence/Transcription/EfTranscriptionStore.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Transcription;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Configurations;
using Npgsql;

namespace Noof.Ledger.Persistence.Transcription;

internal sealed class EfTranscriptionStore(LedgerDbContext db, TimeProvider timeProvider) : ITranscriptionStore
{
    public async Task<bool> CompleteCaptureAsync(Guid transactionId, string transcript, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Only a record still without text takes a transcript: the condition is what makes a re-run of the same
        // Transcribe job, after this commit landed and its SucceedAsync did not, a no-op.
        var updated = await db.Database.ExecuteSqlRawAsync(
            "UPDATE transactions SET raw_text = @transcript WHERE id = @transactionId AND raw_text IS NULL",
            [new NpgsqlParameter("transcript", transcript), new NpgsqlParameter("transactionId", transactionId)],
            cancellationToken);

        if (updated == 0)
            return false;

        db.CategorizationJobs.Add(NewJob(transactionId, JobKind.Categorize, instruction: null, sourceMessageId: null, instructionDay: null));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CompleteCorrectionAsync(
        Guid transactionId, string transcript, int sourceMessageId, DateOnly? instructionDay, CancellationToken cancellationToken)
    {
        var job = NewJob(transactionId, JobKind.Correct, transcript, sourceMessageId, instructionDay);
        db.CategorizationJobs.Add(job);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: CategorizationJobConfiguration.SourceMessageIndex,
        })
        {
            db.Entry(job).State = EntityState.Detached;
            return false;
        }
    }

    CategorizationJob NewJob(Guid transactionId, JobKind kind, string? instruction, int? sourceMessageId, DateOnly? instructionDay)
    {
        var now = timeProvider.GetUtcNow();
        return new CategorizationJob
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Kind = kind,
            Instruction = instruction,
            SourceMessageId = sourceMessageId,
            InstructionDay = instructionDay,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            RunAfter = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
```

`src/Noof.Ledger.Persistence/PersistenceRegistration.cs`: add `services.AddScoped<ITranscriptionStore, EfTranscriptionStore>();` after the `IRecordEditor` line, with the `using`s it needs.

- [ ] **Step 5: Run the tests and see them pass**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS.

Check the test can fail: temporarily delete `AND raw_text IS NULL` from `CompleteCaptureAsync`'s SQL, and `Completing_a_capture_twice_…` must fail. Put the condition back.

- [ ] **Step 6: Commit**

```bash
git add src/Noof.Ledger.Application src/Noof.Ledger.Persistence tests/Noof.Ledger.Persistence.Tests tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs
git commit -m "feat(persistence): store voice captures, spoken corrections and finished transcripts (V3, V4, V7)"
```

---

### Task 4: Transcription in `Noof.Ledger.Ai`, with Groq behind the factory

**Files:**
- Create: `src/Noof.Ledger.Application/Transcription/ITranscriber.cs`, `src/Noof.Ledger.Application/Transcription/ISpeechProvider.cs`
- Create: `src/Noof.Ledger.Ai/ISpeechToTextClientFactory.cs`, `src/Noof.Ledger.Ai/SpeechTranscriber.cs`
- Create: `src/Noof.Ledger.Ai/Groq/GroqOptions.cs`, `GroqSpeechToTextClient.cs`, `GroqSpeechToTextClientFactory.cs`, `GroqRegistration.cs`
- Modify: `src/Noof.Ledger.Ai/AiRegistration.cs`, `src/Noof.Ledger.Ai/Noof.Ledger.Ai.csproj`, `tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
- Modify: `tests/Noof.Ledger.Ai.Tests/AiRegistrationTests.cs`, `tests/Noof.Ledger.Architecture.Tests/AiBoundaryTests.cs`, `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`
- Create: `tests/Noof.Ledger.Ai.Tests/SpeechTranscriberTests.cs`, `tests/Noof.Ledger.Ai.Tests/Groq/GroqSpeechToTextClientTests.cs`, `tests/Noof.Ledger.Ai.Tests/Groq/GroqSpeechToTextClientFactoryTests.cs`

**Interfaces:**
- Consumes: `ModelCallException`, `ModelFailureKind` and `ModelCallExceptionExtensions.AsAccountLevel()`/`IsAccountLevel()` (Application.Categorization), `ISecretStore`, `SecretState`, `ISecretProbe` and `ProbeResult` (Application.Secrets). No other task's output.
- Produces:
  - `public interface ITranscriber { Task<string> TranscribeAsync(Stream audio, CancellationToken cancellationToken); }` returns trimmed text, and `""` when nothing was heard. Failures throw `ModelCallException`.
  - `public interface ISpeechProvider : ISecretProbe { string SecretLabel { get; } Task<bool> IsConfiguredAsync(CancellationToken cancellationToken); }`.
  - Both live in namespace `Noof.Ledger.Application.Transcription`, and both are registered scoped by `AddNoofAi`. `AddNoofAi` also registers the Groq factory as one more `ISecretProbe`.

- [ ] **Step 1: Let the Ai assemblies use the experimental interface**

In `src/Noof.Ledger.Ai/Noof.Ledger.Ai.csproj` and in `tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`, add:

```xml
  <PropertyGroup>
    <!-- ISpeechToTextClient is [Experimental("MEAI001")] in Microsoft.Extensions.AI 10.5.1. It is the transcription
         seam (spec V2) and is confined to Noof.Ledger.Ai; revisit this when the API is declared stable. -->
    <NoWarn>$(NoWarn);MEAI001</NoWarn>
  </PropertyGroup>
```

In the test project, place it inside the existing `PropertyGroup` or as a second one. Do not add it to any other project.

- [ ] **Step 2: Write the failing tests**

`tests/Noof.Ledger.Ai.Tests/SpeechTranscriberTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.Extensions.AI;

namespace Noof.Ledger.Ai.Tests;

public class SpeechTranscriberTests
{
    sealed class ScriptedSpeechToTextClient(string text) : ISpeechToTextClient
    {
        public SpeechToTextOptions? LastOptions { get; private set; }
        public byte[]? LastAudio { get; private set; }
        public bool Disposed { get; private set; }

        public async Task<SpeechToTextResponse> GetTextAsync(
            Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await audioSpeechStream.CopyToAsync(buffer, cancellationToken);
            LastAudio = buffer.ToArray();
            LastOptions = options;
            return new SpeechToTextResponse(text);
        }

        public IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(
            Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposed = true;
    }

    sealed class FixedSpeechToTextClientFactory(ISpeechToTextClient client) : ISpeechToTextClientFactory
    {
        public Task<ISpeechToTextClient> CreateAsync(CancellationToken cancellationToken) => Task.FromResult(client);
    }

    static readonly byte[] SyntheticAudio = "OggS-synthetic-voice-note"u8.ToArray();

    [Fact]
    public async Task Asks_for_russian_and_returns_the_trimmed_transcript()
    {
        var client = new ScriptedSpeechToTextClient("  купил вчера штуку евро \n");
        var transcriber = new SpeechTranscriber(new FixedSpeechToTextClientFactory(client));

        var text = await transcriber.TranscribeAsync(new MemoryStream(SyntheticAudio), TestContext.Current.CancellationToken);

        text.Should().Be("купил вчера штуку евро");
        client.LastOptions!.SpeechLanguage.Should().Be("ru");
        client.LastAudio.Should().Equal(SyntheticAudio);
    }

    [Fact]
    public async Task Whitespace_is_nothing_heard()
    {
        var transcriber = new SpeechTranscriber(new FixedSpeechToTextClientFactory(new ScriptedSpeechToTextClient(" \n\t ")));

        var text = await transcriber.TranscribeAsync(new MemoryStream(SyntheticAudio), TestContext.Current.CancellationToken);

        text.Should().BeEmpty();
    }

    [Fact]
    public async Task Disposes_the_client_it_asked_for()
    {
        var client = new ScriptedSpeechToTextClient("кофе");
        var transcriber = new SpeechTranscriber(new FixedSpeechToTextClientFactory(client));

        await transcriber.TranscribeAsync(new MemoryStream(SyntheticAudio), TestContext.Current.CancellationToken);

        client.Disposed.Should().BeTrue();
    }
}
```

`tests/Noof.Ledger.Ai.Tests/Groq/GroqSpeechToTextClientTests.cs`:

```csharp
using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Noof.Ledger.Ai.Groq;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai.Tests.Groq;

public class GroqSpeechToTextClientTests
{
    const string ApiKey = "gsk-VERY-SECRET-DO-NOT-LEAK-abc123";
    static readonly byte[] SyntheticAudio = "OggS-synthetic-voice-note"u8.ToArray();

    static GroqSpeechToTextClient Client(HttpMessageHandler handler) => new(new HttpClient(handler), ApiKey, new GroqOptions());

    static Task<SpeechToTextResponse> TranscribeAsync(GroqSpeechToTextClient client) =>
        client.GetTextAsync(new MemoryStream(SyntheticAudio), new SpeechToTextOptions { SpeechLanguage = "ru" },
            TestContext.Current.CancellationToken);

    sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    [Fact]
    public async Task Posts_the_note_as_voice_ogg_with_the_model_the_language_and_json()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, """{"text":"кофе 250"}""");

        await TranscribeAsync(Client(handler));

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be(new Uri("https://api.groq.com/openai/v1/audio/transcriptions"));
        request.Headers["Authorization"].Should().Be($"Bearer {ApiKey}");
        request.Body.Should().Contain("name=file; filename=voice.ogg")
            .And.Contain("audio/ogg")
            .And.Contain("OggS-synthetic-voice-note", "the audio bytes go through untouched")
            .And.Contain("whisper-large-v3")
            .And.Contain("name=language").And.Contain("ru")
            .And.Contain("name=response_format").And.Contain("json")
            .And.NotContain(".oga", "Groq refuses Telegram's own extension");
    }

    [Fact]
    public async Task Returns_what_groq_heard()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, """{"text":" купил вчера штуку евро ","x_groq":{"id":"req_1"}}""");

        var response = await TranscribeAsync(Client(handler));

        response.Text.Should().Be(" купил вчера штуку евро ");
        response.ModelId.Should().Be("whisper-large-v3");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ModelFailureKind.Terminal, false)]
    [InlineData(HttpStatusCode.Unauthorized, ModelFailureKind.Terminal, true)]
    [InlineData(HttpStatusCode.Forbidden, ModelFailureKind.Terminal, true)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, ModelFailureKind.Terminal, false)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, ModelFailureKind.Terminal, false)]
    [InlineData(HttpStatusCode.InternalServerError, ModelFailureKind.Transient, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ModelFailureKind.Transient, false)]
    public async Task A_failed_answer_is_classified(HttpStatusCode status, ModelFailureKind kind, bool accountLevel)
    {
        var handler = new StubHttpMessageHandler().Enqueue(status, """{"error":{"message":"no"}}""");

        var act = () => TranscribeAsync(Client(handler));

        var exception = (await act.Should().ThrowAsync<ModelCallException>()).Which;
        exception.Kind.Should().Be(kind);
        exception.IsAccountLevel().Should().Be(accountLevel);
    }

    [Fact]
    public async Task Too_many_requests_is_transient_and_not_account_level()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.TooManyRequests, """{"error":{"message":"rate limit"}}""");

        var act = () => TranscribeAsync(Client(handler));

        var exception = (await act.Should().ThrowAsync<ModelCallException>()).Which;
        exception.Kind.Should().Be(ModelFailureKind.Transient, "the free tier allows 20 requests a minute; a burst of notes waits, it does not fail");
        exception.IsAccountLevel().Should().BeFalse("a rate limit is not a broken key");
    }

    [Fact]
    public async Task An_unreachable_groq_is_transient()
    {
        var act = () => TranscribeAsync(Client(new ThrowingHttpMessageHandler(new HttpRequestException("no route"))));

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Transient);
    }

    [Fact]
    public async Task A_timeout_is_transient()
    {
        var act = () => TranscribeAsync(Client(new ThrowingHttpMessageHandler(new TaskCanceledException("timed out"))));

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Transient);
    }

    [Fact]
    public async Task An_answer_with_no_text_in_it_is_terminal()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, """{"unexpected":true}""");

        var act = () => TranscribeAsync(Client(handler));

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Terminal);
    }

    [Fact]
    public async Task The_key_never_appears_in_a_failure()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.Unauthorized, $$"""{"error":{"message":"bad key {{ApiKey}}"}}""");

        var act = () => TranscribeAsync(Client(handler));

        var exception = (await act.Should().ThrowAsync<ModelCallException>()).Which;
        exception.ToString().Should().NotContain(ApiKey);
    }
}
```

`tests/Noof.Ledger.Ai.Tests/Groq/GroqSpeechToTextClientFactoryTests.cs`:

```csharp
using System.Net;
using AwesomeAssertions;
using Noof.Ledger.Ai.Groq;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai.Tests.Groq;

public class GroqSpeechToTextClientFactoryTests
{
    const string ApiKey = "gsk-VERY-SECRET-DO-NOT-LEAK-abc123";

    static GroqSpeechToTextClientFactory Factory(ISecretStore secretStore, HttpMessageHandler? handler = null) =>
        new(secretStore, new HttpClient(handler ?? new StubHttpMessageHandler()), new GroqOptions());

    sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    [Fact]
    public void The_secret_key_is_exactly_groq_api_key()
    {
        // Data Protection's purpose is derived from this string; changing it orphans the operator's stored key.
        Factory(new StubSecretStore(SecretState.Missing)).SecretKey.Should().Be("groq-api-key");
    }

    [Fact]
    public void The_settings_page_calls_it_the_Groq_API_key()
    {
        Factory(new StubSecretStore(SecretState.Missing)).SecretLabel.Should().Be("Groq API key");
    }

    [Theory]
    [InlineData(SecretState.Present, true)]
    [InlineData(SecretState.Missing, false)]
    [InlineData(SecretState.Unreadable, false)]
    public async Task Is_configured_only_when_a_readable_key_is_stored(SecretState state, bool configured)
    {
        var secretStore = new StubSecretStore(state, state == SecretState.Present ? ApiKey : null);

        (await Factory(secretStore).IsConfiguredAsync(TestContext.Current.CancellationToken)).Should().Be(configured);
        secretStore.PlaintextReads.Should().BeEmpty("asking whether a key is set never decrypts it");
    }

    [Fact]
    public async Task The_probe_lists_models_with_the_key_and_reports_success()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, """{"data":[]}""");

        var result = await Factory(new StubSecretStore(SecretState.Present, ApiKey), handler).ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeTrue();
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.Should().Be(new Uri("https://api.groq.com/openai/v1/models"));
        request.Headers["Authorization"].Should().Be($"Bearer {ApiKey}");
    }

    [Fact]
    public async Task The_probe_reports_a_rejected_key()
    {
        var handler = new StubHttpMessageHandler().Enqueue(HttpStatusCode.Unauthorized, """{"error":{}}""");

        var result = await Factory(new StubSecretStore(SecretState.Present, ApiKey), handler).ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().Be("Groq rejected the key.");
    }

    [Fact]
    public async Task The_probe_without_a_key_never_calls_groq()
    {
        var handler = new StubHttpMessageHandler();

        var result = await Factory(new StubSecretStore(SecretState.Missing), handler).ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_probe_never_echoes_the_key()
    {
        var handler = new ThrowingHttpMessageHandler(new InvalidOperationException($"boom {ApiKey}"));

        var result = await Factory(new StubSecretStore(SecretState.Present, ApiKey), handler).ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().NotContain(ApiKey);
    }

    [Fact]
    public async Task A_client_without_a_key_is_a_terminal_failure()
    {
        var act = () => Factory(new StubSecretStore(SecretState.Missing)).CreateAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<ModelCallException>()).Which.Kind.Should().Be(ModelFailureKind.Terminal);
    }
}
```

`StubSecretStore` already exists in `tests/Noof.Ledger.Ai.Tests`. It records `PlaintextReads` and `StatusReads`.

`tests/Noof.Ledger.Ai.Tests/AiRegistrationTests.cs`: this file needs four changes.

1. Add `using Noof.Ledger.Ai.Groq;` and `using Noof.Ledger.Application.Transcription;`.
2. In `One_provider_instance_per_scope_answers_as_client_factory_model_provider_and_key_probe`, replace the last line (the `ContainSingle` probe assertion) with:

```csharp
        scope.ServiceProvider.GetServices<ISecretProbe>().Should().Contain(probe => ReferenceEquals(probe, factory));
```

3. Append:

```csharp
    [Fact]
    public void AddNoofAi_registers_the_provider_neutral_transcriber()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITranscriber>().Should().BeOfType<SpeechTranscriber>();
    }

    [Fact]
    public void One_speech_provider_instance_per_scope_answers_as_client_factory_speech_provider_and_key_probe()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        var factory = scope.ServiceProvider.GetRequiredService<ISpeechToTextClientFactory>();

        factory.Should().BeOfType<GroqSpeechToTextClientFactory>();
        scope.ServiceProvider.GetRequiredService<ISpeechProvider>().Should().BeSameAs(factory);
        scope.ServiceProvider.GetServices<ISecretProbe>().Should().Contain(probe => ReferenceEquals(probe, factory));
    }

    [Fact]
    public void Exactly_two_keys_can_be_tested_the_models_and_the_speech_providers()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetServices<ISecretProbe>().Should().HaveCount(2);
    }

    [Fact]
    public void AddNoofAi_binds_the_Ai_Groq_configuration_section()
    {
        using var provider = Provider(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:Groq:Model"] = "whisper-large-v3-turbo" })
            .Build());

        provider.GetRequiredService<GroqOptions>().Model.Should().Be("whisper-large-v3-turbo");
    }
```

4. `Ai.Tests` sees `Noof.Ledger.Ai` internals through the existing `InternalsVisibleTo`. Nothing else is needed.

`tests/Noof.Ledger.Architecture.Tests/AiBoundaryTests.cs`: this file needs four changes.

1. Update the class comment to: `// Operator decision D-A (2026-09-23): nothing depends on a model or speech provider except its factory, and everything provider-specific lives in that provider's folder.`
2. Replace `No_type_outside_the_provider_folder_is_named_after_the_provider` with this theory. Keep `SdkReference`, `ProviderFolder`, `InProviderFolder` and the SDK fact as they are, since they are about the Anthropic SDK only:

```csharp
    [Theory]
    [InlineData("Anthropic")]
    [InlineData("Groq")]
    public void No_type_outside_its_provider_folder_is_named_after_the_provider(string provider)
    {
        var folder = Path.Combine(SrcRoot, "Noof.Ledger.Ai", provider) + Path.DirectorySeparatorChar;
        var namedAfterProvider = new Regex(@$"\b(?:class|record|interface|enum|struct)\s+(?<name>\w*{provider}\w*)");

        var offenders = SourceFiles("*.cs")
            .Where(file => !file.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => namedAfterProvider.Matches(File.ReadAllText(file))
                .Select(match => $"{Relative(file)}: {match.Groups["name"].Value}"))
            .ToArray();

        offenders.Should().BeEmpty("a type named after the provider belongs with the provider");
        SourceFiles("*.cs").Where(file => file.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .Should().Contain(file => namedAfterProvider.IsMatch(File.ReadAllText(file)),
                "the provider folder's own types must match, or the pattern is matching nothing");
    }
```

   Delete the now-unused `ProviderNamedType` field.
3. Replace `Nothing_outside_the_Ai_assembly_names_the_provider` with:

```csharp
    [Theory]
    [InlineData("anthropic")]
    [InlineData("groq")]
    public void Nothing_outside_the_Ai_assembly_names_the_provider(string provider)
    {
        // Host and Web learn what they need - which secret to ask for, what to call it, whether it is set -
        // through IModelProvider and ISpeechProvider. Case-insensitive, so a stored key's own spelling
        // ("anthropic-api-key", "groq-api-key") counts too: it belongs to the provider, not to Application.
        var offenders = SourceFiles("*.cs").Concat(SourceFiles("*.razor"))
            .Where(file => !file.StartsWith(AiRoot, StringComparison.OrdinalIgnoreCase))
            .Where(file => File.ReadAllText(file).Contains(provider, StringComparison.OrdinalIgnoreCase))
            .Select(Relative)
            .ToArray();

        offenders.Should().BeEmpty("only Noof.Ledger.Ai may know which provider answers");
    }
```

4. Append:

```csharp
    [Fact]
    public void Only_the_Groq_folder_knows_where_Groq_is()
    {
        var groqFolder = Path.Combine(SrcRoot, "Noof.Ledger.Ai", "Groq") + Path.DirectorySeparatorChar;
        var naming = SourceFiles("*.cs").Where(file => File.ReadAllText(file).Contains("api.groq.com", StringComparison.OrdinalIgnoreCase)).ToArray();

        naming.Where(file => !file.StartsWith(groqFolder, StringComparison.OrdinalIgnoreCase)).Select(Relative)
            .Should().BeEmpty("the endpoint is the provider's own detail");
        naming.Should().NotBeEmpty("the Groq folder must name its endpoint, or the rule proves nothing");
    }
```

`PublicSurfaceTests.Allowed["Noof.Ledger.Application"]`: add `"ITranscriber"` and `"ISpeechProvider"`.

- [ ] **Step 3: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL. `ITranscriber`, `ISpeechProvider`, `ISpeechToTextClientFactory`, `SpeechTranscriber` and the `Groq` types do not exist.

- [ ] **Step 4: Add the Application contracts**

`src/Noof.Ledger.Application/Transcription/ITranscriber.cs`:

```csharp
namespace Noof.Ledger.Application.Transcription;

public interface ITranscriber
{
    // The words spoken in the audio, trimmed; empty when nothing was heard (V6). A failure is a ModelCallException,
    // classified the way the categorizer's are.
    Task<string> TranscribeAsync(Stream audio, CancellationToken cancellationToken);
}
```

`src/Noof.Ledger.Application/Transcription/ISpeechProvider.cs`:

```csharp
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Application.Transcription;

// IModelProvider's counterpart for speech: whether its key is stored, what the settings page calls it, and - through
// ISecretProbe - whether the key works. The provider's name never leaves Noof.Ledger.Ai.
public interface ISpeechProvider : ISecretProbe
{
    string SecretLabel { get; }

    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 5: Add the provider-neutral half of `Ai`**

`src/Noof.Ledger.Ai/ISpeechToTextClientFactory.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Noof.Ledger.Ai;

// The one seam between transcription and whichever provider answers it, as IChatClientFactory is for the chat side.
// An implementation owns everything provider-specific and lives in that provider's own folder.
internal interface ISpeechToTextClientFactory
{
    // A new client per call, never cached: the stored secret can change between two jobs.
    Task<ISpeechToTextClient> CreateAsync(CancellationToken cancellationToken);
}
```

`src/Noof.Ledger.Ai/SpeechTranscriber.cs`:

```csharp
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Transcription;

namespace Noof.Ledger.Ai;

internal sealed class SpeechTranscriber(ISpeechToTextClientFactory clientFactory) : ITranscriber
{
    // The operator speaks Russian with the odd Serbian shop name (2026-09-23). Naming the language beats detecting it
    // from a few seconds of audio.
    const string SpeechLanguage = "ru";

    public async Task<string> TranscribeAsync(Stream audio, CancellationToken cancellationToken)
    {
        using var client = await clientFactory.CreateAsync(cancellationToken);
        var response = await client.GetTextAsync(audio, new SpeechToTextOptions { SpeechLanguage = SpeechLanguage }, cancellationToken);
        return response.Text.Trim();
    }
}
```

- [ ] **Step 6: Add the Groq folder**

`src/Noof.Ledger.Ai/Groq/GroqOptions.cs`:

```csharp
namespace Noof.Ledger.Ai.Groq;

internal sealed class GroqOptions
{
    public string Model { get; init; } = "whisper-large-v3";

    public Uri BaseAddress { get; init; } = new("https://api.groq.com/openai/v1/");

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}
```

`src/Noof.Ledger.Ai/Groq/GroqSpeechToTextClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai.Groq;

// Groq ships no .NET SDK, and owning the request lets the tests assert exactly what goes on the wire (V2). It never
// retries: retrying is the job queue's.
internal sealed class GroqSpeechToTextClient(HttpClient httpClient, string apiKey, GroqOptions groqOptions) : ISpeechToTextClient
{
    // Telegram names a voice note .oga, and Groq refuses that name with 400 while accepting the same bytes as .ogg.
    const string FileName = "voice.ogg";

    public async Task<SpeechToTextResponse> GetTextAsync(
        Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        var model = options?.ModelId ?? groqOptions.Model;

        using var content = new MultipartFormDataContent();
        var audio = new StreamContent(audioSpeechStream);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
        content.Add(audio, "file", FileName);
        content.Add(new StringContent(model), "model");
        if (options?.SpeechLanguage is { } language)
            content.Add(new StringContent(language), "language");
        content.Add(new StringContent("json"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(groqOptions.BaseAddress, "audio/transcriptions"))
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        return new SpeechToTextResponse(ReadText(body)) { ModelId = model };
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelCallException(ModelFailureKind.Transient, "Groq could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelCallException(ModelFailureKind.Transient, "Groq did not answer in time.", ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        // The body is not read into the message: an error body may quote the request back, key included.
        var status = response.StatusCode;
        response.Dispose();
        var exception = new ModelCallException(Classify(status), $"Groq transcription failed with status {(int)status}.");
        throw IsAccountLevel(status) ? exception.AsAccountLevel() : exception;
    }

    static string ReadText(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("text").GetString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ModelCallException(ModelFailureKind.Terminal, "Groq answered with no transcript in it.", ex);
        }
    }

    // What the request itself got wrong is terminal. A rate limit (the free tier allows 20 requests a minute), a
    // server error and anything unrecognised are worth another attempt.
    static ModelFailureKind Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired
            or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.RequestEntityTooLarge
            or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity => ModelFailureKind.Terminal,
        _ => ModelFailureKind.Transient,
    };

    static bool IsAccountLevel(HttpStatusCode status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden;

    public IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(
        Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A voice note is transcribed whole; nothing here streams.");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    // The HttpClient belongs to IHttpClientFactory, not to this client.
    public void Dispose()
    {
    }
}
```

`src/Noof.Ledger.Ai/Groq/GroqSpeechToTextClientFactory.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Application.Transcription;

namespace Noof.Ledger.Ai.Groq;

internal sealed class GroqSpeechToTextClientFactory(ISecretStore secretStore, HttpClient httpClient, GroqOptions options)
    : ISpeechToTextClientFactory, ISpeechProvider
{
    // Data Protection's purpose is derived from this string: renaming it orphans the operator's stored key.
    const string ApiKeySecret = "groq-api-key";

    public string SecretKey => ApiKeySecret;

    public string SecretLabel => "Groq API key";

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken) =>
        (await secretStore.GetStatusAsync(ApiKeySecret, cancellationToken)).State is SecretState.Present;

    public async Task<ISpeechToTextClient> CreateAsync(CancellationToken cancellationToken) =>
        new GroqSpeechToTextClient(httpClient, await ReadKeyAsync(cancellationToken), options);

    // GET /models costs nothing and proves the key without transcribing anything.
    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var apiKey = await ReadKeyAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.BaseAddress, "models"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await httpClient.SendAsync(request, cancellationToken);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => new ProbeResult(true, "Groq accepted the key."),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ProbeResult(false, "Groq rejected the key."),
                var status => new ProbeResult(false, $"Groq answered with status {(int)status}."),
            };
        }
        catch (ModelCallException ex)
        {
            return new ProbeResult(false, ex.Message);
        }
        catch (HttpRequestException)
        {
            return new ProbeResult(false, "Could not reach Groq.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(false, "Groq did not answer in time.");
        }
        // Never ex.Message: an unexpected exception could carry the key.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeResult(false, "The key could not be tested.");
        }
    }

    async Task<string> ReadKeyAsync(CancellationToken cancellationToken)
    {
        var secret = await secretStore.GetAsync(ApiKeySecret, cancellationToken);
        return secret.State switch
        {
            // Present always carries its value.
            SecretState.Present => secret.Value!,
            SecretState.Missing => throw new ModelCallException(ModelFailureKind.Terminal,
                "No Groq API key is configured. Enter one on the settings page."),
            SecretState.Unreadable => throw new ModelCallException(ModelFailureKind.Terminal,
                "The stored Groq API key could not be decrypted."),
            _ => throw new InvalidOperationException($"Unknown secret state {secret.State}."),
        };
    }
}
```

Check `AnthropicChatClientFactory.CreateSdkClientAsync` for the exact shape of `SecretResult` (`State`, `Value`) and mirror it if a member name differs.

`src/Noof.Ledger.Ai/Groq/GroqRegistration.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Application.Transcription;

namespace Noof.Ledger.Ai.Groq;

internal static class GroqRegistration
{
    const string HttpClientName = "groq";

    public static IServiceCollection AddGroqSpeechToText(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new GroqOptions();
        configuration.GetSection("Ai:Groq").Bind(options);
        services.AddSingleton(options);

        // The key rides in the Authorization header. With the logging handlers removed there is nothing a log-level
        // setting could turn back on.
        services.AddHttpClient(HttpClientName, client => client.Timeout = options.Timeout).RemoveAllLoggers();

        services.AddScoped(sp => new GroqSpeechToTextClientFactory(
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            sp.GetRequiredService<GroqOptions>()));

        services.AddScoped<ISpeechToTextClientFactory>(sp => sp.GetRequiredService<GroqSpeechToTextClientFactory>());
        services.AddScoped<ISpeechProvider>(sp => sp.GetRequiredService<GroqSpeechToTextClientFactory>());
        services.AddScoped<ISecretProbe>(sp => sp.GetRequiredService<GroqSpeechToTextClientFactory>());

        return services;
    }
}
```

`src/Noof.Ledger.Ai/AiRegistration.cs`: this file needs three changes.

1. Add `services.AddGroqSpeechToText(configuration);` after `AddAnthropicChatClientFactory`.
2. Add `services.AddScoped<ITranscriber, SpeechTranscriber>();` after the `ICategorizer` line.
3. Update the `CA1515` justification to: `"The one public type in this assembly, and the only way the Host can register ICategorizer, IModelProvider, ITranscriber and ISpeechProvider without naming an implementation or seeing a provider."`

- [ ] **Step 7: Run the tests and see them pass**

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS, with the live suite still skipped.

See the boundary rules fail once each, then revert:
- put the word `groq` in a comment in `src/Noof.Ledger.Web/Components/Pages/Settings/Secrets.razor`. `Nothing_outside_the_Ai_assembly_names_the_provider(provider: "groq")` must fail naming that file;
- move `"https://api.groq.com/openai/v1/"` into a new `const` in `SpeechTranscriber.cs`. `Only_the_Groq_folder_knows_where_Groq_is` must fail.

- [ ] **Step 8: Commit**

```bash
git add src/Noof.Ledger.Application/Transcription src/Noof.Ledger.Ai tests/Noof.Ledger.Ai.Tests tests/Noof.Ledger.Architecture.Tests
git commit -m "feat(ai): transcribe through ISpeechToTextClient, Groq whisper-large-v3 behind the factory (V1, V2)"
```

---

### Task 5: The echo says what was heard

**Files:**
- Modify: `src/Noof.Ledger.Application/Chat/IRecordEcho.cs`, `src/Noof.Ledger.Application/Chat/RecordEcho.cs`
- Test: `tests/Noof.Ledger.Host.Tests/RecordEchoTests.cs`

**Interfaces:**
- Consumes (Task 1): `CategorizationSubject.CaptureKind` and `CaptureKind`.
- Produces, on `IRecordEcho`:
  - `string Transcribing` is `"🎤 Transcribing…"`;
  - `EchoMessage HeardNothing` is `"Heard nothing in that voice note."` with `[Edit]`;
  - `EchoMessage TranscriptionFailure` is `"Couldn't transcribe that voice note."` with `[Edit]`;
  - `EchoMessage ComposeHeardNothing(CategorizationSubject record)` is `"Heard nothing in that voice note.\n\n"` followed by the record's current echo;
  - `Compose(record)` puts `🎤 "<RawText>"` as the first line of a voice record's echo. It does this whenever `CaptureKind` is `Voice`, `RawText` is not empty, and the status is not `Failed`.

The two failure echoes carry **Edit** for the reason `Failure` does. A reply, typed or spoken, still records the purchase through an ordinary correction. That works even with no transcript (Review Focus 2).

- [ ] **Step 1: Write the failing tests**

Append to `tests/Noof.Ledger.Host.Tests/RecordEchoTests.cs`:

```csharp
    static CategorizationSubject Voice(
        string heard, TransactionStatus status = TransactionStatus.Completed, IReadOnlyList<RecordedLine>? lines = null) =>
        new(Guid.NewGuid(), heard, 111L, 42, "Cash", status, Sent, Sent, lines ?? [Coffee], CaptureKind.Voice);

    [Fact]
    public void Transcribing_is_the_voice_notes_acknowledgement()
    {
        Echo.Transcribing.Should().Be("🎤 Transcribing…");
    }

    [Fact]
    public void A_voice_record_starts_with_what_was_heard()
    {
        var echo = Echo.Compose(Voice("кофе двести пятьдесят"));

        echo.Text.Should().Be("🎤 \"кофе двести пятьдесят\"\nRecorded — Cash\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_typed_record_has_no_heard_line()
    {
        Echo.Compose(Record()).Text.Should().StartWith("Recorded — Cash");
    }

    [Fact]
    public void A_voice_record_with_no_transcript_shows_no_heard_line()
    {
        Echo.Compose(Voice(heard: "")).Text.Should().StartWith("Recorded — Cash");
    }

    [Fact]
    public void A_voice_note_waiting_for_its_transcript_says_it_is_transcribing()
    {
        var echo = Echo.Compose(Voice(heard: "", status: TransactionStatus.Captured, lines: []));

        echo.Text.Should().Be("🎤 Transcribing…");
        echo.Actions.Should().BeEmpty();
    }

    [Fact]
    public void A_voice_note_being_read_shows_what_was_heard_above_the_acknowledgement()
    {
        Echo.Compose(Voice("кофе 250", status: TransactionStatus.Captured, lines: [])).Text
            .Should().Be("🎤 \"кофе 250\"\nRecording…");
    }

    [Fact]
    public void A_cancelled_voice_record_keeps_what_was_heard()
    {
        Echo.Compose(Voice("кофе 250", status: TransactionStatus.Cancelled)).Text.Should().StartWith("🎤 \"кофе 250\"\nCancelled — Cash");
    }

    [Fact]
    public void Heard_nothing_says_so_and_offers_edit()
    {
        Echo.HeardNothing.Text.Should().Be("Heard nothing in that voice note.");
        Echo.HeardNothing.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void A_note_that_could_not_be_transcribed_says_so_and_offers_edit()
    {
        Echo.TranscriptionFailure.Text.Should().Be("Couldn't transcribe that voice note.");
        Echo.TranscriptionFailure.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void Hearing_nothing_in_a_spoken_correction_shows_the_record_unchanged_below()
    {
        var echo = Echo.ComposeHeardNothing(Record());

        echo.Text.Should().Be("Heard nothing in that voice note.\n\nRecorded — Cash\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL. `Transcribing`, `HeardNothing`, `TranscriptionFailure` and `ComposeHeardNothing` do not exist.

- [ ] **Step 3: Implement**

`src/Noof.Ledger.Application/Chat/IRecordEcho.cs`: add these members next to the existing ones:

```csharp
    string Transcribing { get; }
    EchoMessage HeardNothing { get; }
    EchoMessage TranscriptionFailure { get; }

    EchoMessage ComposeHeardNothing(CategorizationSubject record);
```

`src/Noof.Ledger.Application/Chat/RecordEcho.cs`: this step makes three changes.

1. Add, after `EditPrompt`:

```csharp
    public string Transcribing => "🎤 Transcribing…";

    // Edit, as on Failure: a reply - typed or spoken - still records the purchase through an ordinary correction.
    public EchoMessage HeardNothing { get; } = new("Heard nothing in that voice note.", [RecordAction.Edit]);

    public EchoMessage TranscriptionFailure { get; } = new("Couldn't transcribe that voice note.", [RecordAction.Edit]);
```

2. Replace `Compose` with:

```csharp
    public EchoMessage Compose(CategorizationSubject record) => WithWhatWasHeard(record, record switch
    {
        { Status: TransactionStatus.Cancelled } =>
            new($"Cancelled — {record.WalletName}\n{Body(record)}".TrimEnd(), [RecordAction.Restore]),
        { Status: TransactionStatus.Failed } => Failure,
        { Status: TransactionStatus.Captured } => new(Waiting(record), []),
        { Lines.Count: 0 } =>
            new($"{record.WalletName}: found no spending here — nothing recorded.", [RecordAction.Edit]),
        _ => new($"Recorded — {record.WalletName}\n{Body(record)}", [RecordAction.Cancel, RecordAction.Edit]),
    });

    public EchoMessage ComposeHeardNothing(CategorizationSubject record)
    {
        var current = Compose(record);
        return current with { Text = $"{HeardNothing.Text}\n\n{current.Text}" };
    }

    string Waiting(CategorizationSubject record) =>
        record is { CaptureKind: CaptureKind.Voice, RawText.Length: 0 } ? Transcribing : Acknowledgement;

    // A voice record's echo opens with what was heard (V5), so a misheard word is told apart from a misread one.
    static EchoMessage WithWhatWasHeard(CategorizationSubject record, EchoMessage echo) =>
        record is { CaptureKind: CaptureKind.Voice, RawText.Length: > 0, Status: not TransactionStatus.Failed }
            ? echo with { Text = $"🎤 \"{record.RawText}\"\n{echo.Text}" }
            : echo;
```

3. Keep `ComposeCorrectionFailure`, `Body`, `FormatLine`, `Totals` and `FormatAmount` unchanged.

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter RecordEchoTests`
Expected: PASS, the 12 existing tests included.

Also run `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`: the Telegram tests build `new RecordEcho()` and must still pass.

- [ ] **Step 5: Commit**

```bash
git add src/Noof.Ledger.Application/Chat tests/Noof.Ledger.Host.Tests/RecordEchoTests.cs
git commit -m "feat(echo): a voice record's echo opens with what was heard (V5, V6, V9)"
```

---

### Task 6: Telegram: voice notes in, voice notes down

**Files:**
- Create: `src/Noof.Ledger.Application/Transcription/IVoiceFileSource.cs`
- Create: `src/Noof.Ledger.Telegram/TelegramVoiceFileSource.cs`
- Modify: `src/Noof.Ledger.Telegram/TelegramUpdateRouter.cs`, `src/Noof.Ledger.Telegram/CorrectionHandler.cs`, `src/Noof.Ledger.Telegram/TelegramRegistration.cs`
- Modify: `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`
- Test: `tests/Noof.Ledger.Telegram.Tests/TelegramVoiceFileSourceTests.cs` (new), `TelegramUpdateRouterTests.cs`, `TelegramRegistrationTests.cs`

**Interfaces:**
- Consumes: `CapturedVoice` and `ICaptureStore.CaptureVoiceAsync` (Task 3). `IRecordEditor.RequestVoiceCorrectionAsync` (Task 3). `IRecordEcho.Transcribing` (Task 5).
- Produces: `public interface IVoiceFileSource { Task<Stream> DownloadAsync(string voiceFileId, CancellationToken cancellationToken); }` in namespace `Noof.Ledger.Application.Transcription`. `AddNoofTelegram` registers it as a singleton.

- [ ] **Step 1: Write the failing tests**

`tests/Noof.Ledger.Telegram.Tests/TelegramVoiceFileSourceTests.cs`:

```csharp
using AwesomeAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramVoiceFileSourceTests
{
    static readonly byte[] SyntheticAudio = "OggS-synthetic-voice-note"u8.ToArray();

    static ITelegramBotClient ClientServing(byte[] audio)
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Is<GetFileRequest>(r => r.FileId == "voice-file-1"), Arg.Any<CancellationToken>())
            .Returns(new TGFile { FileId = "voice-file-1", FileUniqueId = "unique-1", FilePath = "voice/file_1.oga" });
        // GetInfoAndDownloadFile may reach either overload; both write the same bytes.
        client.DownloadFile(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Stream>().WriteAsync(audio).AsTask());
        client.DownloadFile(Arg.Any<TGFile>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Stream>().WriteAsync(audio).AsTask());
        return client;
    }

    [Fact]
    public async Task Downloads_the_note_by_its_file_id_into_a_stream_at_its_start()
    {
        var source = new TelegramVoiceFileSource(new TelegramClientHandle { Current = ClientServing(SyntheticAudio) });

        await using var audio = await source.DownloadAsync("voice-file-1", TestContext.Current.CancellationToken);

        audio.Position.Should().Be(0);
        using var copy = new MemoryStream();
        await audio.CopyToAsync(copy, TestContext.Current.CancellationToken);
        copy.ToArray().Should().Equal(SyntheticAudio);
    }

    [Fact]
    public async Task Throws_when_no_client_is_ready_yet()
    {
        var source = new TelegramVoiceFileSource(new TelegramClientHandle());

        var act = () => source.DownloadAsync("voice-file-1", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_refused_download_propagates_for_the_worker_to_retry()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetFileRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: wrong file_id or the file is temporarily unavailable", 400));
        var source = new TelegramVoiceFileSource(new TelegramClientHandle { Current = client });

        var act = () => source.DownloadAsync("voice-file-1", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ApiRequestException>();
    }
}
```

If `GetInfoAndDownloadFile` turns out to call something other than `SendRequest(GetFileRequest)` and `DownloadFile`, stub what it really calls. Find out from `Telegram.Bot.xml` or by reflection over `TelegramBotClientExtensions`. Do not change the production call.

Append to `tests/Noof.Ledger.Telegram.Tests/TelegramUpdateRouterTests.cs`. Add `using Noof.Ledger.Application.Capture;` if it is not already there:

```csharp
    static Update VoiceNote(long chatId, int messageId, DateTime date, Message? replyTo = null) => new()
    {
        Id = 905,
        Message = new Message
        {
            Id = messageId,
            Chat = new Chat { Id = chatId },
            Date = date,
            ReplyToMessage = replyTo,
            Voice = new Voice { FileId = "voice-file-1", FileUniqueId = "unique-1", Duration = 4 },
        },
    };

    [Fact]
    public async Task Captures_a_voice_note_and_says_it_is_transcribing()
    {
        var (router, captureStore, chatNotifier, _, _) = CreateRouter(ownerChatId: 111L);
        var sentAt = DateTimeOffset.Parse("2026-09-24T21:30:00Z");
        var transactionId = Guid.NewGuid();
        captureStore.CaptureVoiceAsync(Arg.Any<CapturedVoice>(), "Europe/Belgrade", Arg.Any<CancellationToken>()).Returns(transactionId);
        chatNotifier.SendAsync(111L, Echo.Transcribing, Arg.Any<CancellationToken>()).Returns(777);

        await router.HandleAsync(VoiceNote(111L, 5, sentAt.UtcDateTime), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureVoiceAsync(
            Arg.Is<CapturedVoice>(v => v.ChatId == 111L && v.MessageId == 5 && v.VoiceFileId == "voice-file-1"
                && v.DurationSeconds == 4 && v.SentAt == sentAt),
            "Europe/Belgrade",
            Arg.Any<CancellationToken>());
        await captureStore.Received(1).AttachBotMessageAsync(transactionId, 777, Arg.Any<CancellationToken>());
        await captureStore.DidNotReceiveWithAnyArgs().CaptureAsync(default!, default!, default);
    }

    [Fact]
    public async Task Rejects_a_strangers_voice_note_before_reading_it()
    {
        var (router, captureStore, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(VoiceNote(222L, 5, DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.DidNotReceiveWithAnyArgs().CaptureVoiceAsync(default!, default!, default);
        await chatNotifier.DidNotReceiveWithAnyArgs().SendAsync(default, default!, default);
        await editor.DidNotReceiveWithAnyArgs().FindByBotMessageAsync(default, default, default);
    }

    [Fact]
    public async Task A_voice_reply_to_an_echo_queues_a_spoken_correction_instead_of_a_new_capture()
    {
        var (router, captureStore, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        var sentAt = DateTimeOffset.Parse("2026-09-24T10:00:00Z");
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        editor.RequestVoiceCorrectionAsync(transactionId, "voice-file-1", 6, sentAt, Arg.Any<CancellationToken>()).Returns(true);

        await router.HandleAsync(
            VoiceNote(111L, 6, sentAt.UtcDateTime, replyTo: new Message { Id = 42, Chat = new Chat { Id = 111L } }),
            "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).RequestVoiceCorrectionAsync(transactionId, "voice-file-1", 6, sentAt, Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(m => m.Text == Echo.Transcribing), Arg.Any<CancellationToken>());
        await captureStore.DidNotReceiveWithAnyArgs().CaptureVoiceAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_redelivered_voice_reply_edits_nothing_and_captures_nothing()
    {
        var (router, captureStore, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        editor.RequestVoiceCorrectionAsync(transactionId, "voice-file-1", 6, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await router.HandleAsync(
            VoiceNote(111L, 6, DateTime.UtcNow, replyTo: new Message { Id = 42, Chat = new Chat { Id = 111L } }),
            "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.DidNotReceiveWithAnyArgs().EditAsync(default, default, default!, default);
        await captureStore.DidNotReceiveWithAnyArgs().CaptureVoiceAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_voice_reply_to_anything_but_an_echo_is_captured_as_a_new_voice_note()
    {
        var (router, captureStore, _, editor, _) = CreateRouter(ownerChatId: 111L);
        editor.FindByBotMessageAsync(111L, 50, Arg.Any<CancellationToken>()).Returns((EchoTarget?)null);

        await router.HandleAsync(
            VoiceNote(111L, 6, DateTime.UtcNow, replyTo: new Message { Id = 50, Chat = new Chat { Id = 111L } }),
            "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureVoiceAsync(Arg.Any<CapturedVoice>(), "Europe/Belgrade", Arg.Any<CancellationToken>());
        await editor.DidNotReceiveWithAnyArgs().RequestVoiceCorrectionAsync(default, default!, default, default, default);
    }

    [Fact]
    public async Task An_edited_voice_note_is_ignored()
    {
        var (router, _, _, editor, _) = CreateRouter(ownerChatId: 111L);
        var edited = new Update
        {
            Id = 906,
            EditedMessage = new Message
            {
                Id = 5, Chat = new Chat { Id = 111L }, Date = DateTime.UtcNow, Caption = "новая подпись",
                Voice = new Voice { FileId = "voice-file-1", FileUniqueId = "unique-1", Duration = 4 },
            },
        };

        await router.HandleAsync(edited, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.DidNotReceiveWithAnyArgs().FindByUserMessageAsync(default, default, default);
        await editor.DidNotReceiveWithAnyArgs().ReplaceRawTextAsync(default, default!, default);
    }
```

`An_edited_voice_note_is_ignored` passes already, because `HandleEditAsync` returns on a null `Text`. It is written to pin that behaviour, which the spec states (V7). It is the one test here that is not seen red first. Say so in the report.

In `tests/Noof.Ledger.Telegram.Tests/TelegramRegistrationTests.cs`, in `AddNoofTelegram_registers_the_notifier_the_router_and_the_poller`, add this line (with `using Noof.Ledger.Application.Transcription;`):

```csharp
    scope.ServiceProvider.GetRequiredService<IVoiceFileSource>().Should().BeOfType<TelegramVoiceFileSource>();
```

In `PublicSurfaceTests.Allowed["Noof.Ledger.Application"]`, add `"IVoiceFileSource"`.

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL. `IVoiceFileSource` and `TelegramVoiceFileSource` do not exist.

Add them as empty shells: an interface, and a class whose method throws `NotImplementedException`. Then run `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`.
Expected: the router voice tests FAIL, because a voice note is currently ignored as "no text". The download tests fail too.

- [ ] **Step 3: Implement**

`src/Noof.Ledger.Application/Transcription/IVoiceFileSource.cs`:

```csharp
namespace Noof.Ledger.Application.Transcription;

public interface IVoiceFileSource
{
    // The voice note's audio, read into memory and positioned at its start; the caller disposes it. A failed
    // download propagates: the worker treats it as transient (V9).
    Task<Stream> DownloadAsync(string voiceFileId, CancellationToken cancellationToken);
}
```

`src/Noof.Ledger.Telegram/TelegramVoiceFileSource.cs`:

```csharp
using Noof.Ledger.Application.Transcription;
using Telegram.Bot;

namespace Noof.Ledger.Telegram;

internal sealed class TelegramVoiceFileSource(TelegramClientHandle clientHandle) : IVoiceFileSource
{
    public async Task<Stream> DownloadAsync(string voiceFileId, CancellationToken cancellationToken)
    {
        var client = clientHandle.Current ?? throw new InvalidOperationException(
            "The Telegram client is not ready yet: no bot token has been saved.");

        var audio = new MemoryStream();
        await client.GetInfoAndDownloadFile(voiceFileId, audio, cancellationToken);
        audio.Position = 0;
        return audio;
    }
}
```

If an analyzer (CA2000) objects to the `MemoryStream` on the throwing path, dispose it in a `catch { … throw; }` around the download.

`src/Noof.Ledger.Telegram/TelegramRegistration.cs`: add `services.AddSingleton<IVoiceFileSource, TelegramVoiceFileSource>();` after the `IChatNotifier` line.

`src/Noof.Ledger.Telegram/CorrectionHandler.cs`:

```csharp
    readonly EchoMessage transcribing = new(recordEcho.Transcribing, []);

    // The spoken form of TryHandleReplyAsync: the reply's voice is queued for transcription and its transcript
    // becomes the correction (V7). False lets the router capture the note as a new record.
    public async Task<bool> TryHandleVoiceReplyAsync(Message reply, Message repliedTo, Voice voice, CancellationToken cancellationToken)
    {
        if (await editor.FindByBotMessageAsync(reply.Chat.Id, repliedTo.Id, cancellationToken) is not { } target)
            return false;

        // reply.Date deserialises as DateTime with Kind=Utc, same as TelegramUpdateRouter's message.Date.
        var sentAt = new DateTimeOffset(reply.Date);
        if (await editor.RequestVoiceCorrectionAsync(target.TransactionId, voice.FileId, reply.Id, sentAt, cancellationToken)
            && target.EchoMessageId is { } echoId)
            await chatNotifier.EditAsync(reply.Chat.Id, echoId, transcribing, cancellationToken);

        return true;
    }
```

`src/Noof.Ledger.Telegram/TelegramUpdateRouter.cs`: after the owner-gate check in `HandleMessageAsync`, and before the `message.Text` check, insert:

```csharp
        if (message.Voice is { } voice)
        {
            await HandleVoiceAsync(message, voice, timeZoneId, cancellationToken);
            return;
        }
```

Then add:

```csharp
    async Task HandleVoiceAsync(Message message, Voice voice, string timeZoneId, CancellationToken cancellationToken)
    {
        if (message.ReplyToMessage is { } repliedTo
            && await correctionHandler.TryHandleVoiceReplyAsync(message, repliedTo, voice, cancellationToken))
            return;

        // message.Date is the send instant, Kind=Utc - see HandleMessageAsync.
        var captured = new CapturedVoice(message.Chat.Id, message.Id, voice.FileId, voice.Duration, new DateTimeOffset(message.Date));
        var transactionId = await captureStore.CaptureVoiceAsync(captured, timeZoneId, cancellationToken);

        var botMessageId = await chatNotifier.SendAsync(message.Chat.Id, recordEcho.Transcribing, cancellationToken);
        await captureStore.AttachBotMessageAsync(transactionId, botMessageId, cancellationToken);
    }
```

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Noof.Ledger.Application/Transcription/IVoiceFileSource.cs src/Noof.Ledger.Telegram tests/Noof.Ledger.Telegram.Tests tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs
git commit -m "feat(telegram): capture voice notes, take spoken corrections, download notes for transcription (V3, V7)"
```

---

### Task 7: The settings page gets the speech key

**Files:**
- Modify: `src/Noof.Ledger.Web/Components/Pages/Settings/Secrets.razor`
- Test: `tests/Noof.Ledger.E2E.Tests/SettingsSecretsTests.cs`

**Interfaces:**
- Consumes (Task 4): `ISpeechProvider` (`SecretKey`, `SecretLabel`). The Groq factory is also a registered `ISecretProbe`, which gives the row its Test button.
- Produces: a row `#status-groq-api-key` with a `#test-groq-api-key` button. The page source never names the provider.

- [ ] **Step 1: Write the failing E2E test**

In `tests/Noof.Ledger.E2E.Tests/SettingsSecretsTests.cs`, add the constant next to `AnthropicApiKey`, then append the test:

```csharp
    // The speech provider's secret as the page renders it into element ids (spec V11). Tests may name the provider;
    // the page may not.
    const string GroqApiKey = "groq-api-key";

    [Fact]
    public async Task The_speech_key_has_its_own_row_with_a_Test_button()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.GetByText("Groq API key")).ToBeVisibleAsync();
        await Expect(Page.Locator($"#status-{GroqApiKey}")).ToBeVisibleAsync();
        await Expect(Page.Locator($"#test-{GroqApiKey}")).ToBeVisibleAsync();
    }
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj --filter SettingsSecretsTests`
Expected: `The_speech_key_has_its_own_row_with_a_Test_button` FAILS, because there is no such element.

- [ ] **Step 3: Add the row**

`src/Noof.Ledger.Web/Components/Pages/Settings/Secrets.razor`:
- add `@using Noof.Ledger.Application.Transcription` and `@inject ISpeechProvider SpeechProvider` next to the existing `@inject IModelProvider ModelProvider`;
- in `OnInitializedAsync`, insert the new row as the second entry of `rows`, right after the model provider's row:

```csharp
            new() { Key = SpeechProvider.SecretKey, Label = SpeechProvider.SecretLabel },
```

Nothing else changes. The existing loop reads its status with `GetStatusAsync`. It finds its probe among `Probes`.

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj --filter SettingsSecretsTests`
Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS. `SecretsPageSourceTests` and `AiBoundaryTests` stay green, because the page names no provider.

- [ ] **Step 5: Commit**

```bash
git add src/Noof.Ledger.Web/Components/Pages/Settings/Secrets.razor tests/Noof.Ledger.E2E.Tests/SettingsSecretsTests.cs
git commit -m "feat(web): the speech-to-text key has its own row and Test button (V11)"
```

---

### Task 8: `TranscriptionWorker`

**Files:**
- Create: `src/Noof.Ledger.Host/Workers/TranscriptionWorker.cs`
- Modify: `src/Noof.Ledger.Host/Workers/WorkerRegistration.cs`
- Test: `tests/Noof.Ledger.Host.Tests/TranscriptionWorkerTests.cs` (new), `tests/Noof.Ledger.Host.Tests/CategorizationWiringTests.cs`

**Interfaces:**
- Consumes:
  - `IJobQueue.ClaimAsync(workerId, kinds, lease, ct)` (Task 2);
  - `ITranscriptionStore` (Task 3);
  - `ITranscriber` and `ISpeechProvider` (Task 4);
  - `IRecordEcho.HeardNothing`, `.TranscriptionFailure` and `.ComposeHeardNothing` (Task 5);
  - `IVoiceFileSource` (Task 6);
  - the existing `ICategorizationStore`, `IChatNotifier`, `CategorizationWorkerOptions` and `CategorizationTickResult`.
- Produces: a hosted `TranscriptionWorker`, registered by `AddNoofWorkers`.

The worker reuses `CategorizationWorkerOptions` on purpose. Its attempt cap must equal the one `EfJobQueue` enforces, and `Program.cs` binds exactly one. Its own lease, poll interval, backoff and account cooldown come from the same section, and it keeps its own cooldown timer.

- [ ] **Step 1: Write the failing tests**

`tests/Noof.Ledger.Host.Tests/TranscriptionWorkerTests.cs`:

```csharp
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
        await harness.Queue.DidNotReceiveWithAnyArgs().ClaimAsync(default!, default!, default, default);
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
        await harness.TranscriptionStore.DidNotReceiveWithAnyArgs().CompleteCorrectionAsync(default, default!, default, default, default);
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, default);
    }

    [Fact]
    public async Task A_spoken_correction_becomes_a_correction_with_the_transcript_as_its_instruction()
    {
        var harness = Setup(CorrectionJob(), transcript: "нет, полторы тысячи", record: RecordedCoffee());

        await TickAsync(harness);

        await harness.VoiceFiles.Received(1).DownloadAsync("reply-voice", Arg.Any<CancellationToken>());
        await harness.TranscriptionStore.Received(1).CompleteCorrectionAsync(
            TransactionId, "нет, полторы тысячи", 900, new DateOnly(2026, 9, 25), Arg.Any<CancellationToken>());
        await harness.TranscriptionStore.DidNotReceiveWithAnyArgs().CompleteCaptureAsync(default, default!, default);
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
        await harness.TranscriptionStore.DidNotReceiveWithAnyArgs().CompleteCaptureAsync(default, default!, default);
    }

    [Fact]
    public async Task Nothing_heard_in_a_spoken_correction_leaves_the_record_and_says_so()
    {
        var harness = Setup(CorrectionJob(), transcript: "", record: RecordedCoffee());

        await TickAsync(harness);

        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, default);
        await harness.Notifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(m => m.Text.StartsWith("Heard nothing in that voice note.\n\nRecorded — Cash", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await harness.TranscriptionStore.DidNotReceiveWithAnyArgs().CompleteCorrectionAsync(default, default!, default, default, default);
    }

    [Fact]
    public async Task A_transient_failure_retries_and_tells_nobody_yet()
    {
        var harness = Setup(CaptureJob());
        harness.Transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "Groq transcription failed with status 429."));

        await TickAsync(harness);

        await harness.Queue.Received(1).RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Queue.DidNotReceiveWithAnyArgs().FailAsync(default, default!, default!, default);
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, default);
        await harness.Notifier.DidNotReceiveWithAnyArgs().EditAsync(default, default, default!, default);
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

        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, default);
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
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, default);
    }

    [Fact]
    public async Task A_failed_download_is_retried()
    {
        var harness = Setup(CaptureJob());
        harness.VoiceFiles.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("telegram unreachable"));

        await TickAsync(harness);

        await harness.Queue.Received(1).RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.Transcriber.DidNotReceiveWithAnyArgs().TranscribeAsync(default!, default);
    }

    [Fact]
    public async Task A_rerun_whose_transcript_was_already_stored_still_succeeds_the_job()
    {
        var harness = Setup(CaptureJob());
        harness.TranscriptionStore.CompleteCaptureAsync(TransactionId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.Queue.Received(1).SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>());
        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, default);
        await harness.Notifier.DidNotReceiveWithAnyArgs().EditAsync(default, default, default!, default);
    }

    [Fact]
    public async Task A_SucceedAsync_failure_after_the_hand_off_never_fails_the_record()
    {
        var harness = Setup(CaptureJob());
        harness.Queue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("connection reset"));

        (await TickAsync(harness)).Should().Be(CategorizationTickResult.Processed);

        await harness.Store.DidNotReceiveWithAnyArgs().MarkFailedAsync(default, default);
        await harness.Queue.DidNotReceiveWithAnyArgs().RetryAsync(default, default!, default, default!, default);
    }

    [Fact]
    public async Task A_broken_container_is_reported_as_failed_without_throwing()
    {
        var worker = CreateWorker(new ThrowingScopeFactory());

        (await worker.RunTickAsync(TestContext.Current.CancellationToken)).Should().Be(CategorizationTickResult.Failed);
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
                serviceType == typeof(IJobQueue) ? throw new InvalidOperationException("the container cannot resolve IJobQueue") : null;
        }
    }
}
```

Append to `tests/Noof.Ledger.Host.Tests/CategorizationWiringTests.cs`. Add `using Noof.Ledger.Application.Transcription;`:

```csharp
    [Fact]
    public void Every_scoped_transcription_port_resolves_without_touching_the_database()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITranscriptionStore>();
        scope.ServiceProvider.GetRequiredService<ITranscriber>();
        scope.ServiceProvider.GetRequiredService<ISpeechProvider>();
        scope.ServiceProvider.GetRequiredService<IVoiceFileSource>();
    }

    [Fact]
    public void TranscriptionWorker_is_registered_as_a_hosted_service()
    {
        using var factory = Factory();

        factory.Services.GetServices<IHostedService>().Should().Contain(service => service is TranscriptionWorker);
    }
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL. `TranscriptionWorker` does not exist.

Add an empty shell: a `BackgroundService` whose `RunTickAsync` returns `CategorizationTickResult.Idle` and whose `ExecuteAsync` returns `Task.CompletedTask`. Then run `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter TranscriptionWorkerTests`.
Expected: most tests FAIL, which proves they test behaviour. `Without_a_speech_key_it_claims_nothing` may already pass against the shell's `Idle`; that is acceptable.

- [ ] **Step 3: Implement the worker**

`src/Noof.Ledger.Host/Workers/TranscriptionWorker.cs`:

```csharp
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Transcription;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Workers;

// Claims only Transcribe jobs and hands each transcript to the ordinary pipeline (V4, V8). It shares
// CategorizationWorkerOptions on purpose: its attempt cap must be the one EfJobQueue enforces, and there is one.
internal sealed class TranscriptionWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    CategorizationWorkerOptions options,
    string workerId,
    IRecordEcho recordEcho,
    ILogger<TranscriptionWorker> logger)
    : BackgroundService
{
    static readonly JobKind[] ClaimableKinds = [JobKind.Transcribe];

    // In memory and per instance, for CategorizationWorker's reasons; a broken speech key pauses only this worker.
    DateTimeOffset accountCooldownUntil = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = await RunTickAsync(stoppingToken);

            var delay = result == CategorizationTickResult.Processed ? TimeSpan.Zero : options.PollInterval;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, timeProvider, stoppingToken);
        }
    }

    public async Task<CategorizationTickResult> RunTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobQueue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var speechProvider = scope.ServiceProvider.GetRequiredService<ISpeechProvider>();

            var now = timeProvider.GetUtcNow();
            await jobQueue.ReleaseExpiredLeasesAsync(now, cancellationToken);

            // Both checked before claiming, for CategorizationWorker's reason: a claim spends an attempt and nothing
            // gives it back.
            if (!await speechProvider.IsConfiguredAsync(cancellationToken))
                return CategorizationTickResult.Idle;

            if (now < accountCooldownUntil)
                return CategorizationTickResult.Idle;

            var job = await jobQueue.ClaimAsync(workerId, ClaimableKinds, options.Lease, cancellationToken);
            if (job is null)
                return CategorizationTickResult.Idle;

            await ProcessClaimedJobAsync(scope, jobQueue, job, cancellationToken);
            return CategorizationTickResult.Processed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Transcription worker tick failed");
            return CategorizationTickResult.Failed;
        }
    }

    async Task ProcessClaimedJobAsync(IServiceScope scope, IJobQueue jobQueue, CategorizationJob job, CancellationToken cancellationToken)
    {
        var store = scope.ServiceProvider.GetRequiredService<ICategorizationStore>();
        var notifier = scope.ServiceProvider.GetRequiredService<IChatNotifier>();

        CategorizationSubject? record = null;

        try
        {
            record = await store.GetSubjectAsync(job.TransactionId, cancellationToken);
            if (record is null)
            {
                await jobQueue.FailAsync(job.Id, workerId, "the transaction this job points at no longer exists", cancellationToken);
                return;
            }

            var voiceFiles = scope.ServiceProvider.GetRequiredService<IVoiceFileSource>();
            var transcriber = scope.ServiceProvider.GetRequiredService<ITranscriber>();

            // A check constraint holds voice_file_id on every Transcribe job.
            await using var audio = await voiceFiles.DownloadAsync(job.VoiceFileId!, cancellationToken);
            var transcript = await transcriber.TranscribeAsync(audio, cancellationToken);

            if (transcript.Length == 0)
            {
                await ReportNothingHeardAsync(jobQueue, store, notifier, job, record, cancellationToken);
                return;
            }

            var transcriptionStore = scope.ServiceProvider.GetRequiredService<ITranscriptionStore>();
            var handedOn = job.SourceMessageId is { } sourceMessageId
                ? await transcriptionStore.CompleteCorrectionAsync(
                    job.TransactionId, transcript, sourceMessageId, job.InstructionDay, cancellationToken)
                : await transcriptionStore.CompleteCaptureAsync(job.TransactionId, transcript, cancellationToken);

            if (!handedOn)
                logger.LogInformation("Job {JobId}'s transcript was already handed on by an earlier run", job.Id);

            // The hand-off is committed. As in CategorizationWorker, nothing past this line may count as the job
            // failing: that would mark Failed a record whose reading is already queued.
            await SucceedQuietlyAsync(jobQueue, job, cancellationToken);
        }
        catch (ModelCallException ex) when (ex.IsAccountLevel())
        {
            // 401/402/403: the key, not this note, is what is broken. Retried, never failed, and claiming pauses so the
            // backlog is not burned through while the key stays bad.
            accountCooldownUntil = timeProvider.GetUtcNow() + options.AccountCooldown;
            logger.LogWarning(
                "Account-level speech provider failure on job {JobId} ({Message}); pausing new claims for {Cooldown}",
                job.Id, ex.Message, options.AccountCooldown);
            await HandleFailureAsync(jobQueue, store, notifier, job, record, ModelFailureKind.Transient, ex.Message, cancellationToken);
        }
        catch (ModelCallException ex)
        {
            await HandleFailureAsync(jobQueue, store, notifier, job, record, ex.Kind, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed download lands here and is worth another attempt (V9); the attempt cap bounds everything else.
            await HandleFailureAsync(jobQueue, store, notifier, job, record, ModelFailureKind.Transient, ex.Message, cancellationToken);
        }
    }

    async Task ReportNothingHeardAsync(
        IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier, CategorizationJob job,
        CategorizationSubject record, CancellationToken cancellationToken)
    {
        if (await jobQueue.FailAsync(job.Id, workerId, "nothing was heard in the voice note", cancellationToken)
            != JobCompletionOutcome.Applied)
            return;

        // A spoken correction that said nothing leaves the record exactly as it was (V6).
        if (IsCorrection(job))
        {
            await EditQuietlyAsync(notifier, record, recordEcho.ComposeHeardNothing(record), cancellationToken);
            return;
        }

        await store.MarkFailedAsync(job.TransactionId, cancellationToken);
        await EditQuietlyAsync(notifier, record, recordEcho.HeardNothing, cancellationToken);
    }

    async Task HandleFailureAsync(
        IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier, CategorizationJob job,
        CategorizationSubject? record, ModelFailureKind kind, string error, CancellationToken cancellationToken)
    {
        if (kind == ModelFailureKind.Terminal)
        {
            if (await jobQueue.FailAsync(job.Id, workerId, error, cancellationToken) == JobCompletionOutcome.Applied)
                await ReportFailureAsync(store, notifier, job, record, cancellationToken);
            return;
        }

        // The predicate EfJobQueue.RetryAsync evaluates; the two agree because both read the one MaxAttempts.
        var isLastAttempt = job.AttemptCount >= options.MaxAttempts;
        var runAfter = timeProvider.GetUtcNow() + options.ComputeBackoff(job.AttemptCount);
        var outcome = await jobQueue.RetryAsync(job.Id, workerId, runAfter, error, cancellationToken);

        if (outcome == JobCompletionOutcome.Applied && isLastAttempt)
            await ReportFailureAsync(store, notifier, job, record, cancellationToken);
    }

    async Task ReportFailureAsync(
        ICategorizationStore store, IChatNotifier notifier, CategorizationJob job, CategorizationSubject? record,
        CancellationToken cancellationToken)
    {
        // Only a voice capture is marked Failed; a failed spoken correction leaves the record as the person saw it.
        if (!IsCorrection(job))
            await store.MarkFailedAsync(job.TransactionId, cancellationToken);

        if (record is null)
            return;

        var echo = IsCorrection(job) ? recordEcho.ComposeCorrectionFailure(record) : recordEcho.TranscriptionFailure;
        await EditQuietlyAsync(notifier, record, echo, cancellationToken);
    }

    // A capture's transcription has no source message; a spoken correction's is the reply it arrived in.
    static bool IsCorrection(CategorizationJob job) => job.SourceMessageId is not null;

    async Task EditQuietlyAsync(IChatNotifier notifier, CategorizationSubject record, EchoMessage echo, CancellationToken cancellationToken)
    {
        if (record.BotMessageId is not { } messageId)
            return;

        try
        {
            await notifier.EditAsync(record.TelegramChatId, messageId, echo, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to edit Telegram message {MessageId} for transaction {TransactionId}",
                messageId, record.TransactionId);
        }
    }

    async Task SucceedQuietlyAsync(IJobQueue jobQueue, CategorizationJob job, CancellationToken cancellationToken)
    {
        try
        {
            if (await jobQueue.SucceedAsync(job.Id, workerId, cancellationToken) == JobCompletionOutcome.NotOwned)
                logger.LogWarning("Job {JobId} was already reclaimed by another worker; not retrying", job.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "SucceedAsync failed for job {JobId} after its transcript was already handed on", job.Id);
        }
    }
}
```

`src/Noof.Ledger.Host/Workers/WorkerRegistration.cs`: after the `CategorizationWorker` registration, add

```csharp
        services.AddHostedService(sp => new TranscriptionWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            options,
            CategorizationWorker.CreateWorkerId(),
            sp.GetRequiredService<IRecordEcho>(),
            sp.GetRequiredService<ILogger<TranscriptionWorker>>()));
```

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS. The architecture suite includes `Only_ProposalMapper_constructs_a_Money_inside_the_categorization_pipeline`, which scans `Host/Workers`. The worker constructs no `Money`.

- [ ] **Step 5: Run the whole solution (holding the suite lock)**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: every test passes except the skipped live suites.

- [ ] **Step 6: Commit**

```bash
git add src/Noof.Ledger.Host/Workers tests/Noof.Ledger.Host.Tests
git commit -m "feat(host): TranscriptionWorker claims Transcribe jobs and hands transcripts to the pipeline (V4, V6, V8, V9)"
```

---

### Task 9: Live test, documentation, status

**Files:**
- Create: `tests/Noof.Ledger.Ai.Tests/Groq/LiveTranscriptionGate.cs`, `LiveTranscriptionGateTests.cs`, `LiveTranscriptionTests.cs`
- Modify: `docs/OPEN-QUESTIONS.md`, `docs/BACKLOG.md`, `ops/RUNBOOK.md`, `CLAUDE.md`, `README.md`

**Interfaces:**
- Consumes: `GroqSpeechToTextClientFactory`, `GroqOptions`, `SpeechTranscriber` (Task 4) and the test project's `StubSecretStore`.
- Produces: nothing code depends on.

- [ ] **Step 1: The gate, test first**

`tests/Noof.Ledger.Ai.Tests/Groq/LiveTranscriptionGateTests.cs`:

```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Ai.Tests.Groq;

public class LiveTranscriptionGateTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("gsk-x", null)]
    [InlineData(null, @"C:\voice\note.ogg")]
    [InlineData("  ", @"C:\voice\note.ogg")]
    [InlineData("gsk-x", "  ")]
    public void Stays_closed_unless_both_the_key_and_the_file_are_given(string? key, string? file)
    {
        LiveTranscriptionGate.TryParse(key, file, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Opens_when_both_are_given()
    {
        LiveTranscriptionGate.TryParse("gsk-x", @"C:\voice\note.ogg", out var key, out var file).Should().BeTrue();
        key.Should().Be("gsk-x");
        file.Should().Be(@"C:\voice\note.ogg");
    }
}
```

Run: `dotnet build NoofLedger.slnx`. Expected: FAIL, because `LiveTranscriptionGate` does not exist.

`tests/Noof.Ledger.Ai.Tests/Groq/LiveTranscriptionGate.cs`:

```csharp
namespace Noof.Ledger.Ai.Tests.Groq;

static class LiveTranscriptionGate
{
    public const string KeyVariable = "NOOF_LEDGER_LIVE_GROQ_KEY";
    public const string FileVariable = "NOOF_LEDGER_LIVE_VOICE_FILE";

    public static string SkipMessage { get; } =
        $"No live Groq run - set both {KeyVariable} and {FileVariable} (a voice note kept outside the repository) to run it " +
        "(see tests/Noof.Ledger.Ai.Tests/Groq/LiveTranscriptionTests.cs).";

    public static bool TryGet(out string apiKey, out string voiceFile) =>
        TryParse(Environment.GetEnvironmentVariable(KeyVariable), Environment.GetEnvironmentVariable(FileVariable),
            out apiKey, out voiceFile);

    internal static bool TryParse(string? rawKey, string? rawFile, out string apiKey, out string voiceFile)
    {
        apiKey = rawKey ?? "";
        voiceFile = rawFile ?? "";
        return !string.IsNullOrWhiteSpace(rawKey) && !string.IsNullOrWhiteSpace(rawFile);
    }
}
```

`tests/Noof.Ledger.Ai.Tests/Groq/LiveTranscriptionTests.cs`:

```csharp
using AwesomeAssertions;
using Noof.Ledger.Ai.Groq;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai.Tests.Groq;

// Opt-in, and only with the operator's permission: it sends their own voice note to Groq. Skipped unless both
// variables are set. The file lives outside the repository - no real voice is ever committed.
public class LiveTranscriptionTests
{
    [Fact]
    public async Task A_real_voice_note_comes_back_as_text()
    {
        if (!LiveTranscriptionGate.TryGet(out var apiKey, out var voiceFile))
            Assert.Skip(LiveTranscriptionGate.SkipMessage);

        var factory = new GroqSpeechToTextClientFactory(
            new StubSecretStore(SecretState.Present, apiKey), new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, new GroqOptions());
        var transcriber = new SpeechTranscriber(factory);

        await using var audio = File.OpenRead(voiceFile);
        var text = await transcriber.TranscribeAsync(audio, TestContext.Current.CancellationToken);

        text.Should().NotBeEmpty();
        TestContext.Current.SendDiagnosticMessage($"Heard: {text}");
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Expected: PASS, with `A_real_voice_note_comes_back_as_text` **skipped**. Do not set the variables.

Commit: `git add tests/Noof.Ledger.Ai.Tests/Groq && git commit -m "test(ai): opt-in live transcription test, skipped by default"`

- [ ] **Step 2: Check one fact before recording it**

Fetch Anthropic's own documentation on tool use (`https://docs.claude.com/en/docs/agents-and-tools/tool-use/implement-tool-use`, or search "tool_choice forced tool use not supported Opus 5.5"). Look for the statement AWS's Bedrock page makes: *"Claude Opus 5.5, Claude Fable 5.1, and Claude Mythos 5.1 do not support forced tool use"*. Record in P3-2 whether Anthropic's own page confirms it, with the URL, or that only Bedrock's page says it. This is a documentation read, never an API call.

- [ ] **Step 3: `docs/OPEN-QUESTIONS.md`**

Append, in the house style of P2-5 (a `###` heading, prose with bold lead-ins, one table):

```markdown
### P3-1 — Speech-to-text provider (2026-09-23)

**Decision: Groq's hosted `whisper-large-v3`** — the operator's call, *"Давай грок"*, after a comparison of four options researched and adversarially fact-checked against vendor pages on 2026-09-23.

| Option | Why not (or why) |
|---|---|
| **Groq `whisper-large-v3`** (chosen) | Accepts Telegram's OGG/Opus as uploaded (name it `.ogg`; `.oga` is refused). Free tier without a card: 20 requests/min, 28,800 audio-seconds/day — ~60× this volume; paid ≈ $0.42/month. No training on customer data; not retained by default; zero data retention is a self-serve console switch. US-hosted; no SLA. |
| OpenAI `gpt-transcribe` | ≈ $1/month, no retention on the transcription endpoint, no training. Does not accept OGG — needs an Opus decoder in C#. Its older transcription models were deprecated 2026-08-26 (removal 2027-02-26). The operator's first preference, set aside because Groq needs neither the decoder nor a paid balance. |
| AWS (Transcribe + Claude on Bedrock) | Batch transcription requires S3 and polling; Transcribe may keep audio to improve its models unless an Organizations opt-out policy is set; strict tool use on Bedrock is undocumented and reported broken for Sonnet 5. |
| Azure (AI Speech / OpenAI + Claude in Foundry) | Fast transcription has no `ru-RU`; Claude in Foundry has no EU data zone; billing through Azure Marketplace. |
| Local Whisper on the operator's GPU | Private and free, but Vulkan on the RX 7900 XT under Windows was unverified, and a 1.6 GB model and native runtime to ship. Not chosen; the seam keeps it possible. |

**What keeps this cheap to reverse:** transcription goes through `ISpeechToTextClient` from a factory that only `src/Noof.Ledger.Ai/Groq/` implements (V2). OpenAI speaks the same wire protocol; moving there is a new folder, a key and a decoder. Real-voice comparison belongs to Phase 11 calibration.

### P3-2 — Forced tool use rules out some Claude models (found 2026-09-23)

<one paragraph: what the Bedrock documentation says, what Anthropic's own documentation says (Step 2's result, with URLs), and the consequence — the app runs `claude-haiku-4-5`, so nothing breaks today, but Phase 2's forced strict tool call (`tool_choice` any/tool) cannot run on Claude Opus 5.5, Fable 5.1 or Mythos 5.1. Moving to one of them is a design change to the answer contract, not a model-id change.>
```

Write the P3-2 paragraph from Step 2's finding. Do not leave the angle-bracket placeholder in the file.

- [ ] **Step 4: `docs/BACKLOG.md`, `ops/RUNBOOK.md`**

`docs/BACKLOG.md`: add four entries, following the file's existing format.
- **Vocabulary hints for the transcriber.** Groq takes a `prompt` of up to 224 tokens. Merchant names from the directory would help it spell *Maxi*, *Lidl* and *Wolt*. That sends a slice of the shopping profile to Groq, the same trade as Q7. It belongs to Phase 11 calibration.
- **Keeping the audio.** Nothing stores the voice note itself. The `file_id` can fetch it again while Telegram keeps it. A re-transcription corpus for Phase 11 would need the bytes in the database, which would then be in every backup. That is a privacy decision for the operator.
- **Whisper's inventions on silence.** A near-silent note can come back as *«Продолжение следует…»* or *«Субтитры сделал…»*. It is recorded as whatever the model makes of it, and the `🎤 "…"` line shows it. P2-1 forbids a filter. If it happens often, the answer is a decision (for example, Groq's `verbose_json` `no_speech_prob`), not a quiet guard.
- **A second speech provider.** Groq publishes no SLA. The seam allows a fallback, and nothing uses one yet.

`ops/RUNBOOK.md`: add a section `## Speech-to-text (Groq)`:
1. Create a key at console.groq.com (API Keys). The free tier needs no card.
2. In the Groq console, under Data Controls, turn on **Zero Data Retention**. Without it Groq may keep request logs up to 30 days for troubleshooting.
3. In the app, open Settings → Secrets, paste the key into **Groq API key**, save, and press **Test**. The test calls Groq's free `GET /models`.
4. Without a key the bot still acknowledges voice notes with `🎤 Transcribing…` and keeps them queued. They are transcribed once a key is saved. Nothing is lost and no attempt is spent.
5. Rate limits: 20 requests/minute on the free tier. A burst of notes is retried with backoff, not failed.

- [ ] **Step 5: `CLAUDE.md`, `README.md`**

`CLAUDE.md`: keep the file short. It is read in full every session.
- Status block:
  - "Phases 0, 0b, 1A, 1B, 1C, 1D and 2 complete" becomes "… 2 and 3 complete".
  - Add one clause for what Phase 3 delivered: "voice notes are transcribed by Groq's whisper-large-v3 behind `ISpeechToTextClient`, echoed with what was heard, and a spoken reply corrects a record".
  - Update the test count to the actual green count from the final full run. Say which count it is (passed, not total), as Phase 2 did.
  - Mention the new opt-in live test: "…live suites stay skipped unless `NOOF_LEDGER_LIVE_ANTHROPIC_KEY` / `NOOF_LEDGER_LIVE_GROQ_KEY` + `NOOF_LEDGER_LIVE_VOICE_FILE` are set".
  - "Next is Phase 3, voice (…)" becomes "Next is Phase 4, the money model."
- §4 *The model*: replace the D-A bullet with this. Keep its link to `AiBoundaryTests`:

  > **Nothing depends on an AI provider except its factory**: `IChatClientFactory` for the model and `ISpeechToTextClientFactory` for speech, each implemented in its own folder under `src/Noof.Ledger.Ai/<Provider>/` *(D-A, 2026-09-23; speech P3-1)*. Asserted by `AiBoundaryTests`. `ISpeechToTextClient` is experimental (`MEAI001`), and the warning is suppressed in `Noof.Ledger.Ai` and its tests only.

- §4 *The model*, after the forced-tool bullet, add one line: `Forced tool use is unsupported on Claude Opus 5.5, Fable 5.1 and Mythos 5.1 — docs/OPEN-QUESTIONS.md P3-2.`

`README.md` status block:
- Add a sentence after the natural-language one: *Voice notes work the same way: say it, and the bot records what it heard.*
- Change "**voice notes** (next) **and receipt photos**" to "**receipt photos**".
- Update the test count.

- [ ] **Step 6: Run the whole solution and commit**

Run (holding the suite lock): `dotnet test --solution NoofLedger.slnx`. Use its passed count in `CLAUDE.md` and `README.md`.

```bash
git add docs ops CLAUDE.md README.md
git commit -m "docs: close Phase 3 - provider decision P3-1, forced-tool finding P3-2, runbook, backlog, status"
```
