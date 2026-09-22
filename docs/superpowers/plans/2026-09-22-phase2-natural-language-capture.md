# Phase 2: Natural-Language Capture, Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** *"купил вчера штуку евро"* becomes 1000 EUR dated yesterday. The bot echoes the stored record with **Отменить · Изменить**. A reply such as *"нет, 1500"* corrects it, and every state the record passes through is kept.

**Architecture:** The model stops quoting and starts interpreting. It returns a decimal amount, a currency, an optional ISO date, a slug and a merchant name. `ProposalMapper` *parses* those values and never checks them against the message. The safety shifts from rejection to visibility. The worker writes the result together with an append-only revision in one database transaction, reads the record back, and C# renders the echo from the stored rows. Corrections are one more kind of `categorization_jobs` row, so each one gets the claim, lease, retry and attempt cap the queue already provides. Cancel and restore are deterministic database operations that make no model call.

**Tech Stack:** .NET 10 · C# · EF Core 10 + Npgsql · PostgreSQL 18 · Anthropic 12.49.0 through `Microsoft.Extensions.AI` `IChatClient` (structured output via `ChatResponseFormat.ForJsonSchema`) · Telegram.Bot 22.10.3.1 · Blazor Server + MudBlazor · xUnit v3 on Microsoft Testing Platform · AwesomeAssertions · NSubstitute

**Spec:** `docs/superpowers/specs/2026-09-22-natural-language-capture.md` (decisions D1–D8). It amends `docs/superpowers/specs/2026-09-19-noof-finance-design.md` §8 and §12. The decision record is `docs/OPEN-QUESTIONS.md` P2-1 and the deferred work is in `docs/BACKLOG.md`. The predecessor plan is `docs/superpowers/plans/2026-09-21-phase1b-categorisation-and-dashboard.md`.

> ### ⚠ Decisions in this plan that are expensive to reverse. Read these before Task 2.
>
> Each of these is a schema change that migrates `noof_ledger` the next time the operator starts the host (`Database:MigrateOnStartup` defaults to `true`). The spec fixes 1 and 5. This plan chose 2, 3 and 4, and they are flagged here so the operator can veto them before any code exists.
>
> 1. **`transactions.occurred_on date NOT NULL`, backfilled** as `(occurred_at AT TIME ZONE time_zone_id)::date`. The column is added nullable, backfilled, then set to `NOT NULL`, so no row ever holds an invented default date (D3).
> 2. **A correction is a `categorization_jobs` row, not a separate table.** The table gains `kind int NOT NULL DEFAULT 0` (`Categorize=0, Correct=1, Reinterpret=2`), `instruction text NULL` and `source_message_id int NULL`. A check constraint requires `kind <> 1 OR instruction IS NOT NULL`. A partial unique index on `(transaction_id, source_message_id) WHERE source_message_id IS NOT NULL` makes a redelivered reply idempotent. **The reason:** a correction needs exactly what the queue already guarantees (claim with `SKIP LOCKED`, lease, backoff, attempt cap, ownership-fenced completion). A second table would need either a second claim loop or a join in `ClaimAsync`'s `RETURNING`, plus its own lifecycle. The instruction is also only needed until the job runs, and after that the revision row keeps it. **Cost:** `ClaimAsync` gains a per-transaction ordering guard (Task 6). Without the guard, a correction could overtake a first categorisation that is still backing off, and the later categorisation would then overwrite the correction.
> 3. **`transactions.prompt_message_id int NULL`.** This is the id of the Изменить `ForceReply` prompt. It is needed because Telegram nests `reply_to_message` only one level deep, so a reply to the prompt cannot be traced back to the echo through Telegram alone.
> 4. **`transaction_revisions` shape:** `id uuid`, `transaction_id uuid` (FK **RESTRICT**, so a revised transaction can never be deleted), `revision_number int` (unique per transaction, assigned under the transaction's row lock), `kind int` (`Initial, Correction, Edit, Cancel, Restore`), `instruction text NULL`, `status_before int`, `status_after int`, `snapshot jsonb` and `created_at timestamptz`. The snapshot stores **amounts as decimal strings**, never JSON numbers, so no reader can take them as floating point. Append-only is enforced by a trigger that rejects `UPDATE`, `DELETE` and `TRUNCATE`, the same shape as `merchant_aliases_write_once`. `status_before` is how **Вернуть** knows which status to restore, so restore needs no guesswork.
> 5. **`TransactionStatus.Cancelled = 3`**, stored as its integer value like the other statuses.

## Global Constraints

- **Money is `decimal` + `Currency`.** Never `double`, never `float`. A model's amount is a *string* in the schema and in the DTO, and it becomes a `decimal` only through `decimal.TryParse(..., NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, ...)` in `ProposalMapper`.
- **Capture has no validation layer (settled 2026-09-22, D1, P2-1).** Do not add a verbatim check, an evidence span or a sanity bound anywhere. `ProposalMapper` only parses: a decimal, a supported currency, an offered slug, an offered merchant id, and an ISO date. A response that does not parse fails the job. The mapper does not judge whether a value is plausible.
- **Reports, totals and balances are computed by C#.** The echo's figures are read from the stored rows after the write (D4), never from the proposal.
- **The bot speaks Russian.** That covers every string the bot sends, the button labels and the category names in the echo (`name_ru`).
- **`noof_ledger` holds real credentials.** Never run a test, a manual check, `dotnet ef database update` or the published host against it. **`dotnet ef database update` without `--connection` resolves to `noof_ledger`**: `DesignTimeDbContextFactory` → `LedgerConnectionString.Resolve(null)` → the credential file → `Database=noof_ledger`. Always pass `--connection` naming `noof_ledger_test_template` (see "Updating the test template" below).
- **Never call the live model.** The live suite (`LiveModelTests`) stays skipped. Do not set `NOOF_LEDGER_LIVE_ANTHROPIC_KEY`. This plan updates the live tests so they compile and assert the new contract, and running them is the operator's decision in the final phase.
- **After `dotnet ef migrations add`, convert the new migration `.cs` (not the `.Designer.cs`, not the snapshot) to a file-scoped namespace.** `IDE0161` is an error and fails the build otherwise.
- **Never edit a migration that has already been applied, including to the test template.** Each task that changes the schema adds its own migration.
- **Never call `EnsureCreated()`.** Tests run against real PostgreSQL clones and never the InMemory provider.
- **Never seed a test with `DateTimeOffset.UtcNow` and then assert exact equality against a value read back from PostgreSQL.** Seed from a fixed literal, or compare with `BeCloseTo`.
- **Watch every new test fail before making it pass.** For an architecture rule, break the rule deliberately, see the failure name it, and revert.
- **Minimum accessibility.** Types are `internal` unless another assembly names them. Every newly public type is added to `PublicSurfaceTests.Allowed` in the same task that creates it.
- **Modern C#:** file-scoped namespaces, primary constructors, records, collection expressions, pattern matching, and `is null`. Comments only explain *why*, and there are no XML doc blocks.
- **TDD:** a failing test comes first for all behaviour. Migrations, DTOs and `Program.cs` wiring are exempt.
- **`global.json` keeps `{"test":{"runner":"Microsoft.Testing.Platform"}}`.** Use `dotnet test --project <csproj>` or `--solution NoofLedger.slnx`. `--filter <ClassName>` narrows a run.
- **Commit trailer.** Every commit message ends with exactly these two lines:
  ```
  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

### Updating the test template (every task that adds a migration)

`noof_ledger_test_template` is cloned by `MoneyStorageTests` and by the whole E2E suite (`CookieModeHostFixture`, which starts the host with `Database__MigrateOnStartup=false`). Most Persistence tests create an empty database and migrate it themselves, so they pass whether or not the template is current. **The E2E suite fails on a stale template with a missing-column error, and that failure is how you notice you forgot this step.** Run this in PowerShell from the repo root after each new migration builds:

```powershell
$admin = if ($env:NOOF_TEST_PG) { $env:NOOF_TEST_PG } else { (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection").Trim() }
$template = $admin -replace 'Database=postgres', 'Database=noof_ledger_test_template'
if ($template -notmatch 'Database=noof_ledger_test_template') { throw "Refusing: the connection string does not name the test template." }
dotnet ef database update --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --connection $template
```

The output's last line must name the new migration. Do **not** run it without `--connection`.

## Verified facts this plan is built on

| Fact | How it was established | Consequence |
|---|---|---|
| `CallbackQuery.Message`, `Update.EditedMessage`, `Update.CallbackQuery` and `Message.ReplyToMessage` are all typed `Message` in Telegram.Bot 22.10.3.1, and none of these types has `required` members | Reflection over the package DLL | Tests build them with object initialisers, and `query.Message` exposes `.Chat.Id` and `.Id` directly |
| `EditMessageText(chatId, messageId, text, parseMode, replyMarkup, ...)`, `SendMessage(chatId, text, parseMode, replyParameters, replyMarkup, ...)` and `AnswerCallbackQuery(callbackQueryId, text, showAlert, url, cacheTime, cancellationToken)` | Parameter names read by reflection | Named arguments `replyMarkup:`, `replyParameters:` and `cancellationToken:` are safe |
| `InlineKeyboardMarkup` has a constructor taking `IEnumerable<InlineKeyboardButton>`, which makes one row, and `InlineKeyboardButton.WithCallbackData(text, data)` exists | Reflection | Buttons are laid out as one row of up to two |
| `ReplyParameters.MessageId` is `int?`. `ApiRequestException` has a `(string message, int errorCode)` constructor | Reflection | Tests can throw Telegram's real exception type |
| `GetUpdatesRequest.AllowedUpdates` is `IEnumerable<UpdateType>` | Reflection | The polling test can assert which update types are requested |
| `categorization_jobs` has no unique index on `transaction_id` | `tests/Noof.Ledger.Persistence.Tests/schema.expected.sql` | Several jobs per transaction are already legal, and corrections need no index change for that |
| `line_items` has no ordinal column | Same file | Lines are read back ordered by `description`, so the echo is deterministic but not in the message's order. That is recorded in the backlog and not fixed here |
| `CategorizationSchemaTests.No_unsupported_json_schema_keyword_appears_anywhere` forbids `pattern`, `minLength`, `maxLength`, `minimum`, `maximum`, `multipleOf` and `$ref` | Read the test | The amount and the date are constrained by their `description` text only, and C# parses them |
| `DesignTimeDbContextFactory` resolves `noof_ledger` when `--connection` is omitted | `src/Noof.Ledger.Persistence/LedgerConnectionString.cs` | See Global Constraints. `dotnet ef migrations add` and `dotnet ef dbcontext script` never open a connection and are safe |
| Most Persistence tests migrate a fresh empty database, while `MoneyStorageTests` and the E2E suite clone the template | `PostgresFixture.CreateContextAsync` vs `CreateDatabaseAsync`, and `CookieModeHostFixture.CreateCloneAsync` | The template update step above is needed for the E2E suite, not for the Persistence suite |
| `ApplyConfigurationsFromAssembly` already picks up `internal sealed` configurations | Every existing configuration is internal | `TransactionRevisionConfiguration` can be internal too, next to an internal entity (as `AppSecret` is) |

## What this plan deliberately does NOT do

- **No voice.** Voice is Phase 3 and needs a speech-to-text decision first.
- **No editing in the dashboard, and no rollback to an earlier revision.** Both are in `docs/BACKLOG.md` already. The revisions table is what makes both cheap later.
- **No live-model calibration.** The prompt is written against the faked model only. Phase 11 tunes it with the operator's permission.
- **No "lenient" amount parsing.** `"45,30"` fails the job and does not become 45.30 or 4530. The schema and the prompt both say *dot*, and a guessed separator would be a sanity layer by another name (D1). The person sees the failure echo and can reply.
- **No per-message line ordering.** See the facts table.
- **No change to how the dashboard marks a Failed or Captured transaction.** Cancelled rows are simply absent from it (D5).

## File structure

| File | Responsibility |
|---|---|
| `src/Noof.Ledger.Domain/CurrencyCode.cs` | Gains `Supported`, the one list of accepted codes (schema enum and mapper both read it) |
| `src/Noof.Ledger.Domain/TransactionStatus.cs` | Gains `Cancelled = 3` |
| `src/Noof.Ledger.Domain/Transaction.cs` | `RawText` settable, plus `OccurredOn` and `PromptMessageId` |
| `src/Noof.Ledger.Domain/JobKind.cs` | **New.** `Categorize`, `Correct` and `Reinterpret` |
| `src/Noof.Ledger.Domain/CategorizationJob.cs` | Gains `Kind`, `Instruction` and `SourceMessageId` |
| `src/Noof.Ledger.Domain/QuotedAmount.cs` | **Deleted** |
| `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs` | Request with `Today` and an optional `Correction`. Proposal with `Amount`, `MerchantName` and `OccurredOn`. `RecordedLine`, `CategorizationOutcome`, `MappedProposal` and `CorrectionRequest` |
| `src/Noof.Ledger.Application/Categorization/ProposalMapper.cs` | **Replaces `ProposalVerification.cs`.** Parses and never judges |
| `src/Noof.Ledger.Application/Categorization/ICategorizationStore.cs` | `ApplyAsync(Guid, CategorizationOutcome, ...)` |
| `src/Noof.Ledger.Application/Chat/EchoMessage.cs` | **New.** `RecordAction` and `EchoMessage` |
| `src/Noof.Ledger.Application/Chat/RecordEcho.cs` | **New.** Every Russian string the bot sends, rendered from a stored record |
| `src/Noof.Ledger.Application/Chat/IChatNotifier.cs` | `EditAsync(..., EchoMessage)`, `AskAsync` and `AnswerActionAsync` |
| `src/Noof.Ledger.Application/Editing/IRecordEditor.cs` | **New.** Find by message, cancel, restore, request a correction, replace the raw text, attach the prompt |
| `src/Noof.Ledger.Application/Reporting/ISpendingReadModel.cs` | `RecentTransaction` carries `OccurredOn` and `LocalTime?` |
| `src/Noof.Ledger.Ai/CategorizationSchema.cs` · `CategorizationPrompt.cs` · `AnthropicCategorizer.cs` | Interpretation instead of quotation, the date, and corrections |
| `src/Noof.Ledger.Persistence/ZonedClock.cs` | **New.** A UTC instant converted to a local date or time in an IANA zone |
| `src/Noof.Ledger.Persistence/Capture/EfCaptureStore.cs` | Stamps `OccurredOn` at capture |
| `src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs` | Reads the full record, writes the outcome and its revision |
| `src/Noof.Ledger.Persistence/Editing/EfRecordEditor.cs` | **New.** Implements `IRecordEditor` |
| `src/Noof.Ledger.Persistence/Revisions/TransactionRevision.cs` · `RevisionKind.cs` · `RevisionLog.cs` | **New.** The append-only history, internal to Persistence |
| `src/Noof.Ledger.Persistence/Configurations/TransactionRevisionConfiguration.cs` | **New** |
| `src/Noof.Ledger.Persistence/Jobs/EfJobQueue.cs` | Claims return the new columns and never overtake an earlier job for the same transaction |
| `src/Noof.Ledger.Persistence/Reporting/EfSpendingReadModel.cs` | Buckets by `occurred_on` and leaves out cancelled rows |
| `src/Noof.Ledger.Persistence/Migrations/*_AddOccurredOn.cs` · `*_AddCorrectionJobs.cs` · `*_AddTransactionRevisions.cs` | **New**, one per schema task |
| `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs` | Mapper, date, correction context, echo read back from the store, and failure handling by job kind |
| `src/Noof.Ledger.Host/Workers/CategorizationReply.cs` | **Deleted.** Replaced by `RecordEcho` |
| `src/Noof.Ledger.Telegram/TelegramUpdateRouter.cs` | Dispatches messages, edited messages and button presses |
| `src/Noof.Ledger.Telegram/RecordActionButtons.cs` | **New.** Maps each action to its label and callback data in one place |
| `src/Noof.Ledger.Telegram/RecordActionHandler.cs` | **New.** Отменить, Вернуть and Изменить |
| `src/Noof.Ledger.Telegram/CorrectionHandler.cs` | **New.** Replies and edits of the original message |
| `src/Noof.Ledger.Telegram/TelegramChatNotifier.cs` | Keyboards, `ForceReply`, callback answers, and the two refusals it is safe to swallow |
| `src/Noof.Ledger.Telegram/TelegramPollingService.cs` | Asks Telegram for edited messages and callback queries too, and its notices are in Russian |
| `src/Noof.Ledger.Web/Components/Pages/Home.razor` | Shows the purchase day, with the time only when it is known |

---

## Review Focus

Five situations the spec implies that no acceptance line names. They are the ones most likely to hurt a person using this. Each has a pinning test in the task named.

1. **A correction or an edit arrives while an earlier job for the same record is still pending or backing off.** A correction must never be applied first and then silently overwritten by the older categorisation. `ClaimAsync` must not claim a later job for a transaction while an earlier one is `Pending` or `Claimed`. *Task 6, `EfJobQueueTests.A_later_job_for_the_same_transaction_waits_for_the_earlier_one`.*
2. **A correction fails** (the model is down past the attempt cap, or it answers something that does not parse). The record the person already saw confirmed must stay exactly as it was. It must not flip to `Failed` and it must not disappear from the dashboard. The echo must say the correction did not apply. *Task 6, `CategorizationWorkerTests.A_failed_correction_leaves_the_record_as_it_was`.*
3. **Отменить is tapped twice, or Telegram redelivers the same button press.** The result is one cancellation, one revision and no poison update. Telegram refuses an edit that changes nothing ("message is not modified") and that refusal must not surface as an error. *Task 8, `EfRecordEditorTests.Cancelling_twice_changes_nothing_the_second_time` and `TelegramChatNotifierTests.EditAsync_ignores_telegrams_message_is_not_modified_refusal`.*
4. **A button is pressed on an echo while the host was down, and handled hours later.** `AnswerCallbackQuery` then fails with "query is too old". The cancellation must still happen, because answering only removes the button's spinner. *Task 8, `TelegramChatNotifierTests.AnswerActionAsync_ignores_a_refusal_because_the_answer_is_cosmetic`.*
5. **A message sent at 23:50 local time is processed after midnight.** Both the "today" the model is given and the default `occurred_on` are the *send* day, never the processing day. A UTC date that differs from the local date must not leak in either. *Task 2, `EfCaptureStoreTests.Occurred_on_is_the_local_day_the_message_was_sent_in_the_capture_time_zone`. Task 3, `CategorizationWorkerTests.The_model_is_told_the_day_the_message_was_sent_not_the_day_the_job_runs`.*

---

## The contract

This is the end state of every cross-assembly signature this phase touches. Each task's **Interfaces** block says which part of it that task introduces. If code disagrees with this section, the code is wrong.

```csharp
// Noof.Ledger.Domain
public enum TransactionStatus { Captured = 0, Completed = 1, Failed = 2, Cancelled = 3 }
public enum JobKind { Categorize = 0, Correct = 1, Reinterpret = 2 }
// CurrencyCode.Supported : IReadOnlyList<CurrencyCode> = [Eur, Rsd, Usd, Rub, Kzt]
// Transaction: string RawText { get; set; }  DateOnly OccurredOn (required, get; set;)  int? PromptMessageId { get; set; }
// CategorizationJob: JobKind Kind { get; init; }  string? Instruction { get; init; }  int? SourceMessageId { get; init; }

// Noof.Ledger.Application.Categorization
public sealed record CategorizationRequest(
    string RawText, DateOnly Today, IReadOnlyList<CategoryOption> Categories,
    IReadOnlyList<MerchantOption> MerchantHints, IReadOnlyList<MerchantOption> AllMerchants,
    CorrectionRequest? Correction = null);
public sealed record CorrectionRequest(DateOnly CurrentOccurredOn, IReadOnlyList<RecordedLine> CurrentLines, string Instruction);
public sealed record ProposedLineItem(string Description, string Amount, string? CurrencyCode, string CategorySlug, Guid? KnownMerchantId, string? MerchantName);
public sealed record CategorizationProposal(IReadOnlyList<ProposedLineItem> Items, string? OccurredOn = null);
public sealed record ResolvedLineItem(string Description, Money Amount, string CategorySlug, Guid? KnownMerchantId, string? MerchantName);
public sealed record MappedProposal(IReadOnlyList<ResolvedLineItem> Items, DateOnly? OccurredOn);
public sealed record RecordedLine(string Description, Money Amount, string? CategorySlug, string? CategoryName, string? MerchantName);
public sealed record CategorizationSubject(
    Guid TransactionId, string RawText, long TelegramChatId, int? BotMessageId, string WalletName,
    TransactionStatus Status, DateOnly SentOn, DateOnly OccurredOn, IReadOnlyList<RecordedLine> Lines);
public sealed record CategorizationOutcome(
    IReadOnlyList<CategorizedLineItem> Items, DateOnly OccurredOn, JobKind Kind = JobKind.Categorize, string? Instruction = null);
public static class ProposalMapper { public static bool TryMap(CategorizationProposal, IReadOnlyCollection<string> offeredSlugs, IReadOnlyCollection<Guid> offeredMerchantIds, string defaultCurrency, out MappedProposal mapped, out string failure); }
public interface ICategorizationStore {
    Task<CategorizationSubject?> GetSubjectAsync(Guid transactionId, CancellationToken cancellationToken);
    Task ApplyAsync(Guid transactionId, CategorizationOutcome outcome, CancellationToken cancellationToken);
    Task MarkFailedAsync(Guid transactionId, CancellationToken cancellationToken); }

// Noof.Ledger.Application.Chat
public enum RecordAction { Cancel, Edit, Restore }
public sealed record EchoMessage(string Text, IReadOnlyList<RecordAction> Actions);
public static class RecordEcho { Acknowledgement; Correcting; EditPrompt; Failure; Compose(CategorizationSubject); ComposeCorrectionFailure(CategorizationSubject); }
public interface IChatNotifier {
    Task<int> SendAsync(long chatId, string text, CancellationToken cancellationToken);
    Task EditAsync(long chatId, int messageId, EchoMessage message, CancellationToken cancellationToken);
    Task<int> AskAsync(long chatId, int replyToMessageId, string prompt, CancellationToken cancellationToken);
    Task AnswerActionAsync(string actionId, CancellationToken cancellationToken); }

// Noof.Ledger.Application.Editing
public sealed record EchoTarget(Guid TransactionId, int? EchoMessageId);
public interface IRecordEditor {
    Task<EchoTarget?> FindByBotMessageAsync(long chatId, int messageId, CancellationToken cancellationToken);
    Task<EchoTarget?> FindByUserMessageAsync(long chatId, int messageId, CancellationToken cancellationToken);
    Task<bool> CancelAsync(Guid transactionId, CancellationToken cancellationToken);
    Task<bool> RestoreAsync(Guid transactionId, CancellationToken cancellationToken);
    Task<bool> RequestCorrectionAsync(Guid transactionId, string instruction, int sourceMessageId, CancellationToken cancellationToken);
    Task<bool> ReplaceRawTextAsync(Guid transactionId, string rawText, CancellationToken cancellationToken);
    Task AttachPromptAsync(Guid transactionId, int promptMessageId, CancellationToken cancellationToken); }

// Noof.Ledger.Application.Reporting
public sealed record RecentTransaction(Guid Id, DateOnly OccurredOn, TimeOnly? LocalTime, string RawText,
    TransactionStatus Status, string WalletName, IReadOnlyList<RecentLineItem> Items);
```

---

### Task 1: The model interprets amounts. C# maps them and stops verifying them.

**Files:**
- Modify: `src/Noof.Ledger.Domain/CurrencyCode.cs`, `src/Noof.Ledger.Domain/Transaction.cs` (comment only)
- Delete: `src/Noof.Ledger.Domain/QuotedAmount.cs`, `tests/Noof.Ledger.Domain.Tests/QuotedAmountTests.cs`
- Modify: `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs`
- Delete: `src/Noof.Ledger.Application/Categorization/ProposalVerification.cs`, `tests/Noof.Ledger.Persistence.Tests/ProposalVerificationTests.cs`
- Create: `src/Noof.Ledger.Application/Categorization/ProposalMapper.cs`, `tests/Noof.Ledger.Persistence.Tests/ProposalMapperTests.cs`
- Modify: `src/Noof.Ledger.Ai/CategorizationSchema.cs`, `src/Noof.Ledger.Ai/CategorizationPrompt.cs`, `src/Noof.Ledger.Ai/AnthropicCategorizer.cs`
- Modify: `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`, `src/Noof.Ledger.Host/Workers/CategorizationWorkerOptions.cs` (comment only)
- Test: `tests/Noof.Ledger.Domain.Tests/CurrencyCodeTests.cs`, `tests/Noof.Ledger.Ai.Tests/{CategorizationSchemaTests,CategorizationPromptTests,AnthropicCategorizerTests,AnthropicResponses,LiveModelTests}.cs`, `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`, `tests/Noof.Ledger.Architecture.Tests/{AiBoundaryTests,PublicSurfaceTests}.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `CurrencyCode.Supported`. `ProposedLineItem(Description, string Amount, CurrencyCode?, CategorySlug, KnownMerchantId?, string? MerchantName)`. `ResolvedLineItem(..., string? MerchantName)`. `MappedProposal(IReadOnlyList<ResolvedLineItem> Items)` (Task 3 adds `OccurredOn`). `ProposalMapper.TryMap(proposal, offeredSlugs, offeredMerchantIds, defaultCurrency, out MappedProposal mapped, out string failure)`. JSON fields `amount` and `merchant_name` replace `amount_quote` and `merchant_quote`.

- [ ] **Step 1: Write the failing tests**

`tests/Noof.Ledger.Domain.Tests/CurrencyCodeTests.cs`: add

```csharp
    [Fact]
    public void Supported_lists_the_five_codes_the_ledger_accepts()
    {
        CurrencyCode.Supported.Select(code => code.Value).Should().Equal("EUR", "RSD", "USD", "RUB", "KZT");
    }
```

Delete `tests/Noof.Ledger.Domain.Tests/QuotedAmountTests.cs` and `tests/Noof.Ledger.Persistence.Tests/ProposalVerificationTests.cs`. Create `tests/Noof.Ledger.Persistence.Tests/ProposalMapperTests.cs`:

```csharp
using System.Globalization;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

public class ProposalMapperTests
{
    static readonly string[] Slugs = ["groceries", "food-drink"];
    static readonly Guid KnownMerchant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    static ProposedLineItem Line(
        string amount, string? currency = "RSD", string slug = "groceries",
        Guid? knownMerchantId = null, string? merchantName = null, string description = "кофе") =>
        new(description, amount, currency, slug, knownMerchantId, merchantName);

    static bool Map(CategorizationProposal proposal, out MappedProposal mapped, out string failure) =>
        ProposalMapper.TryMap(proposal, Slugs, [KnownMerchant], "RSD", out mapped, out failure);

    [Fact]
    public void An_amount_the_message_never_wrote_in_digits_is_taken_as_the_model_gives_it()
    {
        // "купил штуку евро" has no digits at all. Accepting the model's "1000" is the whole point of D1.
        Map(new([Line("1000", "EUR")]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Amount.Should().Be(new Money(1000m, CurrencyCode.Eur));
    }

    [Theory]
    [InlineData("45.30", 45.30)]
    [InlineData("0.5", 0.5)]
    [InlineData(" 250 ", 250)]
    public void A_plain_decimal_parses(string amount, double expected)
    {
        Map(new([Line(amount)]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Amount.Amount.Should().Be((decimal)expected);
    }

    [Fact]
    public void Parsing_ignores_the_machine_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            Map(new([Line("45.30")]), out var mapped, out _).Should().BeTrue();
            mapped.Items.Single().Amount.Amount.Should().Be(45.30m);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Theory]
    [InlineData("двести")]
    [InlineData("1 500")]
    [InlineData("45,30")]
    [InlineData("-5")]
    [InlineData("")]
    public void An_amount_that_is_not_a_plain_decimal_fails_the_whole_proposal(string amount)
    {
        Map(new([Line("100"), Line(amount)]), out var mapped, out var failure).Should().BeFalse();

        mapped.Items.Should().BeEmpty("a partial answer is never recorded");
        failure.Should().Contain("Item 2");
    }

    [Fact]
    public void No_currency_means_the_configured_default()
    {
        Map(new([Line("250", currency: null)]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Amount.Currency.Should().Be(CurrencyCode.Rsd);
    }

    [Fact]
    public void A_lower_case_currency_maps_to_the_supported_code()
    {
        Map(new([Line("2.50", currency: "eur")]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Amount.Currency.Should().Be(CurrencyCode.Eur);
    }

    [Fact]
    public void A_currency_the_ledger_does_not_support_fails()
    {
        Map(new([Line("10", currency: "GBP")]), out _, out var failure).Should().BeFalse();

        failure.Should().Contain("GBP");
    }

    [Fact]
    public void A_slug_that_was_not_offered_fails_and_a_differently_cased_one_maps_to_the_offered_spelling()
    {
        Map(new([Line("10", slug: "rent")]), out _, out _).Should().BeFalse();

        Map(new([Line("10", slug: "Groceries")]), out var mapped, out _).Should().BeTrue();
        mapped.Items.Single().CategorySlug.Should().Be("groceries");
    }

    [Fact]
    public void A_known_merchant_id_that_was_not_offered_fails()
    {
        Map(new([Line("10", knownMerchantId: Guid.NewGuid())]), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_merchant_name_is_taken_as_given_whether_or_not_the_message_spells_it_that_way()
    {
        Map(new([Line("300", merchantName: "Starbucks")]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().MerchantName.Should().Be("Starbucks");
    }

    [Fact]
    public void A_blank_merchant_name_is_no_merchant()
    {
        Map(new([Line("300", merchantName: "  ")]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().MerchantName.Should().BeNull();
    }

    [Fact]
    public void Over_long_text_is_cut_to_the_column_widths_rather_than_failing_the_job()
    {
        var proposal = new CategorizationProposal([Line("1", merchantName: new string('m', 300), description: new string('d', 600))]);

        Map(proposal, out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Description.Should().HaveLength(512);
        mapped.Items.Single().MerchantName.Should().HaveLength(256);
    }

    [Fact]
    public void Zero_items_is_a_real_answer()
    {
        Map(new([]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Should().BeEmpty();
    }
}
```

`tests/Noof.Ledger.Ai.Tests/CategorizationSchemaTests.cs`: in `Record_spending_with_no_hints_matches_the_pinned_shape`, replace the `expected` literal with

```csharp
        const string expected = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["items"],
          "properties": {
            "items": {
              "type": "array",
              "minItems": 0,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["description", "amount", "category_slug"],
                "properties": {
                  "description": { "type": "string", "description": "What was bought, as short plain text in the language of the message." },
                  "amount": { "type": "string", "description": "The amount the person meant, as a plain decimal number: digits, optionally a dot and decimals, no spaces, no grouping, no currency symbol - for example \"1000\" or \"45.30\". Interpret words, slang and speech: \"штуку\" is 1000, \"полтос\" is 50, \"двести пятьдесят\" is 250." },
                  "currency": { "type": "string", "enum": ["EUR", "RSD", "USD", "RUB", "KZT"] },
                  "category_slug": { "type": "string", "enum": ["groceries", "food-drink"] },
                  "merchant_name": { "type": "string", "description": "The merchant's name as the person wrote it. Only when a merchant is named and it is not one of the known merchants." }
                }
              }
            }
          }
        }
        """;
```

and rename the `"merchant_quote"` in `With_hints_known_merchant_id_lands_between_category_slug_and_merchant_quote` to `"merchant_name"` (rename the method to `..._and_merchant_name` as well).

`tests/Noof.Ledger.Ai.Tests/CategorizationPromptTests.cs`: replace `System_prompt_instructs_verbatim_amount_quoting` with

```csharp
    [Fact]
    public void System_prompt_asks_for_the_meant_amount_as_a_plain_decimal()
    {
        CategorizationPrompt.System.Should().Contain("the number the person meant");
        CategorizationPrompt.System.Should().Contain("\"штуку\"");
        CategorizationPrompt.System.Should().NotContain("character for character",
            "quote-and-verify is gone (D1): an instruction to copy verbatim makes the model refuse exactly the amounts this phase exists to accept");
    }
```

`tests/Noof.Ledger.Ai.Tests/AnthropicResponses.cs`: in `RecordSpendingJsonAnswer`, change `\"amount_quote\":\"3.50\"` to `\"amount\":\"3.50\"`, and add

```csharp
    public const string RecordSpendingFromWordsAnswer = """
        {"id":"msg_05","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"text","text":"{\"items\":[{\"description\":\"продукты\",\"amount\":\"1000\",\"currency\":\"EUR\",\"category_slug\":\"food-drink\",\"merchant_name\":\"Lidl\"}]}"}],
         "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
        """;
```

`tests/Noof.Ledger.Ai.Tests/AnthropicCategorizerTests.cs`: in `Returns_the_proposal_when_the_first_turn_answers_with_JSON_text` change `proposal.Items[0].AmountQuote.Should().Be("3.50");` to `proposal.Items[0].Amount.Should().Be("3.50");`, and add

```csharp
    [Fact]
    public async Task Maps_an_amount_read_from_words_and_a_merchant_name()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingFromWordsAnswer);
        var request = new CategorizationRequest("купил штуку евро в Lidl", Categories, NoMerchantHints, NoMerchantHints);

        var proposal = await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var item = proposal.Items.Should().ContainSingle().Subject;
        item.Amount.Should().Be("1000");
        item.CurrencyCode.Should().Be("EUR");
        item.MerchantName.Should().Be("Lidl");
    }
```

`tests/Noof.Ledger.Architecture.Tests/AiBoundaryTests.cs`: replace `Only_QuotedAmount_constructs_a_Money_inside_the_categorization_pipeline` with

```csharp
    [Fact]
    public void Only_ProposalMapper_constructs_a_Money_inside_the_categorization_pipeline()
    {
        var root = RepoRoot.Find().FullName;
        var mapper = Path.Combine(root, "src", "Noof.Ledger.Application", "Categorization", "ProposalMapper.cs");
        string[] scannedRoots =
        [
            Path.Combine(root, "src", "Noof.Ledger.Ai"),
            Path.Combine(root, "src", "Noof.Ledger.Application", "Categorization"),
            Path.Combine(root, "src", "Noof.Ledger.Host", "Workers"),
        ];

        var offenders = scannedRoots
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(file => !string.Equals(file, mapper, StringComparison.OrdinalIgnoreCase))
            .Where(file => File.ReadAllText(file).Contains("new Money(", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        // One door, not a check: the model's reading of an amount becomes a Money in exactly one place,
        // so a wrong figure has exactly one place to be traced to. The verbatim check that used to live
        // here was removed on purpose (decision D1).
        offenders.Should().BeEmpty("a Money built from a model answer must come from ProposalMapper");
        File.ReadAllText(mapper).Should().Contain("new Money(",
            "the one permitted site must exist, or an empty offender list proves nothing");
    }
```

`tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`: remove `"QuotedAmount"` from the Domain list. In the Application list replace `"ProposalVerification"` with `"ProposalMapper", "MappedProposal"`.

`tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`: rename `A_verification_failure_is_terminal_and_never_retried` to `An_answer_that_does_not_parse_is_terminal_and_never_retried`. Replace its comment and `.Returns(OneGroceryLine(quote: "999"));` with

```csharp
        // "двести" is not a decimal - ProposalMapper.TryMap refuses it, which is real production
        // logic, not a stub.
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneGroceryLine(amount: "двести"));
```

and change the helper's signature to `static CategorizationProposal OneGroceryLine(string amount = "250", string currency = "RSD") => new([new ProposedLineItem("Bread", amount, currency, "groceries", null, null)]);`.

`tests/Noof.Ledger.Ai.Tests/LiveModelTests.cs` compiles against the new contract. It is **not** run.
- `A_single_coffee_purchase_...`: rename to `A_single_coffee_purchase_produces_one_line_item_with_the_amount_and_currency` and assert `item.Amount.Should().Be("250");`.
- The grocery test: rename `..._with_the_amount_quote_pinned_not_the_category` to `..._with_the_amount_pinned_not_the_category` and assert `item.Amount.Should().Be("3400");`.
- `A_message_with_two_amounts_produces_two_line_items_whose_quotes_occur_verbatim`: rename to `A_message_with_two_amounts_produces_two_line_items`, and replace its `foreach` with `proposal.Items.Select(item => item.Amount).Should().BeEquivalentTo(["500", "250"]);`.
- `Every_amount_quote_the_model_returns_passes_verification_against_the_raw_text`: rename to `Every_answer_the_model_returns_maps` and replace its body after the call with
  ```csharp
        var mapped = ProposalMapper.TryMap(proposal, OfferedSlugs, offeredMerchantIds: [], defaultCurrency: "RSD", out var result, out var failure);

        mapped.Should().BeTrue(failure);
        result.Items.Should().NotBeEmpty();
  ```
  and delete the comment that mentions `ProposalVerification`.
- `A_named_merchant_returns_a_merchant_quote_that_occurs_in_the_message`: rename to `A_named_merchant_is_returned_as_a_merchant_name`, and assert `item.MerchantName.Should().ContainEquivalentOf("starbucks");`.
- Add:
  ```csharp
    [Fact]
    public async Task An_amount_said_in_words_is_read_as_a_number()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request("купил штуку евро на продукты"), TestContext.Current.CancellationToken);

        var item = proposal.Items.Should().ContainSingle().Subject;
        item.Amount.Should().Be("1000");
        item.CurrencyCode.Should().Be("EUR");
    }
  ```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL. The compiler reports errors naming `CurrencyCode.Supported`, `ProposalMapper`, `MappedProposal`, `ProposedLineItem.Amount` and `MerchantName`.

- [ ] **Step 3: Implement**

`src/Noof.Ledger.Domain/CurrencyCode.cs`: after `public static readonly CurrencyCode Kzt = new("KZT");` add

```csharp

    // Declared after the five codes on purpose: static fields initialise in textual order, so a list
    // declared above them would hold five default codes with a null Value.
    public static readonly IReadOnlyList<CurrencyCode> Supported = [Eur, Rsd, Usd, Rub, Kzt];
```

Delete `src/Noof.Ledger.Domain/QuotedAmount.cs`. In `src/Noof.Ledger.Domain/Transaction.cs`, delete the two comment lines above `RawText` (they describe quote-and-verify).

`src/Noof.Ledger.Application/Categorization/CategorizationContract.cs`: replace the `ProposedLineItem`, `CategorizationProposal` and `ResolvedLineItem` declarations and their comments with

```csharp
// One line in the model's own reading of the message. Amount is the number the person meant ("1000"
// for "штуку"), written as a plain invariant decimal. ProposalMapper parses it and nothing compares it
// with the message (decision D1). CurrencyCode is null when the message states none.
public sealed record ProposedLineItem(
    string Description,
    string Amount,
    string? CurrencyCode,
    string CategorySlug,
    Guid? KnownMerchantId,
    string? MerchantName);

public sealed record CategorizationProposal(IReadOnlyList<ProposedLineItem> Items);

public sealed record ResolvedLineItem(
    string Description,
    Money Amount,
    string CategorySlug,
    Guid? KnownMerchantId,
    string? MerchantName);

public sealed record MappedProposal(IReadOnlyList<ResolvedLineItem> Items);
```

Delete `ProposalVerification.cs`. Create `src/Noof.Ledger.Application/Categorization/ProposalMapper.cs`:

```csharp
using System.Globalization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Categorization;

// Parses, never judges. Whether a figure is plausible, or appears in the message at all, is for the
// person to see in the echo and correct there (D1, docs/OPEN-QUESTIONS.md P2-1). Do not add a sanity
// bound or a verbatim check here.
public static class ProposalMapper
{
    const int MaxDescriptionLength = 512;
    const int MaxMerchantNameLength = 256;

    public static bool TryMap(
        CategorizationProposal proposal,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        string defaultCurrency,
        out MappedProposal mapped,
        out string failure)
    {
        mapped = new MappedProposal([]);
        var items = new List<ResolvedLineItem>(proposal.Items.Count);

        for (var index = 0; index < proposal.Items.Count; index++)
        {
            var item = proposal.Items[index];
            if (MapItem(item, offeredSlugs, offeredMerchantIds, defaultCurrency, out var reason) is not { } resolved)
            {
                failure = $"Item {index + 1} (\"{item.Description}\"): {reason}";
                return false;
            }

            items.Add(resolved);
        }

        mapped = new MappedProposal(items);
        failure = string.Empty;
        return true;
    }

    static ResolvedLineItem? MapItem(
        ProposedLineItem item,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        string defaultCurrency,
        out string reason)
    {
        if (!decimal.TryParse(item.Amount.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
        {
            reason = $"amount \"{item.Amount}\" is not a plain decimal number.";
            return null;
        }

        var code = string.IsNullOrWhiteSpace(item.CurrencyCode) ? defaultCurrency : item.CurrencyCode.Trim();
        var currency = CurrencyCode.Supported.FirstOrDefault(
            supported => string.Equals(supported.Value, code, StringComparison.OrdinalIgnoreCase));
        if (currency.Value is null)
        {
            reason = $"currency \"{code}\" is not one this ledger supports.";
            return null;
        }

        var slug = offeredSlugs.FirstOrDefault(
            offered => string.Equals(offered, item.CategorySlug, StringComparison.OrdinalIgnoreCase));
        if (slug is null)
        {
            reason = $"category slug \"{item.CategorySlug}\" was not offered.";
            return null;
        }

        if (item.KnownMerchantId is { } merchantId && !offeredMerchantIds.Contains(merchantId))
        {
            reason = $"known merchant id {merchantId} was not offered.";
            return null;
        }

        reason = string.Empty;
        return new ResolvedLineItem(
            Truncate(item.Description, MaxDescriptionLength),
            new Money(amount, currency),
            slug,
            item.KnownMerchantId,
            string.IsNullOrWhiteSpace(item.MerchantName) ? null : Truncate(item.MerchantName.Trim(), MaxMerchantNameLength));
    }

    static string Truncate(string text, int maxLength) => text.Length > maxLength ? text[..maxLength] : text;
}
```

`src/Noof.Ledger.Ai/CategorizationSchema.cs`: delete the `CurrencyCodes` array. Add

```csharp
    const string AmountDescription =
        "The amount the person meant, as a plain decimal number: digits, optionally a dot and decimals, no spaces, "
        + "no grouping, no currency symbol - for example \"1000\" or \"45.30\". Interpret words, slang and speech: "
        + "\"штуку\" is 1000, \"полтос\" is 50, \"двести пятьдесят\" is 250.";
```

Replace the `amount_quote` entry with `new("amount", new JsonObject { ["type"] = "string", ["description"] = AmountDescription }),`. Build the currency enum from `CurrencyCode.Supported.Select(code => (JsonNode)code.Value).ToArray()`. Replace the `merchant_quote` entry with

```csharp
        properties.Add(new("merchant_name", new JsonObject
        {
            ["type"] = "string",
            ["description"] = "The merchant's name as the person wrote it. Only when a merchant is named and it is not one of the known merchants.",
        }));
```

and set `["required"] = new JsonArray("description", "amount", "category_slug")`.

`src/Noof.Ledger.Ai/CategorizationPrompt.cs`: replace the `System` constant with

```csharp
    public const string System = """
        You record spending from a personal expense message so it can be reviewed later. You read
        one message at a time and answer with the spending it describes, nothing more. The person
        sees your answer echoed back in their chat and can cancel or correct it, so give your best
        reading of what they meant rather than leaving out an amount that is not written in digits.

        Each category you are offered has a slug, an English name, a Russian name, and may have a
        parent category. Use the names to understand what each slug means — everyday food and
        drink, transport, household bills, and so on — so a line item lands under the slug whose
        meaning actually matches it. You do not choose the set of allowed slugs; your answer only
        accepts one of the slugs you were given, so pick by meaning and let the schema reject
        anything else.

        For every amount, answer with the number the person meant, written as a plain decimal:
        digits, optionally a dot and decimals, no spaces, no grouping, no currency symbol — "1000",
        "45.30". People write amounts in words, slang and speech-recognised text: "штуку" or
        "штука" is 1000, "пятихатка" is 500, "полтос" is 50, "двести пятьдесят" is 250, "1,5к" is
        1500, "1 500" is 1500. Never add lines up into a total the message did not ask for. If a
        line has no amount at all, do not produce that line.

        Report a currency only when the message actually states one — "евро", "eur", "€", "рсд",
        "динар", "рублей". If the message names no currency at all, leave currency out of your
        answer rather than choosing one — a missing currency is filled in later from a configured
        default, so guessing here would only replace a correct default with a wrong guess.

        A message may name zero, one or several purchases. Produce one line item per purchase that
        has an amount. If a merchant is named and it matches one of the known merchants you were
        given, set known_merchant_id to that merchant's id. If a merchant is named but matches no
        known merchant, put its name in merchant_name as the person wrote it. If no merchant is
        named, leave both out. If a merchant is named and you are unsure whether it is already
        known, you may call list_merchants to check the full list before answering.

        <examples>
        <example>
        Message: "кофе 250 рсд"
        Answer with one item: description "кофе", amount "250", currency "RSD", category_slug the
        one whose meaning is everyday food and drink, no merchant.
        </example>
        <example>
        Message: "купил штуку евро на продукты"
        Answer with one item: description "продукты", amount "1000", currency "EUR", category_slug
        the one whose meaning is groceries, no merchant. "Штуку" is how people say one thousand;
        the message has no digits and does not need any.
        </example>
        <example>
        Message: "такси двести пятьдесят"
        Answer with one item: description "такси", amount "250", category_slug the one whose
        meaning is transport, no merchant. The message names no currency, so currency is left out
        of the answer entirely — do not guess RSD, EUR or anything else.
        </example>
        <example>
        Message: "Lidl 45,30 eur продукты, потом кофе 2.50 eur"
        Answer with two items. First: description "продукты", amount "45.30", currency "EUR",
        category_slug the one whose meaning is groceries, merchant_name "Lidl" (or
        known_merchant_id instead, if Lidl is already a known merchant). Second: description
        "кофе", amount "2.50", currency "EUR", category_slug the one whose meaning is everyday food
        and drink, no merchant. The comma in "45,30" is a decimal separator; the answer always
        uses a dot.
        </example>
        <example>
        Message: "заняла у Маши 5000 рсд"
        Answer with no items at all. The message states an amount but describes a loan received,
        not a purchase — there is nothing here to record as spending.
        </example>
        </examples>
        """;
```

`src/Noof.Ledger.Ai/AnthropicCategorizer.cs`: replace `ProposedLineItemDto` with

```csharp
    sealed record ProposedLineItemDto(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("amount")] string Amount,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("category_slug")] string CategorySlug,
        [property: JsonPropertyName("known_merchant_id")] string? KnownMerchantId,
        [property: JsonPropertyName("merchant_name")] string? MerchantName)
    {
        public ProposedLineItem ToProposedLineItem() => new(
            Description, Amount, Currency, CategorySlug,
            string.IsNullOrEmpty(KnownMerchantId) ? null : Guid.Parse(KnownMerchantId), MerchantName);
    }
```

`src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`: replace the `ProposalVerification.TryResolve(...)` block with

```csharp
            if (!ProposalMapper.TryMap(
                proposal, offeredSlugs, offeredMerchantIds, options.DefaultCurrency, out var mapped, out var failure))
            {
                await FailTerminallyAsync(jobQueue, store, notifier, job, subject, failure, cancellationToken);
                return;
            }
```

In the loop below it, replace `resolvedItems` with `mapped.Items` (three occurrences: the two list capacities and the `foreach`). Replace `item.MerchantText is { Length: > 0 } merchantText` with `item.MerchantName is { Length: > 0 } merchantText`. In `CategorizationWorkerOptions.cs`, change `(see ProposalVerification.TryResolve)` to `(see ProposalMapper.TryMap)`.

- [ ] **Step 4: Run the tests and watch them pass**

Run each command:
- `dotnet test --project tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter ProposalMapperTests`
- `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`

Expected: all PASS. The live suite reports skipped.

- [ ] **Step 5: See the architecture rule go red**

Temporarily add `static readonly Money Probe = new Money(0m, CurrencyCode.Eur);` to `CategorizationWorkerOptions.cs`. Re-run the Architecture tests and confirm `Only_ProposalMapper_constructs_a_Money_inside_the_categorization_pipeline` FAILS naming `src\Noof.Ledger.Host\Workers\CategorizationWorkerOptions.cs`. Remove the line and re-run to PASS.

- [ ] **Step 6: Commit**

```bash
git add -A src tests
git commit -m "$(cat <<'EOF'
feat(capture): the model interprets amounts; ProposalMapper parses and no longer verifies

Quote-and-verify is removed (D1). The schema asks for the meant amount as a
plain decimal and a merchant name; C# parses both and checks nothing against
the message.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 2: `occurred_on`, the column and the record read back

**Files:**
- Modify: `src/Noof.Ledger.Domain/Transaction.cs`
- Create: `src/Noof.Ledger.Persistence/ZonedClock.cs`, `tests/Noof.Ledger.Persistence.Tests/ZonedClockTests.cs`
- Modify: `src/Noof.Ledger.Persistence/Configurations/TransactionConfiguration.cs`, `src/Noof.Ledger.Persistence/Capture/EfCaptureStore.cs`, `src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs`
- Modify: `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs`, `src/Noof.Ledger.Application/Categorization/ICategorizationStore.cs`
- Create: `src/Noof.Ledger.Persistence/Migrations/<timestamp>_AddOccurredOn.cs` (+ Designer, snapshot), via `dotnet ef`
- Modify: `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs` (one line)
- Test: `tests/Noof.Ledger.Persistence.Tests/{EfCaptureStoreTests,EfCategorizationStoreTests,MigrationContractTests,EfJobQueueTests,EfSpendingReadModelTests,LineItemMoneyMappingTests}.cs`, `tests/Noof.Ledger.Persistence.Tests/schema.expected.sql`, `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`, `tests/Noof.Ledger.E2E.Tests/DashboardTests.cs`, `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`

**Interfaces:**
- Consumes: Task 1's contract.
- Produces: `Transaction.OccurredOn` (`required DateOnly`, `get; set;`). `ZonedClock.LocalDateTime(DateTimeOffset, string)` and `ZonedClock.LocalDate(DateTimeOffset, string)` (internal to Persistence, visible to Persistence.Tests, Host.Tests and E2E.Tests). `RecordedLine`. `CategorizationSubject` in its final nine-field shape (see The contract). `CategorizationOutcome(IReadOnlyList<CategorizedLineItem> Items, DateOnly OccurredOn)`. `ICategorizationStore.ApplyAsync(Guid, CategorizationOutcome, CancellationToken)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Noof.Ledger.Persistence.Tests/ZonedClockTests.cs`:

```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Persistence.Tests;

public class ZonedClockTests
{
    [Theory]
    [InlineData("2026-09-21T21:50:00Z", "2026-09-21")]
    [InlineData("2026-09-21T22:30:00Z", "2026-09-22")]
    public void The_local_day_in_Belgrade_is_not_the_UTC_day(string instant, string expected)
    {
        ZonedClock.LocalDate(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture), "Europe/Belgrade")
            .Should().Be(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }
}
```

`EfCaptureStoreTests.cs`: add `using System.Globalization;` and

```csharp
    [Theory]
    [InlineData("2026-09-21T21:50:00Z", "2026-09-21")]
    [InlineData("2026-09-21T22:30:00Z", "2026-09-22")]
    public async Task Occurred_on_is_the_local_day_the_message_was_sent_in_the_capture_time_zone(string sentAt, string expectedDay)
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        // Processed the next morning: the offline queue must not move a purchase to the day it was read (D2).
        var store = new EfCaptureStore(db, new FakeTimeProvider(new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero)));

        await store.CaptureAsync(
            NewMessage(sentAt: DateTimeOffset.Parse(sentAt, CultureInfo.InvariantCulture)),
            "Europe/Belgrade",
            TestContext.Current.CancellationToken);

        var transaction = await db.Transactions.SingleAsync(TestContext.Current.CancellationToken);
        transaction.OccurredOn.Should().Be(DateOnly.Parse(expectedDay, CultureInfo.InvariantCulture));
    }
```

`MigrationContractTests.cs`: add `using Microsoft.EntityFrameworkCore.Infrastructure;` and `using Microsoft.EntityFrameworkCore.Migrations;` and

```csharp
    [Fact]
    public async Task Existing_transactions_get_the_local_day_of_their_own_time_zone()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.GetService<IMigrator>().MigrateAsync("20260921071450_AddAppSecret", TestContext.Current.CancellationToken);

        // 22:30 UTC on 31 August is already 1 September in Belgrade and still 31 August in UTC. This is the
        // row noof_ledger's own history will go through when the operator next starts the host.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO public.transactions
                (id, wallet_id, raw_text, status, time_zone_id, occurred_at, telegram_chat_id, telegram_message_id, created_at)
            VALUES
                ('aaaaaaaa-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000001', 'кофе 250', 1,
                 'Europe/Belgrade', '2026-08-31T22:30:00Z', 1, 1, '2026-08-31T22:30:00Z'),
                ('aaaaaaaa-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000001', 'кофе 250', 1,
                 'Etc/UTC', '2026-08-31T22:30:00Z', 1, 2, '2026-08-31T22:30:00Z');
            """,
            TestContext.Current.CancellationToken);

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var days = await db.Transactions.AsNoTracking()
            .OrderBy(t => t.TelegramMessageId)
            .Select(t => t.OccurredOn)
            .ToListAsync(TestContext.Current.CancellationToken);
        days.Should().Equal(new DateOnly(2026, 9, 1), new DateOnly(2026, 8, 31));
    }
```

`EfCategorizationStoreTests.cs`: change the helper to

```csharp
    static Transaction NewTransaction(
        Guid walletId, long chatId = 1, int messageId = 1,
        DateTimeOffset? occurredAt = null, DateOnly? occurredOn = null) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = walletId,
        RawText = "coffee 3.50, milk 1.20",
        Status = TransactionStatus.Captured,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        OccurredOn = occurredOn ?? DateOnly.FromDateTime(DateTime.UtcNow),
        TelegramChatId = chatId,
        TelegramMessageId = messageId,
        BotMessageId = 42,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    static CategorizationOutcome Outcome(params CategorizedLineItem[] items) => new(items, new DateOnly(2026, 9, 21));
```

Replace every `store.ApplyAsync(transactionId, items, ` (and `storeA.ApplyAsync(transactionId, itemsA, `, `storeB.ApplyAsync(transactionId, itemsB, `) with `...ApplyAsync(transactionId, Outcome(items), ` (respectively `Outcome(itemsA)`, `Outcome(itemsB)`). Replace `GetSubjectAsync_returns_the_subject_in_one_round_trip` with

```csharp
    [Fact]
    public async Task GetSubjectAsync_returns_the_record_as_stored_with_its_lines()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet("Main Wallet");
        // 22:30 UTC is 00:30 the next day in Belgrade: SentOn is the Belgrade day, OccurredOn is what is stored.
        var transaction = NewTransaction(wallet.Id, chatId: 777, messageId: 5,
            occurredAt: new DateTimeOffset(2026, 9, 21, 22, 30, 0, TimeSpan.Zero), occurredOn: new DateOnly(2026, 9, 20));
        var category = NewCategory();
        var merchant = NewMerchant();
        db.AddRange(wallet, transaction, category, merchant);
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            Description = "Coffee",
            Amount = new Money(3.50m, CurrencyCode.Eur),
            CategoryId = category.Id,
            CategorizedBy = CategorizationAuthority.Model,
            MerchantId = merchant.Id,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);

        var subject = await store.GetSubjectAsync(transaction.Id, TestContext.Current.CancellationToken);

        subject.Should().NotBeNull();
        subject!.RawText.Should().Be("coffee 3.50, milk 1.20");
        subject.TelegramChatId.Should().Be(777);
        subject.BotMessageId.Should().Be(42);
        subject.WalletName.Should().Be("Main Wallet");
        subject.Status.Should().Be(TransactionStatus.Captured);
        subject.SentOn.Should().Be(new DateOnly(2026, 9, 22));
        subject.OccurredOn.Should().Be(new DateOnly(2026, 9, 20));
        subject.Lines.Should().Equal(
            new RecordedLine("Coffee", new Money(3.50m, CurrencyCode.Eur), category.Slug, "Тестовая категория", "Test merchant"));
    }

    [Fact]
    public async Task ApplyAsync_stores_the_day_the_outcome_names()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var (transactionId, categoryId, _) = await SeedAsync(db, TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);
        var items = new[] { new CategorizedLineItem("Coffee", new Money(3.50m, CurrencyCode.Eur), categoryId, null) };

        await store.ApplyAsync(transactionId, new CategorizationOutcome(items, new DateOnly(2026, 9, 20)), TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken))
            .OccurredOn.Should().Be(new DateOnly(2026, 9, 20));
    }
```

Add the required `OccurredOn` to every other `Transaction` initialiser in the test projects:
- `EfJobQueueTests.NewTransaction`: after `OccurredAt = now,` add `OccurredOn = DateOnly.FromDateTime(now.UtcDateTime),`
- `EfSpendingReadModelTests.NewTransaction`: add a trailing parameter `DateOnly? occurredOn = null` and the line `OccurredOn = occurredOn ?? DateOnly.FromDateTime(occurredAt.UtcDateTime),`
- `LineItemMoneyMappingTests` (line 20): after `OccurredAt = DateTimeOffset.UtcNow,` add `OccurredOn = DateOnly.FromDateTime(DateTime.UtcNow),`
- `tests/Noof.Ledger.E2E.Tests/DashboardTests.cs`, at both sites (lines 41 and 127): after `OccurredAt = DateTimeOffset.UtcNow,` add `OccurredOn = ZonedClock.LocalDate(DateTimeOffset.UtcNow, "Europe/Belgrade"),`. `using Noof.Ledger.Persistence;` is already present, and Persistence already grants `InternalsVisibleTo` to the E2E project.

`tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`:
- Add `static readonly DateOnly SentOn = new(2026, 9, 21);` and replace the `Subject` helper with
  ```csharp
    static CategorizationSubject Subject(
        int? botMessageId = 42, string rawText = "Bread 250 RSD", DateOnly? occurredOn = null,
        TransactionStatus status = TransactionStatus.Captured, IReadOnlyList<RecordedLine>? lines = null) =>
        new(TransactionId, rawText, 111L, botMessageId, "Cash", status, SentOn, occurredOn ?? SentOn, lines ?? []);
  ```
- Replace every `Arg.Any<IReadOnlyList<CategorizedLineItem>>()` with `Arg.Any<CategorizationOutcome>()`.
- Replace every `Arg.Is<IReadOnlyList<CategorizedLineItem>>(items => ` with `Arg.Is<CategorizationOutcome>(outcome => `, and inside those lambdas replace `items.` with `outcome.Items.` (lines 246, 427, 461, 574).

`PublicSurfaceTests.cs`: add `"RecordedLine", "CategorizationOutcome"` to the Application list.

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL. The errors name `Transaction.OccurredOn`, `ZonedClock`, `RecordedLine`, `CategorizationOutcome` and the new `CategorizationSubject` arity.

- [ ] **Step 3: Implement the model, the clock, capture and the store**

`src/Noof.Ledger.Domain/Transaction.cs`: make `RawText` `{ get; set; }` (Task 9 edits it). After `OccurredAt` add

```csharp

    // The local day the purchase happened. OccurredAt stays the instant the message was sent; when the two
    // name the same local day the time of day is known, otherwise only the day is (decision D3).
    public required DateOnly OccurredOn { get; set; }
```

Create `src/Noof.Ledger.Persistence/ZonedClock.cs`:

```csharp
namespace Noof.Ledger.Persistence;

internal static class ZonedClock
{
    public static DateTime LocalDateTime(DateTimeOffset instant, string timeZoneId) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).DateTime;

    public static DateOnly LocalDate(DateTimeOffset instant, string timeZoneId) =>
        DateOnly.FromDateTime(LocalDateTime(instant, timeZoneId));
}
```

`TransactionConfiguration.cs`: after the `OccurredAt` line add `builder.Property(t => t.OccurredOn).HasColumnName("occurred_on");`

`EfCaptureStore.cs`: after `OccurredAt = message.SentAt,` add `OccurredOn = ZonedClock.LocalDate(message.SentAt, timeZoneId),`

`CategorizationContract.cs`: replace `CategorizationSubject` and its comment with

```csharp
// Everything the worker and the echo need about one transaction: the message, where its echo lives, and the
// record exactly as it is stored now. SentOn is the local day the message was sent - "today" for the model (D2).
public sealed record CategorizationSubject(
    Guid TransactionId,
    string RawText,
    long TelegramChatId,
    int? BotMessageId,
    string WalletName,
    TransactionStatus Status,
    DateOnly SentOn,
    DateOnly OccurredOn,
    IReadOnlyList<RecordedLine> Lines);

// CategoryName is the Russian name: the bot speaks Russian.
public sealed record RecordedLine(
    string Description,
    Money Amount,
    string? CategorySlug,
    string? CategoryName,
    string? MerchantName);

public sealed record CategorizationOutcome(IReadOnlyList<CategorizedLineItem> Items, DateOnly OccurredOn);
```

`ICategorizationStore.cs`: change `ApplyAsync` to `Task ApplyAsync(Guid transactionId, CategorizationOutcome outcome, CancellationToken cancellationToken);` and extend its comment's first sentence to "...and sets status = Completed and occurred_on, in ONE database transaction."

`EfCategorizationStore.cs`: replace `GetSubjectAsync` with

```csharp
    public async Task<CategorizationSubject?> GetSubjectAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var header = await (
            from t in db.Transactions.AsNoTracking()
            join w in db.Wallets.AsNoTracking() on t.WalletId equals w.Id
            where t.Id == transactionId
            select new
            {
                t.Id, t.RawText, t.TelegramChatId, t.BotMessageId, WalletName = w.Name,
                t.Status, t.OccurredAt, t.TimeZoneId, t.OccurredOn,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (header is null)
            return null;

        // line_items has no ordinal column, so the order is made deterministic rather than left to the heap.
        var lines = await (
            from li in db.LineItems.AsNoTracking()
            where li.TransactionId == transactionId
            join c in db.Categories.AsNoTracking() on li.CategoryId equals c.Id into categoryJoin
            from c in categoryJoin.DefaultIfEmpty()
            join m in db.Merchants.AsNoTracking() on li.MerchantId equals m.Id into merchantJoin
            from m in merchantJoin.DefaultIfEmpty()
            orderby li.Description
            select new RecordedLine(
                li.Description,
                li.Amount,
                c == null ? null : c.Slug,
                c == null ? null : c.NameRu,
                m == null ? null : m.DisplayName))
            .ToListAsync(cancellationToken);

        return new CategorizationSubject(
            header.Id, header.RawText, header.TelegramChatId, header.BotMessageId, header.WalletName,
            header.Status, ZonedClock.LocalDate(header.OccurredAt, header.TimeZoneId), header.OccurredOn, lines);
    }
```

In `ApplyAsync`, change the signature to `(Guid transactionId, CategorizationOutcome outcome, CancellationToken cancellationToken)`, loop over `outcome.Items`, and after `transaction.Status = TransactionStatus.Completed;` add `transaction.OccurredOn = outcome.OccurredOn;`.

`CategorizationWorker.cs`: replace `await store.ApplyAsync(job.TransactionId, categorizedItems, cancellationToken);` with `await store.ApplyAsync(job.TransactionId, new CategorizationOutcome(categorizedItems, sub.OccurredOn), cancellationToken);`. Task 3 changes the day. This keeps behaviour unchanged for now.

- [ ] **Step 4: Scaffold the migration and hand-write its body**

Run: `dotnet ef migrations add AddOccurredOn --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence`

Convert the new `src/Noof.Ledger.Persistence/Migrations/<timestamp>_AddOccurredOn.cs` to a file-scoped namespace, and replace its `Up` and `Down` with

```csharp
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateOnly>(
            name: "occurred_on",
            schema: "public",
            table: "transactions",
            type: "date",
            nullable: true);

        // Each existing row gets the local day its OWN stored zone puts it on - the rule capture applies to
        // new rows from now on. Nullable first, backfilled, then NOT NULL: no row ever carries an invented
        // default date.
        migrationBuilder.Sql("UPDATE public.transactions SET occurred_on = (occurred_at AT TIME ZONE time_zone_id)::date;");

        migrationBuilder.AlterColumn<DateOnly>(
            name: "occurred_on",
            schema: "public",
            table: "transactions",
            type: "date",
            nullable: false,
            oldClrType: typeof(DateOnly),
            oldType: "date",
            oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "occurred_on",
            schema: "public",
            table: "transactions");
    }
```

Regenerate the schema snapshot. This does not open a connection:
`dotnet ef dbcontext script --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --output tests/Noof.Ledger.Persistence.Tests/schema.expected.sql`
Then `git diff tests/Noof.Ledger.Persistence.Tests/schema.expected.sql` must show only `occurred_on date NOT NULL,` added to `transactions`.

- [ ] **Step 5: Run the tests and watch them pass**

- `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`

Expected: PASS. That includes `The_model_has_no_pending_changes`, `The_generated_schema_matches_the_committed_snapshot` and the backfill test.

- [ ] **Step 6: Update the test template, then run the E2E suite**

Run the PowerShell block from "Updating the test template". Then:
Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A src tests
git commit -m "$(cat <<'EOF'
feat(persistence): occurred_on - the local purchase day, stamped at capture and backfilled

Adds transactions.occurred_on (D3), backfilled per row from its own time zone.
The store now reads a transaction back as the full stored record with its lines.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 3: The model reads dates

**Files:**
- Modify: `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs`, `ProposalMapper.cs`
- Modify: `src/Noof.Ledger.Ai/CategorizationSchema.cs`, `CategorizationPrompt.cs`, `AnthropicCategorizer.cs`
- Modify: `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`
- Test: `tests/Noof.Ledger.Persistence.Tests/ProposalMapperTests.cs`, `tests/Noof.Ledger.Ai.Tests/{CategorizationSchemaTests,CategorizationPromptTests,AnthropicCategorizerTests,AnthropicResponses,LiveModelTests}.cs`, `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`

**Interfaces:**
- Consumes: `CategorizationSubject.SentOn` and `CategorizationOutcome` (Task 2).
- Produces: `CategorizationRequest(RawText, DateOnly Today, Categories, MerchantHints, AllMerchants)`. `CategorizationProposal(Items, string? OccurredOn = null)`. `MappedProposal(Items, DateOnly? OccurredOn)`. `CategorizationPrompt.BuildUserTurn(CategorizationRequest request)`. The JSON root property `occurred_on`.

- [ ] **Step 1: Write the failing tests**

`ProposalMapperTests.cs`: add

```csharp
    [Fact]
    public void A_named_day_maps_to_that_date()
    {
        Map(new([Line("100")], "2026-09-20"), out var mapped, out _).Should().BeTrue();

        mapped.OccurredOn.Should().Be(new DateOnly(2026, 9, 20));
    }

    [Fact]
    public void No_day_maps_to_no_date()
    {
        Map(new([Line("100")]), out var mapped, out _).Should().BeTrue();

        mapped.OccurredOn.Should().BeNull();
    }

    [Theory]
    [InlineData("вчера")]
    [InlineData("20.09.2026")]
    [InlineData("2026-02-30")]
    public void A_day_that_is_not_an_ISO_date_fails(string occurredOn)
    {
        Map(new([Line("100")], occurredOn), out _, out var failure).Should().BeFalse();

        failure.Should().Contain("occurred_on");
    }
```

`CategorizationSchemaTests.cs`: in the pinned `expected` literal, add after the `"items": {...}` property (inside the root `properties`):

```json
            ,
            "occurred_on": { "type": "string", "description": "The day the purchase happened, as an ISO date (YYYY-MM-DD), worked out from today's date given with the message. Leave it out when the message names no day." }
```

The root `required` stays `["items"]`. Add

```csharp
    [Fact]
    public void Occurred_on_is_offered_at_the_root_but_not_required()
    {
        var schema = CategorizationSchema.BuildRecordSpending(Categories, NoHints);

        schema.GetProperty("properties").TryGetProperty("occurred_on", out _).Should().BeTrue();
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("items");
    }
```

`CategorizationPromptTests.cs`: replace `Build_user_turn_includes_the_raw_text_the_categories_and_the_hints` with

```csharp
    [Fact]
    public void Build_user_turn_gives_today_with_its_weekday_then_the_message_categories_and_hints()
    {
        IReadOnlyList<CategoryOption> categories = [new CategoryOption("groceries", "Groceries", "Продукты", null)];
        var request = new CategorizationRequest("купил вчера штуку евро", new DateOnly(2026, 9, 22), categories, [], []);

        var turn = CategorizationPrompt.BuildUserTurn(request);

        turn.Should().StartWith("Today: 2026-09-22 (Tuesday)");
        turn.Should().Contain("купил вчера штуку евро");
        turn.Should().Contain("groceries: Groceries / Продукты");
        turn.Should().Contain("No known merchants");
    }

    [Fact]
    public void System_prompt_explains_relative_days_are_counted_from_today()
    {
        CategorizationPrompt.System.Should().Contain("occurred_on");
        CategorizationPrompt.System.Should().Contain("counted from today");
    }
```

`AnthropicResponses.cs`: add

```csharp
    public const string RecordSpendingWithDateAnswer = """
        {"id":"msg_06","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"text","text":"{\"items\":[{\"description\":\"продукты\",\"amount\":\"1000\",\"currency\":\"EUR\",\"category_slug\":\"food-drink\"}],\"occurred_on\":\"2026-09-21\"}"}],
         "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
        """;
```

`AnthropicCategorizerTests.cs`: add `static readonly DateOnly Today = new(2026, 9, 22);` and the helper

```csharp
    static CategorizationRequest Request(
        string rawText, IReadOnlyList<MerchantOption>? hints = null, IReadOnlyList<MerchantOption>? all = null) =>
        new(rawText, Today, Categories, hints ?? NoMerchantHints, all ?? NoMerchantHints);
```

Replace every `new CategorizationRequest("X", Categories, NoMerchantHints, NoMerchantHints)` with `Request("X")`, and `new CategorizationRequest("Lidl 3.50 EUR", Categories, hints, all)` with `Request("Lidl 3.50 EUR", hints, all)`. Add

```csharp
    [Fact]
    public async Task Tells_the_model_today_and_maps_the_day_it_answers()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingWithDateAnswer);

        var proposal = await categorizer.ProposeAsync(Request("купил вчера штуку евро"), TestContext.Current.CancellationToken);

        handler.Requests[0].Body.Should().Contain("Today: 2026-09-22 (Tuesday)");
        proposal.OccurredOn.Should().Be("2026-09-21");
    }
```

`LiveModelTests.cs`: change `Request` to `new(rawText, DateOnly.FromDateTime(DateTime.Today), OfferedCategories, [], [])` and add (it is not run):

```csharp
    [Fact]
    public async Task Yesterday_is_answered_as_the_day_before_today()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var today = new DateOnly(2026, 9, 22);
        var proposal = await CreateCategorizer(apiKey).ProposeAsync(
            new CategorizationRequest("купил вчера штуку евро на продукты", today, OfferedCategories, [], []),
            TestContext.Current.CancellationToken);

        proposal.OccurredOn.Should().Be("2026-09-21");
        proposal.Items.Should().ContainSingle().Which.Amount.Should().Be("1000");
    }
```

`CategorizationWorkerTests.cs`: add the helper

```csharp
    static IJobQueue QueueWith(CategorizationJob job)
    {
        var jobQueue = Substitute.For<IJobQueue>();
        jobQueue.ClaimAsync(WorkerId, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(job);
        jobQueue.SucceedAsync(JobId, WorkerId, Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        jobQueue.FailAsync(JobId, WorkerId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(JobCompletionOutcome.Applied);
        jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JobCompletionOutcome.Applied);
        return jobQueue;
    }
```

and the tests

```csharp
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
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL. The errors name `CategorizationRequest`'s `Today`, `CategorizationProposal.OccurredOn`, `MappedProposal.OccurredOn` and the `BuildUserTurn(CategorizationRequest)` overload.

- [ ] **Step 3: Implement**

`CategorizationContract.cs`:

```csharp
// Today is the local day the message was SENT, never the day the job runs: a message that waited in the
// offline queue overnight must not move a day (D2).
public sealed record CategorizationRequest(
    string RawText,
    DateOnly Today,
    IReadOnlyList<CategoryOption> Categories,
    IReadOnlyList<MerchantOption> MerchantHints,
    IReadOnlyList<MerchantOption> AllMerchants);

public sealed record CategorizationProposal(IReadOnlyList<ProposedLineItem> Items, string? OccurredOn = null);

public sealed record MappedProposal(IReadOnlyList<ResolvedLineItem> Items, DateOnly? OccurredOn);
```

`ProposalMapper.TryMap`: change the initial `mapped = new MappedProposal([]);` to `mapped = new MappedProposal([], null);`. Before the loop add

```csharp
        DateOnly? occurredOn = null;
        if (!string.IsNullOrWhiteSpace(proposal.OccurredOn))
        {
            if (!DateOnly.TryParseExact(
                proposal.OccurredOn.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                failure = $"occurred_on \"{proposal.OccurredOn}\" is not an ISO date (YYYY-MM-DD).";
                return false;
            }

            occurredOn = day;
        }
```

and end with `mapped = new MappedProposal(items, occurredOn);`.

`CategorizationSchema.cs`: add

```csharp
    const string OccurredOnDescription =
        "The day the purchase happened, as an ISO date (YYYY-MM-DD), worked out from today's date given with the "
        + "message. Leave it out when the message names no day.";
```

and in the root `properties` add after `["items"] = ...`:

```csharp
                ["occurred_on"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = OccurredOnDescription,
                },
```

`CategorizationPrompt.cs`: add `using System.Globalization;`. In `System`, insert this paragraph after the currency paragraph:

```
        The message comes with today's date and weekday in the person's time zone. When the
        message says which day the purchase happened — "вчера", "позавчера", "в пятницу", "15-го"
        — answer with occurred_on: that day as YYYY-MM-DD, counted from today. A weekday means the
        most recent such day before today. When the message names no day, leave occurred_on out:
        it will be recorded as today.
```

Replace the second example with

```
        <example>
        Message: "купил вчера штуку евро на продукты"
        Answer with one item: description "продукты", amount "1000", currency "EUR", category_slug
        the one whose meaning is groceries, no merchant, and occurred_on the day before today.
        "Штуку" is how people say one thousand; the message has no digits and does not need any.
        </example>
```

Replace `BuildUserTurn` with

```csharp
    public static string BuildUserTurn(CategorizationRequest request) =>
        $"""
        Today: {request.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({request.Today.DayOfWeek})

        Message:
        {request.RawText}

        Categories:
        {RenderCategories(request.Categories)}

        Known merchants:
        {RenderMerchantHints(request.MerchantHints)}
        """;
```

`AnthropicCategorizer.cs`: change the user-turn line to `var userTurn = CategorizationPrompt.BuildUserTurn(request);`, the payload record to

```csharp
    sealed record RecordSpendingPayload(
        [property: JsonPropertyName("items")] IReadOnlyList<ProposedLineItemDto> Items,
        [property: JsonPropertyName("occurred_on")] string? OccurredOn);
```

and `ToProposal`'s return to `return new CategorizationProposal([.. payload.Items.Select(i => i.ToProposedLineItem())], payload.OccurredOn);`.

`CategorizationWorker.cs`: build the request as

```csharp
            var request = new CategorizationRequest(
                sub.RawText,
                sub.SentOn,
                [.. categories.Select(category => new CategoryOption(category.Slug, category.NameEn, category.NameRu, category.ParentSlug))],
                hints,
                allMerchants);
```

and apply with `new CategorizationOutcome(categorizedItems, mapped.OccurredOn ?? sub.SentOn)`.

- [ ] **Step 4: Run the tests and watch them pass**

- `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter ProposalMapperTests`
- `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`

Expected: PASS. The live tests stay skipped.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "$(cat <<'EOF'
feat(capture): the model dates the purchase from the message's own send day

The user turn gives today's local date and weekday; occurred_on comes back as
an ISO date or not at all, and a missing day is the send day (D2).

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 4: The dashboard reads the purchase day and leaves out cancelled records

**Files:**
- Modify: `src/Noof.Ledger.Domain/TransactionStatus.cs`
- Create: `tests/Noof.Ledger.Domain.Tests/TransactionStatusTests.cs`
- Modify: `src/Noof.Ledger.Application/Reporting/ISpendingReadModel.cs`, `src/Noof.Ledger.Persistence/Reporting/EfSpendingReadModel.cs`, `src/Noof.Ledger.Web/Components/Pages/Home.razor`
- Test: `tests/Noof.Ledger.Persistence.Tests/EfSpendingReadModelTests.cs`

**Interfaces:**
- Consumes: `Transaction.OccurredOn` and `ZonedClock` (Task 2).
- Produces: `TransactionStatus.Cancelled = 3`. `RecentTransaction(Guid Id, DateOnly OccurredOn, TimeOnly? LocalTime, string RawText, TransactionStatus Status, string WalletName, IReadOnlyList<RecentLineItem> Items)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Noof.Ledger.Domain.Tests/TransactionStatusTests.cs`:

```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class TransactionStatusTests
{
    [Fact]
    public void Stored_values_never_move()
    {
        ((int)TransactionStatus.Captured).Should().Be(0);
        ((int)TransactionStatus.Completed).Should().Be(1);
        ((int)TransactionStatus.Failed).Should().Be(2);
        ((int)TransactionStatus.Cancelled).Should().Be(3);
    }
}
```

`EfSpendingReadModelTests.cs`: in the first test replace `newestRow.TimeZoneId.Should().Be("Europe/Belgrade");` with `newestRow.OccurredOn.Should().Be(new DateOnly(2026, 9, 15));`. Replace `ThisMonthAsync_buckets_each_row_by_its_own_stored_time_zone_not_a_naive_UTC_bucket` with the three tests below. The per-row time zone is now decided once, at capture and in the backfill, both pinned in Task 2.

```csharp
    [Fact]
    public async Task ThisMonthAsync_buckets_by_the_day_the_purchase_happened_not_when_the_message_was_sent()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        var category = NewCategory("test-occurred-on", "Occurred On");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // "вчера" typed on 1 September is an August purchase; a message sent late on 31 August
        // and dated 1 September belongs to September.
        var backdated = NewTransaction(wallet.Id, new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero), "Europe/Belgrade",
            TransactionStatus.Completed, occurredOn: new DateOnly(2026, 8, 31));
        var forwardDated = NewTransaction(wallet.Id, new DateTimeOffset(2026, 8, 31, 23, 30, 0, TimeSpan.Zero), "Europe/Belgrade",
            TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 1));
        db.Transactions.AddRange(backdated, forwardDated);
        db.LineItems.AddRange(
            NewLineItem(backdated.Id, "august", new Money(20m, CurrencyCode.Eur), category.Id, null),
            NewLineItem(forwardDated.Id, "september", new Money(10m, CurrencyCode.Eur), category.Id, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db,
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero)), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.FirstDay.Should().Be(new DateOnly(2026, 9, 1));
        summary.Totals.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new MonthTotal("Occurred On", CurrencyCode.Eur, 10m));
    }

    [Fact]
    public async Task Cancelled_records_are_left_out_of_recent_and_of_the_month()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        var category = NewCategory("test-cancelled", "Cancelled Test");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var kept = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        var cancelled = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Cancelled);
        db.Transactions.AddRange(kept, cancelled);
        db.LineItems.AddRange(
            NewLineItem(kept.Id, "kept", new Money(5m, CurrencyCode.Eur), category.Id, null),
            NewLineItem(cancelled.Id, "cancelled", new Money(1000m, CurrencyCode.Eur), category.Id, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        (await readModel.RecentAsync(10, TestContext.Current.CancellationToken)).Select(r => r.Id).Should().Equal(kept.Id);
        (await readModel.ThisMonthAsync(TestContext.Current.CancellationToken)).Totals.Should().ContainSingle()
            .Which.Amount.Should().Be(5m);
    }

    [Fact]
    public async Task RecentAsync_gives_a_time_of_day_only_when_the_purchase_day_is_the_send_day()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // 10:15 UTC is 12:15 in Belgrade in September.
        var sentAt = new DateTimeOffset(2026, 9, 21, 10, 15, 0, TimeSpan.Zero);
        var sameDay = NewTransaction(wallet.Id, sentAt, "Europe/Belgrade", TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 21));
        var backdated = NewTransaction(wallet.Id, sentAt, "Europe/Belgrade", TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 20));
        db.Transactions.AddRange(sameDay, backdated);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(sentAt), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(10, TestContext.Current.CancellationToken);

        recent.Select(r => r.Id).Should().Equal(sameDay.Id, backdated.Id);
        recent[0].LocalTime.Should().Be(new TimeOnly(12, 15));
        recent[1].LocalTime.Should().BeNull("a backdated purchase gets no invented time of day (D3)");
    }
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL on `TransactionStatus.Cancelled`, `RecentTransaction.OccurredOn` and `LocalTime`.

- [ ] **Step 3: Implement**

`TransactionStatus.cs`: add `Cancelled = 3,`.

`ISpendingReadModel.cs`:

```csharp
// LocalTime is null when the purchase was dated to a day other than the one the message was sent on:
// only the day is known, and the dashboard does not invent a time for it (D3).
public sealed record RecentTransaction(
    Guid Id,
    DateOnly OccurredOn,
    TimeOnly? LocalTime,
    string RawText,
    TransactionStatus Status,
    string WalletName,
    IReadOnlyList<RecentLineItem> Items);
```

Change the `ThisMonthAsync` comment to: `// "This month" is decided by the read model, not the page, so there is exactly one definition of it. Rows are bucketed by occurred_on, the local day stamped per row at capture.`

`EfSpendingReadModel.cs`: replace the `headers` query with

```csharp
        var headers = await db.Transactions
            .Where(t => t.Status != TransactionStatus.Cancelled)
            .Join(db.Wallets, t => t.WalletId, w => w.Id, (t, w) => new
            {
                t.Id,
                t.OccurredOn,
                t.OccurredAt,
                t.TimeZoneId,
                t.RawText,
                t.Status,
                WalletName = w.Name,
            })
            .OrderByDescending(h => h.OccurredOn)
            .ThenByDescending(h => h.OccurredAt)
            .ThenByDescending(h => h.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
```

Build each result as `new RecentTransaction(h.Id, h.OccurredOn, LocalTimeOf(h.OccurredAt, h.TimeZoneId, h.OccurredOn), h.RawText, h.Status, h.WalletName, [...])` and add

```csharp
    static TimeOnly? LocalTimeOf(DateTimeOffset occurredAt, string timeZoneId, DateOnly occurredOn)
    {
        var local = ZonedClock.LocalDateTime(occurredAt, timeZoneId);
        return DateOnly.FromDateTime(local) == occurredOn ? TimeOnly.FromDateTime(local) : null;
    }
```

Replace the head of `ThisMonthAsync` and its SQL with

```csharp
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), currentZone).DateTime);
        var firstDay = new DateOnly(today.Year, today.Month, 1);
        var firstDayNextMonth = firstDay.AddMonths(1);
```

```sql
                SELECT COALESCE(c.name_en, @uncategorised) AS category_name,
                       li.currency AS currency,
                       SUM(li.amount) AS total
                FROM line_items li
                JOIN transactions t ON t.id = li.transaction_id
                LEFT JOIN categories c ON c.id = li.category_id
                WHERE t.occurred_on >= @firstDay
                  AND t.occurred_on < @firstDayNextMonth
                  AND t.status <> @cancelled
                GROUP BY COALESCE(c.name_en, @uncategorised), li.currency
```

Add the parameter `command.Parameters.Add(new NpgsqlParameter("cancelled", (int)TransactionStatus.Cancelled));`, and return `new MonthSummary(firstDay, totals)`.

`Home.razor`: replace `@FormatOccurredAt(transaction)` with `@FormatOccurred(transaction)`, and replace the `FormatOccurredAt` method and its comment with

```csharp
    static string FormatOccurred(RecentTransaction transaction)
    {
        var day = transaction.OccurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return transaction.LocalTime is { } time
            ? $"{day} {time.ToString("HH:mm", CultureInfo.InvariantCulture)}"
            : day;
    }
```

- [ ] **Step 4: Run the tests and watch them pass**

- `dotnet test --project tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter EfSpendingReadModelTests`
- `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "$(cat <<'EOF'
feat(dashboard): bucket by the purchase day, hide cancelled records, no invented times

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 5: The echo, in Russian, from the stored rows, with buttons

**Files:**
- Create: `src/Noof.Ledger.Application/Chat/EchoMessage.cs`, `src/Noof.Ledger.Application/Chat/RecordEcho.cs`, `tests/Noof.Ledger.Host.Tests/RecordEchoTests.cs`
- Modify: `src/Noof.Ledger.Application/Chat/IChatNotifier.cs`
- Delete: `src/Noof.Ledger.Host/Workers/CategorizationReply.cs`, `tests/Noof.Ledger.Host.Tests/CategorizationReplyTests.cs`
- Modify: `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`
- Create: `src/Noof.Ledger.Telegram/RecordActionButtons.cs`
- Modify: `src/Noof.Ledger.Telegram/TelegramChatNotifier.cs`, `TelegramUpdateRouter.cs`, `TelegramPollingService.cs`
- Test: `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`, `tests/Noof.Ledger.Telegram.Tests/{TelegramChatNotifierTests,TelegramUpdateRouterTests}.cs`, `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`

**Interfaces:**
- Consumes: `CategorizationSubject` in its final shape (Task 2).
- Produces: `RecordAction { Cancel, Edit, Restore }`. `EchoMessage(string Text, IReadOnlyList<RecordAction> Actions)`. `RecordEcho.Acknowledgement`, `.Correcting`, `.EditPrompt`, `.Failure`, `.Compose(CategorizationSubject)` and `.ComposeCorrectionFailure(CategorizationSubject)`. `IChatNotifier.EditAsync(long chatId, int messageId, EchoMessage message, CancellationToken)`. `RecordActionButtons.ToButton(RecordAction)` (internal, Telegram). Callback data `"cancel"`, `"edit"` and `"restore"`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Noof.Ledger.Host.Tests/RecordEchoTests.cs`:

```csharp
using System.Globalization;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Tests;

public class RecordEchoTests
{
    static readonly DateOnly Sent = new(2026, 9, 22);

    static RecordedLine Coffee => new("кофе", new Money(250m, CurrencyCode.Rsd), "food-drink", "Еда и напитки", null);

    static CategorizationSubject Record(
        TransactionStatus status = TransactionStatus.Completed, DateOnly? occurredOn = null,
        IReadOnlyList<RecordedLine>? lines = null) =>
        new(Guid.NewGuid(), "raw", 111L, 42, "Cash", status, Sent, occurredOn ?? Sent, lines ?? [Coffee]);

    [Fact]
    public void A_recorded_line_is_echoed_with_its_total_and_the_cancel_and_edit_buttons()
    {
        var echo = RecordEcho.Compose(Record());

        echo.Text.Should().Be("Записал — Cash\n• кофе — 250.00 RSD · Еда и напитки\n\nИтого: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_merchant_follows_the_category()
    {
        var line = new RecordedLine("продукты", new Money(1000m, CurrencyCode.Eur), "groceries", "Продукты", "Lidl");

        RecordEcho.Compose(Record(lines: [line])).Text.Should().Contain("• продукты — 1000.00 EUR · Продукты · Lidl");
    }

    [Fact]
    public void Totals_are_per_currency_and_ordered_by_code()
    {
        var lines = new[]
        {
            Coffee,
            new RecordedLine("такси", new Money(1000m, CurrencyCode.Eur), "transport", "Транспорт", null),
            new RecordedLine("хлеб", new Money(100m, CurrencyCode.Rsd), "groceries", "Продукты", null),
        };

        RecordEcho.Compose(Record(lines: lines)).Text.Should().EndWith("Итого: 1000.00 EUR, 350.00 RSD");
    }

    [Fact]
    public void A_record_dated_to_another_day_says_which_day()
    {
        RecordEcho.Compose(Record(occurredOn: new DateOnly(2026, 9, 21))).Text
            .Should().StartWith("Записал — Cash\nДата: 21.09.2026\n• кофе");
    }

    [Fact]
    public void A_record_dated_to_the_send_day_names_no_date()
    {
        RecordEcho.Compose(Record()).Text.Should().NotContain("Дата:");
    }

    [Fact]
    public void A_cancelled_record_offers_only_restore()
    {
        var echo = RecordEcho.Compose(Record(TransactionStatus.Cancelled));

        echo.Text.Should().StartWith("Отменено — Cash\n• кофе");
        echo.Actions.Should().Equal(RecordAction.Restore);
    }

    [Fact]
    public void A_read_that_found_nothing_says_so_and_offers_only_edit()
    {
        var echo = RecordEcho.Compose(Record(lines: []));

        echo.Text.Should().Be("Cash: не нашёл здесь трат — ничего не записал.");
        echo.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void A_failed_record_is_the_failure_echo()
    {
        RecordEcho.Compose(Record(TransactionStatus.Failed, lines: [])).Should().Be(RecordEcho.Failure);
        RecordEcho.Failure.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void A_record_still_being_read_is_the_acknowledgement_without_buttons()
    {
        var echo = RecordEcho.Compose(Record(TransactionStatus.Captured, lines: []));

        echo.Text.Should().Be(RecordEcho.Acknowledgement);
        echo.Actions.Should().BeEmpty();
    }

    [Fact]
    public void A_failed_correction_says_so_above_the_unchanged_record()
    {
        var echo = RecordEcho.ComposeCorrectionFailure(Record());

        echo.Text.Should().Be(
            "Не получилось применить исправление — запись не изменилась.\n\n"
            + "Записал — Cash\n• кофе — 250.00 RSD · Еда и напитки\n\nИтого: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_line_with_no_category_says_so()
    {
        var line = new RecordedLine("штраф", new Money(5m, CurrencyCode.Eur), null, null, null);

        RecordEcho.Compose(Record(lines: [line])).Text.Should().Contain("• штраф — 5.00 EUR · без категории");
    }

    [Fact]
    public void Amounts_render_the_same_under_a_Russian_machine_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            RecordEcho.Compose(Record()).Text.Should().Contain("250.00 RSD");
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
```

Delete `tests/Noof.Ledger.Host.Tests/CategorizationReplyTests.cs`.

`CategorizationWorkerTests.cs`:
- Add `using Noof.Ledger.Application.Chat;` if it is missing, and replace every `EditAsync(` argument `Arg.Any<string>()` with `Arg.Any<EchoMessage>()` (lines 277, 303, 325, 367, 396, 503, 526, 558).
- Add the helper (Groceries is the test's only category, which is why the slug and name are fixed):
  ```csharp
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
                    Lines = [.. outcome.Items.Select(item => new RecordedLine(item.Description, item.Amount, Groceries.Slug, Groceries.NameRu, null))],
                };
            });
        return store;
    }
  ```
- In `A_proposal_with_no_items_completes_the_job_honestly_...`, make the `store` `StoreThatRemembersWhatItApplies(Subject(rawText: "заняла у Маши 5000 рсд"))`, drop its separate `GetSubjectAsync` setup, and replace the final `EditAsync` assertion's `Arg.Is<string>(text => text.Contains("nothing", ...))` with `Arg.Is<EchoMessage>(echo => echo.Text.Contains("ничего не записал"))`.
- In `A_transient_model_failure_is_retried_and_the_captured_transaction_survives_until_it_recovers`, make the `store` `StoreThatRemembersWhatItApplies(Subject())`, drop its separate `GetSubjectAsync` setup, and replace the last assertion's matcher with `Arg.Is<EchoMessage>(echo => echo.Text.Contains("250.00 RSD") && echo.Actions.Contains(RecordAction.Cancel))`.
- Add
  ```csharp
    [Fact]
    public async Task The_echo_is_rendered_from_the_stored_record_not_from_the_proposal()
    {
        // The store answers with 300 whatever was applied: if the echo said 250 it would be quoting the
        // model, and D4 says it must show what the database holds.
        var store = Substitute.For<ICategorizationStore>();
        var stored = Subject(status: TransactionStatus.Completed,
            lines: [new RecordedLine("Bread", new Money(300m, CurrencyCode.Rsd), "groceries", "Продукты", null)]);
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(), stored);
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine("250"));
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

        await notifier.Received(1).EditAsync(111L, 42, RecordEcho.Failure, Arg.Any<CancellationToken>());
    }
  ```

`TelegramChatNotifierTests.cs`: add `using Noof.Ledger.Application.Chat;` and `using Telegram.Bot.Exceptions;` and `using NSubstitute.ExceptionExtensions;`. Replace `EditAsync_edits_the_stored_message_by_chat_and_message_id` with

```csharp
    [Fact]
    public async Task EditAsync_edits_the_message_and_attaches_one_row_of_record_buttons()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>()).Returns(new Message { Id = 555 });
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        await notifier.EditAsync(42L, 555, new EchoMessage("Записал", [RecordAction.Cancel, RecordAction.Edit]),
            TestContext.Current.CancellationToken);

        await client.Received(1).SendRequest(
            Arg.Is<EditMessageTextRequest>(r => r.ChatId.Identifier == 42L && r.MessageId == 555 && r.Text == "Записал"
                && r.ReplyMarkup!.InlineKeyboard.Single().Select(b => b.Text).SequenceEqual(new[] { "Отменить", "Изменить" })
                && r.ReplyMarkup.InlineKeyboard.Single().Select(b => b.CallbackData).SequenceEqual(new[] { "cancel", "edit" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditAsync_with_no_actions_sends_no_keyboard()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>()).Returns(new Message { Id = 555 });
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        await notifier.EditAsync(42L, 555, new EchoMessage("Исправляю…", []), TestContext.Current.CancellationToken);

        await client.Received(1).SendRequest(Arg.Is<EditMessageTextRequest>(r => r.ReplyMarkup == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditAsync_ignores_telegrams_message_is_not_modified_refusal()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException(
                "Bad Request: message is not modified: specified new message content and reply markup are exactly the same as a current content and reply markup of the message", 400));
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        var act = () => notifier.EditAsync(42L, 555, new EchoMessage("Отменено", [RecordAction.Restore]), TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("a second tap produced the text the message already shows");
    }

    [Fact]
    public async Task EditAsync_still_throws_any_other_refusal()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<EditMessageTextRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: message to edit not found", 400));
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        var act = () => notifier.EditAsync(42L, 555, new EchoMessage("x", []), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ApiRequestException>();
    }
```

`TelegramUpdateRouterTests.cs`: replace both `TelegramUpdateRouter.ReceiptAcknowledgement` with `RecordEcho.Acknowledgement`.

`PublicSurfaceTests.cs`: add `"RecordAction", "EchoMessage", "RecordEcho"` to the Application list.

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL on `RecordEcho`, `EchoMessage`, `RecordAction` and the `EditAsync` overload.

- [ ] **Step 3: Implement**

Create `src/Noof.Ledger.Application/Chat/EchoMessage.cs`:

```csharp
namespace Noof.Ledger.Application.Chat;

public enum RecordAction
{
    Cancel,
    Edit,
    Restore,
}

public sealed record EchoMessage(string Text, IReadOnlyList<RecordAction> Actions);
```

Create `src/Noof.Ledger.Application/Chat/RecordEcho.cs`:

```csharp
using System.Globalization;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Chat;

// Everything the bot says about a record. Figures are read from the stored rows, never from a model's
// answer, so the echo shows exactly what the database holds (D4).
public static class RecordEcho
{
    public const string Acknowledgement = "Записываю…";
    public const string Correcting = "Исправляю…";
    public const string EditPrompt = "Что исправить? Ответьте на это сообщение — например: «нет, 1500» или «это было вчера».";

    public static readonly EchoMessage Failure = new(
        "Не смог разобрать это сообщение. Оно сохранено — ответьте на это сообщение и напишите, как его записать.",
        [RecordAction.Edit]);

    public static EchoMessage Compose(CategorizationSubject record) => record switch
    {
        { Status: TransactionStatus.Cancelled } =>
            new($"Отменено — {record.WalletName}\n{Body(record)}".TrimEnd(), [RecordAction.Restore]),
        { Status: TransactionStatus.Failed } => Failure,
        { Status: TransactionStatus.Captured } => new(Acknowledgement, []),
        { Lines.Count: 0 } =>
            new($"{record.WalletName}: не нашёл здесь трат — ничего не записал.", [RecordAction.Edit]),
        _ => new($"Записал — {record.WalletName}\n{Body(record)}", [RecordAction.Cancel, RecordAction.Edit]),
    };

    public static EchoMessage ComposeCorrectionFailure(CategorizationSubject record)
    {
        var current = Compose(record);
        return current with { Text = $"Не получилось применить исправление — запись не изменилась.\n\n{current.Text}" };
    }

    static string Body(CategorizationSubject record)
    {
        List<string> lines = [];

        if (record.OccurredOn != record.SentOn)
            lines.Add($"Дата: {record.OccurredOn.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}");

        lines.AddRange(record.Lines.Select(FormatLine));

        if (record.Lines.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"Итого: {Totals(record.Lines)}");
        }

        return string.Join('\n', lines);
    }

    static string FormatLine(RecordedLine line)
    {
        var text = $"• {line.Description} — {FormatAmount(line.Amount.Amount)} {line.Amount.Currency} · {line.CategoryName ?? "без категории"}";
        return line.MerchantName is { } merchant ? $"{text} · {merchant}" : text;
    }

    static string Totals(IReadOnlyList<RecordedLine> lines) => string.Join(", ", lines
        .GroupBy(line => line.Amount.Currency)
        .OrderBy(group => group.Key.Value, StringComparer.Ordinal)
        .Select(group => $"{FormatAmount(group.Sum(line => line.Amount.Amount))} {group.Key}"));

    static string FormatAmount(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
```

`IChatNotifier.cs`: change `EditAsync` to `Task EditAsync(long chatId, int messageId, EchoMessage message, CancellationToken cancellationToken);`.

Create `src/Noof.Ledger.Telegram/RecordActionButtons.cs`:

```csharp
using Noof.Ledger.Application.Chat;
using Telegram.Bot.Types.ReplyMarkups;

namespace Noof.Ledger.Telegram;

// One table for the label a person sees and the data Telegram sends back, so the two can never drift.
internal static class RecordActionButtons
{
    static readonly (RecordAction Action, string Label, string Data)[] Buttons =
    [
        (RecordAction.Cancel, "Отменить", "cancel"),
        (RecordAction.Edit, "Изменить", "edit"),
        (RecordAction.Restore, "Вернуть", "restore"),
    ];

    public static InlineKeyboardButton ToButton(RecordAction action)
    {
        var button = Buttons.Single(candidate => candidate.Action == action);
        return InlineKeyboardButton.WithCallbackData(button.Label, button.Data);
    }
}
```

`TelegramChatNotifier.cs`:

```csharp
using Noof.Ledger.Application.Chat;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;

namespace Noof.Ledger.Telegram;

internal sealed class TelegramChatNotifier(TelegramClientHandle clientHandle) : IChatNotifier
{
    public async Task<int> SendAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var message = await Client().SendMessage(chatId, text, cancellationToken: cancellationToken);
        return message.Id;
    }

    public async Task EditAsync(long chatId, int messageId, EchoMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await Client().EditMessageText(chatId, messageId, message.Text,
                replyMarkup: Keyboard(message.Actions), cancellationToken: cancellationToken);
        }
        // Telegram refuses an edit that changes nothing: a second tap on a button whose first tap already
        // produced this exact text. The chat already shows what it should.
        catch (ApiRequestException exception) when (exception.Message.Contains("message is not modified", StringComparison.Ordinal))
        {
        }
    }

    static InlineKeyboardMarkup? Keyboard(IReadOnlyList<RecordAction> actions) =>
        actions.Count == 0 ? null : new InlineKeyboardMarkup(actions.Select(RecordActionButtons.ToButton));

    ITelegramBotClient Client() =>
        clientHandle.Current ?? throw new InvalidOperationException(
            "The Telegram client is not ready yet: no bot token has been saved.");
}
```

`TelegramUpdateRouter.cs`: delete the `ReceiptAcknowledgement` constant and send `RecordEcho.Acknowledgement` instead.

`TelegramPollingService.cs`: `PoisonUpdateNotice` becomes `"Не смог обработать это сообщение после нескольких попыток — пропускаю его, чтобы не задерживать следующие."`

Delete `src/Noof.Ledger.Host/Workers/CategorizationReply.cs`. In `CategorizationWorker.cs`:
- Remove `categoryNameBySlug`, `replyLines` and the `replyLines.Add(...)` line.
- Replace the whole `if (sub.BotMessageId is { } messageId) { try { ... } catch ... }` block with `await EchoAsync(store, notifier, job, cancellationToken);`.
- Add
  ```csharp
    async Task EchoAsync(ICategorizationStore store, IChatNotifier notifier, CategorizationJob job, CancellationToken cancellationToken)
    {
        try
        {
            // Read back, not composed from the proposal: the echo shows what the database now holds (D4).
            if (await store.GetSubjectAsync(job.TransactionId, cancellationToken) is not { BotMessageId: { } messageId } record)
                return;

            await notifier.EditAsync(record.TelegramChatId, messageId, RecordEcho.Compose(record), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to echo job {JobId}'s result to Telegram; the categorization itself already succeeded", job.Id);
        }
    }
  ```
- In `NotifyFailureAsync` replace `var text = CategorizationReply.ComposeFailure(sub.WalletName);` and its `EditAsync` with `await notifier.EditAsync(sub.TelegramChatId, messageId, RecordEcho.Failure, cancellationToken);`.

- [ ] **Step 4: Run the tests and watch them pass**

- `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A src tests
git commit -m "$(cat <<'EOF'
feat(telegram): echo the stored record in Russian with Отменить / Изменить

The worker reads the record back after the write and C# renders it (D4).
Telegram's "message is not modified" refusal is swallowed: a double tap is
not an error.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 6: Jobs carry a kind, and the worker runs corrections

**Files:**
- Create: `src/Noof.Ledger.Domain/JobKind.cs`
- Modify: `src/Noof.Ledger.Domain/CategorizationJob.cs`, `src/Noof.Ledger.Domain/Transaction.cs`
- Modify: `src/Noof.Ledger.Persistence/Configurations/CategorizationJobConfiguration.cs`, `TransactionConfiguration.cs`, `src/Noof.Ledger.Persistence/Jobs/EfJobQueue.cs`
- Create: `src/Noof.Ledger.Persistence/Migrations/<timestamp>_AddCorrectionJobs.cs` via `dotnet ef`
- Modify: `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs`
- Modify: `src/Noof.Ledger.Ai/CategorizationPrompt.cs`
- Modify: `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`
- Test: `tests/Noof.Ledger.Persistence.Tests/EfJobQueueTests.cs`, `schema.expected.sql`, `tests/Noof.Ledger.Ai.Tests/CategorizationPromptTests.cs`, `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`, `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`

**Interfaces:**
- Consumes: `CategorizationSubject.Lines`, `OccurredOn` and `SentOn` (Task 2). `RecordEcho.ComposeCorrectionFailure` (Task 5).
- Produces: `JobKind`. `CategorizationJob.Kind`, `.Instruction` and `.SourceMessageId`. `Transaction.PromptMessageId` (used in Task 9). `CorrectionRequest(DateOnly CurrentOccurredOn, IReadOnlyList<RecordedLine> CurrentLines, string Instruction)`. `CategorizationRequest`'s trailing `CorrectionRequest? Correction = null`. `CategorizationOutcome(Items, OccurredOn, JobKind Kind = JobKind.Categorize, string? Instruction = null)`. The index name `IX_categorization_jobs_transaction_id_source_message_id` (used in Task 9).

- [ ] **Step 1: Write the failing tests**

`EfJobQueueTests.cs`: extend the helper, keeping existing calls compiling:

```csharp
    static CategorizationJob NewJob(
        Guid transactionId, DateTimeOffset runAfter, JobStatus status = JobStatus.Pending, int attemptCount = 0,
        JobKind kind = JobKind.Categorize, string? instruction = null, int? sourceMessageId = null,
        DateTimeOffset? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        Status = status,
        AttemptCount = attemptCount,
        RunAfter = runAfter,
        Kind = kind,
        Instruction = instruction,
        SourceMessageId = sourceMessageId,
        CreatedAt = createdAt ?? runAfter,
        UpdatedAt = createdAt ?? runAfter,
    };
```

and add

```csharp
    [Fact]
    public async Task A_later_job_for_the_same_transaction_waits_for_the_earlier_one()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var queue = new EfJobQueue(db, time, maxAttempts: 8);
        var corrected = await SeedTransactionAsync(db, now, TestContext.Current.CancellationToken);
        var other = await SeedTransactionAsync(db, now, TestContext.Current.CancellationToken);

        // The first reading is backing off after a transient failure; the correction behind it is due.
        // Claiming the correction first would apply it, then let the older reading overwrite it.
        db.CategorizationJobs.AddRange(
            NewJob(corrected, runAfter: now.AddMinutes(5), createdAt: now.AddMinutes(-10)),
            NewJob(corrected, runAfter: now.AddMinutes(-1), kind: JobKind.Correct, instruction: "нет, 1500",
                sourceMessageId: 7, createdAt: now.AddMinutes(-5)));
        var otherJob = NewJob(other, runAfter: now.AddMinutes(-1), createdAt: now.AddMinutes(-4));
        db.CategorizationJobs.Add(otherJob);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var claimed = await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        claimed!.Id.Should().Be(otherJob.Id);
        (await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken))
            .Should().BeNull("the correction must wait until the first reading is no longer pending");
    }

    [Fact]
    public async Task A_claimed_correction_carries_its_kind_instruction_and_source_message()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var queue = new EfJobQueue(db, new FakeTimeProvider(now), maxAttempts: 8);
        var transactionId = await SeedTransactionAsync(db, now, TestContext.Current.CancellationToken);
        db.CategorizationJobs.Add(NewJob(transactionId, now.AddMinutes(-1), kind: JobKind.Correct, instruction: "нет, 1500", sourceMessageId: 7));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var claimed = await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        claimed!.Kind.Should().Be(JobKind.Correct);
        claimed.Instruction.Should().Be("нет, 1500");
        claimed.SourceMessageId.Should().Be(7);
    }

    [Fact]
    public async Task A_correction_without_an_instruction_is_refused_by_the_database()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var transactionId = await SeedTransactionAsync(db, now, TestContext.Current.CancellationToken);
        db.CategorizationJobs.Add(NewJob(transactionId, now, kind: JobKind.Correct));

        var act = () => db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
    }
```

`CategorizationPromptTests.cs`: add

```csharp
    [Fact]
    public void A_correction_adds_the_current_record_and_the_instruction_to_the_user_turn()
    {
        IReadOnlyList<CategoryOption> categories = [new CategoryOption("groceries", "Groceries", "Продукты", null)];
        var current = new RecordedLine("продукты", new Money(1000m, CurrencyCode.Eur), "groceries", "Продукты", "Lidl");
        var request = new CategorizationRequest("купил штуку евро", new DateOnly(2026, 9, 22), categories, [], [],
            new CorrectionRequest(new DateOnly(2026, 9, 21), [current], "нет, 1500"));

        var turn = CategorizationPrompt.BuildUserTurn(request);

        turn.Should().Contain("Current record (dated 2026-09-21):");
        turn.Should().Contain("- продукты: 1000 EUR, category groceries, merchant Lidl");
        // Raw string literals carry the source file's line endings, so assert per line, not on "\n".
        turn.Should().Contain("Correction from the person:");
        turn.Should().EndWith("нет, 1500");
    }

    [Fact]
    public void A_first_reading_has_no_correction_section()
    {
        var request = new CategorizationRequest("кофе 250", new DateOnly(2026, 9, 22), [], [], []);

        CategorizationPrompt.BuildUserTurn(request).Should().NotContain("Correction from the person");
    }

    [Fact]
    public void System_prompt_asks_for_the_complete_corrected_record()
    {
        CategorizationPrompt.System.Should().Contain("complete corrected record");
    }
```

and add `using Noof.Ledger.Domain;` to that file.

`CategorizationWorkerTests.cs`: change the `Job` helper to

```csharp
    static CategorizationJob Job(int attemptCount = 1, JobKind kind = JobKind.Categorize, string? instruction = null) => new()
    {
        Id = JobId,
        TransactionId = TransactionId,
        Status = JobStatus.Claimed,
        AttemptCount = attemptCount,
        Kind = kind,
        Instruction = instruction,
        RunAfter = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
```

and add

```csharp
    static readonly RecordedLine StoredBread = new("Bread", new Money(250m, CurrencyCode.Rsd), "groceries", "Продукты", null);

    [Fact]
    public async Task A_correction_hands_the_model_the_current_record_and_the_instruction()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed, occurredOn: new DateOnly(2026, 9, 20), lines: [StoredBread]));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine("1500"));
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
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine("1500"));
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job(kind: JobKind.Correct, instruction: "нет, 1500")), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.OccurredOn == new DateOnly(2026, 9, 20)), Arg.Any<CancellationToken>());
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
            Arg.Is<EchoMessage>(echo => echo.Text.StartsWith("Не получилось применить исправление") && echo.Text.Contains("250.00 RSD")),
            Arg.Any<CancellationToken>());
    }
```

`PublicSurfaceTests.cs`: add `"JobKind"` to the Domain list and `"CorrectionRequest"` to the Application list.

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL on `JobKind`, `CategorizationJob.Kind`, `CorrectionRequest` and `CategorizationOutcome.Kind`.

- [ ] **Step 3: Implement the domain, the contract, the configuration and the queue**

Create `src/Noof.Ledger.Domain/JobKind.cs`:

```csharp
namespace Noof.Ledger.Domain;

public enum JobKind
{
    Categorize = 0,
    Correct = 1,
    Reinterpret = 2,
}
```

`CategorizationJob.cs`: after `TransactionId` add

```csharp
    public JobKind Kind { get; init; }

    // The person's own words; set for a Correct job only, which a check constraint holds.
    public string? Instruction { get; init; }

    // The reply that asked for this correction. A unique index makes Telegram redelivering it a no-op.
    public int? SourceMessageId { get; init; }
```

`Transaction.cs`: after `BotMessageId` add

```csharp

    // The Изменить prompt. Telegram nests reply_to_message one level only, so a reply to the prompt can be
    // traced back to this record only through this column.
    public int? PromptMessageId { get; set; }
```

`TransactionConfiguration.cs`: add `builder.Property(t => t.PromptMessageId).HasColumnName("prompt_message_id");`

`CategorizationJobConfiguration.cs`: replace `builder.ToTable("categorization_jobs");` with

```csharp
        builder.ToTable("categorization_jobs", table => table.HasCheckConstraint(
            "ck_categorization_jobs_correction_has_instruction", "kind <> 1 OR instruction IS NOT NULL"));
```

and add

```csharp
        builder.Property(j => j.Kind).HasColumnName("kind");
        builder.Property(j => j.Instruction).HasColumnName("instruction");
        builder.Property(j => j.SourceMessageId).HasColumnName("source_message_id");

        builder.HasIndex(j => new { j.TransactionId, j.SourceMessageId })
            .IsUnique()
            .HasFilter("source_message_id IS NOT NULL");
```

`CategorizationContract.cs`:

```csharp
public sealed record CategorizationRequest(
    string RawText,
    DateOnly Today,
    IReadOnlyList<CategoryOption> Categories,
    IReadOnlyList<MerchantOption> MerchantHints,
    IReadOnlyList<MerchantOption> AllMerchants,
    CorrectionRequest? Correction = null);

// The record as it stands and what the person asked to change. The model answers with the complete corrected
// record, which replaces the model-authored lines exactly as a first reading does (D6).
public sealed record CorrectionRequest(DateOnly CurrentOccurredOn, IReadOnlyList<RecordedLine> CurrentLines, string Instruction);

public sealed record CategorizationOutcome(
    IReadOnlyList<CategorizedLineItem> Items,
    DateOnly OccurredOn,
    JobKind Kind = JobKind.Categorize,
    string? Instruction = null);
```

`EfJobQueue.ClaimAsync`: replace the SQL with

```csharp
                """
                UPDATE categorization_jobs
                SET status = 1,
                    claimed_at = @now,
                    claimed_by = @workerId,
                    attempt_count = attempt_count + 1,
                    run_after = @leaseExpiry,
                    updated_at = @now
                WHERE id = (
                    SELECT j.id FROM categorization_jobs j
                    WHERE j.status = 0 AND j.run_after <= @now
                      AND NOT EXISTS (
                          SELECT 1 FROM categorization_jobs earlier
                          WHERE earlier.transaction_id = j.transaction_id
                            AND earlier.status IN (0, 1)
                            AND earlier.created_at < j.created_at)
                    ORDER BY j.run_after
                    LIMIT 1
                    FOR UPDATE OF j SKIP LOCKED
                )
                RETURNING id, transaction_id, status, attempt_count, run_after, claimed_at, claimed_by, last_error,
                          created_at, updated_at, kind, instruction, source_message_id
                """,
```

and put a comment above `var claimed`: `// NOT EXISTS keeps one transaction's jobs in the order they were asked for: a correction must never be applied before the reading it corrects, which would then overwrite it.`

- [ ] **Step 4: Scaffold the migration**

Run: `dotnet ef migrations add AddCorrectionJobs --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence`

Convert the new migration `.cs` to a file-scoped namespace. Confirm that it adds `kind` (`integer`, `nullable: false`, `defaultValue: 0`, so existing rows become `Categorize`), `instruction`, `source_message_id`, `prompt_message_id`, the check constraint, and an index **named exactly** `IX_categorization_jobs_transaction_id_source_message_id` with `filter: "source_message_id IS NOT NULL"`. Nothing needs hand-writing.

Regenerate `tests/Noof.Ledger.Persistence.Tests/schema.expected.sql` with the `dotnet ef dbcontext script` command from Task 2 Step 4, and check the diff shows only these additions.

- [ ] **Step 5: Implement the prompt**

`CategorizationPrompt.cs`: insert this paragraph into `System` after the date paragraph:

```
        Sometimes the message has already been recorded and the person wants it changed — "нет,
        1500", "это было позавчера", "это подарок". Then you are also given the current record and
        their correction. Answer with the complete corrected record: every line, not only the one
        that changed, with the correction applied and everything it does not mention kept as it
        is. Days in a correction are counted from today, as above.
```

Replace `BuildUserTurn` with

```csharp
    public static string BuildUserTurn(CategorizationRequest request)
    {
        var turn = $"""
            Today: {request.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({request.Today.DayOfWeek})

            Message:
            {request.RawText}

            Categories:
            {RenderCategories(request.Categories)}

            Known merchants:
            {RenderMerchantHints(request.MerchantHints)}
            """;

        return request.Correction is { } correction ? $"{turn}\n\n{RenderCorrection(correction)}" : turn;
    }

    static string RenderCorrection(CorrectionRequest correction) =>
        $"""
        Current record (dated {correction.CurrentOccurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}):
        {RenderCurrentLines(correction.CurrentLines)}

        Correction from the person:
        {correction.Instruction}
        """;

    static string RenderCurrentLines(IReadOnlyList<RecordedLine> lines) =>
        lines.Count == 0
            ? "- nothing was recorded"
            : string.Join('\n', lines.Select(RenderCurrentLine));

    static string RenderCurrentLine(RecordedLine line)
    {
        var text = $"- {line.Description}: {line.Amount.Amount.ToString("0.####", CultureInfo.InvariantCulture)} "
            + $"{line.Amount.Currency}, category {line.CategorySlug ?? "none"}";
        return line.MerchantName is { } merchant ? $"{text}, merchant {merchant}" : text;
    }
```

- [ ] **Step 6: Implement the worker**

In `CategorizationWorker.ProcessClaimedJobAsync`:
- Build the request with the trailing argument `CorrectionFor(job, sub)`.
- Replace the day with `mapped.OccurredOn ?? DefaultDay(job, sub)`.
- Apply `new CategorizationOutcome(categorizedItems, occurredOn, job.Kind, job.Instruction)`.

Add

```csharp
    static CorrectionRequest? CorrectionFor(CategorizationJob job, CategorizationSubject record) =>
        job is { Kind: JobKind.Correct, Instruction: { } instruction }
            ? new CorrectionRequest(record.OccurredOn, record.Lines, instruction)
            : null;

    // A correction that names no day keeps the record's day. Anything read from scratch starts from the
    // day the message was sent (D2).
    static DateOnly DefaultDay(CategorizationJob job, CategorizationSubject record) =>
        job.Kind == JobKind.Correct ? record.OccurredOn : record.SentOn;
```

Change `NotifyFailureAsync` to take the job and branch on its kind. Its callers in `HandleModelFailureAsync` and `FailTerminallyAsync` pass `job` in place of `job.TransactionId`.

```csharp
    async Task NotifyFailureAsync(
        ICategorizationStore store, IChatNotifier notifier, CategorizationSubject? subject, CategorizationJob job,
        CancellationToken cancellationToken)
    {
        // Only a first reading marks the transaction Failed. A correction or a re-read that fails leaves the
        // record the person already saw confirmed exactly as it was.
        if (job.Kind == JobKind.Categorize)
            await store.MarkFailedAsync(job.TransactionId, cancellationToken);

        if (subject is not { BotMessageId: { } messageId } sub)
            return;

        try
        {
            var echo = job.Kind == JobKind.Categorize
                ? RecordEcho.Failure
                : RecordEcho.ComposeCorrectionFailure(await store.GetSubjectAsync(job.TransactionId, cancellationToken) ?? sub);
            await notifier.EditAsync(sub.TelegramChatId, messageId, echo, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to edit Telegram message {MessageId} to report a failed job for transaction {TransactionId}",
                messageId, job.TransactionId);
        }
    }
```

- [ ] **Step 7: Run the tests and watch them pass**

- `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`

Expected: PASS. See the ordering guard go red: delete the `AND NOT EXISTS (...)` clause and confirm `A_later_job_for_the_same_transaction_waits_for_the_earlier_one` FAILS, then restore it and confirm it passes.

- [ ] **Step 8: Update the test template, then run the E2E suite**

Run the template block, then `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`. Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add -A src tests
git commit -m "$(cat <<'EOF'
feat(worker): corrections are jobs - kind, instruction, and per-record ordering

categorization_jobs gains kind/instruction/source_message_id; a claim never
overtakes an earlier job for the same transaction. A correction hands the
model the current record and the instruction; a failed correction leaves
the record as it was.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 7: Every state is kept: `transaction_revisions`

**Files:**
- Create: `src/Noof.Ledger.Persistence/Revisions/TransactionRevision.cs`, `RevisionKind.cs`, `RevisionLog.cs`
- Create: `src/Noof.Ledger.Persistence/Configurations/TransactionRevisionConfiguration.cs`
- Modify: `src/Noof.Ledger.Persistence/LedgerDbContext.cs`, `src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs`
- Create: `src/Noof.Ledger.Persistence/Migrations/<timestamp>_AddTransactionRevisions.cs` via `dotnet ef`, plus trigger SQL
- Create: `tests/Noof.Ledger.Persistence.Tests/TransactionRevisionTests.cs`
- Test: `tests/Noof.Ledger.Persistence.Tests/EfCategorizationStoreTests.cs`, `schema.expected.sql`

**Interfaces:**
- Consumes: `CategorizationOutcome.Kind` and `.Instruction` (Task 6).
- Produces: `EfCategorizationStore(LedgerDbContext db, TimeProvider timeProvider)`. `RevisionLog.AppendAsync(LedgerDbContext db, Transaction transaction, RevisionKind kind, string? instruction, TransactionStatus statusBefore, DateTimeOffset now, CancellationToken)`, internal and used again in Task 8. `RevisionKind { Initial = 0, Correction = 1, Edit = 2, Cancel = 3, Restore = 4 }`. `LedgerDbContext.TransactionRevisions`.

- [ ] **Step 1: Write the failing tests**

`EfCategorizationStoreTests.cs`: add `using Microsoft.Extensions.Time.Testing;`, `static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));`, and replace every `new EfCategorizationStore(db)` (and `(dbA)`, `(dbB)`, `(breaking)`) with the same plus `, Clock`.

Create `tests/Noof.Ledger.Persistence.Tests/TransactionRevisionTests.cs`:

```csharp
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;
using Noof.Ledger.Persistence.Revisions;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class TransactionRevisionTests(PostgresFixture fixture)
{
    static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
    static readonly Guid DefaultWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly Guid CoffeeCategoryId = new("00000000-0000-0000-0001-000000000017");

    static async Task<Guid> SeedTransactionAsync(LedgerDbContext db)
    {
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = DefaultWalletId,
            RawText = "кофе 250",
            Status = TransactionStatus.Captured,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
            OccurredOn = new DateOnly(2026, 9, 21),
            TelegramChatId = 1,
            TelegramMessageId = 1,
            CreatedAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return transaction.Id;
    }

    static CategorizedLineItem Coffee(decimal amount) => new("кофе", new Money(amount, CurrencyCode.Rsd), CoffeeCategoryId, null);

    [Fact]
    public async Task A_first_reading_appends_an_initial_revision_with_a_snapshot_of_what_was_written()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transactionId = await SeedTransactionAsync(db);

        await new EfCategorizationStore(db, Clock).ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21)), TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var revision = await db.TransactionRevisions.SingleAsync(TestContext.Current.CancellationToken);
        revision.RevisionNumber.Should().Be(1);
        revision.Kind.Should().Be(RevisionKind.Initial);
        revision.StatusBefore.Should().Be(TransactionStatus.Captured);
        revision.StatusAfter.Should().Be(TransactionStatus.Completed);
        revision.Instruction.Should().BeNull();
        revision.CreatedAt.Should().Be(Clock.GetUtcNow());

        using var snapshot = JsonDocument.Parse(revision.Snapshot);
        snapshot.RootElement.GetProperty("raw_text").GetString().Should().Be("кофе 250");
        snapshot.RootElement.GetProperty("occurred_on").GetString().Should().Be("2026-09-21");
        var item = snapshot.RootElement.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("amount").ValueKind.Should().Be(JsonValueKind.String, "an amount is never a JSON number");
        decimal.Parse(item.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture).Should().Be(250m);
        item.GetProperty("currency").GetString().Should().Be("RSD");
    }

    [Fact]
    public async Task A_correction_appends_the_next_revision_with_its_instruction()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transactionId = await SeedTransactionAsync(db);
        var store = new EfCategorizationStore(db, Clock);

        await store.ApplyAsync(transactionId, new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21)), TestContext.Current.CancellationToken);
        await store.ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(1500m)], new DateOnly(2026, 9, 21), JobKind.Correct, "нет, 1500"),
            TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var revisions = await db.TransactionRevisions.OrderBy(r => r.RevisionNumber).ToListAsync(TestContext.Current.CancellationToken);
        revisions.Select(r => (r.RevisionNumber, r.Kind)).Should().Equal((1, RevisionKind.Initial), (2, RevisionKind.Correction));
        revisions[1].Instruction.Should().Be("нет, 1500");
        revisions[1].StatusBefore.Should().Be(TransactionStatus.Completed);
    }

    [Fact]
    public async Task A_reinterpretation_is_recorded_as_an_edit()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transactionId = await SeedTransactionAsync(db);

        await new EfCategorizationStore(db, Clock).ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(300m)], new DateOnly(2026, 9, 21), JobKind.Reinterpret), TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        (await db.TransactionRevisions.SingleAsync(TestContext.Current.CancellationToken)).Kind.Should().Be(RevisionKind.Edit);
    }

    [Theory]
    [InlineData("UPDATE public.transaction_revisions SET instruction = 'rewritten'")]
    [InlineData("DELETE FROM public.transaction_revisions")]
    [InlineData("TRUNCATE public.transaction_revisions")]
    public async Task The_history_cannot_be_rewritten(string sql)
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transactionId = await SeedTransactionAsync(db);
        await new EfCategorizationStore(db, Clock).ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21)), TestContext.Current.CancellationToken);

        var act = () => db.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
    }
}
```

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL on `EfCategorizationStore`'s constructor arity, `TransactionRevisions` and `RevisionKind`.

- [ ] **Step 3: Implement**

`src/Noof.Ledger.Persistence/Revisions/RevisionKind.cs`:

```csharp
namespace Noof.Ledger.Persistence.Revisions;

internal enum RevisionKind
{
    Initial = 0,
    Correction = 1,
    Edit = 2,
    Cancel = 3,
    Restore = 4,
}
```

`src/Noof.Ledger.Persistence/Revisions/TransactionRevision.cs`:

```csharp
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Revisions;

internal sealed class TransactionRevision
{
    public required Guid Id { get; init; }
    public required Guid TransactionId { get; init; }
    public required int RevisionNumber { get; init; }
    public required RevisionKind Kind { get; init; }
    public string? Instruction { get; init; }

    // StatusBefore is what Вернуть restores: the status a record had before the cancellation it undoes.
    public required TransactionStatus StatusBefore { get; init; }
    public required TransactionStatus StatusAfter { get; init; }

    public required string Snapshot { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```

`src/Noof.Ledger.Persistence/Configurations/TransactionRevisionConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class TransactionRevisionConfiguration : IEntityTypeConfiguration<TransactionRevision>
{
    public void Configure(EntityTypeBuilder<TransactionRevision> builder)
    {
        builder.ToTable("transaction_revisions");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.TransactionId).HasColumnName("transaction_id");
        builder.Property(r => r.RevisionNumber).HasColumnName("revision_number");
        builder.Property(r => r.Kind).HasColumnName("kind");
        builder.Property(r => r.Instruction).HasColumnName("instruction");
        builder.Property(r => r.StatusBefore).HasColumnName("status_before");
        builder.Property(r => r.StatusAfter).HasColumnName("status_after");
        builder.Property(r => r.Snapshot).HasColumnName("snapshot").HasColumnType("jsonb");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(r => new { r.TransactionId, r.RevisionNumber }).IsUnique();

        builder.HasOne<Transaction>()
            .WithMany()
            .HasForeignKey(r => r.TransactionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
```

`LedgerDbContext.cs`: add `using Noof.Ledger.Persistence.Revisions;` and `public DbSet<TransactionRevision> TransactionRevisions => Set<TransactionRevision>();`

`src/Noof.Ledger.Persistence/Revisions/RevisionLog.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Revisions;

// Called inside the caller's database transaction, after the change itself is saved, so the snapshot is read
// back from the rows exactly as they will commit. The caller holds the transaction's row lock, which is what
// makes max + 1 safe; the unique index is the backstop.
internal static class RevisionLog
{
    public static async Task AppendAsync(
        LedgerDbContext db, Transaction transaction, RevisionKind kind, string? instruction,
        TransactionStatus statusBefore, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var last = await db.TransactionRevisions
            .Where(r => r.TransactionId == transaction.Id)
            .MaxAsync(r => (int?)r.RevisionNumber, cancellationToken) ?? 0;

        db.TransactionRevisions.Add(new TransactionRevision
        {
            Id = Guid.NewGuid(),
            TransactionId = transaction.Id,
            RevisionNumber = last + 1,
            Kind = kind,
            Instruction = instruction,
            StatusBefore = statusBefore,
            StatusAfter = transaction.Status,
            Snapshot = await SnapshotAsync(db, transaction, cancellationToken),
            CreatedAt = now,
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    static async Task<string> SnapshotAsync(LedgerDbContext db, Transaction transaction, CancellationToken cancellationToken)
    {
        var lines = await (
            from li in db.LineItems.AsNoTracking()
            where li.TransactionId == transaction.Id
            join c in db.Categories.AsNoTracking() on li.CategoryId equals c.Id into categoryJoin
            from c in categoryJoin.DefaultIfEmpty()
            orderby li.Description
            select new { li.Description, li.Amount, CategorySlug = c == null ? null : c.Slug, li.MerchantId, li.CategorizedBy })
            .ToListAsync(cancellationToken);

        // Amounts are written as decimal strings, never JSON numbers, so no reader of this history can take
        // them as floating point.
        return JsonSerializer.Serialize(new Snapshot(
            transaction.RawText,
            transaction.OccurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [.. lines.Select(line => new SnapshotLine(
                line.Description,
                line.Amount.Amount.ToString(CultureInfo.InvariantCulture),
                line.Amount.Currency.Value,
                line.CategorySlug,
                line.MerchantId,
                (int)line.CategorizedBy))]));
    }

    sealed record Snapshot(
        [property: JsonPropertyName("raw_text")] string RawText,
        [property: JsonPropertyName("occurred_on")] string OccurredOn,
        [property: JsonPropertyName("items")] IReadOnlyList<SnapshotLine> Items);

    sealed record SnapshotLine(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("amount")] string Amount,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("category_slug")] string? CategorySlug,
        [property: JsonPropertyName("merchant_id")] Guid? MerchantId,
        [property: JsonPropertyName("categorized_by")] int CategorizedBy);
}
```

`EfCategorizationStore.cs`: change the class header to `internal sealed class EfCategorizationStore(LedgerDbContext db, TimeProvider timeProvider) : ICategorizationStore`, add `using Noof.Ledger.Persistence.Revisions;`, and replace the tail of `ApplyAsync` (from `var transaction = ...` to the commit) with

```csharp
        var transaction = await db.Transactions.SingleAsync(t => t.Id == transactionId, cancellationToken);
        var statusBefore = transaction.Status;
        transaction.Status = TransactionStatus.Completed;
        transaction.OccurredOn = outcome.OccurredOn;

        await db.SaveChangesAsync(cancellationToken);
        await RevisionLog.AppendAsync(db, transaction, RevisionKindFor(outcome.Kind), outcome.Instruction,
            statusBefore, timeProvider.GetUtcNow(), cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    static RevisionKind RevisionKindFor(JobKind kind) => kind switch
    {
        JobKind.Categorize => RevisionKind.Initial,
        JobKind.Correct => RevisionKind.Correction,
        JobKind.Reinterpret => RevisionKind.Edit,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No revision kind for this job kind."),
    };
```

- [ ] **Step 4: Scaffold the migration and add the guard**

Run: `dotnet ef migrations add AddTransactionRevisions --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence`

Convert it to a file-scoped namespace. At the end of `Up`, after the scaffolded `CreateTable` and `CreateIndex`, add

```csharp
        migrationBuilder.Sql(
            """
            CREATE FUNCTION public.transaction_revisions_append_only() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'transaction_revisions is append-only: % on % is not permitted', TG_OP, TG_TABLE_NAME;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER transaction_revisions_append_only_guard
                BEFORE UPDATE OR DELETE ON public.transaction_revisions
                FOR EACH ROW EXECUTE FUNCTION public.transaction_revisions_append_only();

            CREATE TRIGGER transaction_revisions_no_truncate
                BEFORE TRUNCATE ON public.transaction_revisions
                FOR EACH STATEMENT EXECUTE FUNCTION public.transaction_revisions_append_only();
            """);
```

At the start of `Down`, before `DropTable`, add

```csharp
        migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS transaction_revisions_no_truncate ON public.transaction_revisions;
            DROP TRIGGER IF EXISTS transaction_revisions_append_only_guard ON public.transaction_revisions;
            DROP FUNCTION IF EXISTS public.transaction_revisions_append_only();
            """);
```

Regenerate `schema.expected.sql` as in Task 2 Step 4. The triggers do not appear in it, because they come from migration SQL and not from the model.

- [ ] **Step 5: Run the tests and watch them pass**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: PASS. See the guard go red: comment out the `CREATE TRIGGER transaction_revisions_no_truncate ...` statement, run `--filter TransactionRevisionTests`, and confirm only the `TRUNCATE` case fails. Then restore it.

- [ ] **Step 6: Update the test template, then run the Host and E2E suites**

Run the template block, then:
- `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`

Expected: PASS. `CategorizationWiringTests` still resolves `ICategorizationStore`, because `TimeProvider` is registered by the Host.

- [ ] **Step 7: Commit**

```bash
git add -A src tests
git commit -m "$(cat <<'EOF'
feat(persistence): transaction_revisions - an append-only history of every state

Each write of a record appends a revision in the same database transaction:
status before and after, the instruction, and a JSON snapshot with amounts
as decimal strings. A trigger refuses UPDATE, DELETE and TRUNCATE (D7).

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 8: Отменить and Вернуть

**Files:**
- Create: `src/Noof.Ledger.Application/Editing/IRecordEditor.cs`
- Create: `src/Noof.Ledger.Persistence/Editing/EfRecordEditor.cs`, `tests/Noof.Ledger.Persistence.Tests/EfRecordEditorTests.cs`
- Modify: `src/Noof.Ledger.Persistence/PersistenceRegistration.cs`, `src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs`
- Modify: `src/Noof.Ledger.Application/Chat/IChatNotifier.cs`
- Create: `src/Noof.Ledger.Telegram/RecordActionHandler.cs`, `tests/Noof.Ledger.Telegram.Tests/RecordActionButtonsTests.cs`
- Modify: `src/Noof.Ledger.Telegram/RecordActionButtons.cs`, `TelegramChatNotifier.cs`, `TelegramUpdateRouter.cs`, `TelegramPollingService.cs`, `TelegramRegistration.cs`
- Test: `tests/Noof.Ledger.Persistence.Tests/TransactionRevisionTests.cs`, `tests/Noof.Ledger.Telegram.Tests/{TelegramUpdateRouterTests,TelegramChatNotifierTests,TelegramPollingServiceTests,TelegramRegistrationTests}.cs`, `tests/Noof.Ledger.Host.Tests/CategorizationWiringTests.cs`, `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`

**Interfaces:**
- Consumes: `RevisionLog` and `RevisionKind` (Task 7). `RecordEcho.Compose` (Task 5).
- Produces: `EchoTarget(Guid TransactionId, int? EchoMessageId)`. `IRecordEditor.FindByBotMessageAsync`, `.CancelAsync` and `.RestoreAsync` (Task 9 adds the rest). `IChatNotifier.AnswerActionAsync(string actionId, CancellationToken)`. `RecordActionButtons.TryParse(string?, out RecordAction)`. `RecordActionHandler(IRecordEditor, ICategorizationStore, IChatNotifier)` with `HandleAsync(CallbackQuery query, Message echo, CancellationToken)`. `TelegramUpdateRouter(ICaptureStore, IChatNotifier, TelegramOwnerGate, RecordActionHandler)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Noof.Ledger.Persistence.Tests/EfRecordEditorTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Editing;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfRecordEditorTests(PostgresFixture fixture)
{
    static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
    static readonly Guid DefaultWalletId = new("00000000-0000-0000-0000-000000000001");

    static async Task<Transaction> SeedAsync(LedgerDbContext db, TransactionStatus status = TransactionStatus.Completed)
    {
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = DefaultWalletId,
            RawText = "кофе 250",
            Status = status,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
            OccurredOn = new DateOnly(2026, 9, 21),
            TelegramChatId = 111,
            TelegramMessageId = 5,
            BotMessageId = 42,
            CreatedAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        return transaction;
    }

    [Fact]
    public async Task Finds_a_record_by_its_echo_and_nothing_by_another_message()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);
        var editor = new EfRecordEditor(db, Clock);

        (await editor.FindByBotMessageAsync(111, 42, TestContext.Current.CancellationToken))
            .Should().Be(new EchoTarget(transaction.Id, 42));
        (await editor.FindByBotMessageAsync(111, 5, TestContext.Current.CancellationToken)).Should().BeNull();
        (await editor.FindByBotMessageAsync(999, 42, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Cancelling_sets_Cancelled_and_records_what_it_was_before()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);
        var editor = new EfRecordEditor(db, Clock);

        (await editor.CancelAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeTrue();

        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(TestContext.Current.CancellationToken)).Status.Should().Be(TransactionStatus.Cancelled);
        var revision = await db.TransactionRevisions.SingleAsync(TestContext.Current.CancellationToken);
        revision.Kind.Should().Be(RevisionKind.Cancel);
        revision.StatusBefore.Should().Be(TransactionStatus.Completed);
        revision.StatusAfter.Should().Be(TransactionStatus.Cancelled);
    }

    [Fact]
    public async Task Cancelling_twice_changes_nothing_the_second_time()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);
        var editor = new EfRecordEditor(db, Clock);

        await editor.CancelAsync(transaction.Id, TestContext.Current.CancellationToken);
        (await editor.CancelAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeFalse();

        db.ChangeTracker.Clear();
        (await db.TransactionRevisions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Restoring_puts_back_the_status_from_before_the_cancellation()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db, TransactionStatus.Failed);
        var editor = new EfRecordEditor(db, Clock);
        await editor.CancelAsync(transaction.Id, TestContext.Current.CancellationToken);

        (await editor.RestoreAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeTrue();

        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(TestContext.Current.CancellationToken)).Status.Should().Be(TransactionStatus.Failed);
        var revisions = await db.TransactionRevisions.OrderBy(r => r.RevisionNumber).ToListAsync(TestContext.Current.CancellationToken);
        revisions.Select(r => r.Kind).Should().Equal(RevisionKind.Cancel, RevisionKind.Restore);
        revisions[1].StatusAfter.Should().Be(TransactionStatus.Failed);
    }

    [Fact]
    public async Task Restoring_a_record_that_is_not_cancelled_does_nothing()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);

        (await new EfRecordEditor(db, Clock).RestoreAsync(transaction.Id, TestContext.Current.CancellationToken)).Should().BeFalse();

        (await db.TransactionRevisions.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }
}
```

`TransactionRevisionTests.cs`: add `using Noof.Ledger.Persistence.Editing;` and

```csharp
    [Fact]
    public async Task A_correction_applied_to_a_cancelled_record_keeps_it_cancelled()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transactionId = await SeedTransactionAsync(db);
        var store = new EfCategorizationStore(db, Clock);
        await store.ApplyAsync(transactionId, new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21)), TestContext.Current.CancellationToken);
        await new EfRecordEditor(db, Clock).CancelAsync(transactionId, TestContext.Current.CancellationToken);

        await store.ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(1500m)], new DateOnly(2026, 9, 21), JobKind.Correct, "нет, 1500"),
            TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken))
            .Status.Should().Be(TransactionStatus.Cancelled, "only Вернуть brings a cancelled record back");
    }
```

Create `tests/Noof.Ledger.Telegram.Tests/RecordActionButtonsTests.cs`:

```csharp
using AwesomeAssertions;
using Noof.Ledger.Application.Chat;

namespace Noof.Ledger.Telegram.Tests;

public class RecordActionButtonsTests
{
    [Theory]
    [InlineData(RecordAction.Cancel)]
    [InlineData(RecordAction.Edit)]
    [InlineData(RecordAction.Restore)]
    public void Every_action_survives_the_round_trip_through_callback_data(RecordAction action)
    {
        var data = RecordActionButtons.ToButton(action).CallbackData;

        RecordActionButtons.TryParse(data, out var parsed).Should().BeTrue();
        parsed.Should().Be(action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("delete")]
    public void Unknown_data_is_not_an_action(string? data)
    {
        RecordActionButtons.TryParse(data, out _).Should().BeFalse();
    }
}
```

`TelegramUpdateRouterTests.cs`: add `using AwesomeAssertions;`, `using Noof.Ledger.Application.Categorization;`, `using Noof.Ledger.Application.Editing;` and `using Noof.Ledger.Domain;`, and replace `CreateRouter` with

```csharp
    sealed record Harness(
        TelegramUpdateRouter Router, ICaptureStore CaptureStore, IChatNotifier ChatNotifier,
        IRecordEditor Editor, ICategorizationStore Store);

    static Harness CreateRouter(long ownerChatId)
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, ownerChatId.ToString()));
        var captureStore = Substitute.For<ICaptureStore>();
        var chatNotifier = Substitute.For<IChatNotifier>();
        var editor = Substitute.For<IRecordEditor>();
        var store = Substitute.For<ICategorizationStore>();
        var router = new TelegramUpdateRouter(captureStore, chatNotifier, new TelegramOwnerGate(secretStore),
            new RecordActionHandler(editor, store, chatNotifier));

        return new Harness(router, captureStore, chatNotifier, editor, store);
    }

    static Update ButtonPress(long chatId, int echoId, string data) => new()
    {
        Id = 903,
        CallbackQuery = new CallbackQuery
        {
            Id = "cb-1",
            Data = data,
            From = new User { Id = chatId },
            Message = new Message { Id = echoId, Chat = new Chat { Id = chatId } },
        },
    };

    static CategorizationSubject RecordWith(TransactionStatus status) =>
        new(Guid.NewGuid(), "кофе 250", 111L, 42, "Cash", status, new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 22),
            [new RecordedLine("кофе", new Money(250m, CurrencyCode.Rsd), "food-drink", "Еда и напитки", null)]);
```

In the existing tests replace `var (router, captureStore, chatNotifier) = CreateRouter(...)` with `var (router, captureStore, chatNotifier, _, _) = CreateRouter(...)` and `var (router, captureStore, _) = ...` with `var (router, captureStore, _, _, _) = ...`. Then add

```csharp
    [Fact]
    public async Task Cancel_cancels_the_record_and_turns_its_echo_into_the_cancelled_one()
    {
        var (router, _, chatNotifier, editor, store) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        store.GetSubjectAsync(transactionId, Arg.Any<CancellationToken>()).Returns(RecordWith(TransactionStatus.Cancelled));

        await router.HandleAsync(ButtonPress(111L, 42, "cancel"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).CancelAsync(transactionId, Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).AnswerActionAsync("cb-1", Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(echo => echo.Text.StartsWith("Отменено") && echo.Actions.SequenceEqual(new[] { RecordAction.Restore })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restore_restores_the_record_and_its_buttons()
    {
        var (router, _, chatNotifier, editor, store) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        store.GetSubjectAsync(transactionId, Arg.Any<CancellationToken>()).Returns(RecordWith(TransactionStatus.Completed));

        await router.HandleAsync(ButtonPress(111L, 42, "restore"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).RestoreAsync(transactionId, Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(echo => echo.Actions.SequenceEqual(new[] { RecordAction.Cancel, RecordAction.Edit })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_strangers_button_press_is_ignored_entirely()
    {
        var (router, _, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);

        await router.HandleAsync(ButtonPress(999L, 42, "cancel"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.DidNotReceive().AnswerActionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await editor.DidNotReceive().CancelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_press_on_a_message_that_is_no_record_is_answered_and_nothing_else()
    {
        var (router, _, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns((EchoTarget?)null);

        await router.HandleAsync(ButtonPress(111L, 42, "cancel"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.Received(1).AnswerActionAsync("cb-1", Arg.Any<CancellationToken>());
        await editor.DidNotReceive().CancelAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await chatNotifier.DidNotReceive().EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
    }
```

`TelegramChatNotifierTests.cs`: add

```csharp
    [Fact]
    public async Task AnswerActionAsync_answers_the_callback_query()
    {
        var client = Substitute.For<ITelegramBotClient>();
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        await notifier.AnswerActionAsync("cb-1", TestContext.Current.CancellationToken);

        await client.Received(1).SendRequest(Arg.Is<AnswerCallbackQueryRequest>(r => r.CallbackQueryId == "cb-1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnswerActionAsync_ignores_a_refusal_because_the_answer_is_cosmetic()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<AnswerCallbackQueryRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: query is too old and response timeout expired or query ID is invalid", 400));
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        var act = () => notifier.AnswerActionAsync("cb-1", TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync("a press handled after downtime must still cancel; only the spinner is lost");
    }
```

`TelegramPollingServiceTests.cs`: add `using Telegram.Bot.Types.Enums;` and

```csharp
    [Fact]
    public async Task Asks_telegram_for_button_presses()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns([]);
        var clientFactory = Substitute.For<ITelegramBotClientFactory>();
        clientFactory.Create("tok1").Returns(client);

        await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle())
            .RunTickAsync(TestContext.Current.CancellationToken);

        await client.Received(1).SendRequest(
            Arg.Is<GetUpdatesRequest>(r => r.AllowedUpdates!.Contains(UpdateType.Message) && r.AllowedUpdates!.Contains(UpdateType.CallbackQuery)),
            Arg.Any<CancellationToken>());
    }
```

`TelegramRegistrationTests.cs`: after the `ICaptureStore` line add `services.AddScoped(_ => Substitute.For<Application.Editing.IRecordEditor>());` and `services.AddScoped(_ => Substitute.For<Application.Categorization.ICategorizationStore>());`.

`CategorizationWiringTests.Every_scoped_categorization_port_resolves_without_touching_the_database`: add `scope.ServiceProvider.GetRequiredService<Noof.Ledger.Application.Editing.IRecordEditor>();`.

`PublicSurfaceTests.cs`: add `"EchoTarget", "IRecordEditor"` to the Application list.

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL on `IRecordEditor`, `EfRecordEditor`, `RecordActionHandler`, `AnswerActionAsync` and `RecordActionButtons.TryParse`.

- [ ] **Step 3: Implement persistence**

Create `src/Noof.Ledger.Application/Editing/IRecordEditor.cs`:

```csharp
namespace Noof.Ledger.Application.Editing;

// EchoMessageId is null only when the acknowledgement could not be sent at capture time.
public sealed record EchoTarget(Guid TransactionId, int? EchoMessageId);

public interface IRecordEditor
{
    Task<EchoTarget?> FindByBotMessageAsync(long chatId, int messageId, CancellationToken cancellationToken);

    // False when already cancelled: a double tap or a redelivered update changes nothing.
    Task<bool> CancelAsync(Guid transactionId, CancellationToken cancellationToken);

    // Puts back the status recorded before the latest cancellation. False when the record is not cancelled.
    Task<bool> RestoreAsync(Guid transactionId, CancellationToken cancellationToken);
}
```

Create `src/Noof.Ledger.Persistence/Editing/EfRecordEditor.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Editing;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Revisions;
using Npgsql;

namespace Noof.Ledger.Persistence.Editing;

internal sealed class EfRecordEditor(LedgerDbContext db, TimeProvider timeProvider) : IRecordEditor
{
    public Task<EchoTarget?> FindByBotMessageAsync(long chatId, int messageId, CancellationToken cancellationToken) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.TelegramChatId == chatId && t.BotMessageId == messageId)
            .Select(t => new EchoTarget(t.Id, t.BotMessageId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> CancelAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockAsync(transactionId, cancellationToken) is not { } transaction
            || transaction.Status == TransactionStatus.Cancelled)
            return false;

        var statusBefore = transaction.Status;
        transaction.Status = TransactionStatus.Cancelled;
        await db.SaveChangesAsync(cancellationToken);
        await RevisionLog.AppendAsync(db, transaction, RevisionKind.Cancel, null, statusBefore, timeProvider.GetUtcNow(), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RestoreAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockAsync(transactionId, cancellationToken) is not { Status: TransactionStatus.Cancelled } transaction)
            return false;

        // CancelAsync is the only way a record becomes Cancelled, and it always writes this revision.
        var statusBeforeCancel = await db.TransactionRevisions
            .Where(r => r.TransactionId == transactionId && r.Kind == RevisionKind.Cancel)
            .OrderByDescending(r => r.RevisionNumber)
            .Select(r => r.StatusBefore)
            .FirstAsync(cancellationToken);

        transaction.Status = statusBeforeCancel;
        await db.SaveChangesAsync(cancellationToken);
        await RevisionLog.AppendAsync(db, transaction, RevisionKind.Restore, null, TransactionStatus.Cancelled,
            timeProvider.GetUtcNow(), cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    // The same row lock EfCategorizationStore.ApplyAsync takes, so a button press and a worker writing the
    // same record serialise instead of interleaving their revisions.
    async Task<Transaction?> LockAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        await db.Database.SqlQueryRaw<Guid>(
            "SELECT id FROM transactions WHERE id = @transactionId FOR UPDATE",
            new NpgsqlParameter("transactionId", transactionId))
            .ToListAsync(cancellationToken);

        return await db.Transactions.SingleOrDefaultAsync(t => t.Id == transactionId, cancellationToken);
    }
}
```

`PersistenceRegistration.cs`: add `using Noof.Ledger.Application.Editing;`, `using Noof.Ledger.Persistence.Editing;` and `services.AddScoped<IRecordEditor, EfRecordEditor>();`.

`EfCategorizationStore.ApplyAsync`: replace `transaction.Status = TransactionStatus.Completed;` with

```csharp
        // A correction arriving for a cancelled record corrects it and leaves it cancelled; only Вернуть
        // brings it back.
        transaction.Status = statusBefore == TransactionStatus.Cancelled ? TransactionStatus.Cancelled : TransactionStatus.Completed;
```

- [ ] **Step 4: Implement Telegram**

`IChatNotifier.cs`: add `Task AnswerActionAsync(string actionId, CancellationToken cancellationToken);`.

`RecordActionButtons.cs`: add

```csharp
    public static bool TryParse(string? data, out RecordAction action)
    {
        foreach (var button in Buttons)
        {
            if (string.Equals(button.Data, data, StringComparison.Ordinal))
            {
                action = button.Action;
                return true;
            }
        }

        action = default;
        return false;
    }
```

`TelegramChatNotifier.cs`: add

```csharp
    public async Task AnswerActionAsync(string actionId, CancellationToken cancellationToken)
    {
        try
        {
            await Client().AnswerCallbackQuery(actionId, cancellationToken: cancellationToken);
        }
        // Answering only stops the button's spinner. A press handled after the host was down gets "query is
        // too old"; the press must still take effect, so the refusal is not allowed to fail the update.
        catch (ApiRequestException)
        {
        }
    }
```

Create `src/Noof.Ledger.Telegram/RecordActionHandler.cs`:

```csharp
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Editing;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

internal sealed class RecordActionHandler(IRecordEditor editor, ICategorizationStore store, IChatNotifier chatNotifier)
{
    public async Task HandleAsync(CallbackQuery query, Message echo, CancellationToken cancellationToken)
    {
        await chatNotifier.AnswerActionAsync(query.Id, cancellationToken);

        if (!RecordActionButtons.TryParse(query.Data, out var action))
            return;

        if (await editor.FindByBotMessageAsync(echo.Chat.Id, echo.Id, cancellationToken) is not { } target)
            return;

        switch (action)
        {
            case RecordAction.Cancel:
                await editor.CancelAsync(target.TransactionId, cancellationToken);
                break;
            case RecordAction.Restore:
                await editor.RestoreAsync(target.TransactionId, cancellationToken);
                break;
            default:
                return;
        }

        // Refreshed even when nothing changed: an earlier attempt may have changed the record and then failed
        // to edit the echo. An identical edit is refused by Telegram and swallowed by the notifier.
        if (await store.GetSubjectAsync(target.TransactionId, cancellationToken) is { } record)
            await chatNotifier.EditAsync(echo.Chat.Id, echo.Id, RecordEcho.Compose(record), cancellationToken);
    }
}
```

`TelegramUpdateRouter.cs`:

```csharp
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Application.Chat;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

internal sealed class TelegramUpdateRouter(
    ICaptureStore captureStore,
    IChatNotifier chatNotifier,
    TelegramOwnerGate ownerGate,
    RecordActionHandler actionHandler)
    : ITelegramUpdateRouter
{
    public async Task HandleAsync(Update update, string timeZoneId, CancellationToken cancellationToken)
    {
        switch (update)
        {
            case { Message: { } message }:
                await HandleMessageAsync(message, timeZoneId, cancellationToken);
                break;
            case { CallbackQuery: { Message: { } echo } query }:
                // Rejected before reading Data, for the reason messages are rejected before reading Text.
                if (await ownerGate.IsAllowedAsync(echo.Chat.Id, cancellationToken))
                    await actionHandler.HandleAsync(query, echo, cancellationToken);
                break;
        }
    }

    async Task HandleMessageAsync(Message message, string timeZoneId, CancellationToken cancellationToken)
    {
        // Reject before reading Text: a stranger's content must never be inspected, not even to
        // decide whether it looks like a spend.
        if (!await ownerGate.IsAllowedAsync(message.Chat.Id, cancellationToken))
            return;

        if (message.Text is not { Length: > 0 } text)
            return;

        // message.Date is when Telegram received it from the sender, not when we got around to
        // processing it -- an outage can queue a message for hours, and every queued message must
        // keep its own moment so time_zone_id buckets it into the correct local day later.
        // Message.Date deserialises as DateTime with Kind=Utc (confirmed against Telegram.Bot
        // 22.10.3.1's UnixDateTimeConverter), so this offset is genuinely zero, not just labelled so.
        var sentAt = new DateTimeOffset(message.Date);
        var captured = new CapturedMessage(message.Chat.Id, message.Id, text, sentAt);
        var transactionId = await captureStore.CaptureAsync(captured, timeZoneId, cancellationToken);

        var botMessageId = await chatNotifier.SendAsync(message.Chat.Id, RecordEcho.Acknowledgement, cancellationToken);
        await captureStore.AttachBotMessageAsync(transactionId, botMessageId, cancellationToken);
    }
}
```

`TelegramPollingService.cs`: `allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery],`

`TelegramRegistration.cs`: add `services.AddScoped<RecordActionHandler>();`.

- [ ] **Step 5: Run the tests and watch them pass**

- `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`

Expected: PASS. No migration in this task.

- [ ] **Step 6: Commit**

```bash
git add -A src tests
git commit -m "$(cat <<'EOF'
feat(telegram): Отменить and Вернуть - deterministic, reversible, no model call

Cancel records the status it replaced; restore puts it back (D5). Both are
idempotent, answer the button even when Telegram calls the query stale, and
refresh the echo from the stored record.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 9: Corrections from Telegram: a reply, Изменить, or an edit of the original message

**Files:**
- Modify: `src/Noof.Ledger.Application/Editing/IRecordEditor.cs`, `src/Noof.Ledger.Application/Chat/IChatNotifier.cs`
- Modify: `src/Noof.Ledger.Persistence/Editing/EfRecordEditor.cs`
- Create: `src/Noof.Ledger.Telegram/CorrectionHandler.cs`
- Modify: `src/Noof.Ledger.Telegram/RecordActionHandler.cs`, `TelegramUpdateRouter.cs`, `TelegramChatNotifier.cs`, `TelegramPollingService.cs`, `TelegramRegistration.cs`
- Test: `tests/Noof.Ledger.Persistence.Tests/EfRecordEditorTests.cs`, `tests/Noof.Ledger.Telegram.Tests/{TelegramUpdateRouterTests,TelegramChatNotifierTests,TelegramPollingServiceTests}.cs`

**Interfaces:**
- Consumes: `Transaction.PromptMessageId`, the index `IX_categorization_jobs_transaction_id_source_message_id` and `JobKind` (Task 6). `RecordEcho.Correcting` and `.EditPrompt` (Task 5).
- Produces: `IRecordEditor.FindByUserMessageAsync`, `.RequestCorrectionAsync`, `.ReplaceRawTextAsync` and `.AttachPromptAsync`. `FindByBotMessageAsync` now also matches the prompt. `IChatNotifier.AskAsync(long chatId, int replyToMessageId, string prompt, CancellationToken)`. `CorrectionHandler(IRecordEditor, IChatNotifier)`. `RecordActionHandler` gains the Изменить branch. `TelegramUpdateRouter(ICaptureStore, IChatNotifier, TelegramOwnerGate, RecordActionHandler, CorrectionHandler)`.

- [ ] **Step 1: Write the failing tests**

`EfRecordEditorTests.cs`: add

```csharp
    [Fact]
    public async Task A_reply_to_the_edit_prompt_finds_the_record_and_its_echo()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);
        var editor = new EfRecordEditor(db, Clock);

        await editor.AttachPromptAsync(transaction.Id, 77, TestContext.Current.CancellationToken);

        (await editor.FindByBotMessageAsync(111, 77, TestContext.Current.CancellationToken))
            .Should().Be(new EchoTarget(transaction.Id, 42), "the echo, not the prompt, is what gets edited afterwards");
    }

    [Fact]
    public async Task Finds_a_record_by_the_persons_own_message()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);

        (await new EfRecordEditor(db, Clock).FindByUserMessageAsync(111, 5, TestContext.Current.CancellationToken))
            .Should().Be(new EchoTarget(transaction.Id, 42));
    }

    [Fact]
    public async Task A_correction_is_queued_once_even_when_telegram_delivers_the_reply_twice()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);
        var editor = new EfRecordEditor(db, Clock);

        (await editor.RequestCorrectionAsync(transaction.Id, "нет, 1500", 8, TestContext.Current.CancellationToken)).Should().BeTrue();
        (await editor.RequestCorrectionAsync(transaction.Id, "нет, 1500", 8, TestContext.Current.CancellationToken)).Should().BeFalse();

        db.ChangeTracker.Clear();
        var job = await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.Kind.Should().Be(JobKind.Correct);
        job.Instruction.Should().Be("нет, 1500");
        job.SourceMessageId.Should().Be(8);
        job.Status.Should().Be(JobStatus.Pending);
        job.RunAfter.Should().Be(Clock.GetUtcNow());
    }

    [Fact]
    public async Task An_edited_original_replaces_the_raw_text_and_queues_a_fresh_reading()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);
        var editor = new EfRecordEditor(db, Clock);

        (await editor.ReplaceRawTextAsync(transaction.Id, "кофе 300", TestContext.Current.CancellationToken)).Should().BeTrue();

        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(TestContext.Current.CancellationToken)).RawText.Should().Be("кофе 300");
        (await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken)).Kind.Should().Be(JobKind.Reinterpret);
    }

    [Fact]
    public async Task An_edit_that_leaves_the_text_as_it_was_queues_nothing()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = await SeedAsync(db);

        (await new EfRecordEditor(db, Clock).ReplaceRawTextAsync(transaction.Id, "кофе 250", TestContext.Current.CancellationToken))
            .Should().BeFalse();

        (await db.CategorizationJobs.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }
```

`TelegramUpdateRouterTests.cs`: make `CreateRouter` construct `new TelegramUpdateRouter(captureStore, chatNotifier, new TelegramOwnerGate(secretStore), new RecordActionHandler(editor, store, chatNotifier), new CorrectionHandler(editor, chatNotifier))`, and add

```csharp
    static Update ReplyTo(long chatId, int replyId, int repliedToId, string text) => new()
    {
        Id = 904,
        Message = new Message
        {
            Id = replyId,
            Chat = new Chat { Id = chatId },
            Text = text,
            Date = DateTime.UtcNow,
            ReplyToMessage = new Message { Id = repliedToId, Chat = new Chat { Id = chatId } },
        },
    };

    [Fact]
    public async Task A_reply_to_an_echo_queues_a_correction_instead_of_a_new_capture()
    {
        var (router, captureStore, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        editor.RequestCorrectionAsync(transactionId, "нет, 1500", 8, Arg.Any<CancellationToken>()).Returns(true);

        await router.HandleAsync(ReplyTo(111L, 8, 42, "нет, 1500"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).RequestCorrectionAsync(transactionId, "нет, 1500", 8, Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).EditAsync(111L, 42,
            Arg.Is<EchoMessage>(echo => echo.Text == RecordEcho.Correcting && echo.Actions.Count == 0), Arg.Any<CancellationToken>());
        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_redelivered_reply_edits_nothing_and_captures_nothing()
    {
        var (router, captureStore, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        editor.RequestCorrectionAsync(transactionId, "нет, 1500", 8, Arg.Any<CancellationToken>()).Returns(false);

        await router.HandleAsync(ReplyTo(111L, 8, 42, "нет, 1500"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await chatNotifier.DidNotReceive().EditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
        await captureStore.DidNotReceive().CaptureAsync(Arg.Any<CapturedMessage>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reply_to_anything_but_an_echo_is_captured_as_a_new_message()
    {
        var (router, captureStore, _, editor, _) = CreateRouter(ownerChatId: 111L);
        editor.FindByBotMessageAsync(111L, 3, Arg.Any<CancellationToken>()).Returns((EchoTarget?)null);

        await router.HandleAsync(ReplyTo(111L, 8, 3, "хлеб 100"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await captureStore.Received(1).CaptureAsync(Arg.Is<CapturedMessage>(m => m.Text == "хлеб 100"), "Europe/Belgrade", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_asks_what_to_change_and_remembers_the_prompt()
    {
        var (router, _, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByBotMessageAsync(111L, 42, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        chatNotifier.AskAsync(111L, 42, RecordEcho.EditPrompt, Arg.Any<CancellationToken>()).Returns(77);

        await router.HandleAsync(ButtonPress(111L, 42, "edit"), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).AttachPromptAsync(transactionId, 77, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Editing_the_original_message_re_reads_it()
    {
        var (router, _, chatNotifier, editor, _) = CreateRouter(ownerChatId: 111L);
        var transactionId = Guid.NewGuid();
        editor.FindByUserMessageAsync(111L, 5, Arg.Any<CancellationToken>()).Returns(new EchoTarget(transactionId, 42));
        editor.ReplaceRawTextAsync(transactionId, "кофе 300", Arg.Any<CancellationToken>()).Returns(true);
        var update = new Update { Id = 905, EditedMessage = new Message { Id = 5, Chat = new Chat { Id = 111L }, Text = "кофе 300" } };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.Received(1).ReplaceRawTextAsync(transactionId, "кофе 300", Arg.Any<CancellationToken>());
        await chatNotifier.Received(1).EditAsync(111L, 42, Arg.Is<EchoMessage>(echo => echo.Text == RecordEcho.Correcting), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_strangers_edit_is_ignored()
    {
        var (router, _, _, editor, _) = CreateRouter(ownerChatId: 111L);
        var update = new Update { Id = 906, EditedMessage = new Message { Id = 5, Chat = new Chat { Id = 999L }, Text = "x" } };

        await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

        await editor.DidNotReceive().FindByUserMessageAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
```

`TelegramChatNotifierTests.cs`: add

```csharp
    [Fact]
    public async Task AskAsync_sends_a_force_reply_prompt_quoting_the_echo()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new Message { Id = 77 });
        var notifier = new TelegramChatNotifier(new TelegramClientHandle { Current = client });

        var promptId = await notifier.AskAsync(42L, 555, "Что исправить?", TestContext.Current.CancellationToken);

        promptId.Should().Be(77);
        await client.Received(1).SendRequest(
            Arg.Is<SendMessageRequest>(r => r.Text == "Что исправить?" && r.ReplyParameters!.MessageId == 555
                && r.ReplyMarkup is Telegram.Bot.Types.ReplyMarkups.ForceReplyMarkup),
            Arg.Any<CancellationToken>());
    }
```

`TelegramPollingServiceTests.Asks_telegram_for_button_presses`: rename it to `Asks_telegram_for_edits_and_button_presses` and add `&& r.AllowedUpdates!.Contains(UpdateType.EditedMessage)` to its predicate.

- [ ] **Step 2: Run the tests and watch them fail**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL on `CorrectionHandler`, `AskAsync`, `AttachPromptAsync`, `FindByUserMessageAsync`, `RequestCorrectionAsync` and `ReplaceRawTextAsync`.

- [ ] **Step 3: Implement persistence**

`IRecordEditor.cs`: add

```csharp
    Task<EchoTarget?> FindByUserMessageAsync(long chatId, int messageId, CancellationToken cancellationToken);

    // False when this exact reply is already queued: Telegram redelivers an update whose handling failed partway.
    Task<bool> RequestCorrectionAsync(Guid transactionId, string instruction, int sourceMessageId, CancellationToken cancellationToken);

    // False when the text is unchanged, so a redelivered edit queues nothing.
    Task<bool> ReplaceRawTextAsync(Guid transactionId, string rawText, CancellationToken cancellationToken);

    Task AttachPromptAsync(Guid transactionId, int promptMessageId, CancellationToken cancellationToken);
```

`EfRecordEditor.cs`: widen `FindByBotMessageAsync`'s predicate to

```csharp
            .Where(t => t.TelegramChatId == chatId
                && t.BotMessageId != null
                && (t.BotMessageId == messageId || t.PromptMessageId == messageId))
```

and add

```csharp
    const string CorrectionSourceIndex = "IX_categorization_jobs_transaction_id_source_message_id";

    public Task<EchoTarget?> FindByUserMessageAsync(long chatId, int messageId, CancellationToken cancellationToken) =>
        db.Transactions.AsNoTracking()
            .Where(t => t.TelegramChatId == chatId && t.TelegramMessageId == messageId)
            .Select(t => new EchoTarget(t.Id, t.BotMessageId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> RequestCorrectionAsync(
        Guid transactionId, string instruction, int sourceMessageId, CancellationToken cancellationToken)
    {
        var job = NewJob(transactionId, JobKind.Correct, instruction, sourceMessageId);
        db.CategorizationJobs.Add(job);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: CorrectionSourceIndex,
        })
        {
            db.Entry(job).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> ReplaceRawTextAsync(Guid transactionId, string rawText, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockAsync(transactionId, cancellationToken) is not { } transaction
            || string.Equals(transaction.RawText, rawText, StringComparison.Ordinal))
            return false;

        transaction.RawText = rawText;
        db.CategorizationJobs.Add(NewJob(transactionId, JobKind.Reinterpret, instruction: null, sourceMessageId: null));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task AttachPromptAsync(Guid transactionId, int promptMessageId, CancellationToken cancellationToken)
    {
        var transaction = await db.Transactions.SingleAsync(t => t.Id == transactionId, cancellationToken);
        transaction.PromptMessageId = promptMessageId;
        await db.SaveChangesAsync(cancellationToken);
    }

    CategorizationJob NewJob(Guid transactionId, JobKind kind, string? instruction, int? sourceMessageId)
    {
        var now = timeProvider.GetUtcNow();
        return new CategorizationJob
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Kind = kind,
            Instruction = instruction,
            SourceMessageId = sourceMessageId,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            RunAfter = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
```

- [ ] **Step 4: Implement Telegram**

`IChatNotifier.cs`: add `Task<int> AskAsync(long chatId, int replyToMessageId, string prompt, CancellationToken cancellationToken);`.

`TelegramChatNotifier.cs`: add

```csharp
    public async Task<int> AskAsync(long chatId, int replyToMessageId, string prompt, CancellationToken cancellationToken)
    {
        var message = await Client().SendMessage(chatId, prompt,
            replyParameters: new ReplyParameters { MessageId = replyToMessageId },
            replyMarkup: new ForceReplyMarkup { InputFieldPlaceholder = "нет, 1500" },
            cancellationToken: cancellationToken);
        return message.Id;
    }
```

with `using Telegram.Bot.Types;`.

Create `src/Noof.Ledger.Telegram/CorrectionHandler.cs`:

```csharp
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Editing;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram;

internal sealed class CorrectionHandler(IRecordEditor editor, IChatNotifier chatNotifier)
{
    static readonly EchoMessage Correcting = new(RecordEcho.Correcting, []);

    // A reply to anything other than a record's echo or its Изменить prompt is not a correction: returning
    // false lets the router capture it as a new message.
    public async Task<bool> TryHandleReplyAsync(Message reply, Message repliedTo, string instruction, CancellationToken cancellationToken)
    {
        if (await editor.FindByBotMessageAsync(reply.Chat.Id, repliedTo.Id, cancellationToken) is not { } target)
            return false;

        if (await editor.RequestCorrectionAsync(target.TransactionId, instruction, reply.Id, cancellationToken)
            && target.EchoMessageId is { } echoId)
            await chatNotifier.EditAsync(reply.Chat.Id, echoId, Correcting, cancellationToken);

        return true;
    }

    public async Task HandleEditAsync(Message edited, CancellationToken cancellationToken)
    {
        if (edited.Text is not { Length: > 0 } text)
            return;

        if (await editor.FindByUserMessageAsync(edited.Chat.Id, edited.Id, cancellationToken) is not { } target)
            return;

        if (await editor.ReplaceRawTextAsync(target.TransactionId, text, cancellationToken)
            && target.EchoMessageId is { } echoId)
            await chatNotifier.EditAsync(edited.Chat.Id, echoId, Correcting, cancellationToken);
    }
}
```

`RecordActionHandler.cs`: replace the `default: return;` arm with

```csharp
            case RecordAction.Edit:
                var promptId = await chatNotifier.AskAsync(echo.Chat.Id, echo.Id, RecordEcho.EditPrompt, cancellationToken);
                await editor.AttachPromptAsync(target.TransactionId, promptId, cancellationToken);
                return;
            default:
                return;
```

`TelegramUpdateRouter.cs`: add the constructor parameter `CorrectionHandler correctionHandler`. Add a case between the two existing ones:

```csharp
            case { EditedMessage: { } edited }:
                if (await ownerGate.IsAllowedAsync(edited.Chat.Id, cancellationToken))
                    await correctionHandler.HandleEditAsync(edited, cancellationToken);
                break;
```

In `HandleMessageAsync`, directly after the `message.Text is not { Length: > 0 } text` guard, add

```csharp
        if (message.ReplyToMessage is { } repliedTo
            && await correctionHandler.TryHandleReplyAsync(message, repliedTo, text, cancellationToken))
            return;
```

`TelegramPollingService.cs`: `allowedUpdates: [UpdateType.Message, UpdateType.EditedMessage, UpdateType.CallbackQuery],`. In `NotifyOperatorOfSkippedUpdateAsync` replace `if (update.Message is not { } message)` with `if ((update.Message ?? update.EditedMessage ?? update.CallbackQuery?.Message) is not { } message)`.

`TelegramRegistration.cs`: add `services.AddScoped<CorrectionHandler>();`.

- [ ] **Step 5: Run the tests and watch them pass**

- `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
- `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`

Expected: PASS. See the idempotency guard go red: in `RequestCorrectionAsync`, change `CorrectionSourceIndex` in the `when` filter to `"no_such_index"`. Confirm `A_correction_is_queued_once_even_when_telegram_delivers_the_reply_twice` FAILS with a `DbUpdateException`, then restore it.

- [ ] **Step 6: Commit**

```bash
git add -A src tests
git commit -m "$(cat <<'EOF'
feat(telegram): correct a record by reply, by Изменить, or by editing the message

A reply to the echo or to the ForceReply prompt queues a correction; editing
the original replaces raw_text and queues a fresh reading (D6). A redelivered
reply or an unchanged edit queues nothing.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 10: Close the phase

**Files:**
- Modify: `CLAUDE.md`, `README.md`, `ops/RUNBOOK.md`, `docs/BACKLOG.md`, `docs/OPEN-QUESTIONS.md`

**Interfaces:**
- Consumes: everything above.
- Produces: a clean tree, documentation that tells the truth, and the Fable review.

- [ ] **Step 1: Run the whole solution and record the real numbers**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: green. **Write down the actual succeeded, skipped and total counts from the output.** They go into Steps 2 and 3 and the commit message. If anything is red, stop and fix it first. The live suite must show **10 skipped** (8 before this phase, +1 in Task 1, +1 in Task 3). If it shows fewer skips, the key is set in this shell: stop, unset it, and re-run.

Run: `pwsh -ExecutionPolicy Bypass -File ops/publish.ps1` and confirm it builds and its own test gate passes. **Do not launch the published host.** It resolves `noof_ledger` through the credential file, and that database holds real credentials.

- [ ] **Step 2: `CLAUDE.md`**

- Status block: change "Phases 0, 0b, 1A, 1B, 1C and 1D complete" to "Phases 0, 0b, 1A, 1B, 1C, 1D and 2 complete". Replace "LLM categorisation with a write-once merchant identity table" with "natural-language capture — the model reads amounts and dates from how people talk, the bot echoes the stored record with Отменить · Изменить, a reply or an edit corrects it, and every state is kept in an append-only revision history — with a write-once merchant identity table". Replace the test count with Step 1's. Replace "An opt-in live-model suite of 8" with the new count (10). Replace the "Next is Phase 2 ..." sentence with "Next is Phase 3, voice (a speech-to-text decision comes first); the money model is Phase 4."
- §4 **Database**, add one bullet: `- **After adding a migration, update noof_ledger_test_template** with the command in ops/RUNBOOK.md ("After adding a migration"). The E2E suite clones it and fails on a stale one; never run \`dotnet ef database update\` without \`--connection\` — it resolves noof_ledger.`
- §4 **Database**, add one bullet: `- **transaction_revisions is append-only** (a trigger refuses UPDATE/DELETE/TRUNCATE). Any code that changes a record writes a revision inside the same database transaction, through RevisionLog.AppendAsync.`

- [ ] **Step 3: `README.md`**

Replace the status paragraph's second sentence ("Next (Phase 2): ...") with: `Say it the way you would say it — *"купил вчера штуку евро"* is recorded as 1000 EUR dated yesterday, echoed back in the chat, and can be cancelled with one tap or corrected by a reply.` Replace the test count with Step 1's number. In "Still missing", replace "**voice notes and receipt photos**" with "**voice notes** (next) **and receipt photos**".

- [ ] **Step 4: `ops/RUNBOOK.md`**

Add a section `## After adding a migration` containing the PowerShell block from this plan's "Updating the test template", plus two sentences. The first: the E2E suite and `MoneyStorageTests` clone the template and fail with a missing-column error on a stale one. The second: `noof_ledger` is migrated only when the operator starts the host, never by hand.

- [ ] **Step 5: `docs/OPEN-QUESTIONS.md` and `docs/BACKLOG.md`**

Grep both files first and do not duplicate. Add to `OPEN-QUESTIONS.md`, under P2-1:

```markdown
### P2-2 — a correction is a job, not a table *(taken 2026-09-22, Phase 2 plan)*

`categorization_jobs` gained `kind`, `instruction` and `source_message_id` instead of a separate
corrections table, because a correction needs exactly the claim/lease/retry/attempt-cap machinery the
queue already has. The cost is an ordering rule in `ClaimAsync`: a job is never claimed while an earlier
job for the same transaction is Pending or Claimed, or a correction could be applied and then
overwritten by the reading it corrected. A failed correction never marks a transaction Failed.

### P2-3 — the revision history's shape *(taken 2026-09-22, Phase 2 plan)*

One `transaction_revisions` row per state, with `status_before` and `status_after`, the instruction,
and a jsonb snapshot whose amounts are decimal strings. `status_before` is what Вернуть restores.
Append-only by trigger; the FK is RESTRICT, so a revised transaction can never be deleted.
```

Add to `BACKLOG.md`:

```markdown
## Line items keep no order

`line_items` has no ordinal column, so the echo and the snapshot list lines by description rather than
in the order the message named them. Harmless for one- and two-line messages; a receipt (Phase 6) will
want its own order, and that is a column plus a migration.

## Pressing Изменить twice forgets the first prompt

`transactions.prompt_message_id` holds one prompt. A reply to an older, superseded prompt is not
recognised and is captured as a new message. Rare, visible in the chat, and fixed by a small table of
prompts if it ever matters.

## An edited original whose re-reading fails leaves no revision of the new text

`ReplaceRawTextAsync` changes `raw_text` and queues a re-reading; the Edit revision is written when that
reading is applied. If it fails, the new text lives only on the transaction row. The previous state is
still in the history.
```

In the existing entry "The test template can silently drift from what migrations produce", add one sentence: `Phase 2 added the runbook step "After adding a migration" and a CLAUDE.md rule; the drift guard itself is still missing.`

- [ ] **Step 6: Leave nothing uncommitted**

```bash
git add CLAUDE.md README.md ops/RUNBOOK.md docs/BACKLOG.md docs/OPEN-QUESTIONS.md
git commit -m "$(cat <<'EOF'
docs: close Phase 2 - natural-language capture, echo, cancel and correct

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
git status --short
```

Expected: `git status --short` prints nothing.

- [ ] **Step 7: The closing review, on Fable 5.1**

`CLAUDE.md` §1 requires the review that closes a phase to run on a different model family. Launch a subagent with `model: "fable"` and give it:
- the whole diff of this phase (`git diff <commit before Task 1>..HEAD`), not a summary;
- this plan's "The contract" and "Review Focus" sections, and the spec `docs/superpowers/specs/2026-09-22-natural-language-capture.md`;
- these questions, each answered with a file and line:
  1. Is there any path by which a number from the model reaches the echo without first being written to and read back from the database (D4)?
  2. Can any write to a record (apply, cancel, restore, raw-text replace) commit without its revision, or write a revision outside the transaction's row lock?
  3. Can a correction be applied before the categorisation it corrects, and then be overwritten by it? Check both `ClaimAsync`'s ordering and the lease-expiry path.
  4. Can a Telegram update be turned into a poison update, and so skipped with a notice, by anything that is harmless: a double tap, a stale callback, an unchanged edit, a redelivered reply?
  5. Is there any remaining verbatim check, sanity bound or "plausible amount" logic? Its absence is required (D1).
  6. Does anything read or write `noof_ledger`, or call the live model, in the default test run?

Fix what it finds as separate commits. Amend this plan in place for each fix, so a re-run from scratch reproduces the fixed code.
