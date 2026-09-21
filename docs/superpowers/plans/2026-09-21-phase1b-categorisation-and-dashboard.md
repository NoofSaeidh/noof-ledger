# Phase 1B — Categorisation, Merchants, the Worker and the Dashboard: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A captured Telegram message becomes a categorised expense — per line item, with a merchant identity the database owns — and appears on a dashboard, with the Telegram message edited in place to show figures C# computed.

**Architecture:** Phase 1A wrote a transaction and a queued job atomically and stopped there. 1B drains that queue. A `BackgroundService` claims a job, loads the raw text and the live category tree, asks Claude for a structured proposal through strict tool use, **verifies every amount against the raw text before a number exists in C#**, resolves merchants through a write-once alias table that is the sole authority on identity, writes line items in one database transaction under a precedence predicate, and edits the Telegram reply. The dashboard reads it back through a read-model port so the UI project never sees EF.

**Tech Stack:** .NET 10 · C# · EF Core 10 + Npgsql · PostgreSQL 18 · **Anthropic 12.49.0** (official C# SDK, MIT, GA), consumed through **`Microsoft.Extensions.AI.Abstractions` 10.5.1** (`IChatClient`) rather than the SDK's native `Messages.Create` surface · Telegram.Bot 22.10.3.1 · Blazor Server (RCL + Host) · xUnit v3 on Microsoft Testing Platform · AwesomeAssertions · NSubstitute

**Spec:** `docs/superpowers/specs/2026-09-19-noof-finance-design.md` · decisions in `docs/OPEN-QUESTIONS.md` ("Phase 1 decisions — taken 2026-09-21") · deferred work in `docs/BACKLOG.md` · predecessor plan `docs/superpowers/plans/2026-09-21-phase1a-capture-and-storage.md`

## Global Constraints

- **Money is `decimal` + `Currency`.** Never `double`, never `float`. `numeric(19,4)` in PostgreSQL, `timestamptz` for every `DateTimeOffset` — both enforced by a global convention in `LedgerDbContext.ConfigureConventions`.
- **No number in user-facing output ever originates from a model.** The model quotes a substring; C# asserts it occurs verbatim in the stored raw text and parses it. `QuotedAmount.TryResolve` is that gate and it is the **only** way a `Money` may be built from a model response.
- **Secrets are encrypted in the database and entered through the UI.** Never in `appsettings.json`, never in the repo, never in a log, an exception message, or an LLM prompt. This is a public repository.
- **`Noof.Ledger.Domain` has zero NuGet references**, asserted by a test. `Noof.Ledger.Application` likewise.
- **`Noof.Ledger.Web` is UI only** — no `DbContext`, no EF types, no `HttpClient`, no `Program.cs`. Enforced by `DisableTransitiveProjectReferences` plus architecture tests whose reference and package sets are EXACT, not subsets.
- **Never call `EnsureCreated()`**, anywhere, including test helpers. Tests run against real PostgreSQL; the EF InMemory provider is banned.
- **TDD**: a failing test first, for all behaviour. Exempt: migrations, DTOs, `Program.cs` wiring.
- **The inner test loop never touches the network or a real model.** The live-model suite is opt-in and skipped by default (Task 3).
- **File-scoped namespaces.** `IDE0161` and `TreatWarningsAsErrors` are compile errors.
- **Central Package Management.** An inline `Version=` is NU1008. `NuGetAuditMode=all` with `NU1903`/`NU1904` as errors.
- **`TimeProvider` everywhere time is read inside the host.** Tests advance a `FakeTimeProvider` rather than sleeping. There is exactly one existing exception, verified by grep: `src/Noof.Ledger.Host/Cli/UserCommand.cs:64` calls `DateTimeOffset.UtcNow`, because the CLI verb short-circuits before `WebApplication.CreateBuilder` and has no container to resolve a `TimeProvider` from. Do not "fix" it in this phase and do not copy it either; it is recorded in `docs/BACKLOG.md` by Task 9.
- **`global.json` must keep `{"test":{"runner":"Microsoft.Testing.Platform"}}`.** Positional test paths are rejected; use `--project` or `--solution`. The solution file is `NoofLedger.slnx`.
- **Build output is `artifacts/bin/<project>/<config>/`**, not `bin/Debug/net10.0/` — `Directory.Build.props` sets `ArtifactsPath` to a repo-root `artifacts/`. Any relative path climbing out of `AppContext.BaseDirectory` needs **four** `..`, not three.
- **Do not add** MediatR, AutoMapper, generic repositories over `DbContext`, CQRS scaffolding, or a second model-provider SDK.

## Verified facts this plan is built on

Each was established by running something, not by recollection. They are recorded because several of them break a plausible implementation.

| Fact | How it was established | Consequence |
|---|---|---|
| `ChatOptions.Temperature` compiles, unlike the SDK's `[Obsolete]` `MessageCreateParams.Temperature` | Compiled both ways; the raw one produced `error CS0618` under `TreatWarningsAsErrors` | **Never set temperature anyway.** Determinism comes from the schema-constrained response format, not a sampling parameter. `AnthropicOptions` has no `Temperature` property for the same reason |
| A raw `JsonElement` schema reaches the wire verbatim through `Microsoft.Extensions.AI` | Captured the outgoing HTTP body against a stub handler: our schema arrived unchanged, `additionalProperties: false`, `enum` and `minItems` included | `CategorizationSchema` returns a `JsonElement`. No `InputSchema`, no `FromRawUnchecked`. The schema is not compiler-checked; a test asserts its shape instead |
| `AnthropicApiException` exposes only `StatusCode`, `ErrorType`, `Message` | `.Error`, `.Type`, `.Body`, `.Headers`, `.RequestID` each produced `error CS1061` | Terminal-vs-transient classification is built from the status code alone |
| `client.Models.List()` returns `Task<ModelListPage>` | Compiled; `await foreach` over the call does not bind | Await first, then iterate `page.Items` |
| `ChatResponseFormat.ForJsonSchema(schema, name, description)` lands as `output_config.format` | Captured body: `"output_config":{"format":{"type":"json_schema","schema":{...}}}` | This is Anthropic's GA structured-outputs mode and it **replaces strict tool use**. `strict` constrains a tool's *input*; our answer is the *response*, and `output_config` is its equivalent. No `strict` is emitted and none is needed |
| A tool and a response format combine in one request | Captured body with both: one tool, `tool_choice {"type":"auto"}`, and the `output_config` block together | `list_merchants` stays a real tool the model may call; `record_spending` is answered, not called |
| Declaring a tool in BOTH `ChatOptions.Tools` and `RawRepresentationFactory` sends it **twice** | Captured body had `tools` of length 2, same name | Found while trying to add `strict` through the raw hatch. **Never use `RawRepresentationFactory` for a tool the abstraction already declares.** A test asserts `tools` has length 1 |
| The tool round trip works end to end through `IChatClient` | Ran it against a queued stub: turn 1 returned `FunctionCallContent` with a `CallId`, we answered with `new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, json)])`, turn 2 returned the schema-constrained JSON as `response.Text` | The accumulated conversation is a plain `List<ChatMessage>`; nothing is reconstructed by hand |
| Omitting `Tools` on the follow-up request removes them from the wire | Captured second body: no `tools` key at all, three messages (`user`, `assistant`, `user`) | The "one round trip only" rule is **structural**, not a counter: with no tools declared the model cannot ask again. Better than the forced `tool_choice` this plan first specified |
| `chat.GetResponseAsync(messages, options, cancellationToken)` takes a token | Compiled and ran | Cancellation reaches the HTTP call, so host shutdown is not blocked by an in-flight model call |
| `client.Models.List(cancellationToken)` does **not** compile | Second compile spike: `error CS1503: cannot convert from 'System.Threading.CancellationToken' to 'Anthropic.Models.Models.ModelListParams?'` | The probe must call `client.Models.List(null, cancellationToken)`. The obvious spelling is the wrong one |
| Exceptions are **not** wrapped by the abstraction | Ran 401, 429 and 529 through a stub: `AnthropicUnauthorizedException`, `AnthropicRateLimitException`, `Anthropic5xxException`, each with `StatusCode` and `ErrorType` | Terminal-vs-transient classification is unchanged by the switch, and 529 surfaces as its numeric status |
| `Anthropic` 12.49.0 has no `NU1903`/`NU1904` advisory | `dotnet restore --force` with `NuGetAuditMode=all` fetched the live advisory database and produced none | The package can be added without suppressions |
| The package's dependencies are `Microsoft.Extensions.AI.Abstractions`, `System.Net.ServerSentEvents`, `System.Text.Json` | nuget.org registration API | The architecture test's exact-package assertion for `Noof.Ledger.Ai` lists only `Anthropic`; the three arrive transitively |
| Structured outputs (JSON outputs) and tool use are GA — no beta header | platform.claude.com/docs/en/build-with-claude/structured-outputs, confirmed by the captured body carrying none | No `anthropic-beta` header anywhere in this plan |
| Constrained decoding guarantees **shape**, never **semantics** | Same page, stated explicitly | An enum-constrained `category_slug` is guaranteed to be one of ours; an `amount_quote` is guaranteed to be a string and nothing more. Hence `QuotedAmount` |
| Citations — the only API feature that guarantees verbatim text — returns 400 when combined with structured outputs | Same page | There is no way to have the API itself certify the quote. Verification in C# is not a belt-and-braces choice; it is the only option |
| Prompt caching needs **4096 tokens minimum on Haiku 4.5** | platform.claude.com pricing/caching pages | Our system prompt plus category list is far below that. Caching would silently never engage. **Do not build it** |
| `IJobQueue` has no DI registration anywhere in `src/` | `grep -rn "IJobQueue\|EfJobQueue" src/` returns only the interface and the class | Task 5 registers it. Nothing consumed the queue before this phase |
| `TelegramChatNotifier.EditAsync` exists, is tested, and is called by no production code | Same grep over `src/Noof.Ledger.Telegram` | Task 6 is its first caller |
| `categorization_jobs.claimed_by` is `varchar(128)` | Migration `20260921065115_AddCaptureModel` | The worker id must fit in 128 characters |
| Line item `description` is `varchar(512)`, merchant `display_name` is `varchar(256)`, alias `folded` is `varchar(256)` | Same migration | Model-supplied strings are length-guarded before they reach EF, or `SaveChangesAsync` throws a `PostgresException` that fails the job for a cosmetic reason |

## What this plan deliberately does NOT do

Stating these prevents a well-meaning implementer from building them and a reviewer from flagging them as gaps.

- **No database migration.** Every table, column, index and trigger this phase needs was created in Phase 1A. If you find yourself running `dotnet ef migrations add`, stop — something has drifted from this plan and the schema snapshot test will tell you what.
- **No `DeterministicOfflineCategorizer`.** The spec asks for two categoriser implementations from day one, which made sense when extraction was deterministic. With LLM extraction (decision P1-4) there is nothing to categorise offline; the offline guarantee is durable raw capture plus retry.
- **No parser, tokenizer or currency-alias table.** Extraction is the model's job, by user decision. `QuotedAmount` verifies and parses a quote; it does not find one.
- **No prompt caching.** See the fact table: it cannot engage at our prompt size.
- **No temperature, top_p or top_k.** See the fact table.
- **No category management UI**, no per-line-item correction UI, no merge inbox for near-duplicate merchants. All three are in `docs/BACKLOG.md`. The schema supports every operation they need.
- **No FX conversion and no cross-currency totals.** The dashboard groups by currency and never adds two of them together. Money model is Phase 2.
- **Merchant canonicalisation is NOT batched into the categorisation call, and that is a knowing deviation from spec §11.** The spec says the model canonicalises "only on a miss, in the same batched call as categorisation" — which was written when extraction was deterministic and the raw merchant string was therefore known *before* any model call. With LLM extraction (decision P1-4) it is not: nothing knows the merchant text until the model has read the message, so "the same call" cannot also be conditioned on a miss. The operator chose the separate-request shape explicitly: check locally first, offer a local hit to the model as an option, and send a second request only when nothing matched. **The cost, stated plainly: a message naming N merchants the alias table has never seen costs 1 + N calls, not 1.** At this application's volume that is cents, and every one of those N writes a permanent alias row that makes the next sighting a primary-key lookup with no model call at all — so the cost is paid once per merchant that ever exists, not once per message. `CategorizationWorkerOptions` caps canonicalisation calls per job so a pathological message cannot fan out; see Task 7.
- **No `getMe` probe for the Telegram token.** Only the Anthropic key gets a Test button this phase (Task 8); the Telegram equivalent is recorded in the backlog by that task.
- **No change to the PostgreSQL credential.** Deferred by explicit instruction to a later hardening phase.

## File structure

| File | Responsibility |
|---|---|
| `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs` | The request/proposal/resolution records — the one artifact every other file agrees with |
| `src/Noof.Ledger.Application/Categorization/ICategorizer.cs` | The model port. Two methods: propose, and canonicalise one merchant |
| `src/Noof.Ledger.Application/Categorization/ModelCallException.cs` | Transient vs terminal, decided in the Ai layer, obeyed by the worker |
| `src/Noof.Ledger.Application/Categorization/MerchantScan.cs` | The local alias scan that runs **before** the model call |
| `src/Noof.Ledger.Application/Categorization/ProposalVerification.cs` | The gate: every quote verified against the raw text, every slug one we offered |
| `src/Noof.Ledger.Application/Categorization/ICategoryCatalog.cs` · `IMerchantDirectory.cs` · `ICategorizationStore.cs` | Read the tree, resolve identity, write the result |
| `src/Noof.Ledger.Application/Reporting/ISpendingReadModel.cs` | What the dashboard is allowed to know |
| `src/Noof.Ledger.Application/Secrets/ISecretProbe.cs` | "Is this key any good" without the page ever touching a plaintext secret |
| `src/Noof.Ledger.Ai/AnthropicOptions.cs` | Exactly one `Model` property. No tier can be expressed |
| `src/Noof.Ledger.Ai/CategorizationSchema.cs` | The raw JSON Schema, built from live slugs and merchant ids |
| `src/Noof.Ledger.Ai/CategorizationPrompt.cs` | The system prompt text, in one place, asserted by test |
| `src/Noof.Ledger.Ai/AnthropicCategorizer.cs` | The call, the one-shot tool loop, and the error classification |
| `src/Noof.Ledger.Ai/AnthropicKeyProbe.cs` | `GET /v1/models` — costs no tokens |
| `src/Noof.Ledger.Persistence/Categorization/EfCategoryCatalog.cs` · `EfMerchantDirectory.cs` · `EfCategorizationStore.cs` | EF implementations of the three ports |
| `src/Noof.Ledger.Persistence/Reporting/EfSpendingReadModel.cs` | Two queries, both timezone-aware per row |
| `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs` | Claim, call, verify, write, edit, complete — and never take the host down |
| `src/Noof.Ledger.Host/Workers/CategorizationWorkerOptions.cs` | Lease, attempt cap, backoff, poll interval |
| `src/Noof.Ledger.Host/Workers/CategorizationReply.cs` | The text C# composes for Telegram, success and failure |
| `src/Noof.Ledger.Web/Components/Pages/Home.razor` | Becomes the dashboard |
| `tests/Noof.Ledger.Ai.Tests/` | New project. Fake `HttpMessageHandler`, canned responses, and the opt-in live suite |

---

## The contract

**This section is the source of truth for every signature in this plan.** It is reproduced here in full because a task's implementer sees only their own task. If an implementation disagrees with this section, the implementation is wrong — do not "fix" the contract to match code you already wrote; stop and say so.

### Application — `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs`

```csharp
namespace Noof.Ledger.Application.Categorization;

// What the model is offered. Slug is the stable key the model answers with; NameEn/NameRu are
// renameable display text, which is exactly why they are not the key (decision P1-1).
public sealed record CategoryOption(string Slug, string NameEn, string NameRu, string? ParentSlug);

// A merchant the database already knows. Id is what the model returns when it accepts one.
public sealed record MerchantOption(Guid Id, string DisplayName);

public sealed record CategorizationRequest(
    string RawText,
    IReadOnlyList<CategoryOption> Categories,
    // Merchants whose alias the local scan already found in RawText. These go into the prompt as
    // suggestions the model may accept or ignore.
    IReadOnlyList<MerchantOption> MerchantHints,
    // Every merchant the ledger knows. NOT put in the prompt - it is the answer to the
    // list_merchants tool, and only ever serialised if the model actually asks. Without this the
    // tool would have to answer with the hints it was already given, which is not what its own
    // description promises and would make the tool pointless.
    IReadOnlyList<MerchantOption> AllMerchants);

// One line the model proposes. Nothing here is trusted: AmountQuote is a claim about the raw
// text, CategorySlug is a claim about the offered list, and MerchantQuote is a claim about a
// name that appears in the message. ProposalVerification turns claims into values.
public sealed record ProposedLineItem(
    string Description,
    string AmountQuote,
    string CurrencyCode,
    string CategorySlug,
    Guid? KnownMerchantId,
    string? MerchantQuote);

public sealed record CategorizationProposal(IReadOnlyList<ProposedLineItem> Items);

// A line that survived verification. Amount is a Money built by C# from text C# re-read.
public sealed record ResolvedLineItem(
    string Description,
    Money Amount,
    string CategorySlug,
    Guid? KnownMerchantId,
    string? MerchantText);

// A line ready to be written: identity resolved, slug resolved to a real row.
public sealed record CategorizedLineItem(
    string Description,
    Money Amount,
    Guid CategoryId,
    Guid? MerchantId);

// Everything the worker needs about the transaction it claimed, in one round trip.
public sealed record CategorizationSubject(
    Guid TransactionId,
    string RawText,
    long TelegramChatId,
    int? BotMessageId,
    string WalletName);

public sealed record CategoryEntry(Guid Id, string Slug, string NameEn, string NameRu, string? ParentSlug);

public sealed record MerchantAliasEntry(string Folded, Guid MerchantId, string DisplayName);
```

`Money` and `CurrencyCode` come from `Noof.Ledger.Domain` unchanged. `Money` is `readonly record struct Money(decimal Amount, CurrencyCode Currency)`.

### Application — the ports

```csharp
namespace Noof.Ledger.Application.Categorization;

public interface ICategorizer
{
    Task<CategorizationProposal> ProposeAsync(CategorizationRequest request, CancellationToken cancellationToken);

    // Called ONLY when a merchant the model quoted is absent from the alias table. knownMerchants
    // is the full canonical list, so the model can answer with an existing spelling instead of
    // minting a near-duplicate. Returns the display name to store.
    Task<string> CanonicalizeMerchantAsync(
        string merchantText, IReadOnlyList<MerchantOption> knownMerchants, CancellationToken cancellationToken);
}

public interface ICategoryCatalog
{
    Task<IReadOnlyList<CategoryEntry>> ActiveAsync(CancellationToken cancellationToken);
}

public interface IMerchantDirectory
{
    Task<IReadOnlyList<MerchantAliasEntry>> AliasesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MerchantOption>> MerchantsAsync(CancellationToken cancellationToken);

    // Write-once. Creates a merchant row with displayName and inserts the alias keyed on folded.
    // If another worker inserted the same folded key first, returns THAT merchant's id and writes
    // nothing — the table decides identity, not whoever got there second.
    Task<Guid> LinkAliasAsync(string folded, string displayName, CancellationToken cancellationToken);
}

public interface ICategorizationStore
{
    Task<CategorizationSubject?> GetSubjectAsync(Guid transactionId, CancellationToken cancellationToken);

    // Replaces model-authored line items for this transaction and sets status = Completed, in ONE
    // database transaction. A line whose categorized_by is above Model is never touched, so a
    // hand-corrected line survives a re-run. Idempotent: running it twice leaves the same rows.
    Task ApplyAsync(Guid transactionId, IReadOnlyList<CategorizedLineItem> items, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid transactionId, CancellationToken cancellationToken);
}
```

```csharp
namespace Noof.Ledger.Application.Categorization;

public enum ModelFailureKind
{
    // Retry later: the network, a rate limit, an overloaded API, a malformed answer.
    Transient = 0,

    // Never retry: a bad key, no credit, a request this code will build identically next time.
    Terminal = 1,
}

public sealed class ModelCallException(ModelFailureKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ModelFailureKind Kind { get; } = kind;
}
```

### Application — the pure logic

```csharp
namespace Noof.Ledger.Application.Categorization;

public static class MerchantScan
{
    // Known aliases whose folded text occurs in the folded raw text on whole-word boundaries,
    // longest first, at most `limit` of them. This runs BEFORE the model call so a merchant the
    // database already knows is offered to the model as an option rather than re-invented.
    public static IReadOnlyList<MerchantAliasEntry> Matches(
        string rawText, IReadOnlyList<MerchantAliasEntry> aliases, int limit);
}

public static class ProposalVerification
{
    // Turns a proposal into resolved lines, or fails the whole proposal. Partial acceptance is
    // NOT offered: a bill with one unreadable amount must fail, because nothing downstream ever
    // re-reads the raw text and a half-recorded bill would look complete forever.
    public static bool TryResolve(
        string rawText,
        CategorizationProposal proposal,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        out IReadOnlyList<ResolvedLineItem> items,
        out string failure);
}
```

### Application — reporting and the probe

```csharp
namespace Noof.Ledger.Application.Reporting;

public sealed record RecentLineItem(string Description, Money Amount, string? CategoryName, string? MerchantName);

public sealed record RecentTransaction(
    Guid Id,
    DateTimeOffset OccurredAt,
    string TimeZoneId,
    string RawText,
    TransactionStatus Status,
    string WalletName,
    IReadOnlyList<RecentLineItem> Items);

// CategoryName is the ENGLISH name (categories.name_en). The settled rule is that the interface
// is English and only model-authored prose is Russian; NameRu exists for the model's benefit, not
// the dashboard's. A line with no category is labelled rather than dropped - see Task 6.
public sealed record MonthTotal(string CategoryName, CurrencyCode Currency, decimal Amount);

public sealed record MonthSummary(DateOnly FirstDay, IReadOnlyList<MonthTotal> Totals);

public interface ISpendingReadModel
{
    Task<IReadOnlyList<RecentTransaction>> RecentAsync(int limit, CancellationToken cancellationToken);

    // "This month" is decided by the read model, not the page, so there is exactly one definition
    // of it. Each row is bucketed by ITS OWN stored time zone (decision P1-3).
    Task<MonthSummary> ThisMonthAsync(CancellationToken cancellationToken);
}
```

```csharp
namespace Noof.Ledger.Application.Secrets;

public readonly record struct ProbeResult(bool Ok, string Message);

// A page may hold one of these and learn whether a stored secret works, without any method in
// its reach that returns a plaintext secret. SecretsPageSourceTests enforces that the page never
// contains the text "GetAsync(" — ProbeAsync is how the Test button stays inside that rule.
public interface ISecretProbe
{
    string SecretKey { get; }

    Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken);
}
```

### Ai — options

```csharp
namespace Noof.Ledger.Ai;

public sealed class AnthropicOptions
{
    // Exactly one model property, deliberately. No AdviserModel, no EscalationModel, no
    // FallbackModel: making the type incapable of expressing a tier is stronger than choosing
    // not to configure one. A pinned dated id, not a floating alias, so a model change is a
    // commit rather than a surprise.
    public string Model { get; init; } = "claude-haiku-4-5-20251001";

    public int MaxTokens { get; init; } = 2048;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    // There is no Temperature property and there must not be one: it is [Obsolete] in the SDK and
    // a compile error under TreatWarningsAsErrors.
}
```

### The tool schema, once

Built by `CategorizationSchema.BuildRecordSpending(...)` and `CategorizationSchema.BuildListMerchants()`, both returning a `JsonElement`. `BuildRecordSpending`'s output goes to `ChatResponseFormat.ForJsonSchema(...)` as the **response** schema — `record_spending` is answered, not called as a tool; `BuildListMerchants`' output goes to a raw-schema `AIFunctionDeclaration`, the one real tool this plan declares. `known_merchant_id` is present **only** when there is at least one hint — an empty `enum` is not a valid schema.

```json
{
  "type": "object",
  "additionalProperties": false,
  "required": ["items"],
  "properties": {
    "items": {
      "type": "array",
      "minItems": 1,
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": ["description", "amount_quote", "currency", "category_slug"],
        "properties": {
          "description": { "type": "string", "description": "What was bought, as short plain text in the language of the message." },
          "amount_quote": { "type": "string", "description": "The amount copied from the message character for character, exactly as written. Do not convert digits, do not add or remove separators, do not add a currency symbol, and never compute or sum anything. If the message does not state an amount for this line, do not produce the line." },
          "currency": { "type": "string", "enum": ["EUR", "RSD", "USD", "RUB", "KZT"] },
          "category_slug": { "type": "string", "enum": ["groceries", "food-drink", "..."] },
          "known_merchant_id": { "type": "string", "enum": ["<hinted guid>"], "description": "Set this only if the merchant in the message is one of the listed known merchants." },
          "merchant_quote": { "type": "string", "description": "The merchant name copied from the message character for character. Only when a merchant is actually named and it is not one of the known merchants." }
        }
      }
    }
  }
}
```

The second tool, offered alongside it, lets the model ask for the full merchant list instead of guessing. It is allowed **once** per job; the follow-up request forces `record_spending`.

```json
{
  "name": "list_merchants",
  "description": "List every merchant this ledger already knows, with the id to use for each. Call this only if the message names a merchant that is not in the known merchants given to you, and you want to check whether it is already known under a different spelling.",
  "input_schema": { "type": "object", "additionalProperties": false, "properties": {}, "required": [] }
}
```

### Flow, end to end

```
CategorizationWorker tick
 ├─ ISecretStore.GetStatusAsync(anthropic-api-key)   → not Present ⇒ idle, WITHOUT claiming
 │    ClaimAsync increments attempt_count unconditionally and no verb undoes just that increment,
 │    so claiming before the key exists would burn the whole attempt budget while the operator is
 │    still walking to the settings page, and the job would be Failed before the key was ever set.
 ├─ IJobQueue.ReleaseExpiredLeasesAsync(now)
 ├─ IJobQueue.ClaimAsync(workerId, lease)            → null ⇒ idle
 ├─ ICategorizationStore.GetSubjectAsync(job.TransactionId)
 ├─ ICategoryCatalog.ActiveAsync()                   → offered slugs
 ├─ IMerchantDirectory.AliasesAsync() ▸ MerchantScan.Matches(rawText, aliases, 10)  → hints
 ├─ IMerchantDirectory.MerchantsAsync()                 → AllMerchants (answers list_merchants only)
 ├─ ICategorizer.ProposeAsync(request)               → ModelCallException ⇒ Retry or Fail by Kind
 ├─ ProposalVerification.TryResolve(...)             → false ⇒ terminal failure, never a retry
 ├─ for each line with MerchantText and no KnownMerchantId:
 │    MerchantName.Fold(text) ▸ alias hit?  yes ⇒ that merchant id
 │                                          no  ⇒ ICategorizer.CanonicalizeMerchantAsync
 │                                                ▸ IMerchantDirectory.LinkAliasAsync
 ├─ ICategorizationStore.ApplyAsync(transactionId, items)     ← one database transaction
 ├─ IChatNotifier.EditAsync(chatId, botMessageId, reply)      ← failures here never fail the job
 └─ IJobQueue.SucceedAsync(jobId, workerId)          → NotOwned ⇒ log and stop, never retry
```

---

### Task 1: The contract, and the two pure functions that guard it

**Files:**
- Create: `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs`
- Create: `src/Noof.Ledger.Application/Categorization/ICategorizer.cs`
- Create: `src/Noof.Ledger.Application/Categorization/ICategoryCatalog.cs`
- Create: `src/Noof.Ledger.Application/Categorization/IMerchantDirectory.cs`
- Create: `src/Noof.Ledger.Application/Categorization/ICategorizationStore.cs`
- Create: `src/Noof.Ledger.Application/Categorization/ModelCallException.cs`
- Create: `src/Noof.Ledger.Application/Categorization/MerchantScan.cs`
- Create: `src/Noof.Ledger.Application/Categorization/ProposalVerification.cs`
- Create: `src/Noof.Ledger.Application/Reporting/ISpendingReadModel.cs`
- Create: `src/Noof.Ledger.Application/Secrets/ISecretProbe.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/MerchantScanTests.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/ProposalVerificationTests.cs`
- No `.csproj` edit anywhere. `Noof.Ledger.Application.csproj` is a bare SDK project with only a `ProjectReference` to `Noof.Ledger.Domain` and no explicit `<Compile>` items — every `.cs` file dropped under it is picked up by default globbing (confirmed: `Auth/`, `Capture/`, `Chat/`, `Jobs/`, `Secrets/` are already populated this way with no matching `<ItemGroup>`). `Noof.Ledger.Persistence.Tests.csproj` already references `Noof.Ledger.Persistence`, which transitively references `Noof.Ledger.Application`, and already carries `AwesomeAssertions` — no test-project edit either.

**Precondition:** this is the first task of Phase 1B; nothing from 1B exists yet. It consumes only what Phase 1A already landed in `src/Noof.Ledger.Domain/`: `Money` (`readonly record struct Money(decimal Amount, CurrencyCode Currency)`), `CurrencyCode` (`readonly record struct`, statics `Eur`/`Rsd`/`Usd`/`Rub`/`Kzt`), `TransactionStatus`, `MerchantName.Fold(string raw) : string` (trim, collapse-whitespace, `ToUpperInvariant`), and `QuotedAmount.TryResolve(string rawText, string? amountQuote, string? currencyCode, out Money money, out string failure) : bool`. None of these are modified here.

**Interfaces:**
- Consumes: `Money`, `CurrencyCode`, `TransactionStatus`, `MerchantName.Fold`, `QuotedAmount.TryResolve` (all `Noof.Ledger.Domain`, Phase 1A).
- Produces, exact, for every later task in this plan to build against:
  - Records: `CategoryOption`, `MerchantOption`, `CategorizationRequest`, `ProposedLineItem`, `CategorizationProposal`, `ResolvedLineItem`, `CategorizedLineItem`, `CategorizationSubject`, `CategoryEntry`, `MerchantAliasEntry` (`Noof.Ledger.Application.Categorization`).
  - Ports: `ICategorizer`, `ICategoryCatalog`, `IMerchantDirectory`, `ICategorizationStore` (`Noof.Ledger.Application.Categorization`).
  - `enum ModelFailureKind { Transient = 0, Terminal = 1 }` and `sealed class ModelCallException(ModelFailureKind kind, string message, Exception? innerException = null) : Exception` with `Kind` (`Noof.Ledger.Application.Categorization`).
  - `static class MerchantScan { static IReadOnlyList<MerchantAliasEntry> Matches(string rawText, IReadOnlyList<MerchantAliasEntry> aliases, int limit); }`.
  - `static class ProposalVerification { static bool TryResolve(string rawText, CategorizationProposal proposal, IReadOnlyCollection<string> offeredSlugs, IReadOnlyCollection<Guid> offeredMerchantIds, out IReadOnlyList<ResolvedLineItem> items, out string failure); }`.
  - `RecentLineItem`, `RecentTransaction`, `MonthTotal`, `MonthSummary`, `ISpendingReadModel` (`Noof.Ledger.Application.Reporting`).
  - `readonly record struct ProbeResult(bool Ok, string Message)`, `ISecretProbe` (`Noof.Ledger.Application.Secrets`).
  - Task 3's `AnthropicCategorizer` implements `ICategorizer` and throws `ModelCallException`. Task 5's `EfCategoryCatalog`/`EfMerchantDirectory`/`EfCategorizationStore` implement the three store ports. Task 7's `CategorizationWorker` calls `MerchantScan.Matches` and `ProposalVerification.TryResolve` directly, in that order, exactly as shown in the contract's flow diagram.

**Design decisions locked by this task** (the contract leaves these open; once this task's tests are green they are load-bearing for every later task — do not silently change them):

1. **`MerchantScan` matches on whole-token sequences with `StringComparison.Ordinal`, never a substring search.** Folded text is space-separated by construction (`MerchantName.Fold`), so both the raw text and every alias are split into token arrays and an alias matches only if its token array occurs contiguously inside the raw token array. This is what makes `"MAXI"` match `"KUPIO U MAXI DANAS"` (a whole token) but not `"MAXIMALNO"` (a different, longer token) — a naive `IndexOf`/`Contains` would match both. It is also what keeps this ordinal: an `IndexOf` under a culture-aware comparer risks exactly the Serbian `LJ`-collation defect this repository already treats as settled everywhere else (`CurrencyCodeTests.Orders_ordinally_regardless_of_culture`, `QuotedAmount`'s own ordinal parse). Token-array equality via `string.Equals(..., StringComparison.Ordinal)` has no collation path to go wrong on.
2. **Ordering is longest-`Folded`-length first, tiebroken by an ordinal string comparison on `Folded` itself** — not by input-list order. This makes the result fully deterministic even if the caller's alias list order changes between calls (e.g. a different DB read order), which matters because `IMerchantDirectory.AliasesAsync()` gives no ordering guarantee. Overlapping matches (both `"MAXI"` and `"MAXI PLUS"` present) are **both returned, only reordered** — `MerchantScan` does not deduplicate or exclude the shorter one. Deciding which hint to actually act on is the worker's job in a later task, not the scan's.
3. **`ProposalVerification.TryResolve` fails the whole proposal on the first invalid item and never leaks a partially built result.** `items` is assigned the empty list *before* the per-item loop starts, and every failing `return false` inside that loop leaves it untouched — the loop's own local accumulator (holding any earlier, individually-valid items) is only ever assigned to the `items` out-parameter on the final, all-items-passed success path. A test proves this with a valid item first and an invalid one second, specifically to rule out the accumulator leaking.
4. **`CategorySlug` is matched against `offeredSlugs` case-insensitively (ordinal) but the *resolved* value is normalized to the offered collection's own casing, not the model's.** The Anthropic docs note enum values can come back in unexpected casing; comparing case-insensitively avoids rejecting an otherwise-valid answer for that reason alone, and resolving to the offered casing means every downstream ordinal `slug -> CategoryId` lookup only ever sees the one casing this codebase minted.
5. **`Description` (the 512-character `line_items.description` column) is truncated, not rejected, when over length.** It is free display text, never an identity key, and it stays correctable later (`CategorizationAuthority.User` can override a model-written line per the contract). Losing the tail of an unusually verbose description costs nothing that matters; discarding an otherwise-correct categorization over it would.
6. **`MerchantQuote` (the 256-character `merchant_alias.folded`/`merchant.display_name` columns) is rejected, not truncated — and its RAW length is the only check needed.** Truncating a merchant name, unlike truncating a description, risks writing a plausible-looking but wrong identity into a table that `IMerchantDirectory.LinkAliasAsync` never overwrites, so an over-long quote fails the whole proposal instead.

   > **A second check on the folded length was specified here and then removed, which is worth recording so nobody adds it back.** The reasoning for it was that `ToUpperInvariant` expands `'ß'` (U+00DF) to `"SS"`, letting a quote at exactly 256 raw characters overflow the folded column. **That is true of Unicode's full case mapping and of Java, and false on .NET**, which uses simple 1:1 case mapping. Measured on this repository's runtime rather than argued: `"ß".ToUpperInvariant()` returns `"ß"`, and sweeping every code point in the Basic Multilingual Plane found **zero** whose uppercase is longer than its input. `Fold` splits, joins and uppercases — none of which can lengthen a string — so `raw.Length <= 256` already implies `Fold(raw).Length <= 256`. A second check would be defensive boilerplate for a condition that cannot occur, which CLAUDE.md forbids by name.
7. **`MerchantQuote`, when present, must occur verbatim (plain ordinal substring) in the raw text.** The contract states this rule for `AmountQuote` explicitly but is silent on `MerchantQuote`; this task extends it by the same reasoning the contract already gives for amounts, scaled by consequence: a hallucinated merchant name becomes a **permanent** write-once alias row, so the cost of accepting one unchecked is unbounded in time. Unlike `QuotedAmount`, no whole-word boundary check is applied here — the boundary check exists specifically because `"500"` inside `"1500"` is a different *number*, not a truncated one; `"Café"` occurring inside `"Café Central"` is still the same literal text, not a different claim, so a plain `Contains` is the right amount of strictness.

---

- [x] **Step 1: Create the contract records**

Create `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs`:

```csharp
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Categorization;

// What the model is offered. Slug is the stable key the model answers with; NameEn/NameRu are
// renameable display text, which is exactly why they are not the key (decision P1-1).
public sealed record CategoryOption(string Slug, string NameEn, string NameRu, string? ParentSlug);

// A merchant the database already knows. Id is what the model returns when it accepts one.
public sealed record MerchantOption(Guid Id, string DisplayName);

public sealed record CategorizationRequest(
    string RawText,
    IReadOnlyList<CategoryOption> Categories,
    IReadOnlyList<MerchantOption> MerchantHints,
    IReadOnlyList<MerchantOption> AllMerchants);

// One line the model proposes. Nothing here is trusted: AmountQuote is a claim about the raw
// text, CategorySlug is a claim about the offered list, and MerchantQuote is a claim about a
// name that appears in the message. ProposalVerification turns claims into values.
public sealed record ProposedLineItem(
    string Description,
    string AmountQuote,
    string CurrencyCode,
    string CategorySlug,
    Guid? KnownMerchantId,
    string? MerchantQuote);

public sealed record CategorizationProposal(IReadOnlyList<ProposedLineItem> Items);

// A line that survived verification. Amount is a Money built by C# from text C# re-read.
public sealed record ResolvedLineItem(
    string Description,
    Money Amount,
    string CategorySlug,
    Guid? KnownMerchantId,
    string? MerchantText);

// A line ready to be written: identity resolved, slug resolved to a real row.
public sealed record CategorizedLineItem(
    string Description,
    Money Amount,
    Guid CategoryId,
    Guid? MerchantId);

// Everything the worker needs about the transaction it claimed, in one round trip.
public sealed record CategorizationSubject(
    Guid TransactionId,
    string RawText,
    long TelegramChatId,
    int? BotMessageId,
    string WalletName);

public sealed record CategoryEntry(Guid Id, string Slug, string NameEn, string NameRu, string? ParentSlug);

public sealed record MerchantAliasEntry(string Folded, Guid MerchantId, string DisplayName);
```

This is a fixed contract of plain data records with no branching logic — the DTO exemption from the standing TDD rule applies, same as `CapturedMessage`/`ICaptureStore` in Phase 1A's Task 6. No failing test to write here.

- [x] **Step 2: Create the model port**

Create `src/Noof.Ledger.Application/Categorization/ICategorizer.cs`:

```csharp
namespace Noof.Ledger.Application.Categorization;

public interface ICategorizer
{
    Task<CategorizationProposal> ProposeAsync(CategorizationRequest request, CancellationToken cancellationToken);

    // Called ONLY when a merchant the model quoted is absent from the alias table. knownMerchants
    // is the full canonical list, so the model can answer with an existing spelling instead of
    // minting a near-duplicate. Returns the display name to store.
    Task<string> CanonicalizeMerchantAsync(
        string merchantText, IReadOnlyList<MerchantOption> knownMerchants, CancellationToken cancellationToken);
}
```

- [x] **Step 3: Create the read-side store ports**

Create `src/Noof.Ledger.Application/Categorization/ICategoryCatalog.cs`:

```csharp
namespace Noof.Ledger.Application.Categorization;

public interface ICategoryCatalog
{
    Task<IReadOnlyList<CategoryEntry>> ActiveAsync(CancellationToken cancellationToken);
}
```

Create `src/Noof.Ledger.Application/Categorization/IMerchantDirectory.cs`:

```csharp
namespace Noof.Ledger.Application.Categorization;

public interface IMerchantDirectory
{
    Task<IReadOnlyList<MerchantAliasEntry>> AliasesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<MerchantOption>> MerchantsAsync(CancellationToken cancellationToken);

    // Write-once. Creates a merchant row with displayName and inserts the alias keyed on folded.
    // If another worker inserted the same folded key first, returns THAT merchant's id and writes
    // nothing — the table decides identity, not whoever got there second.
    Task<Guid> LinkAliasAsync(string folded, string displayName, CancellationToken cancellationToken);
}
```

- [x] **Step 4: Create the write-side store port**

Create `src/Noof.Ledger.Application/Categorization/ICategorizationStore.cs`:

```csharp
namespace Noof.Ledger.Application.Categorization;

public interface ICategorizationStore
{
    Task<CategorizationSubject?> GetSubjectAsync(Guid transactionId, CancellationToken cancellationToken);

    // Replaces model-authored line items for this transaction and sets status = Completed, in ONE
    // database transaction. A line whose categorized_by is above Model is never touched, so a
    // hand-corrected line survives a re-run. Idempotent: running it twice leaves the same rows.
    Task ApplyAsync(Guid transactionId, IReadOnlyList<CategorizedLineItem> items, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid transactionId, CancellationToken cancellationToken);
}
```

- [x] **Step 5: Create the model-failure classification**

Create `src/Noof.Ledger.Application/Categorization/ModelCallException.cs`:

```csharp
namespace Noof.Ledger.Application.Categorization;

public enum ModelFailureKind
{
    // Retry later: the network, a rate limit, an overloaded API, a malformed answer.
    Transient = 0,

    // Never retry: a bad key, no credit, a request this code will build identically next time.
    Terminal = 1,
}

public sealed class ModelCallException(ModelFailureKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ModelFailureKind Kind { get; } = kind;
}
```

- [x] **Step 6: Create the reporting port**

Create `src/Noof.Ledger.Application/Reporting/ISpendingReadModel.cs`:

```csharp
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting;

public sealed record RecentLineItem(string Description, Money Amount, string? CategoryName, string? MerchantName);

public sealed record RecentTransaction(
    Guid Id,
    DateTimeOffset OccurredAt,
    string TimeZoneId,
    string RawText,
    TransactionStatus Status,
    string WalletName,
    IReadOnlyList<RecentLineItem> Items);

public sealed record MonthTotal(string CategoryName, CurrencyCode Currency, decimal Amount);

public sealed record MonthSummary(DateOnly FirstDay, IReadOnlyList<MonthTotal> Totals);

public interface ISpendingReadModel
{
    Task<IReadOnlyList<RecentTransaction>> RecentAsync(int limit, CancellationToken cancellationToken);

    // "This month" is decided by the read model, not the page, so there is exactly one definition
    // of it. Each row is bucketed by ITS OWN stored time zone (decision P1-3).
    Task<MonthSummary> ThisMonthAsync(CancellationToken cancellationToken);
}
```

- [x] **Step 7: Create the secret probe port**

Create `src/Noof.Ledger.Application/Secrets/ISecretProbe.cs`:

```csharp
namespace Noof.Ledger.Application.Secrets;

public readonly record struct ProbeResult(bool Ok, string Message);

// A page may hold one of these and learn whether a stored secret works, without any method in
// its reach that returns a plaintext secret. SecretsPageSourceTests enforces that the page never
// contains the text "GetAsync(" — ProbeAsync is how the Test button stays inside that rule.
public interface ISecretProbe
{
    string SecretKey { get; }

    Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken);
}
```

- [x] **Step 8: Build and confirm the contract compiles clean**

Run: `dotnet build src/Noof.Ledger.Application/Noof.Ledger.Application.csproj`
Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`. `Noof.Ledger.Application` still has zero `PackageReference` entries — you have not added one; everything above resolves against `Noof.Ledger.Domain` and the BCL only.

- [x] **Step 9: Commit the contract and the ports**

```bash
git add src/Noof.Ledger.Application/Categorization/CategorizationContract.cs src/Noof.Ledger.Application/Categorization/ICategorizer.cs src/Noof.Ledger.Application/Categorization/ICategoryCatalog.cs src/Noof.Ledger.Application/Categorization/IMerchantDirectory.cs src/Noof.Ledger.Application/Categorization/ICategorizationStore.cs src/Noof.Ledger.Application/Categorization/ModelCallException.cs src/Noof.Ledger.Application/Reporting/ISpendingReadModel.cs src/Noof.Ledger.Application/Secrets/ISecretProbe.cs
git commit -m "$(cat <<'EOF'
feat(application): Phase 1B contract - categorization records, ports and ModelCallException

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [x] **Step 10: Write the failing test matrix for `MerchantScan.Matches`**

Create `tests/Noof.Ledger.Persistence.Tests/MerchantScanTests.cs`:

```csharp
using System.Globalization;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Persistence.Tests;

public class MerchantScanTests
{
    [Fact]
    public void Matches_an_alias_that_occurs_on_whole_word_boundaries()
    {
        var aliases = new[] { new MerchantAliasEntry("MAXI", Guid.NewGuid(), "Maxi") };

        var matches = MerchantScan.Matches("kupio u maxi danas", aliases, 10);

        matches.Should().ContainSingle().Which.Folded.Should().Be("MAXI");
    }

    [Fact]
    public void Does_not_match_an_alias_that_is_only_a_substring_of_a_longer_word()
    {
        var aliases = new[] { new MerchantAliasEntry("MAXI", Guid.NewGuid(), "Maxi") };

        var matches = MerchantScan.Matches("kupio maximalno danas", aliases, 10);

        matches.Should().BeEmpty();
    }

    [Fact]
    public void An_alias_does_not_match_when_its_words_are_not_adjacent_in_the_raw_text()
    {
        var aliases = new[] { new MerchantAliasEntry("MAXI PLUS", Guid.NewGuid(), "Maxi Plus") };

        var matches = MerchantScan.Matches("kupio u maxi ali ne plus danas", aliases, 10);

        matches.Should().BeEmpty();
    }

    [Fact]
    public void Orders_the_longer_alias_first_when_both_match()
    {
        var maxi = new MerchantAliasEntry("MAXI", Guid.NewGuid(), "Maxi");
        var maxiPlus = new MerchantAliasEntry("MAXI PLUS", Guid.NewGuid(), "Maxi Plus");

        var matches = MerchantScan.Matches("kupio u maxi plus danas", [maxi, maxiPlus], 10);

        matches.Select(m => m.Folded).Should().Equal("MAXI PLUS", "MAXI");
    }

    [Fact]
    public void Ties_on_length_are_broken_ordinally_regardless_of_input_order()
    {
        var idea = new MerchantAliasEntry("IDEA", Guid.NewGuid(), "Idea");
        var maxi = new MerchantAliasEntry("MAXI", Guid.NewGuid(), "Maxi");

        var matches = MerchantScan.Matches("kupio u maxi i idea danas", [maxi, idea], 10);

        matches.Select(m => m.Folded).Should().Equal("IDEA", "MAXI");
    }

    [Fact]
    public void Returns_at_most_limit_matches()
    {
        var idea = new MerchantAliasEntry("IDEA", Guid.NewGuid(), "Idea");
        var maxi = new MerchantAliasEntry("MAXI", Guid.NewGuid(), "Maxi");

        var matches = MerchantScan.Matches("kupio u maxi i idea danas", [maxi, idea], 1);

        matches.Should().ContainSingle().Which.Folded.Should().Be("IDEA");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_limit_returns_no_matches(int limit)
    {
        var aliases = new[] { new MerchantAliasEntry("MAXI", Guid.NewGuid(), "Maxi") };

        MerchantScan.Matches("kupio u maxi danas", aliases, limit).Should().BeEmpty();
    }

    [Fact]
    public void Matching_is_ordinal_and_unaffected_by_the_current_culture()
    {
        // Mirrors MerchantNameTests.Uppercases_using_the_invariant_culture's trap at the scan
        // level: a culture-sensitive comparison here would be the Serbian LJ-collation defect
        // this codebase treats as settled everywhere else, not a hypothetical one.
        var previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

        try
        {
            var aliases = new[] { new MerchantAliasEntry("MAXI", Guid.NewGuid(), "Maxi") };

            var matches = MerchantScan.Matches("kupio u maxi danas", aliases, 10);

            matches.Should().ContainSingle().Which.Folded.Should().Be("MAXI");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
```

- [x] **Step 11: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter MerchantScanTests`
Expected: build error — `error CS0246: The type or namespace name 'MerchantScan' could not be found` (`MerchantAliasEntry` already exists from Step 1, so only `MerchantScan` is missing).

- [x] **Step 12: Write the minimal implementation**

Create `src/Noof.Ledger.Application/Categorization/MerchantScan.cs`:

```csharp
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Categorization;

public static class MerchantScan
{
    public static IReadOnlyList<MerchantAliasEntry> Matches(
        string rawText, IReadOnlyList<MerchantAliasEntry> aliases, int limit)
    {
        if (limit <= 0)
            return [];

        var rawTokens = MerchantName.Fold(rawText).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rawTokens.Length == 0)
            return [];

        return aliases
            .Where(alias => OccursAsTokenSequence(rawTokens, alias.Folded.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .OrderByDescending(alias => alias.Folded.Length)
            .ThenBy(alias => alias.Folded, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    // Token-array equality, not a substring search: matching "MAXI" with IndexOf/Contains would
    // also match inside "MAXIMALNO", and a culture-aware IndexOf risks the Serbian LJ-collation
    // defect this codebase treats as settled everywhere else. Folded text is space-separated by
    // construction (MerchantName.Fold), which is what makes comparing token arrays directly work.
    static bool OccursAsTokenSequence(string[] rawTokens, string[] aliasTokens)
    {
        if (aliasTokens.Length == 0 || aliasTokens.Length > rawTokens.Length)
            return false;

        for (var start = 0; start <= rawTokens.Length - aliasTokens.Length; start++)
        {
            var matched = true;
            for (var offset = 0; offset < aliasTokens.Length; offset++)
            {
                if (!string.Equals(rawTokens[start + offset], aliasTokens[offset], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return true;
        }

        return false;
    }
}
```

- [x] **Step 13: Run the tests and watch them pass**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter MerchantScanTests`
Expected: PASS — 7 `[Fact]`s plus the 2-case `[Theory]`, 9 test cases total, `Errors: 0, Failed: 0`.

- [x] **Step 14: Commit**

```bash
git add src/Noof.Ledger.Application/Categorization/MerchantScan.cs tests/Noof.Ledger.Persistence.Tests/MerchantScanTests.cs
git commit -m "$(cat <<'EOF'
feat(application): MerchantScan.Matches - ordinal, whole-word alias hints before the model call

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [x] **Step 15: Write the failing test matrix for `ProposalVerification.TryResolve`**

This is the second most security-relevant function in the phase, after `QuotedAmount` itself — it is where every one of the contract's per-item guarantees gets composed and enforced together. Create `tests/Noof.Ledger.Persistence.Tests/ProposalVerificationTests.cs`:

```csharp
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

public class ProposalVerificationTests
{
    static readonly Guid GroceryMerchantId = Guid.NewGuid();

    static ProposedLineItem ValidItem(
        string description = "coffee",
        string amountQuote = "250",
        string currency = "RSD",
        string categorySlug = "groceries",
        Guid? knownMerchantId = null,
        string? merchantQuote = null) =>
        new(description, amountQuote, currency, categorySlug, knownMerchantId, merchantQuote);

    [Fact]
    public void An_empty_item_list_fails()
    {
        var proposal = new CategorizationProposal([]);

        var resolved = ProposalVerification.TryResolve(
            "кофе 250 рсд", proposal, ["groceries"], [], out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void Resolves_a_valid_single_item_proposal()
    {
        var proposal = new CategorizationProposal([ValidItem()]);

        var resolved = ProposalVerification.TryResolve(
            "кофе 250 рсд", proposal, ["groceries"], [], out var items, out var failure);

        resolved.Should().BeTrue();
        failure.Should().BeEmpty();
        items.Should().ContainSingle().Which.Should().Be(
            new ResolvedLineItem("coffee", new Money(250m, CurrencyCode.Rsd), "groceries", null, null));
    }

    [Fact]
    public void An_amount_quote_that_does_not_occur_verbatim_fails_the_whole_proposal()
    {
        var proposal = new CategorizationProposal([ValidItem(amountQuote: "999")]);

        var resolved = ProposalVerification.TryResolve(
            "кофе 250 рсд", proposal, ["groceries"], [], out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void An_amount_quote_that_is_only_a_fragment_of_a_larger_number_is_refused()
    {
        // The exact trap QuotedAmount itself guards against
        // (QuotedAmountTests.A_quote_that_is_only_a_fragment_of_a_larger_number_fails), re-proven
        // here at the composition level: "500" is a true substring of "1500" but is a different
        // number, not a truncated quote.
        var proposal = new CategorizationProposal([ValidItem(amountQuote: "500")]);

        var resolved = ProposalVerification.TryResolve(
            "кофе 1500 рсд", proposal, ["groceries"], [], out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void A_category_slug_that_was_not_offered_fails()
    {
        var proposal = new CategorizationProposal([ValidItem(categorySlug: "not-offered")]);

        var resolved = ProposalVerification.TryResolve(
            "кофе 250 рсд", proposal, ["groceries"], [], out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().Contain("not-offered");
    }

    [Fact]
    public void A_category_slug_matches_case_insensitively_and_resolves_to_the_offered_casing()
    {
        var proposal = new CategorizationProposal([ValidItem(categorySlug: "GROCERIES")]);

        var resolved = ProposalVerification.TryResolve(
            "кофе 250 рсд", proposal, ["groceries"], [], out var items, out var failure);

        resolved.Should().BeTrue();
        failure.Should().BeEmpty();
        items.Should().ContainSingle().Which.CategorySlug.Should().Be("groceries");
    }

    [Fact]
    public void A_known_merchant_id_that_was_not_offered_fails()
    {
        var proposal = new CategorizationProposal([ValidItem(knownMerchantId: GroceryMerchantId)]);

        var resolved = ProposalVerification.TryResolve(
            "кофе 250 рсд", proposal, ["groceries"], [], out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().Contain(GroceryMerchantId.ToString());
    }

    [Fact]
    public void A_known_merchant_id_that_was_offered_resolves()
    {
        var proposal = new CategorizationProposal([ValidItem(knownMerchantId: GroceryMerchantId)]);

        var resolved = ProposalVerification.TryResolve(
            "кофе 250 рсд", proposal, ["groceries"], [GroceryMerchantId], out var items, out var failure);

        resolved.Should().BeTrue();
        failure.Should().BeEmpty();
        items.Should().ContainSingle().Which.KnownMerchantId.Should().Be(GroceryMerchantId);
    }

    [Fact]
    public void A_description_longer_than_512_characters_is_truncated_not_rejected()
    {
        var longDescription = new string('a', 600);
        var proposal = new CategorizationProposal([ValidItem(description: longDescription)]);

        var resolved = ProposalVerification.TryResolve(
            "кофе 250 рсд", proposal, ["groceries"], [], out var items, out var failure);

        resolved.Should().BeTrue();
        failure.Should().BeEmpty();
        items.Should().ContainSingle().Which.Description.Length.Should().Be(512);
    }

    [Fact]
    public void A_merchant_quote_longer_than_256_characters_fails_the_whole_proposal()
    {
        var longMerchant = new string('a', 300);
        var proposal = new CategorizationProposal([ValidItem(merchantQuote: longMerchant)]);

        var resolved = ProposalVerification.TryResolve(
            $"кофе 250 рсд у {longMerchant}", proposal, ["groceries"], [], out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().NotBeEmpty();
    }

    [Fact]
    public void A_merchant_quote_that_does_not_occur_verbatim_in_the_raw_text_fails()
    {
        // The write-once alias table never overwrites (IMerchantDirectory.LinkAliasAsync), so a
        // hallucinated merchant name would live forever. Requiring the same raw-text presence
        // QuotedAmount requires for money closes that door for merchants too.
        var proposal = new CategorizationProposal([ValidItem(merchantQuote: "Ghost Store")]);

        var resolved = ProposalVerification.TryResolve(
            "кофе 250 рсд", proposal, ["groceries"], [], out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().Contain("Ghost Store");
    }

    [Fact]
    public void A_merchant_quote_present_verbatim_resolves()
    {
        var proposal = new CategorizationProposal([ValidItem(merchantQuote: "Maxi")]);

        var resolved = ProposalVerification.TryResolve(
            "кофе 250 рсд у Maxi", proposal, ["groceries"], [], out var items, out var failure);

        resolved.Should().BeTrue();
        failure.Should().BeEmpty();
        items.Should().ContainSingle().Which.MerchantText.Should().Be("Maxi");
    }

    [Fact]
    public void One_bad_item_fails_the_whole_proposal_even_when_an_earlier_item_was_individually_valid()
    {
        // Proves partial acceptance is not offered: the first item alone would resolve fine, but
        // TryResolve must not leak that partial result once the second item fails.
        var proposal = new CategorizationProposal(
        [
            ValidItem(description: "good"),
            ValidItem(description: "bad", amountQuote: "999"),
        ]);

        var resolved = ProposalVerification.TryResolve(
            "кофе 250 рсд", proposal, ["groceries"], [], out var items, out var failure);

        resolved.Should().BeFalse();
        items.Should().BeEmpty();
        failure.Should().NotBeEmpty();
    }
}
```

- [x] **Step 16: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter ProposalVerificationTests`
Expected: build error — `error CS0246: The type or namespace name 'ProposalVerification' could not be found`.

- [x] **Step 17: Write the minimal implementation**

Create `src/Noof.Ledger.Application/Categorization/ProposalVerification.cs`:

```csharp
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Categorization;

public static class ProposalVerification
{
    const int MaxDescriptionLength = 512;
    const int MaxMerchantQuoteLength = 256;

    public static bool TryResolve(
        string rawText,
        CategorizationProposal proposal,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        out IReadOnlyList<ResolvedLineItem> items,
        out string failure)
    {
        // Assigned once, up front, and never reassigned on any failing path below - the resolved
        // list built inside the loop is only ever handed out on total success. This is what makes
        // "partial acceptance is not offered" true even when an earlier item individually verified
        // fine before a later one failed.
        items = [];

        if (proposal.Items.Count == 0)
        {
            failure = "Proposal has no items.";
            return false;
        }

        var resolved = new List<ResolvedLineItem>(proposal.Items.Count);

        for (var index = 0; index < proposal.Items.Count; index++)
        {
            var item = proposal.Items[index];
            var label = $"Item {index + 1} (\"{item.Description}\")";

            if (!QuotedAmount.TryResolve(rawText, item.AmountQuote, item.CurrencyCode, out var money, out var amountFailure))
            {
                failure = $"{label}: {amountFailure}";
                return false;
            }

            var matchedSlug = offeredSlugs.FirstOrDefault(
                slug => string.Equals(slug, item.CategorySlug, StringComparison.OrdinalIgnoreCase));
            if (matchedSlug is null)
            {
                failure = $"{label}: category slug \"{item.CategorySlug}\" was not offered.";
                return false;
            }

            if (item.KnownMerchantId is { } knownMerchantId && !offeredMerchantIds.Contains(knownMerchantId))
            {
                failure = $"{label}: known merchant id {knownMerchantId} was not offered.";
                return false;
            }

            if (!string.IsNullOrEmpty(item.MerchantQuote))
            {
                if (item.MerchantQuote.Length > MaxMerchantQuoteLength)
                {
                    failure = $"{label}: merchant quote \"{item.MerchantQuote}\" is {item.MerchantQuote.Length} characters, " +
                        $"longer than the {MaxMerchantQuoteLength}-character merchant name column.";
                    return false;
                }

                var foldedLength = MerchantName.Fold(item.MerchantQuote).Length;
                if (foldedLength > MaxMerchantQuoteLength)
                {
                    failure = $"{label}: merchant quote \"{item.MerchantQuote}\" folds to {foldedLength} characters, " +
                        $"longer than the {MaxMerchantQuoteLength}-character merchant name column.";
                    return false;
                }

                if (!rawText.Contains(item.MerchantQuote, StringComparison.Ordinal))
                {
                    failure = $"{label}: merchant quote \"{item.MerchantQuote}\" does not occur verbatim in the raw text.";
                    return false;
                }
            }

            var description = item.Description.Length > MaxDescriptionLength
                ? item.Description[..MaxDescriptionLength]
                : item.Description;

            resolved.Add(new ResolvedLineItem(description, money, matchedSlug, item.KnownMerchantId, item.MerchantQuote));
        }

        items = resolved;
        failure = string.Empty;
        return true;
    }
}
```

- [x] **Step 18: Run the tests and watch them pass**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter ProposalVerificationTests`
Expected: PASS — 13 `[Fact]`s, `Errors: 0, Failed: 0`.

- [x] **Step 19: Run the whole solution once to confirm nothing else moved**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: PASS, exit 0 — total test count grown by 23 (9 `MerchantScanTests` cases + 14 `ProposalVerificationTests` cases) relative to Phase 1A's baseline, everything else unchanged.

- [x] **Step 20: Commit**

```bash
git add src/Noof.Ledger.Application/Categorization/ProposalVerification.cs tests/Noof.Ledger.Persistence.Tests/ProposalVerificationTests.cs
git commit -m "$(cat <<'EOF'
feat(application): ProposalVerification.TryResolve - the gate between a model claim and a written line

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 2: The Ai project — options, the response schema, and the prompt

**Files:**
- Modify: `Directory.Packages.props` — add `<PackageVersion Include="Anthropic" Version="12.49.0" />`
- Modify: `src/Noof.Ledger.Ai/Noof.Ledger.Ai.csproj` — add `<PackageReference Include="Anthropic" />`
- Create: `src/Noof.Ledger.Ai/AnthropicOptions.cs`
- Create: `src/Noof.Ledger.Ai/CategorizationSchema.cs`
- Create: `src/Noof.Ledger.Ai/CategorizationPrompt.cs`
- Create: `tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
- Create: `tests/Noof.Ledger.Ai.Tests/xunit.runner.json`
- Create: `tests/Noof.Ledger.Ai.Tests/CategorizationSchemaTests.cs`
- Create: `tests/Noof.Ledger.Ai.Tests/CategorizationPromptTests.cs`
- Modify: `NoofLedger.slnx` — register the new test project in the `/tests/` folder

**Interfaces — produced, exact:**
```csharp
namespace Noof.Ledger.Ai;

public sealed class AnthropicOptions
{
    public string Model { get; init; } = "claude-haiku-4-5-20251001";
    public int MaxTokens { get; init; } = 2048;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);
}

public static class CategorizationSchema
{
    // The schema BODY only (type/additionalProperties/required/properties), as a JsonElement ready
    // for ChatResponseFormat.ForJsonSchema(...) (record_spending) or a raw-schema
    // AIFunctionDeclaration's JsonSchema (list_merchants). AnthropicCategorizer (Task 3) supplies
    // each one's name and description when it builds the actual request — this type only ever
    // returns the schema body, never a name, a description, or an SDK type.
    public static JsonElement BuildRecordSpending(
        IReadOnlyList<CategoryOption> categories, IReadOnlyList<MerchantOption> merchantHints);

    public static JsonElement BuildListMerchants();
}

public static class CategorizationPrompt
{
    public const string System;

    public static string RenderCategories(IReadOnlyList<CategoryOption> categories);
    public static string RenderMerchantHints(IReadOnlyList<MerchantOption> merchantHints);
    public static string BuildUserTurn(
        string rawText, IReadOnlyList<CategoryOption> categories, IReadOnlyList<MerchantOption> merchantHints);
}
```
- Consumes (locked, from `Noof.Ledger.Application.Categorization`, shipped by an earlier 1B task — **not present in the repo yet** if this task is run out of order; if you hit "type or namespace `CategoryOption`/`MerchantOption` could not be found", that earlier task hasn't landed, this one didn't break anything): `CategoryOption(string Slug, string NameEn, string NameRu, string? ParentSlug)`, `MerchantOption(Guid Id, string DisplayName)` — both reproduced verbatim in the plan's "The contract" section.
- Consumes (locked, from `Noof.Ledger.Domain`, shipped in Phase 0): `CurrencyCode` — its five statics `Eur`, `Rsd`, `Usd`, `Rub`, `Kzt`, each exposing `.Value` as the three-letter code.

**Read first, before writing anything:** `src/Noof.Ledger.Domain/CurrencyCode.cs` (already read for this task — the five statics and their `.Value` property are exactly what feeds the `currency` enum); `Directory.Packages.props` and `src/Noof.Ledger.Ai/Noof.Ledger.Ai.csproj` (both read — `Noof.Ledger.Ai.csproj` currently has zero `PackageReference` items, only the two `ProjectReference`s to `Application` and `Domain`); `NoofLedger.slnx` (its `/tests/` folder is alphabetical — `Architecture.Tests`, `Domain.Tests`, `Host.Tests`, `Persistence.Tests`, `Telegram.Tests`, `TestKit` — and `Noof.Ledger.Ai.Tests` sorts *before* `Architecture.Tests` ("Ai" < "Ar"), so it becomes the new first entry, not the last).

**Locked design decisions** (the contract leaves these open; once this task's tests are green they are load-bearing for Task 3, which is the schema's only other consumer):

1. **`CategorizationSchema` returns a `JsonElement`, not a `Tool`, not a dictionary.** `BuildRecordSpending`/`BuildListMerchants` both return the literal `{"type": "object", ...}` shape, ready to be handed straight to `ChatResponseFormat.ForJsonSchema(schema, name, description)` or wrapped in a raw-schema `AIFunctionDeclaration`. This is a change from an earlier draft of this task, written against the SDK's native tool-use surface, which returned `IReadOnlyDictionary<string, JsonElement>` for `InputSchema.FromRawUnchecked(...)`. **The operator asked for the `Microsoft.Extensions.AI` layer instead** (`IChatClient`, proved by capturing the actual HTTP body against a stub handler — see Task 3's fact list) — `ChatResponseFormat.ForJsonSchema` and `AIFunctionDeclaration.JsonSchema` both take a single `JsonElement`, so the dictionary wrapper this method used to return no longer has a consumer. Neither method knows the string `"record_spending"` or `"list_merchants"` as a name, nor a top-level `"description"` — both are supplied by `AnthropicCategorizer` in Task 3, which is the only place SDK/abstraction types are touched. Keeping schema-building free of any SDK or abstraction type is what makes it testable with zero mocking, exactly as before.
2. **`known_merchant_id` is inserted between `category_slug` and `merchant_quote`, never appended last.** Built by composing an ordered `List<KeyValuePair<string, JsonNode?>>` before wrapping it in one `JsonObject`, specifically so a conditional `Add` in the middle of the list lands the property in the right position — appending after `merchant_quote` was built first would silently reorder it and still be schema-valid JSON, which is exactly the kind of drift a byte-shape test exists to catch.
3. **`known_merchant_id`'s enum values are `Guid.ToString()` in the default `"D"` format** (lowercase, hyphenated, no braces) — the same format `Guid.Parse` round-trips without a style argument, which is what `AnthropicCategorizer` will need to do with the model's answer.
4. **The two schemas use different top-level key orders on purpose.** `BuildRecordSpending`'s root and its line-item object both order keys `type, additionalProperties, required, properties`. `BuildListMerchants` orders them `type, additionalProperties, properties, required`. This is not a bug to "fix" for consistency: JSON object key order carries no semantic meaning to the API.
5. **Exact-shape tests compare parsed structure, not raw bytes.** Tests reserialize the returned `JsonElement` (via `.GetRawText()`, parsed back into a `JsonNode`) and compare with `JsonNode.DeepEquals` against a parsed expected literal: `DeepEquals` ignores member order within a JSON *object* but is order-sensitive within a JSON *array* (`enum`/`required` order is pinned on purpose). Separately, a same-inputs-twice string-equality test on `.GetRawText()` pins that repeated calls are internally consistent — the literal reading of "byte-identical output for identical input."
6. **`CategorizationPrompt.System` is a compile-time `const` raw string literal.** Because it is `const`, it is structurally incapable of containing a live category slug or a live merchant id — those can only ever appear in the *user* turn, built per-request by `BuildUserTurn`. Unchanged from the strict-tool-use draft: this part of the design never depended on which SDK surface calls it.
7. **No multilingual-prompting technique is invented.** Anthropic publishes no guidance for mixed Russian/English input as of this writing. The five `<example>` blocks in `System` are themselves bilingual — that is the entire strategy, recorded as a comment on `System` rather than as an invented technique.
8. **`CategorizationSchema`'s output round-trips through `JsonSerializer.Deserialize<JsonElement>` unchanged.** New relative to the strict-tool-use draft, because the schema is now handed to the API as a `JsonElement` directly rather than reassembled from a dictionary — a test asserts that serialising and re-deserialising the returned `JsonElement` produces the same structure, which is the property `ChatResponseFormat.ForJsonSchema` actually relies on at the call site.

---

#### Stage 0 — package, the options DTO, and the test project

- [x] **Step 1: Add the Anthropic package**

`Directory.Packages.props` — append to the first `<ItemGroup>` (not the `Testing`-labelled one), after the `Microsoft.Extensions.Hosting.Abstractions` line:

```xml
    <PackageVersion Include="Anthropic" Version="12.49.0" />
```

`src/Noof.Ledger.Ai/Noof.Ledger.Ai.csproj` — it currently has one `ItemGroup` (the two `ProjectReference`s) and zero packages. Add a `PackageReference` `ItemGroup` above it:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="Anthropic" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Noof.Ledger.Application\Noof.Ledger.Application.csproj" />
    <ProjectReference Include="..\Noof.Ledger.Domain\Noof.Ledger.Domain.csproj" />
  </ItemGroup>

</Project>
```

This is a package-only change with nothing to test-first — same category as Task 8 Stage 0's package addition in the 1A plan.

- [x] **Step 2: Build and confirm the package restores cleanly**

Run: `dotnet build src/Noof.Ledger.Ai/Noof.Ledger.Ai.csproj`
Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`. `Anthropic` 12.49.0 brings `Microsoft.Extensions.AI.Abstractions` 10.5.1 transitively (plus `System.Net.ServerSentEvents` and `System.Text.Json`) — none of the three are written as `<PackageReference>` lines in this `.csproj`, and none need to be; `NuGetAuditMode=all` does not block this restore.

- [x] **Step 3: Add `AnthropicOptions`**

This is a plain options DTO with no branching logic — exempt from test-first per `CLAUDE.md`'s testing rules ("Exempt: migrations, DTOs, `Program.cs` wiring"), the same exemption Task 1 of the 1A plan used for its three trailing enums.

Create `src/Noof.Ledger.Ai/AnthropicOptions.cs`:

```csharp
namespace Noof.Ledger.Ai;

public sealed class AnthropicOptions
{
    // Exactly one model property, deliberately. No AdviserModel, no EscalationModel, no
    // FallbackModel: making the type incapable of expressing a tier is stronger than choosing
    // not to configure one. A pinned dated id, not a floating alias, so a model change is a
    // commit rather than a surprise.
    public string Model { get; init; } = "claude-haiku-4-5-20251001";

    public int MaxTokens { get; init; } = 2048;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    // There is no Temperature property and there must not be one. Microsoft.Extensions.AI's
    // ChatOptions.Temperature exists and would compile, unlike the raw SDK's
    // MessageCreateParams.Temperature (which is [Obsolete] and a compile error under
    // TreatWarningsAsErrors) — but it is never set either. Determinism here comes from the
    // JSON-schema-constrained response format, not from a sampling parameter, and giving this
    // options type a Temperature property would invite someone to "tune" a call that is supposed
    // to be deterministic by construction.
}
```

- [x] **Step 4: Scaffold `Noof.Ledger.Ai.Tests`**

Create `tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="AwesomeAssertions" />
    <PackageReference Include="xunit.v3.mtp-v2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Noof.Ledger.Ai\Noof.Ledger.Ai.csproj" />
    <ProjectReference Include="..\..\src\Noof.Ledger.Application\Noof.Ledger.Application.csproj" />
  </ItemGroup>

</Project>
```

Create `tests/Noof.Ledger.Ai.Tests/xunit.runner.json` (identical to every other test project):

```json
{
    "$schema": "https://xunit.net/schema/current/xunit.runner.schema.json"
}
```

In `NoofLedger.slnx`, add the project to the `/tests/` folder as the **first** entry — `"Ai"` sorts before `"Architecture"`:

```xml
  <Folder Name="/tests/">
    <Project Path="tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj" />
    <Project Path="tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj" />
    <Project Path="tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj" />
    <Project Path="tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj" />
    <Project Path="tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj" />
    <Project Path="tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj" />
    <Project Path="tests/Noof.Ledger.TestKit/Noof.Ledger.TestKit.csproj" />
  </Folder>
```

> **This line is not optional.** `dotnet test --solution NoofLedger.slnx` only runs projects listed in `NoofLedger.slnx`. `tests/Noof.Ledger.E2E.Tests` exists on disk in this repository right now and is **not** in the solution file — its tests silently never run under `--solution`, and that trap has already been sprung once in this project. If this step is skipped, every later `dotnet test --solution` in this task (and in Task 3) reports success while running zero tests from this project.

- [x] **Step 5: Confirm the scaffold builds and wires into the solution**

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Expected: `Build succeeded`; the runner reports zero tests found. That is not a failure — there are no test classes yet — it only confirms the project compiles and its references resolve.

- [x] **Step 6: Commit the scaffold**

```bash
git add Directory.Packages.props src/Noof.Ledger.Ai/Noof.Ledger.Ai.csproj src/Noof.Ledger.Ai/AnthropicOptions.cs tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj tests/Noof.Ledger.Ai.Tests/xunit.runner.json NoofLedger.slnx
git commit -m "$(cat <<'EOF'
chore(ai): Anthropic package, AnthropicOptions, and the Ai.Tests project

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

#### Stage 1 — `CategorizationSchema`

- [x] **Step 7: Write the failing test file**

Create `tests/Noof.Ledger.Ai.Tests/CategorizationSchemaTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Noof.Ledger.Ai;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai.Tests;

public class CategorizationSchemaTests
{
    static readonly IReadOnlyList<CategoryOption> Categories =
    [
        new CategoryOption("groceries", "Groceries", "Продукты", null),
        new CategoryOption("food-drink", "Food & Drink", "Еда и напитки", null),
    ];

    static readonly IReadOnlyList<MerchantOption> NoHints = [];

    static readonly IReadOnlyList<MerchantOption> OneHint =
    [
        new MerchantOption(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Lidl"),
    ];

    [Fact]
    public void Record_spending_with_no_hints_matches_the_pinned_shape()
    {
        var schema = CategorizationSchema.BuildRecordSpending(Categories, NoHints);

        const string expected = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["items"],
          "properties": {
            "items": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["description", "amount_quote", "currency", "category_slug"],
                "properties": {
                  "description": { "type": "string", "description": "What was bought, as short plain text in the language of the message." },
                  "amount_quote": { "type": "string", "description": "The amount copied from the message character for character, exactly as written. Do not convert digits, do not add or remove separators, do not add a currency symbol, and never compute or sum anything. If the message does not state an amount for this line, do not produce the line." },
                  "currency": { "type": "string", "enum": ["EUR", "RSD", "USD", "RUB", "KZT"] },
                  "category_slug": { "type": "string", "enum": ["groceries", "food-drink"] },
                  "merchant_quote": { "type": "string", "description": "The merchant name copied from the message character for character. Only when a merchant is actually named and it is not one of the known merchants." }
                }
              }
            }
          }
        }
        """;

        JsonNode.DeepEquals(Reserialize(schema), JsonNode.Parse(expected)).Should().BeTrue();
    }

    [Fact]
    public void An_empty_hint_list_omits_known_merchant_id_entirely()
    {
        var schema = CategorizationSchema.BuildRecordSpending(Categories, NoHints);

        LineItemProperties(schema).TryGetProperty("known_merchant_id", out _).Should().BeFalse();
    }

    [Fact]
    public void With_hints_known_merchant_id_lands_between_category_slug_and_merchant_quote()
    {
        var schema = CategorizationSchema.BuildRecordSpending(Categories, OneHint);

        var names = LineItemProperties(schema).EnumerateObject().Select(p => p.Name);

        names.Should().ContainInOrder("category_slug", "known_merchant_id", "merchant_quote");
    }

    [Fact]
    public void Known_merchant_id_enum_is_exactly_the_hinted_guids()
    {
        var schema = CategorizationSchema.BuildRecordSpending(Categories, OneHint);

        var knownMerchantId = LineItemProperties(schema).GetProperty("known_merchant_id");

        knownMerchantId.GetProperty("type").GetString().Should().Be("string");
        knownMerchantId.GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["11111111-1111-1111-1111-111111111111"]);
        knownMerchantId.GetProperty("description").GetString().Should().Be(
            "Set this only if the merchant in the message is one of the listed known merchants.");
    }

    [Fact]
    public void Category_slug_enum_is_exactly_the_offered_slugs_and_nothing_else()
    {
        var schema = CategorizationSchema.BuildRecordSpending(Categories, NoHints);

        var slugs = LineItemProperties(schema).GetProperty("category_slug").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString());

        slugs.Should().BeEquivalentTo(Categories.Select(c => c.Slug));
    }

    [Fact]
    public void Currency_enum_is_the_five_CurrencyCode_statics()
    {
        var schema = CategorizationSchema.BuildRecordSpending(Categories, NoHints);

        var currencies = LineItemProperties(schema).GetProperty("currency").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString());

        currencies.Should().BeEquivalentTo(["EUR", "RSD", "USD", "RUB", "KZT"]);
    }

    [Fact]
    public void List_merchants_matches_the_pinned_shape()
    {
        var schema = CategorizationSchema.BuildListMerchants();

        const string expected =
            """{ "type": "object", "additionalProperties": false, "properties": {}, "required": [] }""";

        JsonNode.DeepEquals(Reserialize(schema), JsonNode.Parse(expected)).Should().BeTrue();
    }

    [Fact]
    public void Additional_properties_false_appears_at_every_object_level_of_record_spending()
    {
        var json = CategorizationSchema.BuildRecordSpending(Categories, OneHint).GetRawText();

        CountOccurrences(json, "\"additionalProperties\":false").Should().Be(2);
    }

    [Fact]
    public void Additional_properties_false_appears_on_list_merchants()
    {
        var json = CategorizationSchema.BuildListMerchants().GetRawText();

        CountOccurrences(json, "\"additionalProperties\":false").Should().Be(1);
    }

    [Theory]
    [InlineData("minLength")]
    [InlineData("maxLength")]
    [InlineData("\"minimum\"")]
    [InlineData("\"maximum\"")]
    [InlineData("multipleOf")]
    [InlineData("$ref")]
    [InlineData("pattern")]
    public void No_unsupported_json_schema_keyword_appears_anywhere(string forbidden)
    {
        var recordSpending = CategorizationSchema.BuildRecordSpending(Categories, OneHint).GetRawText();
        var listMerchants = CategorizationSchema.BuildListMerchants().GetRawText();

        recordSpending.Should().NotContain(forbidden);
        listMerchants.Should().NotContain(forbidden);
    }

    [Fact]
    public void Every_minItems_value_is_zero_or_one()
    {
        var json = CategorizationSchema.BuildRecordSpending(Categories, OneHint).GetRawText();

        foreach (Match match in Regex.Matches(json, "\"minItems\":(\\d+)"))
            match.Groups[1].Value.Should().BeOneOf("0", "1");
    }

    [Fact]
    public void Same_inputs_produce_byte_identical_json_twice()
    {
        var first = CategorizationSchema.BuildRecordSpending(Categories, OneHint).GetRawText();
        var second = CategorizationSchema.BuildRecordSpending([.. Categories], [.. OneHint]).GetRawText();

        first.Should().Be(second);
    }

    [Fact]
    public void Schema_round_trips_through_JsonElement_deserialization_unchanged()
    {
        // The schema is handed to ChatResponseFormat.ForJsonSchema as a JsonElement directly, not
        // reassembled from a dictionary — this pins the property that call site actually relies on.
        var schema = CategorizationSchema.BuildRecordSpending(Categories, OneHint);
        var roundTripped = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(schema));

        JsonNode.DeepEquals(JsonNode.Parse(roundTripped.GetRawText()), JsonNode.Parse(schema.GetRawText())).Should().BeTrue();
    }

    static JsonElement LineItemProperties(JsonElement schema) =>
        schema.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties");

    static JsonNode Reserialize(JsonElement schema) => JsonNode.Parse(schema.GetRawText())!;

    static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
```

- [x] **Step 8: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Expected: build error — `error CS0246: The type or namespace name 'CategorizationSchema' could not be found`.

- [x] **Step 9: Implement `CategorizationSchema`**

Create `src/Noof.Ledger.Ai/CategorizationSchema.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Ai;

// Raw JSON Schema, built as a JsonElement rather than through a typed builder or reflection:
// ChatResponseFormat.ForJsonSchema and a raw-schema AIFunctionDeclaration both need
// "additionalProperties": false at every object level, which is easiest to guarantee by building
// the JsonObject tree directly and controlling every key by hand.
public static class CategorizationSchema
{
    static readonly string[] CurrencyCodes =
    [
        CurrencyCode.Eur.Value,
        CurrencyCode.Rsd.Value,
        CurrencyCode.Usd.Value,
        CurrencyCode.Rub.Value,
        CurrencyCode.Kzt.Value,
    ];

    public static JsonElement BuildRecordSpending(
        IReadOnlyList<CategoryOption> categories, IReadOnlyList<MerchantOption> merchantHints)
    {
        var properties = new List<KeyValuePair<string, JsonNode?>>
        {
            new("description", new JsonObject
            {
                ["type"] = "string",
                ["description"] = "What was bought, as short plain text in the language of the message.",
            }),
            new("amount_quote", new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The amount copied from the message character for character, exactly as written. Do not convert digits, do not add or remove separators, do not add a currency symbol, and never compute or sum anything. If the message does not state an amount for this line, do not produce the line.",
            }),
            new("currency", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(CurrencyCodes.Select(code => (JsonNode)code).ToArray()),
            }),
            new("category_slug", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(categories.Select(c => (JsonNode)c.Slug).ToArray()),
            }),
        };

        if (merchantHints.Count > 0)
        {
            properties.Add(new("known_merchant_id", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(merchantHints.Select(m => (JsonNode)m.Id.ToString()).ToArray()),
                ["description"] = "Set this only if the merchant in the message is one of the listed known merchants.",
            }));
        }

        properties.Add(new("merchant_quote", new JsonObject
        {
            ["type"] = "string",
            ["description"] = "The merchant name copied from the message character for character. Only when a merchant is actually named and it is not one of the known merchants.",
        }));

        var lineItem = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("description", "amount_quote", "currency", "category_slug"),
            ["properties"] = new JsonObject(properties),
        };

        var root = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("items"),
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["items"] = lineItem,
                },
            },
        };

        return ToElement(root);
    }

    public static JsonElement BuildListMerchants()
    {
        var root = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray(),
        };

        return ToElement(root);
    }

    static JsonElement ToElement(JsonObject root)
    {
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }
}
```

- [x] **Step 10: Run the tests and watch them pass**

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Expected: PASS. The runner counts theory rows individually, so `CategorizationSchemaTests` reports **19** cases (12 facts plus the 7 rows of `No_unsupported_json_schema_keyword_appears_anywhere`) — one more than a strict-tool-use draft of this test would have had, because of the new round-trip fact. A smaller number means a test was dropped; a larger one means you added one — reconcile either way rather than assuming this plan's count is stale.

- [x] **Step 11: Commit**

```bash
git add src/Noof.Ledger.Ai/CategorizationSchema.cs tests/Noof.Ledger.Ai.Tests/CategorizationSchemaTests.cs
git commit -m "$(cat <<'EOF'
feat(ai): CategorizationSchema — raw JSON Schema as a JsonElement for record_spending and list_merchants

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

#### Stage 2 — `CategorizationPrompt`

- [x] **Step 12: Write the failing test file**

Create `tests/Noof.Ledger.Ai.Tests/CategorizationPromptTests.cs`:

```csharp
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Noof.Ledger.Ai;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai.Tests;

public class CategorizationPromptTests
{
    [Fact]
    public void System_prompt_instructs_verbatim_amount_quoting()
    {
        CategorizationPrompt.System.Should().Contain("character for character");
        CategorizationPrompt.System.Should().Contain("never compute, round or sum anything");
        CategorizationPrompt.System.Should().Contain("do not produce that line");
    }

    [Fact]
    public void System_prompt_wraps_examples_in_the_examples_tag()
    {
        CategorizationPrompt.System.Should().Contain("<examples>");
        CategorizationPrompt.System.Should().Contain("</examples>");
    }

    [Fact]
    public void System_prompt_has_between_three_and_five_examples()
    {
        var opening = Regex.Matches(CategorizationPrompt.System, "<example>").Count;
        var closing = Regex.Matches(CategorizationPrompt.System, "</example>").Count;

        opening.Should().BeInRange(3, 5);
        closing.Should().Be(opening);
    }

    [Fact]
    public void System_prompt_contains_no_secret_looking_text()
    {
        CategorizationPrompt.System.Should().NotContain("sk-ant");
        CategorizationPrompt.System.Should().NotMatchRegex("(?i)api[_-]?key");
        CategorizationPrompt.System.Should().NotMatchRegex("[A-Za-z0-9_-]{32,}");
    }

    [Fact]
    public void Render_categories_shows_both_names_and_the_parent_when_present()
    {
        IReadOnlyList<CategoryOption> categories =
        [
            new CategoryOption("groceries", "Groceries", "Продукты", null),
            new CategoryOption("dairy", "Dairy", "Молочные продукты", "groceries"),
        ];

        var rendered = CategorizationPrompt.RenderCategories(categories);

        rendered.Should().Contain("groceries: Groceries / Продукты");
        rendered.Should().Contain("dairy (under groceries): Dairy / Молочные продукты");
    }

    [Fact]
    public void Render_merchant_hints_lists_id_and_display_name()
    {
        IReadOnlyList<MerchantOption> hints =
            [new MerchantOption(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Lidl")];

        CategorizationPrompt.RenderMerchantHints(hints)
            .Should().Contain("11111111-1111-1111-1111-111111111111: Lidl");
    }

    [Fact]
    public void Render_merchant_hints_says_so_when_there_are_none()
    {
        CategorizationPrompt.RenderMerchantHints([]).Should().Contain("No known merchants");
    }

    [Fact]
    public void Build_user_turn_includes_the_raw_text_the_categories_and_the_hints()
    {
        IReadOnlyList<CategoryOption> categories = [new CategoryOption("groceries", "Groceries", "Продукты", null)];
        IReadOnlyList<MerchantOption> hints = [];

        var turn = CategorizationPrompt.BuildUserTurn("кофе 250 рсд", categories, hints);

        turn.Should().Contain("кофе 250 рсд");
        turn.Should().Contain("groceries: Groceries / Продукты");
        turn.Should().Contain("No known merchants");
    }
}
```

- [x] **Step 13: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Expected: build error — `error CS0246: The type or namespace name 'CategorizationPrompt' could not be found`.

- [x] **Step 14: Implement `CategorizationPrompt`**

Create `src/Noof.Ledger.Ai/CategorizationPrompt.cs`:

```csharp
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai;

public static class CategorizationPrompt
{
    // A role sentence "focuses Claude's behavior" — the categories, the hints and the message
    // itself are variable input and belong in the user turn built by BuildUserTurn, never in this
    // constant. Under Microsoft.Extensions.AI this string becomes ChatOptions.Instructions, which
    // the captured request body confirms lands as the request's "system" field — the same field
    // the raw SDK's MessageCreateParams.System would have used, so this text needed no rewriting
    // for the switch away from strict tool use.
    //
    // The closed set of category_slug values is enforced by CategorizationSchema's enum via the
    // response format, which is Anthropic's recommended mechanism for a closed label set. This
    // prompt exists to explain what each category MEANS so a line item lands under the right one;
    // it deliberately never repeats "choose only from this list" in prose, since a live slug could
    // not even appear here — System is a compile-time const, so it cannot embed data from the
    // current request.
    //
    // Messages arrive in Russian and English, sometimes both in one message. Anthropic publishes no
    // multilingual-prompting guidance as of this writing, so no technique is invented for it beyond
    // the bilingual examples below — that is the whole strategy: show, not instruct.
    public const string System = """
        You record spending from a personal expense message so it can be reviewed later. You read
        one message at a time and answer with the spending it describes, nothing more.

        Each category you are offered has a slug, an English name, a Russian name, and may have a
        parent category. Use the names to understand what each slug means — everyday food and
        drink, transport, household bills, and so on — so a line item lands under the slug whose
        meaning actually matches it. You do not choose the set of allowed slugs; your answer only
        accepts one of the slugs you were given, so pick by meaning and let the schema reject
        anything else.

        For every amount: copy it out of the message character for character, exactly as written.
        Never convert digits, never add or remove separators, never add a currency symbol, and never
        compute, round or sum anything — even when two lines obviously add up to a total the message
        also states. If the message does not clearly state an amount for a line, do not produce that
        line at all; an unreadable amount is not a zero and is not a guess.

        A message may name zero, one or several purchases. Produce one line item per purchase that
        has a stated amount. If a merchant is named and it matches one of the known merchants you
        were given, set known_merchant_id to that merchant's id instead of merchant_quote. If a
        merchant is named but matches no known merchant, copy its name into merchant_quote character
        for character, the same rule as amounts. If no merchant is named, leave both empty. If a
        merchant is named and you are unsure whether it is already known, you may call
        list_merchants to check the full list before answering.

        <examples>
        <example>
        Message: "кофе 250 рсд"
        Answer with one item: description "coffee", amount_quote "250", currency "RSD",
        category_slug the one whose meaning is everyday food and drink, no merchant.
        </example>
        <example>
        Message: "продукты 3400 рсд молоко хлеб сыр"
        Answer with one item: description "groceries: milk, bread, cheese", amount_quote "3400",
        currency "RSD", category_slug the one whose meaning is groceries, no merchant. There is one
        stated amount, so there is one line, even though three goods are named.
        </example>
        <example>
        Message: "taxi 1200 rsd"
        Answer with one item: description "taxi", amount_quote "1200", currency "RSD",
        category_slug the one whose meaning is transport, no merchant.
        </example>
        <example>
        Message: "Lidl 45.30 eur продукты, потом кофе 2.50 eur"
        Answer with two items. First: description "groceries", amount_quote "45.30", currency
        "EUR", category_slug the one whose meaning is groceries, merchant_quote "Lidl" (or
        known_merchant_id instead, if Lidl is already a known merchant). Second: description
        "coffee", amount_quote "2.50", currency "EUR", category_slug the one whose meaning is
        everyday food and drink, no merchant. Two purchases with two stated amounts make two lines.
        </example>
        <example>
        Message: "заняла у Маши 5000 рсд"
        Answer with no items at all. The message states an amount but describes a loan received,
        not a purchase — there is nothing here to categorise as spending.
        </example>
        </examples>
        """;

    public static string RenderCategories(IReadOnlyList<CategoryOption> categories) =>
        string.Join('\n', categories.Select(RenderCategory));

    public static string RenderMerchantHints(IReadOnlyList<MerchantOption> merchantHints) =>
        merchantHints.Count == 0
            ? "No known merchants are offered for this message."
            : string.Join('\n', merchantHints.Select(m => $"- {m.Id}: {m.DisplayName}"));

    public static string BuildUserTurn(
        string rawText, IReadOnlyList<CategoryOption> categories, IReadOnlyList<MerchantOption> merchantHints) =>
        $"""
        Message:
        {rawText}

        Categories:
        {RenderCategories(categories)}

        Known merchants:
        {RenderMerchantHints(merchantHints)}
        """;

    static string RenderCategory(CategoryOption category) =>
        category.ParentSlug is null
            ? $"- {category.Slug}: {category.NameEn} / {category.NameRu}"
            : $"- {category.Slug} (under {category.ParentSlug}): {category.NameEn} / {category.NameRu}";
}
```

> **Wording note.** `System`'s references to "Call record_spending" from a strict-tool-use draft were rewritten to "Answer with" throughout, and one sentence about `list_merchants` was added ("you may call list_merchants to check the full list before answering") — under the new approach `record_spending` is not a tool the model calls, it is the JSON answer itself, constrained by the response format; only `list_merchants` remains a real tool. `CategorizationPromptTests` does not pin the literal word "Call" or "Answer with" anywhere, so this rewrite does not change what the existing test assertions check.

- [x] **Step 15: Run the tests and watch them pass**

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Expected: PASS, all facts from Stage 1 and Stage 2 green.

- [x] **Step 16: Commit**

```bash
git add src/Noof.Ledger.Ai/CategorizationPrompt.cs tests/Noof.Ledger.Ai.Tests/CategorizationPromptTests.cs
git commit -m "$(cat <<'EOF'
feat(ai): CategorizationPrompt — the system prompt, examples, and per-request rendering

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

#### Stage 3 — confirm the whole solution sees the new project

- [x] **Step 17: Run the full solution suite**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green, and the assembly list in the output includes `Noof.Ledger.Ai.Tests` — if it does not appear at all, Step 4's `NoofLedger.slnx` edit did not land. No commit here; this step is verification only, and Steps 6/11/16 already committed everything this task changed.

---

### Task 3: The categoriser — one `IChatClient` call, one optional tool round trip, and what counts as terminal

**Files:**
- Create: `src/Noof.Ledger.Ai/AnthropicClientFactory.cs`
- Create: `src/Noof.Ledger.Ai/AnthropicCategorizer.cs`
- Create: `src/Noof.Ledger.Ai/AnthropicKeyProbe.cs`
- Create: `tests/Noof.Ledger.Ai.Tests/StubHttpMessageHandler.cs`
- Create: `tests/Noof.Ledger.Ai.Tests/AnthropicResponses.cs`
- Create: `tests/Noof.Ledger.Ai.Tests/AnthropicCategorizerTests.cs`
- Create: `tests/Noof.Ledger.Ai.Tests/AnthropicKeyProbeTests.cs`
- Modify (only if the check in Step 0 shows it's missing — do not touch it otherwise): `tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`

**Interfaces:**

*Consumes, in place from earlier tasks in this plan:*
- `ISecretStore`, `SecretResult`, `SecretState`, `SecretKeys.AnthropicApiKey` — `Noof.Ledger.Application.Secrets`, unchanged:
  ```csharp
  public interface ISecretStore
  {
      Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken);
      Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken);
      Task SetAsync(string key, string plaintext, CancellationToken cancellationToken);
      Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken);
  }

  public readonly record struct SecretResult(SecretState State, string? Value);
  public enum SecretState { Present = 0, Missing = 1, Unreadable = 2 }
  public static class SecretKeys { public const string AnthropicApiKey = "anthropic-api-key"; /* ... */ }
  ```
- `ICategorizer`, `CategorizationRequest`, `CategorizationProposal`, `ProposedLineItem`, `MerchantOption`, `ModelCallException`, `ModelFailureKind` — `Noof.Ledger.Application.Categorization`, exact shapes from the plan's contract section:
  ```csharp
  public interface ICategorizer
  {
      Task<CategorizationProposal> ProposeAsync(CategorizationRequest request, CancellationToken cancellationToken);
      Task<string> CanonicalizeMerchantAsync(
          string merchantText, IReadOnlyList<MerchantOption> knownMerchants, CancellationToken cancellationToken);
  }

  public sealed record CategorizationRequest(
      string RawText,
      IReadOnlyList<CategoryOption> Categories,
      IReadOnlyList<MerchantOption> MerchantHints,
      IReadOnlyList<MerchantOption> AllMerchants);

  public sealed record ProposedLineItem(
      string Description, string AmountQuote, string CurrencyCode, string CategorySlug,
      Guid? KnownMerchantId, string? MerchantQuote);

  public sealed record CategorizationProposal(IReadOnlyList<ProposedLineItem> Items);

  public enum ModelFailureKind { Transient = 0, Terminal = 1 }

  public sealed class ModelCallException(ModelFailureKind kind, string message, Exception? innerException = null)
      : Exception(message, innerException)
  {
      public ModelFailureKind Kind { get; } = kind;
  }
  ```
- `ISecretProbe`, `ProbeResult` — `Noof.Ledger.Application.Secrets`:
  ```csharp
  public readonly record struct ProbeResult(bool Ok, string Message);

  public interface ISecretProbe
  {
      string SecretKey { get; }
      Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken);
  }
  ```
- `AnthropicOptions` — `Noof.Ledger.Ai` (Task 2, no `Temperature` property): `Model`, `MaxTokens`, `Timeout`.
- `CategorizationSchema.BuildRecordSpending(...)`, `CategorizationSchema.BuildListMerchants()` — both return `JsonElement` (Task 2).
- `CategorizationPrompt.System` (const), `CategorizationPrompt.BuildUserTurn(rawText, categories, merchantHints)` (Task 2).
- `Anthropic` 12.49.0, already restorable, already `<PackageReference>`d by `src/Noof.Ledger.Ai/Noof.Ledger.Ai.csproj` (Task 2). Brings `Microsoft.Extensions.AI.Abstractions` 10.5.1 transitively — this task is the first to actually use it.

*Facts this task is built on, established by capturing the actual HTTP body the `Microsoft.Extensions.AI` layer sends against a stub handler — not read in documentation. Do not contradict them and do not re-verify them:*

```csharp
using Anthropic;
using Microsoft.Extensions.AI;

AnthropicClient raw = new() { ApiKey = keyFromDatabase, HttpClient = injected, MaxRetries = 0, Timeout = ... };
IChatClient chat = raw.AsIChatClient(options.Model);
```

1. A raw JSON Schema (a `JsonElement`) passes through `ChatResponseFormat.ForJsonSchema(schema, name, description)` verbatim, and through a subclass of the abstract `AIFunctionDeclaration` (which has an overridable `JsonElement JsonSchema`) for a tool declared from raw JSON. The captured body contained the schema unchanged, `additionalProperties: false`, `enum` and `minItems` included.
2. `ChatResponseFormat.ForJsonSchema(...)` lands as `"output_config": {"format": {"type": "json_schema", "schema": { ... }}}` — Anthropic's GA structured-outputs mode. This is what constrains the final answer and **replaces strict tool use**: the answer is the response text, and `output_config` is the response-side equivalent of a tool's `strict`. **No `strict` field is emitted and none is needed.**
3. Tools and `ResponseFormat` combine in one request: a capture with `Tools = [list_merchants]`, `ToolMode = Auto` and `ResponseFormat` produced exactly one tool, `tool_choice {"type":"auto"}`, and the `output_config` block together. `record_spending` is **not** a tool — only `list_merchants` is.
4. `ChatToolMode.RequireAny`/`ChatToolMode.RequireSpecific(name)` land as `tool_choice {"type":"any"}`/`{"type":"tool","name":...}` — not used by this task, recorded because `ChatToolMode.Auto` (used below) is the one that matters here.
5. **TRAP, verified:** declaring a tool in both `ChatOptions.Tools` and `ChatOptions.RawRepresentationFactory` sends it **twice** — a captured body had `tools` of length 2 for one tool. This task never touches `RawRepresentationFactory`; tools are declared through `Tools` only.
6. Exceptions are **not** wrapped by the layer. With `MaxRetries = 0`: HTTP 401 throws `Anthropic.Exceptions.AnthropicUnauthorizedException`, 429 throws `AnthropicRateLimitException`, 529 throws `Anthropic5xxException`, all deriving from `AnthropicApiException`, which exposes only `StatusCode` (`HttpStatusCode`; 529 shows as the numeric value), `ErrorType` (`Anthropic.Models.ErrorType`) and `Message` — `.Error`, `.Body`, `.Headers`, `.RequestID` do **not** exist.
7. The answer arrives as JSON **text**: `response.Messages` carry a `TextContent` whose `Text` is the schema-constrained JSON. A tool call arrives as `FunctionCallContent` with `CallId`, `Name`, `IDictionary<string, object?> Arguments`; it is answered with `FunctionResultContent(callId, result)` in a `ChatMessage` with `ChatRole.Tool`, then `GetResponseAsync` is called again with the accumulated `IList<ChatMessage>`.
8. `MessageCreateParams.Temperature` (the raw SDK surface) is `[Obsolete]`. `ChatOptions.Temperature` exists on the `Microsoft.Extensions.AI` surface and compiles, but it is never set here — determinism comes from the schema-constrained response, not a sampling parameter (same reasoning `AnthropicOptions.cs`'s comment gives).
9. `client.Models.List(cancellationToken)` does **not** compile — the first parameter is `ModelListParams?`; the call is `client.Models.List(null, cancellationToken)`. Unaffected by the `IChatClient` switch: `AnthropicKeyProbe` still calls the raw `AnthropicClient` directly, never a chat abstraction, because listing models has nothing to do with chat.

*Produces, for Task 7 (the worker) and Task 8 (the settings page) to consume:*
- `IAnthropicClientFactory` / `AnthropicClientFactory` — `Noof.Ledger.Ai`. **Unchanged in shape from a strict-tool-use draft**: it still only ever builds and returns the raw `Anthropic.AnthropicClient`. The conversion to `IChatClient` (`raw.AsIChatClient(options.Model)`) happens at each call site inside `AnthropicCategorizer`, not inside the factory — this is what keeps the factory's constructor and its one member identical regardless of which SDK surface a caller wants, and is the reason Task 7's `Program.cs` registration needs no change (see `## Ripple`).
- `AnthropicCategorizer : ICategorizer` — `Noof.Ledger.Ai`. **Not wired into DI anywhere by this task** — the same deferral pattern Task 7 of Phase 1A used for `EfJobQueue`.
- `AnthropicKeyProbe : ISecretProbe` — `Noof.Ledger.Ai`. Unchanged: still calls the raw client's `Models.List`.

---

## Locked design decisions

**1. A fresh `AnthropicClient` is built on every call, from whatever key is in `ISecretStore` right now.** No caching, no "rebuild only if the key changed" tracking — unaffected by the `IChatClient` switch, since the client being wrapped is the same raw `AnthropicClient` either way. Anthropic calls are one request/response each, `MaxRetries = 0`, no persistent connection state lives on `AnthropicClient` itself; the actual socket pooling is `HttpClient`'s job via `IHttpClientFactory`. Constructing a new `AnthropicClient` wrapper (and calling `.AsIChatClient(...)` on it) costs an object allocation, not a connection.

**2. The `"anthropic"` named `HttpClient` does *not* get `.RemoveAllLoggers()`.** The SDK sends the key as the `x-api-key` request header regardless of which abstraction sits on top, never in the URI or query string, and .NET's default HTTP client logging handlers do not log header contents at any built-in level without an explicit opt-in this code never adds. Default logging stays on for `"anthropic"` — unchanged reasoning from the strict-tool-use draft.

**3. The tool loop runs at most twice, period — never a third request under any response shape.** The first call offers `list_merchants` via `Tools = [listMerchantsTool]` and `ToolMode = ChatToolMode.Auto`, alongside `ResponseFormat` for `record_spending`. If it answers directly (JSON text matching the schema), that is the proposal. If it calls `list_merchants` instead, exactly one follow-up call is sent — **with no `Tools` at all**, only `ResponseFormat` — so nothing but a JSON answer is possible from the model on that second call; there is no forced-tool-choice mechanism needed because there is no tool left to choose. Whatever the second call returns — a JSON answer (success), a stray tool-use the model produced anyway despite none being offered, garbage, nothing — the method returns or throws; it never sends a third request. The code enforces this structurally: there is exactly one call site for the first request and exactly one call site for the follow-up, and neither is inside a loop. This is a change from a strict-tool-use draft's `ToolChoiceTool { Name = "record_spending" }`, which forced a specific *tool*; here `record_spending` was never a tool to force a choice onto, so omitting `Tools` entirely is what plays the same structural role.

**4. Status-code classification is a pure function of `(int)ex.StatusCode`, not `ErrorType`.** `AnthropicApiException` exposes only `StatusCode`, `ErrorType`, `Message` — unaffected by the `IChatClient` switch, since exceptions are not wrapped by the `Microsoft.Extensions.AI` layer at all (fact 6 above) and surface exactly as the raw SDK throws them. `ErrorType` is a string the API controls and could change wording without a status-code change; the status code is the contractual, versioned signal.

  | Status | Kind | Why |
  |---|---|---|
  | 400 Bad Request | Terminal | The request this code built is malformed; identical next time |
  | 401 Unauthorized | Terminal | Bad key |
  | 402 Payment Required | Terminal | No credit |
  | 403 Forbidden | Terminal | Key lacks access |
  | 404 Not Found | Terminal | Wrong model id / endpoint; identical next time |
  | 413 Payload Too Large | Terminal | This request is too big; retrying the same request is too |
  | 408 Request Timeout | Transient | Server-side timeout, may not recur |
  | 409 Conflict | Transient | Anthropic uses this for transient state conflicts, not a client bug |
  | 429 Too Many Requests | Transient | Rate limit; a later attempt has a different clock |
  | 5xx (500, 529 overloaded, etc.) | Transient | Anthropic's own outage, not this request's fault |
  | Anything else not listed | Transient | Defaults to "retry later" |

**5. `ProposedLineItem.CurrencyCode` is deserialised from the JSON field `"currency"`, not `"currency_code"`.** Unchanged: the answer's JSON still uses the field names `CategorizationSchema` declared, regardless of transport. Deserialisation goes through a private DTO with explicit `[JsonPropertyName]` attributes per field, not a naming-policy convention.

**6. `record_spending` is not a `Tool` — it is the response format.** Only `list_merchants` is declared through `ChatOptions.Tools`, as a raw-schema `AIFunctionDeclaration`. `record_spending`'s schema (`CategorizationSchema.BuildRecordSpending(...)`) is handed to `ChatResponseFormat.ForJsonSchema(schema, "record_spending", description)` on every call this method makes, first and follow-up alike, so the model is always constrained to answer in that shape the moment it isn't calling `list_merchants`.

**7. `canonicalize_merchant` is a separate call with its own tiny `ResponseFormat`, and offers no tools at all.** There is nothing to look up mid-call for canonicalisation — the full known-merchant list is already in the prompt — so unlike `ProposeAsync` there is only ever one request, never a follow-up.

**8. No `Temperature` is ever set, on either call.** `ChatOptions.Temperature` compiles (unlike the raw SDK's `[Obsolete] MessageCreateParams.Temperature`), which is exactly why this has to be a design decision instead of a compile error catching it: nothing stops a future edit from adding it. `AnthropicOptions` has no `Temperature` property for the same reason (Task 2).

**9. `list_merchants`'s answer is built from `request.AllMerchants`, never `request.MerchantHints`.** The hints are the handful the local scan already found and the model has already seen in the prompt; it only calls `list_merchants` when none of them fit, so answering with the same short list would make the tool pointless — and `ProposalVerification` would then reject any id it learned from the tool. `CategorizationRequest.AllMerchants` exists specifically to answer this tool.

**10. `RawRepresentationFactory` is never used to add a tool.** Per fact 5 above (the TRAP), the only place a tool is declared is `ChatOptions.Tools`. If a future change needs to pass an Anthropic-specific option not exposed by the abstraction, it must not go through `RawRepresentationFactory` for anything that duplicates a tool already in `Tools`.

**11. Both SDK calls take the `CancellationToken`.** `chat.GetResponseAsync(messages, options, cancellationToken)` compiles and threads the token through to the HTTP call. `client.Models.List(cancellationToken)` does **not** compile (`error CS1503`); the probe calls `client.Models.List(null, cancellationToken)`.

---

- [x] **Step 0: Confirm the ground this task builds on (read-only)**

Run, and read the output before writing anything:
```
grep -n "Anthropic" src/Noof.Ledger.Ai/Noof.Ledger.Ai.csproj Directory.Packages.props
grep -rn "class AnthropicOptions" src/Noof.Ledger.Ai
grep -n "PackageReference\|ProjectReference" tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj
grep -rn "JsonElement BuildRecordSpending\|JsonElement BuildListMerchants\|class CategorizationPrompt" src/Noof.Ledger.Ai
```
Expected: the `Anthropic` package reference and `AnthropicOptions` already exist (Task 2's job); `Noof.Ledger.Ai.Tests.csproj` already references `Noof.Ledger.Ai`, `AwesomeAssertions`, and `xunit.v3.mtp-v2`. If `CategorizationSchema`/`CategorizationPrompt` are not yet present with the `JsonElement`-returning shape, Task 2 hasn't landed yet — stop and wait for it; this task's Step 5 code will not compile without them. If the test csproj is missing a package reference this task needs, add only that missing line — do not otherwise touch a file another task owns.

- [x] **Step 1: Write the HTTP stub and the canned responses**

Create `tests/Noof.Ledger.Ai.Tests/StubHttpMessageHandler.cs`:
```csharp
using System.Net;
using System.Text;

namespace Noof.Ledger.Ai.Tests;

// Answers requests strictly in the order they were enqueued and records every request it saw
// (method, URI, headers, body) so a test can assert what the SDK actually sent, not just what it
// returned. Throwing on an unqueued request — rather than e.g. returning a default 200 — is
// deliberate: it turns "the code made one extra call it shouldn't have" into an immediate,
// unambiguous test failure instead of a silently-passing assertion on a truncated request list.
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    readonly Queue<(HttpStatusCode StatusCode, string Body)> responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public StubHttpMessageHandler Enqueue(HttpStatusCode statusCode, string body)
    {
        responses.Enqueue((statusCode, body));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers, body));

        if (responses.Count == 0)
            throw new InvalidOperationException(
                $"StubHttpMessageHandler received request #{Requests.Count} to {request.RequestUri} with no canned response queued. " +
                "This means the code under test sent more requests than the test expected.");

        var (statusCode, responseBody) = responses.Dequeue();
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        };
    }
}

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body);
```

Create `tests/Noof.Ledger.Ai.Tests/AnthropicResponses.cs`:
```csharp
namespace Noof.Ledger.Ai.Tests;

// Real response shapes, kept in one place so every test reads from the same ground truth instead
// of each hand-rolling its own JSON. Under the response-format approach, a successful answer is a
// plain assistant message whose one text block is the schema-constrained JSON — there is no
// "tool_use" block for record_spending or canonicalize_merchant, unlike list_merchants, which
// remains a genuine tool call in Anthropic's native shape.
public static class AnthropicResponses
{
    public const string RecordSpendingJsonAnswer = """
        {"id":"msg_01","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"text","text":"{\"items\":[{\"description\":\"Coffee\",\"amount_quote\":\"3.50\",\"currency\":\"EUR\",\"category_slug\":\"food-drink\"}]}"}],
         "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
        """;

    public const string ListMerchantsToolUse = """
        {"id":"msg_02","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_02","name":"list_merchants","input":{}}],
         "stop_reason":"tool_use","stop_sequence":null,
         "usage":{"input_tokens":80,"output_tokens":10}}
        """;

    public const string CanonicalizeMerchantJsonAnswer = """
        {"id":"msg_03","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"text","text":"{\"display_name\":\"Lidl\"}"}],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":40,"output_tokens":8}}
        """;

    public const string NoAnswerAtAll = """
        {"id":"msg_04","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":50,"output_tokens":0}}
        """;

    public const string AuthenticationError = """
        {"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}
        """;

    public static string GenericError(string type, string message) =>
        $$"""{"type":"error","error":{"type":"{{type}}","message":"{{message}}"}}""";
}
```

- [x] **Step 2: Write the failing tests**

Create `tests/Noof.Ledger.Ai.Tests/AnthropicCategorizerTests.cs`:
```csharp
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai.Tests;

public class AnthropicCategorizerTests
{
    static readonly IReadOnlyList<CategoryOption> Categories =
        [new CategoryOption("food-drink", "Food & Drink", "Еда и напитки", null)];

    static readonly IReadOnlyList<MerchantOption> NoMerchantHints = [];

    static (AnthropicCategorizer Categorizer, StubHttpMessageHandler Handler) Build(
        SecretState state = SecretState.Present, string? key = "sk-ant-test-key-do-not-log-me")
    {
        var handler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var secretStore = new StubSecretStore(state, key);
        var options = new AnthropicOptions { Model = "claude-haiku-4-5-20251001", MaxTokens = 2048, Timeout = TimeSpan.FromSeconds(90) };
        var clientFactory = new AnthropicClientFactory(secretStore, httpClient, options);
        return (new AnthropicCategorizer(clientFactory, options), handler);
    }

    [Fact]
    public async Task Returns_the_proposal_when_the_first_turn_answers_with_JSON_text()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingJsonAnswer);
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

        var proposal = await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        proposal.Items.Should().ContainSingle();
        proposal.Items[0].Description.Should().Be("Coffee");
        proposal.Items[0].AmountQuote.Should().Be("3.50");
        proposal.Items[0].CurrencyCode.Should().Be("EUR", "the JSON field is \"currency\", not \"currency_code\" — this is the mapping Locked Decision 5 exists for");
        proposal.Items[0].CategorySlug.Should().Be("food-drink");
        handler.Requests.Should().ContainSingle("a direct JSON answer on the first turn must not trigger a follow-up call");
    }

    [Fact]
    public async Task Sends_json_schema_output_config_and_exactly_one_tool_on_the_first_call()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingJsonAnswer);
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

        await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var sent = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        sent.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString().Should().Be("json_schema");
        var currencyEnum = sent.GetProperty("output_config").GetProperty("format").GetProperty("schema")
            .GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties")
            .GetProperty("currency").GetProperty("enum").EnumerateArray().Select(e => e.GetString());
        currencyEnum.Should().BeEquivalentTo(["EUR", "RSD", "USD", "RUB", "KZT"]);

        var tools = sent.GetProperty("tools");
        tools.GetArrayLength().Should().Be(1, "guarding against the RawRepresentationFactory trap: a tool must never appear twice");
        tools[0].GetProperty("name").GetString().Should().Be("list_merchants");
        sent.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("auto");

        sent.TryGetProperty("temperature", out _).Should().BeFalse("ChatOptions.Temperature must never be set");
    }

    [Fact]
    public async Task No_request_carries_an_anthropic_beta_header()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingJsonAnswer);
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

        await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        handler.Requests[0].Headers.Should().NotContainKey("anthropic-beta",
            "structured outputs and tool use are GA — no beta header anywhere");
    }

    [Fact]
    public async Task Answers_list_merchants_then_sends_one_follow_up_that_offers_no_tools()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.ListMerchantsToolUse);
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.RecordSpendingJsonAnswer);
        var hints = new List<MerchantOption> { new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Lidl") };
        var all = new List<MerchantOption>
        {
            new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Lidl"),
            new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Maxi"),
        };
        var request = new CategorizationRequest("Lidl 3.50 EUR", Categories, hints, all);

        var proposal = await categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        proposal.Items.Should().ContainSingle();
        handler.Requests.Should().HaveCount(2, "the loop runs exactly once: one first call, one follow-up, never a third");

        var secondSent = JsonDocument.Parse(handler.Requests[1].Body).RootElement;
        secondSent.TryGetProperty("tools", out _).Should().BeFalse(
            "the follow-up call offers no tools at all, which is what structurally rules out a third list_merchants call");

        // The answer is the FULL directory. "Maxi" is deliberately absent from the hints, so a
        // regression that answers with request.MerchantHints instead of request.AllMerchants makes
        // this line fail - and nothing else in the suite would have noticed.
        handler.Requests[1].Body.Should().Contain("Maxi");
    }

    [Fact]
    public async Task A_model_that_asks_for_the_merchant_list_twice_never_gets_a_third_request()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.ListMerchantsToolUse);
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.ListMerchantsToolUse);
        var request = new CategorizationRequest("Lidl 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Transient);
        handler.Requests.Should().HaveCount(2,
            "exactly two requests were queued and consumed; a third would have hit StubHttpMessageHandler's empty-queue guard and thrown InvalidOperationException instead of ModelCallException");
    }

    [Fact]
    public async Task Neither_JSON_nor_a_known_tool_call_on_the_first_turn_is_a_transient_failure()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.NoAnswerAtAll);
        var request = new CategorizationRequest("what is this", Categories, NoMerchantHints, NoMerchantHints);

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Transient);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ModelFailureKind.Terminal)]
    [InlineData(HttpStatusCode.Unauthorized, ModelFailureKind.Terminal)]
    [InlineData(HttpStatusCode.PaymentRequired, ModelFailureKind.Terminal)]
    [InlineData(HttpStatusCode.Forbidden, ModelFailureKind.Terminal)]
    [InlineData(HttpStatusCode.NotFound, ModelFailureKind.Terminal)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, ModelFailureKind.Terminal)]
    [InlineData(HttpStatusCode.RequestTimeout, ModelFailureKind.Transient)]
    [InlineData(HttpStatusCode.Conflict, ModelFailureKind.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, ModelFailureKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, ModelFailureKind.Transient)]
    [InlineData((HttpStatusCode)529, ModelFailureKind.Transient)]
    [InlineData((HttpStatusCode)599, ModelFailureKind.Transient)]
    public async Task Classifies_every_Anthropic_status_code_per_the_locked_table(HttpStatusCode statusCode, ModelFailureKind expectedKind)
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(statusCode, AnthropicResponses.GenericError("some_error", "boom"));
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(expectedKind, $"status {(int)statusCode} must classify as {expectedKind}");
    }

    [Fact]
    public async Task A_missing_key_is_a_terminal_failure_without_ever_calling_the_network()
    {
        var (categorizer, handler) = Build(state: SecretState.Missing, key: null);
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Terminal);
        handler.Requests.Should().BeEmpty("a missing key must fail before any HTTP call is attempted");
    }

    [Fact]
    public async Task An_unreadable_key_is_a_terminal_failure_without_ever_calling_the_network()
    {
        var (categorizer, handler) = Build(state: SecretState.Unreadable, key: null);
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ModelCallException>();
        assertion.Which.Kind.Should().Be(ModelFailureKind.Terminal);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_api_key_never_appears_anywhere_in_the_thrown_exceptions_ToString()
    {
        const string secretKeyText = "sk-ant-VERY-SECRET-DO-NOT-LEAK-abc123";
        var (categorizer, handler) = Build(key: secretKeyText);
        handler.Enqueue(HttpStatusCode.Unauthorized, AnthropicResponses.AuthenticationError);
        var request = new CategorizationRequest("Coffee 3.50 EUR", Categories, NoMerchantHints, NoMerchantHints);

        var act = () => categorizer.ProposeAsync(request, TestContext.Current.CancellationToken);
        var assertion = await act.Should().ThrowAsync<ModelCallException>();

        assertion.Which.ToString().Should().NotContain(secretKeyText);
        (assertion.Which.InnerException?.ToString() ?? "").Should().NotContain(secretKeyText);
        handler.Requests[0].Headers.GetValueOrDefault("x-api-key", "").Should().Be(secretKeyText,
            "confirms the key really was sent as a header, so the ToString() assertions above are actually exercising the leak path, not skipping it");
    }

    [Fact]
    public async Task CanonicalizeMerchantAsync_sends_a_single_call_with_json_schema_output_and_returns_the_display_name()
    {
        var (categorizer, handler) = Build();
        handler.Enqueue(HttpStatusCode.OK, AnthropicResponses.CanonicalizeMerchantJsonAnswer);
        var known = new List<MerchantOption> { new(Guid.NewGuid(), "Lidl Beograd") };

        var displayName = await categorizer.CanonicalizeMerchantAsync("lidl", known, TestContext.Current.CancellationToken);

        displayName.Should().Be("Lidl");
        handler.Requests.Should().ContainSingle();
        var sent = JsonDocument.Parse(handler.Requests[0].Body).RootElement;
        sent.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString().Should().Be("json_schema");
        sent.TryGetProperty("tools", out _).Should().BeFalse("canonicalisation never offers a tool - it is a plain structured-output call");
    }

    sealed class StubSecretStore(SecretState state, string? value) : ISecretStore
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new SecretResult(state, value));

        public Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not exercised by this test double");

        public Task SetAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not exercised by this test double");

        public Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not exercised by this test double");
    }
}
```

Create `tests/Noof.Ledger.Ai.Tests/AnthropicKeyProbeTests.cs`:
```csharp
using System.Net;
using AwesomeAssertions;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai.Tests;

public class AnthropicKeyProbeTests
{
    [Fact]
    public void SecretKey_is_the_Anthropic_api_key()
    {
        var probe = new AnthropicKeyProbe(new AnthropicClientFactory(
            new ThrowingSecretStore(), new HttpClient(new StubHttpMessageHandler()), new AnthropicOptions()));

        probe.SecretKey.Should().Be(SecretKeys.AnthropicApiKey);
    }

    [Fact]
    public async Task A_working_key_probes_ok_and_costs_no_tokens()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"data":[{"id":"claude-haiku-4-5-20251001"}],"has_more":false}""");
        var factory = new AnthropicClientFactory(
            new StubSecretStore(SecretState.Present, "sk-ant-test"), new HttpClient(handler), new AnthropicOptions());
        var probe = new AnthropicKeyProbe(factory);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        JsonDocumentBodyShouldBeEmpty(handler.Requests[0].Body);
    }

    [Fact]
    public async Task A_bad_key_probes_not_ok_with_a_message_and_never_throws()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.Unauthorized, AnthropicResponses.AuthenticationError);
        var factory = new AnthropicClientFactory(
            new StubSecretStore(SecretState.Present, "sk-ant-bad"), new HttpClient(handler), new AnthropicOptions());
        var probe = new AnthropicKeyProbe(factory);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_missing_key_probes_not_ok_without_ever_calling_the_network()
    {
        var handler = new StubHttpMessageHandler();
        var factory = new AnthropicClientFactory(
            new StubSecretStore(SecretState.Missing, null), new HttpClient(handler), new AnthropicOptions());
        var probe = new AnthropicKeyProbe(factory);

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    static void JsonDocumentBodyShouldBeEmpty(string body) => body.Should().BeNullOrEmpty("GET /v1/models has no request body");

    sealed class StubSecretStore(SecretState state, string? value) : ISecretStore
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new SecretResult(state, value));
        public Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task SetAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    sealed class ThrowingSecretStore : ISecretStore
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException("SecretKey is a property read; it must not touch the store at all");
        public Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task SetAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Expected: build errors — `AnthropicClientFactory`, `AnthropicCategorizer`, `AnthropicKeyProbe` do not exist yet.

- [x] **Step 3: Confirm the failure is the one you expect**

Same command as Step 2. If the error is a Postgres/network error or anything other than "type or namespace not found" for the three new Ai types, stop and fix that first. This test project touches no database and no network by design.

- [x] **Step 4: `AnthropicClientFactory`**

Create `src/Noof.Ledger.Ai/AnthropicClientFactory.cs`:
```csharp
using Anthropic;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai;

public interface IAnthropicClientFactory
{
    // Never caches; always returns the raw client. Callers that want an IChatClient call
    // raw.AsIChatClient(options.Model) themselves — AnthropicKeyProbe needs the raw client
    // (Models.List), so the factory cannot commit to one abstraction for every caller.
    Task<AnthropicClient> CreateAsync(CancellationToken cancellationToken);
}

public sealed class AnthropicClientFactory(
    ISecretStore secretStore, HttpClient httpClient, AnthropicOptions options) : IAnthropicClientFactory
{
    public async Task<AnthropicClient> CreateAsync(CancellationToken cancellationToken)
    {
        var secret = await secretStore.GetAsync(SecretKeys.AnthropicApiKey, cancellationToken);

        return secret.State switch
        {
            SecretState.Present => new AnthropicClient
            {
                ApiKey = secret.Value!,
                HttpClient = httpClient,
                MaxRetries = 0,
                Timeout = options.Timeout,
            },
            SecretState.Missing => throw new ModelCallException(
                ModelFailureKind.Terminal,
                "No Anthropic API key is configured. The worker is expected to check this before claiming work; " +
                "reaching this path means that check was skipped or the key was cleared mid-job."),
            SecretState.Unreadable => throw new ModelCallException(
                ModelFailureKind.Terminal,
                "The stored Anthropic API key could not be decrypted."),
            _ => throw new InvalidOperationException($"Unhandled {nameof(SecretState)} value: {secret.State}."),
        };
    }
}
```

- [x] **Step 5: `AnthropicCategorizer`**

Create `src/Noof.Ledger.Ai/AnthropicCategorizer.cs`:
```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai;

public sealed class AnthropicCategorizer(IAnthropicClientFactory clientFactory, AnthropicOptions options) : ICategorizer
{
    const string RecordSpendingName = "record_spending";
    const string RecordSpendingDescription = "Record every distinct spending line item found in the message.";

    const string ListMerchantsName = "list_merchants";
    const string ListMerchantsDescription =
        "List every merchant this ledger already knows, with the id to use for each. " +
        "Call this only if the message names a merchant that is not in the known merchants given to you, " +
        "and you want to check whether it is already known under a different spelling.";

    const string CanonicalizeMerchantName = "canonicalize_merchant";
    const string CanonicalizeMerchantDescription = "The display name to store for this merchant.";

    static readonly JsonElement CanonicalizeMerchantSchema = BuildCanonicalizeMerchantSchema();

    public async Task<CategorizationProposal> ProposeAsync(CategorizationRequest request, CancellationToken cancellationToken)
    {
        var raw = await clientFactory.CreateAsync(cancellationToken);
        var chat = raw.AsIChatClient(options.Model);

        // System is the SAME across turns; per-request data - categories, hints - lives only in the
        // user turn, built by CategorizationPrompt.BuildUserTurn. Instructions lands as the request's
        // "system" per the captured HTTP body (fact 1/2 above).
        var userTurn = CategorizationPrompt.BuildUserTurn(request.RawText, request.Categories, request.MerchantHints);
        var recordSpendingSchema = CategorizationSchema.BuildRecordSpending(request.Categories, request.MerchantHints);
        var listMerchantsTool = new RawSchemaFunctionDeclaration(
            ListMerchantsName, ListMerchantsDescription, CategorizationSchema.BuildListMerchants());

        var messages = new List<ChatMessage> { new(ChatRole.User, userTurn) };

        var firstOptions = new ChatOptions
        {
            MaxOutputTokens = options.MaxTokens,
            Instructions = CategorizationPrompt.System,
            Tools = [listMerchantsTool],
            ToolMode = ChatToolMode.Auto,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(recordSpendingSchema, RecordSpendingName, RecordSpendingDescription),
        };

        var firstResponse = await CallAsync(chat, messages, firstOptions, cancellationToken);

        if (TryGetText(firstResponse, out var firstJson))
            return ToProposal(firstJson);

        if (!TryGetFunctionCall(firstResponse, ListMerchantsName, out var call))
        {
            throw new ModelCallException(
                ModelFailureKind.Transient,
                $"First turn produced neither a JSON answer nor a {ListMerchantsName} call (finish reason: {firstResponse.FinishReason}).");
        }

        // AllMerchants, not MerchantHints: the hints are the handful the local scan already found
        // and the model has already seen. It only calls this tool when none of them fit, so
        // answering with the same short list would make the tool pointless - which is exactly why
        // the contract carries AllMerchants as a second, prompt-free field (Locked design decision 9).
        messages.AddRange(firstResponse.Messages);
        messages.Add(new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent(call.CallId, JsonSerializer.Serialize(request.AllMerchants))]));

        // The loop runs exactly once: this is the ONLY follow-up request this method ever sends,
        // regardless of what it gets back. No tools are offered this time - that is what
        // structurally rules out a third request, rather than relying on the model to behave
        // (Locked design decision 3).
        var followUpOptions = new ChatOptions
        {
            MaxOutputTokens = options.MaxTokens,
            Instructions = CategorizationPrompt.System,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(recordSpendingSchema, RecordSpendingName, RecordSpendingDescription),
        };

        var followUpResponse = await CallAsync(chat, messages, followUpOptions, cancellationToken);

        if (TryGetText(followUpResponse, out var followUpJson))
            return ToProposal(followUpJson);

        throw new ModelCallException(
            ModelFailureKind.Transient,
            $"Second turn, after answering {ListMerchantsName}, still produced no JSON answer (finish reason: {followUpResponse.FinishReason}).");
    }

    public async Task<string> CanonicalizeMerchantAsync(
        string merchantText, IReadOnlyList<MerchantOption> knownMerchants, CancellationToken cancellationToken)
    {
        var raw = await clientFactory.CreateAsync(cancellationToken);
        var chat = raw.AsIChatClient(options.Model);

        var instructions =
            $"""
            Decide the display name to store for a merchant mentioned in a spending message. The
            message named: "{merchantText}". If it is clearly the same merchant as one already
            known, answer with THAT existing display name exactly, character for character.
            Otherwise answer with a short, tidy display name for the new merchant.

            Known merchants (id and display name, as JSON): {JsonSerializer.Serialize(knownMerchants)}
            """;

        var callOptions = new ChatOptions
        {
            MaxOutputTokens = 256,
            Instructions = instructions,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                CanonicalizeMerchantSchema, CanonicalizeMerchantName, CanonicalizeMerchantDescription),
        };

        var response = await CallAsync(chat, [new ChatMessage(ChatRole.User, merchantText)], callOptions, cancellationToken);

        if (!TryGetText(response, out var json))
        {
            throw new ModelCallException(
                ModelFailureKind.Transient,
                $"{CanonicalizeMerchantName} produced no JSON answer (finish reason: {response.FinishReason}).");
        }

        var payload = JsonSerializer.Deserialize<CanonicalizeMerchantPayload>(json);
        if (payload is null || string.IsNullOrWhiteSpace(payload.DisplayName))
            throw new ModelCallException(ModelFailureKind.Transient, $"{CanonicalizeMerchantName} returned an empty display_name.");

        return payload.DisplayName;
    }

    static async Task<ChatResponse> CallAsync(
        IChatClient chat, IList<ChatMessage> messages, ChatOptions callOptions, CancellationToken cancellationToken)
    {
        try
        {
            return await chat.GetResponseAsync(messages, callOptions, cancellationToken);
        }
        catch (AnthropicApiException ex)
        {
            throw new ModelCallException(Classify(ex.StatusCode), $"Anthropic call failed with status {(int)ex.StatusCode}.", ex);
        }
        catch (TaskCanceledException ex)
        {
            // AnthropicClient's own Timeout (from AnthropicOptions) fires as a TaskCanceledException,
            // the same type .NET uses for caller-requested cancellation. This layer cannot reliably
            // tell the two apart from inside a static helper with no access to the original
            // CancellationToken, so both are treated as transient here - a caller-driven cancellation
            // (host shutdown) unwinds through ModelCallException.Transient rather than
            // OperationCanceledException. The worker does not retry on host shutdown regardless,
            // because the process is going down, so this does not change behaviour where it matters.
            throw new ModelCallException(ModelFailureKind.Transient, "The Anthropic call timed out or was cancelled.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelCallException(ModelFailureKind.Transient, "A network failure occurred calling Anthropic.", ex);
        }
    }

    static ModelFailureKind Classify(HttpStatusCode statusCode) => (int)statusCode switch
    {
        400 or 401 or 402 or 403 or 404 or 413 => ModelFailureKind.Terminal,
        _ => ModelFailureKind.Transient,
    };

    static bool TryGetText(ChatResponse response, out string text)
    {
        var builder = new StringBuilder();
        foreach (var message in response.Messages)
        foreach (var content in message.Contents)
        {
            if (content is TextContent textContent)
                builder.Append(textContent.Text);
        }

        text = builder.ToString();
        return text.Length > 0;
    }

    static bool TryGetFunctionCall(ChatResponse response, string name, out FunctionCallContent call)
    {
        foreach (var message in response.Messages)
        foreach (var content in message.Contents)
        {
            if (content is FunctionCallContent candidate && candidate.Name == name)
            {
                call = candidate;
                return true;
            }
        }

        call = null!;
        return false;
    }

    static CategorizationProposal ToProposal(string json)
    {
        var payload = JsonSerializer.Deserialize<RecordSpendingPayload>(json);
        if (payload is null)
            throw new ModelCallException(ModelFailureKind.Transient, $"{RecordSpendingName} returned an empty payload.");

        return new CategorizationProposal([.. payload.Items.Select(i => i.ToProposedLineItem())]);
    }

    static JsonElement BuildCanonicalizeMerchantSchema()
    {
        const string raw = """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["display_name"],
              "properties": {
                "display_name": { "type": "string", "description": "The display name to store for this merchant." }
              }
            }
            """;
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    // AIFunctionDeclaration is abstract in Microsoft.Extensions.AI.Abstractions; this is the minimal
    // subclass needed to declare a tool from a raw JSON Schema instead of one generated by
    // reflection. Declaration-only: it is never asked to invoke anything - FunctionCallContent is
    // handled by hand in ProposeAsync's tool loop instead. See ## CONTRACT GAP: the exact base
    // member list (whether Name/Description/JsonSchema are abstract vs. virtual, and whether a base
    // constructor exists) is assumed from the fact that JsonSchema is "abstract ... overridable",
    // not itself captured, and must be checked against the compiled package.
    sealed class RawSchemaFunctionDeclaration : AIFunctionDeclaration
    {
        public RawSchemaFunctionDeclaration(string name, string description, JsonElement schema)
        {
            Name = name;
            Description = description;
            JsonSchema = schema;
        }

        public override string Name { get; }
        public override string Description { get; }
        public override JsonElement JsonSchema { get; }
    }

    // Deserialisation-only DTOs, private to this file: the JSON field names the tool schema
    // promises ("currency", not "currency_code") do not all match ProposedLineItem's C# property
    // names, so this maps explicitly, field by field, rather than trusting a naming-policy
    // convention that is wrong for exactly one field.
    sealed record RecordSpendingPayload([property: JsonPropertyName("items")] IReadOnlyList<ProposedLineItemDto> Items);

    sealed record ProposedLineItemDto(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("amount_quote")] string AmountQuote,
        [property: JsonPropertyName("currency")] string Currency,
        [property: JsonPropertyName("category_slug")] string CategorySlug,
        [property: JsonPropertyName("known_merchant_id")] string? KnownMerchantId,
        [property: JsonPropertyName("merchant_quote")] string? MerchantQuote)
    {
        public ProposedLineItem ToProposedLineItem() => new(
            Description, AmountQuote, Currency, CategorySlug,
            string.IsNullOrEmpty(KnownMerchantId) ? null : Guid.Parse(KnownMerchantId), MerchantQuote);
    }

    sealed record CanonicalizeMerchantPayload([property: JsonPropertyName("display_name")] string DisplayName);
}
```

> **This shape follows the captured HTTP body (facts 1–7 above), not a spike against the compiled `Microsoft.Extensions.AI.Abstractions` types themselves.** Member names for `ChatResponse` (`Messages`, `FinishReason`), `ChatMessage` (`Contents`, the `(ChatRole, IList<AIContent>)` constructor), and `AIFunctionDeclaration`'s exact override surface are standard shapes for that package version but were not individually captured the way the HTTP body was. If any of these fail to compile, that is exactly the signal to fix here, against the real package, not by changing what the tests expect — see `## CONTRACT GAP`.

- [x] **Step 6: Run the tests**

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Expected: still red — `AnthropicKeyProbe` does not exist yet (Step 2's `AnthropicKeyProbeTests.cs` references it). Every `AnthropicCategorizerTests` fact should now compile and, if Step 5's assumed member shapes compile as written, pass. If a member name is wrong, this is exactly the signal the callout above describes — resolve it against the compiled package before moving on, not by changing test expectations.

- [x] **Step 7: `AnthropicKeyProbe`**

Create `src/Noof.Ledger.Ai/AnthropicKeyProbe.cs`:
```csharp
using Anthropic.Exceptions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai;

// GET /v1/models costs no tokens — this is the only network call a "Test" button in the settings
// page (Task 8) is allowed to trigger. Uses the raw AnthropicClient directly, never IChatClient:
// listing models has nothing to do with chat, and the SDK's own Models.List is the only surface
// for it either way.
public sealed class AnthropicKeyProbe(IAnthropicClientFactory clientFactory) : ISecretProbe
{
    public string SecretKey => SecretKeys.AnthropicApiKey;

    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = await clientFactory.CreateAsync(cancellationToken);
            // List(cancellationToken) does NOT compile - the first parameter is ModelListParams?.
            var page = await client.Models.List(null, cancellationToken);
            return new ProbeResult(true, $"Connected. {page.Items.Count} model(s) visible to this key.");
        }
        catch (ModelCallException ex)
        {
            return new ProbeResult(false, ex.Message);
        }
        catch (AnthropicApiException ex)
        {
            return new ProbeResult(false, $"Anthropic returned status {(int)ex.StatusCode}.");
        }
        catch (HttpRequestException)
        {
            return new ProbeResult(false, "Could not reach Anthropic: a network error occurred.");
        }
    }
}
```

- [x] **Step 8: Run the full Ai test project**

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Expected: all green — `AnthropicCategorizerTests` (11 facts: happy-path JSON answer, first-call shape incl. the trap-5 guard, no beta header, list_merchants-then-no-tools-follow-up, the double-list_merchants no-third-request guard, neither-JSON-nor-tool-transient, the 12-row status-code `Theory`, missing-key, unreadable-key, key-never-leaks, canonicalize-merchant) and `AnthropicKeyProbeTests` (4 facts).

Then run the full solution once to confirm nothing else broke: `dotnet test --solution NoofLedger.slnx`

- [x] **Step 9: Commit**

```bash
git add src/Noof.Ledger.Ai/AnthropicClientFactory.cs src/Noof.Ledger.Ai/AnthropicCategorizer.cs src/Noof.Ledger.Ai/AnthropicKeyProbe.cs tests/Noof.Ledger.Ai.Tests/StubHttpMessageHandler.cs tests/Noof.Ledger.Ai.Tests/AnthropicResponses.cs tests/Noof.Ledger.Ai.Tests/AnthropicCategorizerTests.cs tests/Noof.Ledger.Ai.Tests/AnthropicKeyProbeTests.cs
git commit -m "$(cat <<'EOF'
feat(ai): AnthropicCategorizer on Microsoft.Extensions.AI — response-format JSON with a one-shot merchant-list tool round trip

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```
(If Step 0 required adding a package reference to `tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`, add that file to the same commit.)

---

## CONTRACT GAP

1. **CLOSED (Task 3).** **`AIFunctionDeclaration`'s exact member list.** The established fact is "`AIFunctionDeclaration` is `abstract` with an overridable `JsonElement JsonSchema`" — proved by a captured HTTP body showing the schema passed through unchanged. This task's `RawSchemaFunctionDeclaration` additionally assumes `Name` and `Description` are each an overridable `string` property with no other required override, and that a plain parameterless-then-property-initialised subclass (no mandatory base constructor argument) compiles. If the real base type requires a constructor argument (e.g. a `protected AIFunctionDeclaration(string name)`), adjust `RawSchemaFunctionDeclaration`'s constructor accordingly — nothing else in this task depends on its internals beyond `Name`/`Description`/`JsonSchema` being readable back the way `AnthropicCategorizer` uses them. Confirmed by compiling `RawSchemaFunctionDeclaration` exactly as written against the real `Anthropic` 12.49.0 / `Microsoft.Extensions.AI.Abstractions` 10.5.1 packages: no base constructor argument is required, and `Name`/`Description`/`JsonSchema` are each an overridable property as assumed.
2. **CLOSED (Task 3).** **`ChatMessage`'s multi-content constructor and `ChatResponse`'s member names.** `new ChatMessage(ChatRole.Tool, [new FunctionResultContent(...)])`, `response.Messages` (`IList<ChatMessage>`), `message.Contents` (`IEnumerable<AIContent>`), and `response.FinishReason` are the standard shapes for this package version but were not individually captured by the stub-handler run the way the request-body facts (1–9 in this task's Interfaces section) were. If any of these compile under a different name (e.g. `StopReason` instead of `FinishReason`), fix the reference here rather than reshaping the test expectations, per Step 5's callout. Confirmed: `AnthropicCategorizer.cs` compiles unchanged against these exact member names, and the full `AnthropicCategorizerTests` suite (including the tool round trip) passes against them.
3. **CLOSED (Task 3).** **`ChatOptions.Tools`' element type.** Assumed to be `IList<AITool>?` (or an equivalent collection type a collection expression can target) with `AIFunctionDeclaration` deriving from `AITool`. If `Tools` instead requires a different collection type or `AIFunctionDeclaration` requires an adapter to become an `AITool`, Step 5's `Tools = [listMerchantsTool]` line is the only line that needs to change. Confirmed: `Tools = [listMerchantsTool]` compiles unchanged, and `Sends_json_schema_output_config_and_exactly_one_tool_on_the_first_call` verifies exactly one tool reaches the wire.

---

### Task 4: The live-model suite — opt-in, skipped by default

**Files:**
- Modify: `tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj` (add a `ProjectReference` to `Noof.Ledger.Application` if Task 2 has not already added one — this suite needs `ISecretStore`, `SecretResult`, `SecretState` and the `Categorization` contract types, all defined there)
- Create: `tests/Noof.Ledger.Ai.Tests/LiveModelGate.cs`
- Create: `tests/Noof.Ledger.Ai.Tests/LiveModelGateTests.cs`
- Create: `tests/Noof.Ledger.Ai.Tests/LiveModelTests.cs`

**Interfaces:**
- Consumes: `ICategorizer`, `CategorizationRequest`, `CategoryOption`, `MerchantOption`, `CategorizationProposal`, `ProposalVerification` (`Noof.Ledger.Application.Categorization`, Task 1); `ISecretStore`, `SecretResult`, `SecretState` (`Noof.Ledger.Application.Secrets`, Phase 0b); `ISecretProbe`, `ProbeResult` (`Noof.Ledger.Application.Secrets`, Task 1); `AnthropicOptions`, `AnthropicCategorizer` (`Noof.Ledger.Ai`, Task 2); `AnthropicKeyProbe` (`Noof.Ledger.Ai`, Task 3); the `Anthropic` SDK client (Task 2's dependency, 12.49.0).
- Produces: `LiveModelGate` — the single place in the repository that reads `NOOF_LEDGER_LIVE_ANTHROPIC_KEY`. Nothing outside this file may read that variable.

**Why this task exists.** Every other Ai test in this phase (Task 2, Task 3) runs against a fake `HttpMessageHandler` returning canned JSON. Canned JSON proves the C# side of the contract — schema shape, tool-loop plumbing, error classification — but it cannot prove the real model answers `amount_quote` with the literal substring instead of a normalised number, which is the one failure mode `QuotedAmount`/`ProposalVerification` exists to stop. This suite is the only place that asks the real model and checks.

**Locked design decisions:**

- **The key.** `NOOF_LEDGER_LIVE_ANTHROPIC_KEY`, read by `LiveModelGate` only. It is deliberately not `NOOF_LEDGER_ANTHROPIC_KEY` or anything that echoes the application's own naming — the application's key lives encrypted in the `app_secret` table and is entered through `/settings/secrets`; nothing under `src/` ever reads an environment variable for it, and this task must not add a second, weaker way to configure the same secret in production. `grep -rn "NOOF_LEDGER_LIVE_ANTHROPIC_KEY" src/` must return nothing, forever — that is what makes this test-only rather than a backdoor.
- **`.gitignore` already covers the realistic risk.** The mechanism here is "set it in the shell for one run" (Step 9), which never touches a file at all. If a developer instead chooses to persist it locally for convenience — a `.env` file, say — `.env` and `.env.*` are already ignored (`!.env.example` is the only exception). No new `.gitignore` entry is needed; nothing this task adds writes the key to disk.
- **Skip, never fail, when the key is absent.** `Assert.Skip(LiveModelGate.SkipMessage)` — the exact idiom `LoginTests.cs` and `SettingsSecretsTests.cs` already use for "no reachable PostgreSQL". `dotnet test --solution NoofLedger.slnx` on a machine with no key set must show these as **Skipped**, not absent and not failed, and the run must stay green.
- **The gate is a pure function under test, not a live environment read.** `LiveModelGate.TryGetApiKey` (reads `Environment.GetEnvironmentVariable`) is a one-line wrapper over `LiveModelGate.TryParse` (takes the raw string). `LiveModelGateTests` exercises `TryParse` directly so the guard test is deterministic regardless of what happens to be set in the process's real environment.
- **Properties, not exact strings.** The docs say identical inputs may produce different outputs from the same model. Every live assertion below checks a *property* — the quote occurs verbatim in the raw text, the item count matches the number of amounts, the slug is one of the ones offered — never an exact JSON payload or an exact category choice.
- **The category slug is never pinned.** `CategoryOption` slugs are operator-editable (decision P1-1, contract). A live test asserting `CategorySlug == "groceries"` becomes a false failure the day someone renames that category, so the grocery test asserts the amount quote and that the returned slug is a member of the offered set — never which one.
- **Cost.** Five of the seven tests below call `ICategorizer.ProposeAsync` against the real API; the other two call the key probe, which is a bare `GET /v1/models` and the fact table records that endpoint as costing no tokens. Each `ProposeAsync` call sends a short system prompt, five category options and one short message (well under 1,000 input tokens) and gets back a handful of JSON fields (well under 300 output tokens). At Haiku 4.5's published rates — $1 per million input tokens, $5 per million output tokens — five such calls cost on the order of a cent; call it under $0.05 for the whole suite with room to spare. This is stated so nobody hesitates to run it.

---

- [x] **Step 1: Confirm the test project can see `Noof.Ledger.Application`**

Open `tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`. If Task 2 already added a `ProjectReference` to `..\..\src\Noof.Ledger.Application\Noof.Ledger.Application.csproj`, do nothing. If it is missing, add it:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\Noof.Ledger.Ai\Noof.Ledger.Ai.csproj" />
  <ProjectReference Include="..\..\src\Noof.Ledger.Application\Noof.Ledger.Application.csproj" />
</ItemGroup>
```

Run: `dotnet build tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Expected: builds clean.

---

- [x] **Step 2: Write the failing guard test for the gate helper**

Create `tests/Noof.Ledger.Ai.Tests/LiveModelGateTests.cs`:

```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Ai.Tests;

// Not part of the live suite - this class runs on every machine, every time, with no key and no
// network. It is the proof that the opt-in mechanism itself cannot silently start spending money:
// if TryParse ever started treating an absent or blank variable as "found", this is what would
// turn red and stop it before it reached LiveModelTests.
public sealed class LiveModelGateTests
{
    [Fact]
    public void An_absent_variable_is_reported_as_not_found()
    {
        LiveModelGate.TryParse(null, out _).Should().BeFalse();
    }

    [Fact]
    public void A_blank_or_whitespace_variable_is_reported_as_not_found()
    {
        LiveModelGate.TryParse("   ", out _).Should().BeFalse();
    }

    [Fact]
    public void A_real_looking_value_is_reported_as_found_and_returned_unchanged()
    {
        LiveModelGate.TryParse("sk-ant-test-value", out var apiKey).Should().BeTrue();
        apiKey.Should().Be("sk-ant-test-value");
    }
}
```

- [x] **Step 3: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj --filter "LiveModelGateTests"`
Expected: BUILD FAILS — `CS0246: The type or namespace name 'LiveModelGate' could not be found`.

---

- [x] **Step 4: Write the gate helper**

Create `tests/Noof.Ledger.Ai.Tests/LiveModelGate.cs`:

```csharp
namespace Noof.Ledger.Ai.Tests;

// The ONLY place in the repository that reads NOOF_LEDGER_LIVE_ANTHROPIC_KEY. This is a
// test-only variable: nothing under src/ ever reads it, the application's own Anthropic key
// lives encrypted in app_secret and is entered through /settings/secrets, and this rule does not
// bend for tests - a test process just has no database of its own to read that key from. The
// name is deliberately unlike any application configuration key so it can never be mistaken for
// one, and it must never be written to a committed file.
static class LiveModelGate
{
    public const string EnvironmentVariableName = "NOOF_LEDGER_LIVE_ANTHROPIC_KEY";

    public static string SkipMessage { get; } =
        $"No live Anthropic key - set {EnvironmentVariableName} to run this suite " +
        "(see tests/Noof.Ledger.Ai.Tests/LiveModelTests.cs).";

    public static bool TryGetApiKey(out string apiKey) =>
        TryParse(Environment.GetEnvironmentVariable(EnvironmentVariableName), out apiKey);

    internal static bool TryParse(string? rawValue, out string apiKey)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            apiKey = "";
            return false;
        }

        apiKey = rawValue;
        return true;
    }
}
```

- [x] **Step 5: Run it and watch it pass**

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj --filter "LiveModelGateTests"`
Expected: PASS — 3 of 3.

---

- [x] **Step 6: Write the live suite**

Create `tests/Noof.Ledger.Ai.Tests/LiveModelTests.cs`:

```csharp
using Anthropic;
using AwesomeAssertions;
using Noof.Ledger.Ai;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Secrets;

namespace Noof.Ledger.Ai.Tests;

// Spends real money against the real Anthropic API. Every test starts by checking
// LiveModelGate.TryGetApiKey and calls Assert.Skip when it is false, so this class is silent and
// green in the default `dotnet test` run on a machine with no key set. See LiveModelGate for how
// the key is supplied, and LiveModelGateTests for the guard that keeps that property true.
public sealed class LiveModelTests
{
    static readonly IReadOnlyList<CategoryOption> OfferedCategories =
    [
        new CategoryOption("groceries", "Groceries", "Продукты", null),
        new CategoryOption("food-drink", "Food & Drink", "Еда и напитки", null),
        new CategoryOption("transport", "Transport", "Транспорт", null),
        new CategoryOption("shopping", "Shopping", "Покупки", null),
        new CategoryOption("other", "Other", "Прочее", null),
    ];

    static readonly IReadOnlyList<string> OfferedSlugs = [.. OfferedCategories.Select(c => c.Slug)];

    // Both the categoriser and the probe take IAnthropicClientFactory (Task 3), never a raw client:
    // the factory is what reads the key through ISecretStore and applies MaxRetries = 0. Building a
    // client here instead would test a client configured differently from the one that actually runs.
    static AnthropicClientFactory CreateFactory(string apiKey) =>
        new(new FixedSecretStore(apiKey), new HttpClient(), new AnthropicOptions());

    static AnthropicCategorizer CreateCategorizer(string apiKey) =>
        new(CreateFactory(apiKey), new AnthropicOptions());

    static CategorizationRequest Request(string rawText) =>
        new(rawText, OfferedCategories, [], []);

    [Fact]
    public async Task A_single_coffee_purchase_produces_one_line_item_with_the_literal_amount_and_currency()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        const string rawText = "кофе 250 рсд";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        var item = proposal.Items.Should().ContainSingle().Subject;
        item.AmountQuote.Should().Be("250");
        item.CurrencyCode.Should().Be("RSD");
    }

    [Fact]
    public async Task A_grocery_run_is_categorised_sensibly_with_the_amount_quote_pinned_not_the_category()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        const string rawText = "продукты 3400 рсд молоко хлеб сыр";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        // The category slug is pinned to "one of the categories we offered", not to a specific
        // slug - the list is operator-editable, and pinning "groceries" here would turn an
        // operator renaming a category into a false failure in this suite.
        var item = proposal.Items.Should().ContainSingle().Subject;
        item.AmountQuote.Should().Be("3400");
        OfferedSlugs.Should().Contain(item.CategorySlug);
    }

    [Fact]
    public async Task A_message_with_two_amounts_produces_two_line_items_whose_quotes_occur_verbatim()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        const string rawText = "такси 500 рсд, кофе 250 рсд";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        proposal.Items.Should().HaveCount(2);
        foreach (var item in proposal.Items)
            rawText.Should().Contain(item.AmountQuote);
    }

    [Fact]
    public async Task Every_amount_quote_the_model_returns_passes_verification_against_the_raw_text()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        // The gate test that matters most: this is what would catch the real model paraphrasing
        // or normalising a number - "500" becoming "500.00", a separator added or removed, a
        // currency symbol prepended. ProposalVerification.TryResolve is the only gate that stands
        // between a model response and a Money value; if the live model can defeat it here,
        // nothing else in this repository would ever notice.
        const string rawText = "такси 500 рсд, кофе 250 рсд, продукты 3400 рсд молоко хлеб";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        var resolved = ProposalVerification.TryResolve(
            rawText,
            proposal,
            OfferedSlugs,
            offeredMerchantIds: [],
            out var items,
            out var failure);

        resolved.Should().BeTrue(failure);
        items.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_named_merchant_returns_a_merchant_quote_that_occurs_in_the_message()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        // No merchant hints are offered (Request passes an empty list), so the schema does not
        // expose known_merchant_id at all - merchant_quote is the only way the model can name
        // this merchant.
        const string rawText = "кофе в Starbucks 300 рсд";
        var proposal = await CreateCategorizer(apiKey)
            .ProposeAsync(Request(rawText), TestContext.Current.CancellationToken);

        var item = proposal.Items.Should().ContainSingle().Subject;
        item.MerchantQuote.Should().NotBeNullOrEmpty();
        rawText.Should().Contain(item.MerchantQuote!);
    }

    [Fact]
    public async Task The_key_probe_reports_Ok_for_a_real_key()
    {
        if (!LiveModelGate.TryGetApiKey(out var apiKey))
            Assert.Skip(LiveModelGate.SkipMessage);

        var probe = new AnthropicKeyProbe(CreateFactory(apiKey));

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeTrue(result.Message);
    }

    [Fact]
    public async Task The_key_probe_reports_not_Ok_for_an_obviously_invalid_key()
    {
        if (!LiveModelGate.TryGetApiKey(out _))
            Assert.Skip(LiveModelGate.SkipMessage);

        // Deliberately does not use the real key from the environment - this exercises the
        // rejection path. GET /v1/models costs no tokens whether the key is valid or not, so this
        // carries no cost risk beyond one extra network round trip.
        var probe = new AnthropicKeyProbe(CreateFactory("sk-ant-obviously-invalid-0000000000000000000000000000"));

        var result = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        result.Ok.Should().BeFalse();
    }

    // A minimal, read-only ISecretStore standing in for the encrypted app_secret table: it hands
    // back whatever plaintext it was built with for any key asked of it. Nothing about the
    // "secrets live encrypted in the database" rule is being worked around by this - the probe
    // still only ever sees a value through ISecretStore.GetAsync, exactly as it does in
    // production; this fake just supplies a real value that came from the environment for this
    // suite, not a database, because a test process has no database of its own.
    sealed class FixedSecretStore(string plaintext) : ISecretStore
    {
        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new SecretResult(SecretState.Present, plaintext));

        public Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new SecretStatus(SecretState.Present, DateTimeOffset.UnixEpoch));

        public Task SetAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake is read-only - the live suite never writes a secret.");

        public Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This fake is read-only - the live suite never writes a secret.");
    }
}
```

---

- [x] **Step 7: Confirm it builds and skips cleanly with no key set**

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj --filter "LiveModelTests"`
Expected: BUILD succeeds; all 7 tests report **Skipped**, none report **Failed**. This is the property that keeps the default loop honest — a developer with no key configured must see skips, never failures, never a silent pass that ran nothing.

---

- [ ] **Step 8 (optional — costs a fraction of a cent, run at your own discretion): confirm the suite is actually green against the real model**

In PowerShell, for this session only:

```powershell
$env:NOOF_LEDGER_LIVE_ANTHROPIC_KEY = "<paste the real key here — never commit this>"
dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj --filter "LiveModelTests"
Remove-Item Env:\NOOF_LEDGER_LIVE_ANTHROPIC_KEY
```

Expected: PASS — 7 of 7. If a categorisation test fails, read the failure message before assuming the test is wrong — a failure here is either a genuinely bad model answer (rare, and exactly what this suite exists to catch) or this task's assumed constructor shapes for `AnthropicCategorizer`/`AnthropicKeyProbe` not matching what Task 2/Task 3 actually built (see `## CONTRACT GAP` at the end of this plan). The variable is removed immediately after so it cannot linger in this shell's environment or leak into an unrelated later command's output.

---

- [x] **Step 9: Confirm the whole solution stays green with no key at all**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: PASS/Skipped only, zero Failed. This is the property the task exists to guarantee: a suite that spends money must never be able to start doing so by accident.

---

- [x] **Step 10: Commit**

```
git add tests/Noof.Ledger.Ai.Tests/LiveModelGate.cs tests/Noof.Ledger.Ai.Tests/LiveModelGateTests.cs tests/Noof.Ledger.Ai.Tests/LiveModelTests.cs tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj
git commit -m "Add the opt-in live-model suite, skipped by default"
```

---

## CONTRACT GAP

This task was drafted before Task 3 (`AnthropicCategorizer`, `AnthropicClientFactory` and `AnthropicKeyProbe`) exists as code, so the following are assumptions, not verified facts, and the implementer reconciling all four tasks should check them:

1. ~~`AnthropicCategorizer`'s constructor.~~ **CLOSED** against Task 3 as drafted: `AnthropicCategorizer(IAnthropicClientFactory clientFactory, AnthropicOptions options)`. The helper above was corrected to build the real factory rather than a raw client.
2. ~~`AnthropicKeyProbe`'s constructor.~~ **CLOSED**: `AnthropicKeyProbe(IAnthropicClientFactory clientFactory)`. `FixedSecretStore` is still needed, one level deeper — it now feeds the factory, which is the thing that calls `ISecretStore.GetAsync`. That keeps the property the contract cares about: the only code that can reach a plaintext secret is the factory, never a page.
3. ~~The Anthropic SDK client type and constructor.~~ **CLOSED** by the compile spike: the type is `Anthropic.AnthropicClient` and it is built with an object initialiser, not a constructor argument — `new() { ApiKey = key, HttpClient = http, MaxRetries = 0, Timeout = ... }`. `new AnthropicClient(apiKey)` is not the shape. Task 3 owns the factory; this suite consumes it rather than constructing a client itself.
4. **`Noof.Ledger.Ai.Tests.csproj`'s existing references.** Task 2 creates the project with a reference to `Noof.Ledger.Ai`; `Noof.Ledger.Application` arrives transitively but this suite names types from it directly, so Step 1 adds it explicitly. Harmless if already present.

---

### Task 5: The three persistence ports — the tree, merchant identity, and the write

**Files:**
- Create: `src/Noof.Ledger.Persistence/Categorization/EfCategoryCatalog.cs`
- Create: `src/Noof.Ledger.Persistence/Categorization/EfMerchantDirectory.cs`
- Create: `src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/EfCategoryCatalogTests.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/EfMerchantDirectoryTests.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/EfCategorizationStoreTests.cs`

No `.csproj` or `Directory.Packages.props` edit is needed. `Noof.Ledger.Persistence.Tests` already reaches `Npgsql.EntityFrameworkCore.PostgreSQL` (and the bare `Npgsql` types — `NpgsqlParameter`, `PostgresException`, `NpgsqlConnection`) transitively through its `ProjectReference` to `Noof.Ledger.Persistence`, which is not `PrivateAssets`-restricted for that package. `PostgresFixture.cs` and `EfCaptureStoreTests.cs` already prove this by using `NpgsqlConnection`/`NpgsqlCommand`/`.UseNpgsql(...)` with no direct `Npgsql` `PackageReference` in the test project.

**Interfaces:**
- **Consumes (must already exist from Task 1 of this plan — not yet in this repo as of this plan's authoring; verified via `grep -rln "ICategoryCatalog\|IMerchantDirectory\|ICategorizationStore\|CategorizationContract" src/` returning nothing).** If you land on this task before Task 1 has shipped `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs`, `ICategoryCatalog.cs`, `IMerchantDirectory.cs` and `ICategorizationStore.cs`, stop and do Task 1 first — every signature below is reproduced from the plan's "The contract" section, not invented here:
  ```csharp
  namespace Noof.Ledger.Application.Categorization;

  public sealed record CategoryOption(string Slug, string NameEn, string NameRu, string? ParentSlug);
  public sealed record MerchantOption(Guid Id, string DisplayName);
  public sealed record CategorizedLineItem(string Description, Money Amount, Guid CategoryId, Guid? MerchantId);
  public sealed record CategorizationSubject(Guid TransactionId, string RawText, long TelegramChatId, int? BotMessageId, string WalletName);
  public sealed record CategoryEntry(Guid Id, string Slug, string NameEn, string NameRu, string? ParentSlug);
  public sealed record MerchantAliasEntry(string Folded, Guid MerchantId, string DisplayName);

  public interface ICategoryCatalog
  {
      Task<IReadOnlyList<CategoryEntry>> ActiveAsync(CancellationToken cancellationToken);
  }

  public interface IMerchantDirectory
  {
      Task<IReadOnlyList<MerchantAliasEntry>> AliasesAsync(CancellationToken cancellationToken);
      Task<IReadOnlyList<MerchantOption>> MerchantsAsync(CancellationToken cancellationToken);
      Task<Guid> LinkAliasAsync(string folded, string displayName, CancellationToken cancellationToken);
  }

  public interface ICategorizationStore
  {
      Task<CategorizationSubject?> GetSubjectAsync(Guid transactionId, CancellationToken cancellationToken);
      Task ApplyAsync(Guid transactionId, IReadOnlyList<CategorizedLineItem> items, CancellationToken cancellationToken);
      Task MarkFailedAsync(Guid transactionId, CancellationToken cancellationToken);
  }
  ```
  Also consumes, already in this repo and unchanged by this task: `LedgerDbContext` with `Categories`, `Merchants`, `MerchantAliases`, `Transactions`, `Wallets`, `LineItems`; the domain types `Category`, `Merchant`, `MerchantAlias`, `Transaction`, `Wallet`, `LineItem`, `CategorizationAuthority` (`None = 0, Model = 1, Rule = 2, User = 4`), `TransactionStatus` (`Captured = 0, Completed = 1, Failed = 2`), `MerchantKind`, `Money`, `CurrencyCode`; and the migration `20260921065115_AddCaptureModel`, whose real names this task depends on byte-for-byte (see the locked decisions below).
- **Produces**, for the DI-wiring/worker task that follows this one (the plan's Task 7): `EfCategoryCatalog(LedgerDbContext db) : ICategoryCatalog`, `EfMerchantDirectory(LedgerDbContext db, TimeProvider timeProvider) : IMerchantDirectory` and `EfCategorizationStore(LedgerDbContext db) : ICategorizationStore`, all in namespace `Noof.Ledger.Persistence.Categorization`. **None is wired into DI here** — the same deferral `EfJobQueue` and `EfCaptureStore` used in their own tasks.

**Locked design decisions:**

1. **`ActiveAsync` resolves the parent's slug with one `LEFT JOIN` query, not N+1.** `CategoryEntry.ParentSlug` is a `string?`, so the query is `categories AS c` (filtered to `is_active`) left-joined to `categories AS p` on `c.parent_id = p.id`, projecting `p == null ? null : p.Slug`. Written as LINQ query syntax with `join ... into ... from ... DefaultIfEmpty()`, this translates to a single `LEFT JOIN` in the generated SQL — no per-row follow-up query for the parent.

2. **The real constraint name for the alias race is `PK_merchant_aliases`.** Confirmed in `20260921065115_AddCaptureModel.cs`: `table.PrimaryKey("PK_merchant_aliases", x => x.folded);` — `folded` is the alias table's primary key, not a separate unique index, so the exception to catch is a primary-key violation on `PK_merchant_aliases`, exactly mirroring `EfCaptureStore.IsDuplicateCaptureViolation`'s `ConstraintName` check.

3. **`LinkAliasAsync` stages the `Merchant` and its `MerchantAlias` in one `SaveChangesAsync` call, and this is why no orphaned merchant row needs cleaning up.** EF Core wraps everything a single `SaveChangesAsync` call sends in one implicit database transaction. When the alias insert hits the `PK_merchant_aliases` violation, Postgres aborts that whole transaction — the merchant insert that was staged alongside it in the same call is rolled back with it. So the loser of the race writes *nothing*, not "writes a merchant that nothing points to." This is proven, not just asserted: `LinkAliasAsync_the_loser_of_a_race_returns_the_winners_id_and_leaves_no_orphaned_merchant` (Step 4) asserts `Merchants.CountAsync()` is `1` after the race, using the same held-open-transaction technique `EfCaptureStoreTests.Concurrent_CaptureAsync_calls_for_the_same_chat_and_message_let_exactly_one_caller_insert` already uses. (Had this task instead committed the `Merchant` row on its own before attempting the alias insert, an orphan *would* exist on the losing side — that two-step shape is deliberately avoided here specifically because it does not need the "is this acceptable?" question at all.)

4. **`ApplyAsync`'s precedence predicate is `categorized_by <= @modelAuthority` where `@modelAuthority = (int)CategorizationAuthority.Model` (`1`).** `None = 0` and `Model = 1` are deleted; `Rule = 2` and `User = 4` are not. This is the `DELETE`'s own `WHERE` clause, not an `if` wrapped around a broader delete — there is no code path that can apply a model result without this predicate.

5. **`ApplyAsync` uses one explicit `db.Database.BeginTransactionAsync()` spanning a raw-SQL `DELETE`, the new `LineItem` inserts, and the `Transaction.Status` flip, committed once at the end.** All three participate in the same ambient transaction automatically (EF enlists both `ExecuteSqlRawAsync` calls and `SaveChangesAsync` in whatever transaction is open on the `DbContext`). If anything throws before `tx.CommitAsync(...)` runs, the `await using` disposal of `tx` rolls the whole thing back — this is the same "dispose without commit rolls back" behaviour Npgsql/EF already rely on elsewhere in this codebase; Step 8's atomicity test proves it rather than trusting it.

6. **The atomicity test reuses `ThrowsBeforeCommitInterceptor`, already sitting in `tests/Noof.Ledger.Persistence.Tests/ThrowsBeforeCommitInterceptor.cs`** (added for `EfCaptureStoreTests.A_failure_before_commit_leaves_neither_row_behind`). It throws from `TransactionCommittingAsync`, i.e. after every statement has been sent to Postgres but before `COMMIT` — the strongest available proof that a multi-statement write (`DELETE` + inserts + `UPDATE`) is genuinely atomic, not just individually well-formed. No new interceptor class is written; it is `internal` to the same test project and namespace (`Noof.Ledger.Persistence.Tests`), so `EfCategorizationStoreTests` uses it directly.

7. **`EfCategorizationStore` does not length-guard `CategorizedLineItem.Description` against `line_items.description`'s `varchar(512)`.** Unlike `EfMerchantDirectory.LinkAliasAsync` (explicitly required to guard `folded`/`displayName` by this task's brief), nothing in this task's scope hands `EfCategorizationStore` a model-supplied string before verification — `CategorizedLineItem` is the *already-resolved* record `ProposalVerification` (a different task) produces. Guarding here as well would be a second, silently-diverging copy of a rule that belongs upstream, at the one place a model's raw text actually enters the pipeline. **CONTRACT GAP:** this plan's own "Verified facts" table states model-supplied strings must be "length-guarded before they reach EF... or `SaveChangesAsync` throws a `PostgresException`" but does not say which task owns that guard for `Description` specifically (it *is* explicit for `folded`/`display_name`, both guarded in decision 8 below). This task takes the position that the guard belongs to whichever task builds `ProposalVerification`/the worker, and treats the resulting `PostgresException` here as an acceptable, correctly-atomic failure mode — which is also exactly what Step 8's atomicity test exploits: it does *not* need a contrived over-long value, because `ThrowsBeforeCommitInterceptor` forces the same class of late failure deterministically.

8. **`LinkAliasAsync` guards both `folded` and `displayName` against 256 characters**, matching `merchant_aliases.folded varchar(256)` and `merchants.display_name varchar(256)` from the migration, in the same loud-rejection style as `EfJobQueue.RequireUtc` — an `ArgumentException` naming the parameter, thrown before any SQL is sent, rather than a `PostgresException` surfacing three layers down.

9. **A newly linked merchant gets `MerchantKind.Retail`.** The contract's `MerchantOption`/`LinkAliasAsync` carry no `Kind` input, and `Merchant.Kind` is a required, non-nullable enum, so *something* has to be chosen here. **CONTRACT GAP:** the contract does not say what. `Retail` is chosen as the most common case for a merchant recognised from free-text spending messages (groceries, retail purchases); `Service` and `ExchangeVenue` merchants, if ever needed, are a later correction, not a Phase-1B concern — the backlog already defers per-line-item correction UI. Flag this for the closing review; it is a real, if minor, design call this task made unilaterally.

10. **`GetSubjectAsync` joins `transactions` to `wallets` in one query** (`from t in ... join w in ... on t.WalletId equals w.Id ... select new CategorizationSubject(...)`) rather than loading the transaction and then querying the wallet — "in one round trip" per the contract's own comment on `CategorizationSubject`.

11. **`MarkFailedAsync` is a single raw `UPDATE transactions SET status = @status WHERE id = @transactionId`**, not a load-then-`SaveChangesAsync` round trip — mirroring `EfJobQueue.FailAsync`'s shape — so that "sets status to Failed and nothing else" is true by construction: there is no tracked entity in play that some future edit to this method could accidentally also mutate.

12. **Every read-back assertion after a write uses either `.AsNoTracking()` or `db.ChangeTracker.Clear()` first.** `ApplyAsync`'s raw-SQL `DELETE` is invisible to the change tracker — a `LineItem` entity added and saved by an earlier `ApplyAsync` call stays tracked as `Unchanged` even after a later call deletes its row directly in Postgres. `LineItemMoneyMappingTests` already hit an adjacent version of this defect once (a tracked entity's client-side value surviving a read-back that should have reflected the database's own rounding); Step 7's idempotency test calls out the same risk explicitly before its final read.

- [ ] **Step 1: Write the failing test for `EfCategoryCatalog`**

Create `tests/Noof.Ledger.Persistence.Tests/EfCategoryCatalogTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfCategoryCatalogTests(PostgresFixture fixture)
{
    static Category NewCategory(bool isActive, Guid? parentId = null) => new()
    {
        Id = Guid.NewGuid(),
        ParentId = parentId,
        Slug = $"test-{Guid.NewGuid():N}",
        NameEn = "Test category",
        NameRu = "Тестовая категория",
        IsActive = isActive,
    };

    [Fact]
    public async Task Active_returns_only_active_categories_with_the_parents_slug_resolved()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var parent = NewCategory(isActive: true);
        var child = NewCategory(isActive: true, parentId: parent.Id);
        var inactive = NewCategory(isActive: false);
        db.Categories.AddRange(parent, child, inactive);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var catalog = new EfCategoryCatalog(db);

        var active = await catalog.ActiveAsync(TestContext.Current.CancellationToken);

        active.Should().NotContain(c => c.Id == inactive.Id, "an inactive category must never be offered to the model");
        var parentEntry = active.Single(c => c.Id == parent.Id);
        parentEntry.ParentSlug.Should().BeNull("a top-level category has no parent");
        var childEntry = active.Single(c => c.Id == child.Id);
        childEntry.ParentSlug.Should().Be(parent.Slug, "the model is offered the parent's stable slug, not its Guid");
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: build error — `The type or namespace name 'Categorization' does not exist in the namespace 'Noof.Ledger.Persistence'` (from `using Noof.Ledger.Persistence.Categorization;`) and `The type or namespace name 'EfCategoryCatalog' could not be found`.

- [ ] **Step 2: Confirm the failure is the one you expect**

Re-run the Step 1 command and read the output. If it is anything other than "type not found" (a Postgres connection error, or an error inside the test file itself), stop and fix that first.

- [ ] **Step 3: Write `EfCategoryCatalog`**

Create `src/Noof.Ledger.Persistence/Categorization/EfCategoryCatalog.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Persistence.Categorization;

public sealed class EfCategoryCatalog(LedgerDbContext db) : ICategoryCatalog
{
    public async Task<IReadOnlyList<CategoryEntry>> ActiveAsync(CancellationToken cancellationToken) =>
        await (
            from c in db.Categories.AsNoTracking()
            where c.IsActive
            join p in db.Categories.AsNoTracking() on c.ParentId equals p.Id into parents
            from p in parents.DefaultIfEmpty()
            select new CategoryEntry(c.Id, c.Slug, c.NameEn, c.NameRu, p == null ? null : p.Slug))
            .ToListAsync(cancellationToken);
}
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: `EfCategoryCatalogTests` is green.

- [ ] **Step 4: Write the failing tests for `EfMerchantDirectory`**

Create `tests/Noof.Ledger.Persistence.Tests/EfMerchantDirectoryTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Noof.Ledger.Persistence.Categorization;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfMerchantDirectoryTests(PostgresFixture fixture)
{
    // A fresh Guid keeps every test's folded key unique against every other test's, and against
    // the fixed strings a future seed migration might add - a collision here would be a unique
    // constraint violation unrelated to what the test is actually checking.
    static string Folded(string seed) => $"TEST {seed} {Guid.NewGuid():N}".ToUpperInvariant();

    [Fact]
    public async Task LinkAliasAsync_creates_a_merchant_and_an_alias_keyed_on_the_folded_text()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var directory = new EfMerchantDirectory(db, time);
        var folded = Folded("LIDL");

        var merchantId = await directory.LinkAliasAsync(folded, "Lidl", TestContext.Current.CancellationToken);

        var merchant = await db.Merchants.AsNoTracking()
            .SingleAsync(m => m.Id == merchantId, TestContext.Current.CancellationToken);
        merchant.DisplayName.Should().Be("Lidl");
        var alias = await db.MerchantAliases.AsNoTracking()
            .SingleAsync(a => a.Folded == folded, TestContext.Current.CancellationToken);
        alias.MerchantId.Should().Be(merchantId);
        alias.CreatedAt.Should().Be(time.GetUtcNow());
    }

    [Fact]
    public async Task LinkAliasAsync_the_loser_of_a_race_returns_the_winners_id_and_leaves_no_orphaned_merchant()
    {
        await using var dbA = await fixture.CreateContextAsync();
        await dbA.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var optionsB = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(dbA.Database.GetConnectionString())
            .Options;
        await using var dbB = new LedgerDbContext(optionsB);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var directoryA = new EfMerchantDirectory(dbA, time);
        var directoryB = new EfMerchantDirectory(dbB, time);
        var folded = Folded("MAXI");

        await using var txA = await dbA.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var idA = await directoryA.LinkAliasAsync(folded, "Maxi (winner)", TestContext.Current.CancellationToken);

        var idBTask = directoryB.LinkAliasAsync(folded, "Maxi (loser)", TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(idBTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        finished.Should().NotBeSameAs(idBTask,
            "the loser's insert must block on the winner's uncommitted alias row, not race past it undetected");

        await txA.CommitAsync(TestContext.Current.CancellationToken);

        var idB = await idBTask;

        idB.Should().Be(idA, "the table decides identity - the loser must adopt the winner's merchant id, not mint its own");
        (await dbA.Merchants.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1,
            "merchant and alias were staged in one SaveChangesAsync call, so the loser's alias violation rolled its merchant insert back too - there is no orphan to clean up");
        var merchant = await dbA.Merchants.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        merchant.DisplayName.Should().Be("Maxi (winner)");
    }

    [Fact]
    public async Task Changing_an_existing_alias_fails_loudly_instead_of_silently_rewriting_history()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var folded = Folded("SPAR");
        await directory.LinkAliasAsync(folded, "Spar", TestContext.Current.CancellationToken);

        var act = async () => await db.Database.ExecuteSqlRawAsync(
            "UPDATE merchant_aliases SET merchant_id = @newMerchantId WHERE folded = @folded",
            [new NpgsqlParameter("newMerchantId", Guid.NewGuid()), new NpgsqlParameter("folded", folded)],
            TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<PostgresException>();
        assertion.WithMessage("*write-once*");
    }

    [Fact]
    public async Task LinkAliasAsync_rejects_a_folded_key_longer_than_256_characters()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var tooLong = new string('X', 257);

        var act = () => directory.LinkAliasAsync(tooLong, "Whatever", TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ArgumentException>();
        assertion.And.ParamName.Should().Be("folded");
    }

    [Fact]
    public async Task LinkAliasAsync_rejects_a_display_name_longer_than_256_characters()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var tooLong = new string('X', 257);

        var act = () => directory.LinkAliasAsync(Folded("OK"), tooLong, TestContext.Current.CancellationToken);

        var assertion = await act.Should().ThrowAsync<ArgumentException>();
        assertion.And.ParamName.Should().Be("displayName");
    }

    [Fact]
    public async Task AliasesAsync_returns_every_alias_with_its_merchants_display_name()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var folded = Folded("IDEA");
        var merchantId = await directory.LinkAliasAsync(folded, "Idea", TestContext.Current.CancellationToken);

        var aliases = await directory.AliasesAsync(TestContext.Current.CancellationToken);

        var entry = aliases.Single(a => a.Folded == folded);
        entry.MerchantId.Should().Be(merchantId);
        entry.DisplayName.Should().Be("Idea");
    }

    [Fact]
    public async Task MerchantsAsync_returns_every_merchant_as_an_option()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var directory = new EfMerchantDirectory(db, new FakeTimeProvider());
        var merchantId = await directory.LinkAliasAsync(Folded("DM"), "DM", TestContext.Current.CancellationToken);

        var merchants = await directory.MerchantsAsync(TestContext.Current.CancellationToken);

        merchants.Single(m => m.Id == merchantId).DisplayName.Should().Be("DM");
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: build error — `The type or namespace name 'EfMerchantDirectory' could not be found`.

- [ ] **Step 5: Confirm the failure is the one you expect**

Same command as Step 4. If the error is not "type not found," stop and fix that first.

- [ ] **Step 6: Write `EfMerchantDirectory`**

Create `src/Noof.Ledger.Persistence/Categorization/EfMerchantDirectory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Categorization;

public sealed class EfMerchantDirectory(LedgerDbContext db, TimeProvider timeProvider) : IMerchantDirectory
{
    public async Task<IReadOnlyList<MerchantAliasEntry>> AliasesAsync(CancellationToken cancellationToken) =>
        await (
            from a in db.MerchantAliases.AsNoTracking()
            join m in db.Merchants.AsNoTracking() on a.MerchantId equals m.Id
            select new MerchantAliasEntry(a.Folded, a.MerchantId, m.DisplayName))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MerchantOption>> MerchantsAsync(CancellationToken cancellationToken) =>
        await db.Merchants.AsNoTracking()
            .Select(m => new MerchantOption(m.Id, m.DisplayName))
            .ToListAsync(cancellationToken);

    public async Task<Guid> LinkAliasAsync(string folded, string displayName, CancellationToken cancellationToken)
    {
        RequireMaxLength(folded, 256, nameof(folded));
        RequireMaxLength(displayName, 256, nameof(displayName));

        var merchant = new Merchant
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName,
            Kind = MerchantKind.Retail,
        };
        var alias = new MerchantAlias
        {
            Folded = folded,
            MerchantId = merchant.Id,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        db.Merchants.Add(merchant);
        db.MerchantAliases.Add(alias);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return merchant.Id;
        }
        catch (DbUpdateException ex) when (IsDuplicateAliasViolation(ex))
        {
            // Both rows were staged in this same SaveChangesAsync call, which EF wraps in one
            // implicit transaction - the alias's primary-key violation rolled the merchant insert
            // back with it. There is no orphaned merchant row to clean up; the loser wrote nothing.
            db.Entry(merchant).State = EntityState.Detached;
            db.Entry(alias).State = EntityState.Detached;

            var winner = await db.MerchantAliases.AsNoTracking()
                .SingleAsync(a => a.Folded == folded, cancellationToken);
            return winner.MerchantId;
        }
    }

    static bool IsDuplicateAliasViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_merchant_aliases",
        };

    static void RequireMaxLength(string value, int maxLength, string paramName)
    {
        if (value.Length > maxLength)
            throw new ArgumentException($"must be at most {maxLength} characters, but was {value.Length}.", paramName);
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: `EfMerchantDirectoryTests` is green — all 7 facts.

> **Postgres blocks, then rejects, the loser — it does not let both inserts through.** `LinkAliasAsync_the_loser_of_a_race_returns_the_winners_id_and_leaves_no_orphaned_merchant` holds worker A's transaction open after its insert; worker B's insert against the same `folded` primary key cannot know yet whether A will commit or roll back, so Postgres blocks B until A resolves. Only once A commits does B's insert get evaluated and fail with the `PK_merchant_aliases` violation. This is why the test asserts `finished.Should().NotBeSameAs(idBTask, ...)` before committing A — proving B was genuinely blocked, not merely fast — exactly the same shape as `EfCaptureStoreTests.Concurrent_CaptureAsync_calls_for_the_same_chat_and_message_let_exactly_one_caller_insert`.

- [ ] **Step 7: Write the failing tests for `EfCategorizationStore`**

Create `tests/Noof.Ledger.Persistence.Tests/EfCategorizationStoreTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfCategorizationStoreTests(PostgresFixture fixture)
{
    static Wallet NewWallet(string name = "Cash") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Currency = CurrencyCode.Eur,
        IsDefault = false,
    };

    static Transaction NewTransaction(Guid walletId, long chatId = 1, int messageId = 1) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = walletId,
        RawText = "coffee 3.50, milk 1.20",
        Status = TransactionStatus.Captured,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = DateTimeOffset.UtcNow,
        TelegramChatId = chatId,
        TelegramMessageId = messageId,
        BotMessageId = 42,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    static Category NewCategory() => new()
    {
        Id = Guid.NewGuid(),
        Slug = $"test-{Guid.NewGuid():N}",
        NameEn = "Test category",
        NameRu = "Тестовая категория",
        IsActive = true,
    };

    static Merchant NewMerchant() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "Test merchant",
        Kind = MerchantKind.Retail,
    };

    static async Task<(Guid TransactionId, Guid CategoryId, Guid MerchantId)> SeedAsync(
        LedgerDbContext db, CancellationToken cancellationToken)
    {
        var wallet = NewWallet();
        var transaction = NewTransaction(wallet.Id);
        var category = NewCategory();
        var merchant = NewMerchant();
        db.Wallets.Add(wallet);
        db.Transactions.Add(transaction);
        db.Categories.Add(category);
        db.Merchants.Add(merchant);
        await db.SaveChangesAsync(cancellationToken);
        return (transaction.Id, category.Id, merchant.Id);
    }

    [Fact]
    public async Task ApplyAsync_writes_model_authored_lines_and_completes_the_transaction()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var (transactionId, categoryId, merchantId) = await SeedAsync(db, TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);
        var items = new[] { new CategorizedLineItem("Coffee", new Money(3.50m, CurrencyCode.Eur), categoryId, merchantId) };

        await store.ApplyAsync(transactionId, items, TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var transaction = await db.Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);
        transaction.Status.Should().Be(TransactionStatus.Completed);
        var lines = await db.LineItems.AsNoTracking()
            .Where(l => l.TransactionId == transactionId).ToListAsync(TestContext.Current.CancellationToken);
        lines.Should().ContainSingle();
        lines[0].Description.Should().Be("Coffee");
        lines[0].Amount.Should().Be(new Money(3.50m, CurrencyCode.Eur));
        lines[0].CategoryId.Should().Be(categoryId);
        lines[0].MerchantId.Should().Be(merchantId);
        lines[0].CategorizedBy.Should().Be(CategorizationAuthority.Model);
    }

    [Fact]
    public async Task ApplyAsync_replaces_a_previous_model_authored_line_rather_than_appending_to_it()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var (transactionId, categoryId, merchantId) = await SeedAsync(db, TestContext.Current.CancellationToken);
        db.LineItems.Add(new LineItem
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Description = "Stale model guess",
            Amount = new Money(9.99m, CurrencyCode.Eur),
            CategoryId = categoryId,
            CategorizedBy = CategorizationAuthority.Model,
            MerchantId = null,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);
        var items = new[] { new CategorizedLineItem("Coffee", new Money(3.50m, CurrencyCode.Eur), categoryId, merchantId) };

        await store.ApplyAsync(transactionId, items, TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var lines = await db.LineItems.AsNoTracking()
            .Where(l => l.TransactionId == transactionId).ToListAsync(TestContext.Current.CancellationToken);
        lines.Should().ContainSingle("the stale model line must be replaced, not accumulated alongside the new one");
        lines[0].Description.Should().Be("Coffee");
    }

    [Fact]
    public async Task ApplyAsync_leaves_a_user_authored_line_untouched()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var (transactionId, categoryId, merchantId) = await SeedAsync(db, TestContext.Current.CancellationToken);
        var userLineId = Guid.NewGuid();
        db.LineItems.Add(new LineItem
        {
            Id = userLineId,
            TransactionId = transactionId,
            Description = "Hand-corrected by the user",
            Amount = new Money(1.00m, CurrencyCode.Eur),
            CategoryId = categoryId,
            CategorizedBy = CategorizationAuthority.User,
            MerchantId = null,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);
        var items = new[] { new CategorizedLineItem("Coffee", new Money(3.50m, CurrencyCode.Eur), categoryId, merchantId) };

        await store.ApplyAsync(transactionId, items, TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var lines = await db.LineItems.AsNoTracking()
            .Where(l => l.TransactionId == transactionId).ToListAsync(TestContext.Current.CancellationToken);
        lines.Should().HaveCount(2, "the user's line survives alongside the new model line");
        var userLine = lines.Single(l => l.Id == userLineId);
        userLine.Description.Should().Be("Hand-corrected by the user");
        userLine.Amount.Should().Be(new Money(1.00m, CurrencyCode.Eur));
        userLine.CategorizedBy.Should().Be(CategorizationAuthority.User);
    }

    [Fact]
    public async Task ApplyAsync_is_idempotent_applying_the_same_result_twice_leaves_the_same_rows()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var (transactionId, categoryId, merchantId) = await SeedAsync(db, TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);
        var items = new[]
        {
            new CategorizedLineItem("Coffee", new Money(3.50m, CurrencyCode.Eur), categoryId, merchantId),
            new CategorizedLineItem("Milk", new Money(1.20m, CurrencyCode.Eur), categoryId, null),
        };

        await store.ApplyAsync(transactionId, items, TestContext.Current.CancellationToken);
        await store.ApplyAsync(transactionId, items, TestContext.Current.CancellationToken);

        // The second ApplyAsync deletes the first call's line items with a raw SQL DELETE, which
        // the change tracker never learns about - the first call's LineItem entities stay tracked
        // as Unchanged even though their rows are gone. ChangeTracker.Clear() is required before
        // this read, or a query touching the identity map can serve those stale instances instead
        // of what is actually in the database - LineItemMoneyMappingTests hit this same class of
        // defect once already (a tracked client-side value surviving a round trip the DB rounded).
        db.ChangeTracker.Clear();
        var lines = await db.LineItems.AsNoTracking()
            .Where(l => l.TransactionId == transactionId).ToListAsync(TestContext.Current.CancellationToken);
        lines.Should().HaveCount(2, "applying an identical result twice must not double the bill");
        lines.Select(l => l.Description).Should().BeEquivalentTo(["Coffee", "Milk"]);
    }

    [Fact]
    public async Task ApplyAsync_commits_nothing_when_the_transaction_fails_before_commit()
    {
        var connectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();
        Guid transactionId, categoryId, merchantId;

        await using (var seed = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options))
        {
            await seed.Database.MigrateAsync(TestContext.Current.CancellationToken);
            (transactionId, categoryId, merchantId) = await SeedAsync(seed, TestContext.Current.CancellationToken);
        }

        await using var breaking = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(new ThrowsBeforeCommitInterceptor())
                .Options);
        var store = new EfCategorizationStore(breaking);
        var items = new[] { new CategorizedLineItem("Coffee", new Money(3.50m, CurrencyCode.Eur), categoryId, merchantId) };

        var act = async () => await store.ApplyAsync(transactionId, items, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var verify = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options);
        var transaction = await verify.Transactions.SingleAsync(t => t.Id == transactionId, TestContext.Current.CancellationToken);
        transaction.Status.Should().Be(TransactionStatus.Captured,
            "the commit never happened, so the status flip must not be visible either");
        (await verify.LineItems.CountAsync(l => l.TransactionId == transactionId, TestContext.Current.CancellationToken))
            .Should().Be(0, "the insert was staged inside the same rolled-back transaction as the failed commit");
    }

    [Fact]
    public async Task GetSubjectAsync_returns_the_subject_in_one_round_trip()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet("Main Wallet");
        var transaction = NewTransaction(wallet.Id, chatId: 777, messageId: 5);
        db.Wallets.Add(wallet);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);

        var subject = await store.GetSubjectAsync(transaction.Id, TestContext.Current.CancellationToken);

        subject.Should().NotBeNull();
        subject!.TransactionId.Should().Be(transaction.Id);
        subject.RawText.Should().Be("coffee 3.50, milk 1.20");
        subject.TelegramChatId.Should().Be(777);
        subject.BotMessageId.Should().Be(42);
        subject.WalletName.Should().Be("Main Wallet");
    }

    [Fact]
    public async Task GetSubjectAsync_returns_null_when_the_transaction_is_gone()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);

        var subject = await store.GetSubjectAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        subject.Should().BeNull();
    }

    [Fact]
    public async Task MarkFailedAsync_sets_status_to_failed_and_nothing_else()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet();
        var transaction = NewTransaction(wallet.Id);
        db.Wallets.Add(wallet);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db);

        await store.MarkFailedAsync(transaction.Id, TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var reloaded = await db.Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken);
        reloaded.Status.Should().Be(TransactionStatus.Failed);
        reloaded.RawText.Should().Be(transaction.RawText);
        reloaded.TimeZoneId.Should().Be(transaction.TimeZoneId);
        reloaded.BotMessageId.Should().Be(transaction.BotMessageId);
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: build error — `The type or namespace name 'EfCategorizationStore' could not be found`.

- [ ] **Step 8: Confirm the failure is the one you expect**

Same command as Step 7. If the error is not "type not found," stop and fix that first.

- [ ] **Step 9: Write `EfCategorizationStore`**

Create `src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Categorization;

public sealed class EfCategorizationStore(LedgerDbContext db) : ICategorizationStore
{
    public async Task<CategorizationSubject?> GetSubjectAsync(Guid transactionId, CancellationToken cancellationToken) =>
        await (
            from t in db.Transactions.AsNoTracking()
            join w in db.Wallets.AsNoTracking() on t.WalletId equals w.Id
            where t.Id == transactionId
            select new CategorizationSubject(t.Id, t.RawText, t.TelegramChatId, t.BotMessageId, w.Name))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task ApplyAsync(Guid transactionId, IReadOnlyList<CategorizedLineItem> items, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // The precedence predicate lives in the DELETE statement itself, not in an `if` around it -
        // a Rule- or User-authored line (2 or 4) is never eligible for deletion by a model re-run,
        // and there is nothing downstream that could forget to check that, because there is nothing
        // to forget.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM line_items WHERE transaction_id = @transactionId AND categorized_by <= @modelAuthority",
            [
                new NpgsqlParameter("transactionId", transactionId),
                new NpgsqlParameter("modelAuthority", (int)CategorizationAuthority.Model),
            ],
            cancellationToken);

        foreach (var item in items)
        {
            db.LineItems.Add(new LineItem
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                Description = item.Description,
                Amount = item.Amount,
                CategoryId = item.CategoryId,
                CategorizedBy = CategorizationAuthority.Model,
                MerchantId = item.MerchantId,
            });
        }

        var transaction = await db.Transactions.SingleAsync(t => t.Id == transactionId, cancellationToken);
        transaction.Status = TransactionStatus.Completed;

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public Task MarkFailedAsync(Guid transactionId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            "UPDATE transactions SET status = @status WHERE id = @transactionId",
            [
                new NpgsqlParameter("status", (int)TransactionStatus.Failed),
                new NpgsqlParameter("transactionId", transactionId),
            ],
            cancellationToken);
}
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: `EfCategorizationStoreTests` is green — all 8 facts, including `ApplyAsync_commits_nothing_when_the_transaction_fails_before_commit`. If that one fails with rows visible in `verify`, the transaction isn't actually wrapping all three statements — go back and check `ExecuteSqlRawAsync`, `SaveChangesAsync` and `tx.CommitAsync` are all called on the same `db`/`tx` pairing, with nothing committing early.

> **Why `await using var tx` needs no explicit `catch`/`RollbackAsync`.** Disposing an `IDbContextTransaction` that was never committed rolls it back — the same guarantee `ThrowsBeforeCommitInterceptor`'s own use in `EfCaptureStoreTests` already relies on. An explicit `try/catch { await tx.RollbackAsync(...); throw; }` here would be exactly the kind of defensive boilerplate CLAUDE.md §3 asks not to write for a condition (the transaction staying uncommitted) that the `await using` already guarantees handles itself.

- [ ] **Step 10: Run the full suite**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green — the 16 new facts across `EfCategoryCatalogTests` (1), `EfMerchantDirectoryTests` (7) and `EfCategorizationStoreTests` (8), plus every pre-existing test.

- [ ] **Step 11: Commit**

```bash
git add src/Noof.Ledger.Persistence/Categorization/EfCategoryCatalog.cs src/Noof.Ledger.Persistence/Categorization/EfMerchantDirectory.cs src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs tests/Noof.Ledger.Persistence.Tests/EfCategoryCatalogTests.cs tests/Noof.Ledger.Persistence.Tests/EfMerchantDirectoryTests.cs tests/Noof.Ledger.Persistence.Tests/EfCategorizationStoreTests.cs
git commit -m "feat(persistence): EfCategoryCatalog, EfMerchantDirectory and EfCategorizationStore"
```

---

### Task 6: The read model — recent spending, and a month bucketed by each row's own time zone

**Depends on:** Task 1 (`Noof.Ledger.Application/Reporting/ISpendingReadModel.cs` — `ISpendingReadModel`, `RecentLineItem`, `RecentTransaction`, `MonthTotal`, `MonthSummary`, reproduced in full in the contract section). This task assumes those five types exist under `Noof.Ledger.Application.Reporting` exactly as the contract states. If they do not exist yet, stop and land Task 1 first — do not invent or restate the contract here.

**Files:**
- Create: `src/Noof.Ledger.Persistence/Reporting/EfSpendingReadModel.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/EfSpendingReadModelTests.cs`

No other file changes. In particular, this task does **not** touch `LedgerDbContext` (no new `DbSet`, no keyless entity), `Directory.Packages.props` (no new package — `Microsoft.Extensions.TimeProvider.Testing` and `AwesomeAssertions` are already referenced by `Noof.Ledger.Persistence.Tests` from Phase 1A Task 6), or `Program.cs` (DI registration of `EfSpendingReadModel` and resolution of `Capture:TimeZone` into the `TimeZoneInfo` this class takes is the dashboard-wiring task's job, not this one — see Interfaces below).

**Interfaces:**
- Consumes: `ISpendingReadModel`, `RecentLineItem`, `RecentTransaction`, `MonthTotal`, `MonthSummary` (Application/Reporting, Task 1) · `LedgerDbContext`, `Transaction`, `LineItem`, `Category`, `Merchant`, `Wallet`, `TransactionStatus`, `CategorizationAuthority`, `MerchantKind`, `CurrencyCode`, `Money` (Domain/Persistence, already present) · `PostgresFixture` (test infra, already present, unmodified).
- Produces: `EfSpendingReadModel(LedgerDbContext db, TimeProvider timeProvider, TimeZoneInfo currentZone) : ISpendingReadModel`, consumed by the dashboard task (Task 8) both for its DI registration and directly by `Home.razor`'s code-behind.

Verified context, do not re-derive:
- `transactions.time_zone_id` is `character varying(64)`, `NOT NULL` (migration `20260921065115_AddCaptureModel.cs`, `TransactionConfiguration.cs`). It is plain text, so `occurred_at AT TIME ZONE t.time_zone_id` composes: PostgreSQL's `AT TIME ZONE` operator accepts a text expression on its right-hand side and evaluates it per row, not just a literal. `occurred_at` is `timestamptz`; the result of `AT TIME ZONE` on a `timestamptz` is `timestamp without time zone` — the local wall clock in that row's own zone.
- `Transaction`, `LineItem`, `Category`, `Merchant`, `Wallet` carry **no navigation properties** — every relationship in `Configurations/*.cs` is `HasOne<T>().WithMany()` against the FK property alone (e.g. `TransactionConfiguration.cs:28`). There is nothing to `.Include(...)` via a lambda; any join has to be written out.
- `LineItem.Amount` is a `Money` mapped as a EF Core complex property (`LineItemConfiguration.cs:22`, `ComplexProperty`), backed by two plain columns (`amount numeric(19,4)`, `currency varchar(3)`). It is safe to project directly whenever the `LineItem` row itself is guaranteed to exist (an ordinary `from li in db.LineItems where ...`); it is *not* something this task risks projecting through an **optional** join, for the reason given in Decision 1 below.
- `Category.NameEn` / `Merchant.DisplayName` are the display strings; `LineItem.CategoryId` / `LineItem.MerchantId` are nullable FKs — a line item can legitimately have neither.
- `CaptureTimeZoneGuard.Resolve(string) : TimeZoneInfo` (`src/Noof.Ledger.Host/Startup/CaptureTimeZoneGuard.cs`, Phase 1A Task 6) already validates `Capture:TimeZone` (default `Europe/Belgrade`) at Host startup and rejects anything that is not a real IANA id. This class is `Noof.Ledger.Host`-only and this task must not reference it or reimplement its validation — `EfSpendingReadModel` simply takes the already-resolved `TimeZoneInfo` as a constructor parameter, and **Task 7** is the one that calls `CaptureTimeZoneGuard.Resolve(...)` again (it already runs once in `Program.cs:32`) and passes the result into this class's DI registration — Task 7 owns every `Program.cs` change in this phase.
- `db.Database.SqlQuery<T>($"...")` (interpolated, EF-parameterised) is already used once in this repo (Phase 1A Task 3's truncate-guard test), proving the EF Core/Npgsql version here supports raw-SQL query methods on `DatabaseFacade`. This task deliberately does **not** use it — see Decision 2.

---

### Locked design decisions

**1. `RecentAsync` is two fixed queries, not one flattened `LEFT JOIN`, and that is still "not N+1."**
The obvious single-query shape is a `GroupJoin`/`DefaultIfEmpty` chain: transactions LEFT JOIN line_items LEFT JOIN categories LEFT JOIN merchants, in one `SELECT`. That was tried and rejected here: it makes `LineItem` itself the *optional* side of a join (a transaction with no line items must still appear), and `LineItem.Amount` is an EF Core complex property — projecting a complex property through a row that may not exist (all its backing columns NULL) is exactly the kind of provider/version-sensitive edge case this plan cannot verify without running `dotnet build`/`dotnet test`, which is off-limits while drafting. So the query is split at the one place that makes the optionality safe: `db.LineItems` is queried directly (a `LineItem` row, when it exists, always has a real `Amount` — it is never the outer/possibly-absent side of anything), and only `Category`/`Merchant` — plain nullable-Guid lookups — are the optional joins. That is **two queries total, run once per call, each independent of how many transactions or line items exist**: fetch up to `limit` transaction headers (with wallet name, one `INNER JOIN`, ordered and paged in SQL), then fetch every line item belonging to those transaction ids (with category/merchant names, two `LEFT JOIN`s), and group the second result under the first in memory. This is not N+1 — N+1 means one query *per transaction*; this is a fixed two queries regardless of N. "Single query shape" is satisfied in the sense the task means it: one shape reused for the whole call, not a shape repeated per row.

**2. `ThisMonthAsync` is raw ADO.NET against the `DbContext`'s own connection, not `FromSqlRaw<TEntity>` and not `Database.SqlQuery<T>`.**
`FromSqlRaw<TEntity>` requires the result to map onto a type already in the model; `MonthTotal` (Application) is not, and adding a keyless entity type for it would mean touching `LedgerDbContext.OnModelCreating`, which is out of this task's file list. `Database.SqlQuery<T>` (used once elsewhere in this repo, for a single `int` column) is EF Core's ad-hoc non-entity projection — but it is untested here against a multi-column result binding into a type with a validating constructor (`CurrencyCode`'s constructor throws on bad input and is not something EF's ad-hoc materialiser is under any obligation to call correctly), and the task's explicit instruction is to follow `EfJobQueue.cs`'s style: **explicit `NpgsqlParameter`s, never a string interpolated into SQL.** `EfJobQueue.cs` gets away with `FromSqlRaw`/`ExecuteSqlRawAsync` because every one of its raw-SQL calls targets `CategorizationJob`, which *is* in the model. Nothing here is. So this task opens the context's own ADO.NET connection (`Database.OpenConnectionAsync` / `Database.GetDbConnection()` / `Database.CloseConnectionAsync` — public, documented `DatabaseFacade` members, not new API surface), builds an `NpgsqlCommand` with three `NpgsqlParameter`s, and materialises `MonthTotal` by hand from the `DbDataReader`. This is more verbose than a one-liner but every step of it is something this plan can reason about without executing it.

**3. "This month" is computed once, at the top of `ThisMonthAsync`, from `TimeProvider` and the constructor-injected `currentZone` — never from a literal, never re-derived per row.**
`var nowInZone = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), currentZone);` gives the operator's current wall-clock moment; `new DateTime(nowInZone.Year, nowInZone.Month, 1, 0, 0, 0, DateTimeKind.Unspecified)` is the first instant of that calendar month, as an *unzoned* value — which is exactly the type `AT TIME ZONE` produces on the PostgreSQL side, so the two compare directly with no further conversion. The half-open interval `[firstDay, firstDay.AddMonths(1))` is applied against each row's own `occurred_at AT TIME ZONE t.time_zone_id` — the row's own zone, not `currentZone`. `currentZone` only ever decides *which* month is "this month"; it never decides which bucket an individual row falls into. Getting these two zones swapped is the bug this task exists to prevent.

**4. Uncategorised money is labelled, never dropped.**
`MonthTotal.CategoryName` is `string`, not `string?` — the contract already forces a decision here. A line item with `category_id IS NULL` is labelled `"Uncategorised"` (a constant, `EfSpendingReadModel.UncategorisedLabel`, `internal` so the test file can assert against it instead of duplicating the literal) and still contributes to a `MonthTotal` row, grouped separately from every named category. This is the SQL's `COALESCE(c.name_en, @uncategorised)`, used identically in both the `SELECT` list and the `GROUP BY` clause so PostgreSQL treats every uncategorised row as one group rather than one group per `NULL` category id (which would, in fact, still coalesce correctly under standard `GROUP BY NULL` semantics — but writing the same `COALESCE(...)` expression in both places removes any doubt and keeps the two clauses visibly in agreement).

**5. Never two currencies in one figure.**
The `GROUP BY` key is `(COALESCE(c.name_en, @uncategorised), li.currency)` — currency is *part of the grouping key*, not applied after the fact. There is no code path in this file that sums two `Money` values of different currencies (which `Money.operator +` would throw on anyway) or that could silently coalesce RSD and EUR into one row.

**6. `db.ChangeTracker.Clear()` before every read-back in tests.**
Called out explicitly because this defect has already occurred once in this repository (Phase 1A). Every test below writes through `db`, calls `SaveChangesAsync`, then `db.ChangeTracker.Clear()`, before constructing the read model and calling it against the same `db` — otherwise a query could be answered from the identity map instead of proving the SQL actually works.

**7. The bucketing test uses `Etc/GMT±N` zones, not city names.**
`Etc/GMT-14` (UTC+14) and `Etc/GMT+11` (UTC−11) — note the inverted POSIX sign convention on `Etc/GMT` ids, which is standard tzdata behaviour, not a typo — are fixed-offset, DST-free, and guaranteed present in any IANA tzdata build. A city zone (e.g. `Pacific/Kiritimati`) would make the same point but adds a dependency on that specific name still being a live (non-linked) zone in whatever tzdata ships on the CI/build machine; the `Etc/GMT` zones remove that variable entirely, which matters here because the test's entire point is to depend on `AT TIME ZONE` doing real, zone-specific arithmetic.

---

- [ ] **Step 1: Write the failing tests for `EfSpendingReadModel`**

Create `tests/Noof.Ledger.Persistence.Tests/EfSpendingReadModelTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Reporting;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfSpendingReadModelTests(PostgresFixture fixture)
{
    static int nextTelegramMessageId;

    static Wallet NewWallet(CurrencyCode currency) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Cash",
        Currency = currency,
        IsDefault = false,
    };

    static Category NewCategory(string slug, string nameEn) => new()
    {
        Id = Guid.NewGuid(),
        Slug = slug,
        NameEn = nameEn,
        NameRu = nameEn,
        IsActive = true,
    };

    static Merchant NewMerchant(string displayName) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = displayName,
        Kind = MerchantKind.Retail,
    };

    static Transaction NewTransaction(
        Guid walletId, DateTimeOffset occurredAt, string timeZoneId, TransactionStatus status) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = walletId,
        RawText = "test capture",
        Status = status,
        TimeZoneId = timeZoneId,
        OccurredAt = occurredAt,
        TelegramChatId = 1,
        TelegramMessageId = Interlocked.Increment(ref nextTelegramMessageId),
        CreatedAt = occurredAt,
    };

    static LineItem NewLineItem(
        Guid transactionId, string description, Money amount, Guid? categoryId, Guid? merchantId) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        Description = description,
        Amount = amount,
        CategoryId = categoryId,
        CategorizedBy = CategorizationAuthority.Model,
        MerchantId = merchantId,
    };

    [Fact]
    public async Task RecentAsync_returns_transactions_newest_first_including_ones_awaiting_categorisation_and_ones_that_failed()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var oldest = NewTransaction(wallet.Id, now.AddHours(-2), "Europe/Belgrade", TransactionStatus.Failed);
        var middle = NewTransaction(wallet.Id, now.AddHours(-1), "Europe/Belgrade", TransactionStatus.Captured);
        var newest = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        db.Transactions.AddRange(oldest, middle, newest);
        db.LineItems.Add(NewLineItem(newest.Id, "coffee", new Money(3.5m, CurrencyCode.Eur), null, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(10, TestContext.Current.CancellationToken);

        recent.Select(r => r.Id).Should().ContainInOrder(newest.Id, middle.Id, oldest.Id);
        recent.Single(r => r.Id == oldest.Id).Status.Should().Be(TransactionStatus.Failed);
        recent.Single(r => r.Id == oldest.Id).Items.Should().BeEmpty("a failed transaction still has to show up, not vanish");
        recent.Single(r => r.Id == middle.Id).Items.Should().BeEmpty("still awaiting categorisation");
        var newestRow = recent.Single(r => r.Id == newest.Id);
        newestRow.WalletName.Should().Be(wallet.Name);
        newestRow.TimeZoneId.Should().Be("Europe/Belgrade");
        newestRow.Items.Should().ContainSingle().Which.Description.Should().Be("coffee");
    }

    [Fact]
    public async Task RecentAsync_resolves_category_and_merchant_names_and_leaves_them_null_when_absent()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        var category = NewCategory("test-recent-groceries", "Groceries");
        var merchant = NewMerchant("Lidl");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        db.Merchants.Add(merchant);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = DateTimeOffset.UtcNow;
        var transaction = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        db.Transactions.Add(transaction);
        db.LineItems.AddRange(
            NewLineItem(transaction.Id, "bread", new Money(2m, CurrencyCode.Eur), category.Id, merchant.Id),
            NewLineItem(transaction.Id, "cash withdrawal fee", new Money(1m, CurrencyCode.Eur), null, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(10, TestContext.Current.CancellationToken);

        recent.Single().Items.Should().BeEquivalentTo(
        [
            new RecentLineItem("bread", new Money(2m, CurrencyCode.Eur), "Groceries", "Lidl"),
            new RecentLineItem("cash withdrawal fee", new Money(1m, CurrencyCode.Eur), null, null),
        ]);
    }

    [Fact]
    public async Task RecentAsync_respects_the_limit_and_returns_only_the_most_recent_ones()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            db.Transactions.Add(NewTransaction(wallet.Id, now.AddMinutes(i), "Europe/Belgrade", TransactionStatus.Captured));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(2, TestContext.Current.CancellationToken);

        recent.Select(r => r.OccurredAt).Should().BeEquivalentTo([now.AddMinutes(4), now.AddMinutes(3)],
            "only the two most recently occurred transactions should survive the limit");
    }

    [Fact]
    public async Task ThisMonthAsync_buckets_each_row_by_its_own_stored_time_zone_not_a_naive_UTC_bucket()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        var category = NewCategory("test-month-boundary", "Boundary Test");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The identical UTC instant, stored under two different zones. A naive `date_trunc('month',
        // occurred_at)` would put BOTH rows in August, because occurred_at - the UTC instant - is the
        // same value for both. Correct per-row bucketing must split them: +14 rolls the local wall
        // clock into September, -11 keeps it in August.
        var instant = new DateTimeOffset(2026, 8, 31, 23, 30, 0, TimeSpan.Zero);
        var rollsIntoSeptember = NewTransaction(wallet.Id, instant, "Etc/GMT-14", TransactionStatus.Completed);
        var staysInAugust = NewTransaction(wallet.Id, instant, "Etc/GMT+11", TransactionStatus.Completed);
        db.Transactions.AddRange(rollsIntoSeptember, staysInAugust);
        db.LineItems.AddRange(
            NewLineItem(rollsIntoSeptember.Id, "september spend", new Money(10m, CurrencyCode.Eur), category.Id, null),
            NewLineItem(staysInAugust.Id, "august spend", new Money(20m, CurrencyCode.Eur), category.Id, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        // "This month" is decided once, from the operator's CURRENTLY configured zone - fixed to UTC
        // here so the test does not depend on the build machine's own zone. 2026-09-15 UTC is
        // unambiguously September under UTC.
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.FirstDay.Should().Be(new DateOnly(2026, 9, 1));
        summary.Totals.Should()
            .ContainSingle("only the row whose OWN zone rolls the instant into September belongs in this bucket")
            .Which.Should().BeEquivalentTo(new MonthTotal("Boundary Test", CurrencyCode.Eur, 10m));
    }

    [Fact]
    public async Task ThisMonthAsync_never_adds_two_currencies_together()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Rsd);
        var category = NewCategory("test-no-fx-mixing", "Groceries");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var rsdTransaction = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        var eurTransaction = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        db.Transactions.AddRange(rsdTransaction, eurTransaction);
        db.LineItems.AddRange(
            NewLineItem(rsdTransaction.Id, "market", new Money(1500m, CurrencyCode.Rsd), category.Id, null),
            NewLineItem(rsdTransaction.Id, "market again", new Money(500m, CurrencyCode.Rsd), category.Id, null),
            NewLineItem(eurTransaction.Id, "import", new Money(20m, CurrencyCode.Eur), category.Id, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.Totals.Should().HaveCount(2, "RSD and EUR must never collapse into one combined figure");
        summary.Totals.Should().Contain(new MonthTotal("Groceries", CurrencyCode.Rsd, 2000m));
        summary.Totals.Should().Contain(new MonthTotal("Groceries", CurrencyCode.Eur, 20m));
    }

    [Fact]
    public async Task ThisMonthAsync_labels_a_line_item_with_no_category_instead_of_dropping_it()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var transaction = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Captured);
        db.Transactions.Add(transaction);
        db.LineItems.Add(NewLineItem(transaction.Id, "unlabelled", new Money(9.99m, CurrencyCode.Eur), null, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.Totals.Should().ContainSingle("uncategorised money must still be counted, never silently omitted")
            .Which.Should().BeEquivalentTo(new MonthTotal(EfSpendingReadModel.UncategorisedLabel, CurrencyCode.Eur, 9.99m));
    }

    [Fact]
    public async Task ThisMonthAsync_excludes_line_items_outside_the_current_month_boundaries()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Eur);
        var category = NewCategory("test-outside-boundaries", "Groceries");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var lastMonth = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        var thisMonth = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var nextMonth = new DateTimeOffset(2026, 10, 1, 0, 0, 1, TimeSpan.Zero);
        var lastMonthTransaction = NewTransaction(wallet.Id, lastMonth, "Etc/UTC", TransactionStatus.Completed);
        var thisMonthTransaction = NewTransaction(wallet.Id, thisMonth, "Etc/UTC", TransactionStatus.Completed);
        var nextMonthTransaction = NewTransaction(wallet.Id, nextMonth, "Etc/UTC", TransactionStatus.Completed);
        db.Transactions.AddRange(lastMonthTransaction, thisMonthTransaction, nextMonthTransaction);
        db.LineItems.AddRange(
            NewLineItem(lastMonthTransaction.Id, "august", new Money(1m, CurrencyCode.Eur), category.Id, null),
            NewLineItem(thisMonthTransaction.Id, "september", new Money(2m, CurrencyCode.Eur), category.Id, null),
            NewLineItem(nextMonthTransaction.Id, "october", new Money(4m, CurrencyCode.Eur), category.Id, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(thisMonth), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.Totals.Should().ContainSingle().Which.Should().BeEquivalentTo(new MonthTotal("Groceries", CurrencyCode.Eur, 2m));
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: build error — `EfSpendingReadModel` does not exist in `Noof.Ledger.Persistence.Reporting`.

- [ ] **Step 3: Write `EfSpendingReadModel`**

Create `src/Noof.Ledger.Persistence/Reporting/EfSpendingReadModel.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Reporting;

public sealed class EfSpendingReadModel(LedgerDbContext db, TimeProvider timeProvider, TimeZoneInfo currentZone)
    : ISpendingReadModel
{
    internal const string UncategorisedLabel = "Uncategorised";

    public async Task<IReadOnlyList<RecentTransaction>> RecentAsync(int limit, CancellationToken cancellationToken)
    {
        var headers = await db.Transactions
            .Join(db.Wallets, t => t.WalletId, w => w.Id, (t, w) => new
            {
                t.Id,
                t.OccurredAt,
                t.TimeZoneId,
                t.RawText,
                t.Status,
                WalletName = w.Name,
            })
            .OrderByDescending(h => h.OccurredAt)
            .ThenByDescending(h => h.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var transactionIds = headers.Select(h => h.Id).ToList();

        // li is never the optional side of a join here - only categories/merchants can be absent -
        // so this stays one flat SELECT with two LEFT JOINs, run once for every transaction header
        // fetched above rather than once per transaction. See Decision 1 for why the two entire
        // queries are not merged into one.
        var lineItemRows = await (
            from li in db.LineItems
            where transactionIds.Contains(li.TransactionId)
            join c in db.Categories on li.CategoryId equals c.Id into categoryJoin
            from c in categoryJoin.DefaultIfEmpty()
            join m in db.Merchants on li.MerchantId equals m.Id into merchantJoin
            from m in merchantJoin.DefaultIfEmpty()
            select new
            {
                li.TransactionId,
                li.Description,
                li.Amount,
                CategoryName = c == null ? null : c.NameEn,
                MerchantName = m == null ? null : m.DisplayName,
            })
            .ToListAsync(cancellationToken);

        var lineItemsByTransaction = lineItemRows.ToLookup(r => r.TransactionId);

        return
        [
            .. headers.Select(h => new RecentTransaction(
                h.Id,
                h.OccurredAt,
                h.TimeZoneId,
                h.RawText,
                h.Status,
                h.WalletName,
                [.. lineItemsByTransaction[h.Id]
                    .Select(li => new RecentLineItem(li.Description, li.Amount, li.CategoryName, li.MerchantName))])),
        ];
    }

    public async Task<MonthSummary> ThisMonthAsync(CancellationToken cancellationToken)
    {
        var nowInZone = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), currentZone);
        var firstDay = new DateTime(nowInZone.Year, nowInZone.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var firstDayNextMonth = firstDay.AddMonths(1);

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();

            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                """
                SELECT COALESCE(c.name_en, @uncategorised) AS category_name,
                       li.currency AS currency,
                       SUM(li.amount) AS total
                FROM line_items li
                JOIN transactions t ON t.id = li.transaction_id
                LEFT JOIN categories c ON c.id = li.category_id
                WHERE (t.occurred_at AT TIME ZONE t.time_zone_id) >= @firstDay
                  AND (t.occurred_at AT TIME ZONE t.time_zone_id) < @firstDayNextMonth
                GROUP BY COALESCE(c.name_en, @uncategorised), li.currency
                """;
            command.Parameters.Add(new NpgsqlParameter("uncategorised", UncategorisedLabel));
            command.Parameters.Add(new NpgsqlParameter("firstDay", firstDay));
            command.Parameters.Add(new NpgsqlParameter("firstDayNextMonth", firstDayNextMonth));

            var totals = new List<MonthTotal>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    totals.Add(new MonthTotal(
                        reader.GetString(0),
                        new CurrencyCode(reader.GetString(1)),
                        reader.GetDecimal(2)));
                }
            }

            return new MonthSummary(DateOnly.FromDateTime(firstDay), totals);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
```

> **Never write `date_trunc('month', occurred_at)` here.** It reads as a simplification of the two `AT TIME ZONE t.time_zone_id` expressions above, and it is the one change that would make `ThisMonthAsync_buckets_each_row_by_its_own_stored_time_zone_not_a_naive_UTC_bucket` (Step 1) fail: both of that test's transactions share one `occurred_at` UTC instant and differ only in `time_zone_id`, specifically so that a UTC-only bucketing merges them into one month while the correct, per-row bucketing splits them into two.
>
> `command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();` matters even though nothing in this task opens an explicit transaction: if a future caller ever wraps `ThisMonthAsync` inside one (e.g. a larger reporting transaction), Npgsql throws rather than silently reading outside it unless the raw command is enlisted — this line is what enlists it, and it is a no-op (`null`) in every test above.

- [ ] **Step 4: Run the persistence tests green**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: all green, including the 7 new `EfSpendingReadModelTests`.

- [ ] **Step 5: Run the full solution and commit**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green.

```bash
git add src/Noof.Ledger.Persistence/Reporting/EfSpendingReadModel.cs tests/Noof.Ledger.Persistence.Tests/EfSpendingReadModelTests.cs
git commit -m "feat(reporting): EfSpendingReadModel buckets each transaction by its own stored time zone"
```

---

---

### Task 7: The worker — claim, call, verify, write, edit, complete

**Files:**
- Modify: `tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
- Create: `tests/Noof.Ledger.Host.Tests/CategorizationReplyTests.cs`
- Create: `tests/Noof.Ledger.Host.Tests/CategorizationWorkerOptionsTests.cs`
- Create: `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`
- Create: `tests/Noof.Ledger.Host.Tests/CategorizationWiringTests.cs`
- Create: `src/Noof.Ledger.Host/Workers/CategorizationReply.cs`
- Create: `src/Noof.Ledger.Host/Workers/CategorizationWorkerOptions.cs`
- Create: `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`
- Modify: `src/Noof.Ledger.Host/Program.cs`

**Interfaces — consumed, exact (reproduced from the plan's contract section; do not re-derive these):**

```csharp
namespace Noof.Ledger.Application.Categorization;

public sealed record CategoryOption(string Slug, string NameEn, string NameRu, string? ParentSlug);
public sealed record MerchantOption(Guid Id, string DisplayName);
public sealed record CategorizationRequest(
    string RawText,
    IReadOnlyList<CategoryOption> Categories,
    IReadOnlyList<MerchantOption> MerchantHints,
    IReadOnlyList<MerchantOption> AllMerchants);
public sealed record ProposedLineItem(string Description, string AmountQuote, string CurrencyCode, string CategorySlug, Guid? KnownMerchantId, string? MerchantQuote);
public sealed record CategorizationProposal(IReadOnlyList<ProposedLineItem> Items);
public sealed record ResolvedLineItem(string Description, Money Amount, string CategorySlug, Guid? KnownMerchantId, string? MerchantText);
public sealed record CategorizedLineItem(string Description, Money Amount, Guid CategoryId, Guid? MerchantId);
public sealed record CategorizationSubject(Guid TransactionId, string RawText, long TelegramChatId, int? BotMessageId, string WalletName);
public sealed record CategoryEntry(Guid Id, string Slug, string NameEn, string NameRu, string? ParentSlug);
public sealed record MerchantAliasEntry(string Folded, Guid MerchantId, string DisplayName);

public interface ICategorizer
{
    Task<CategorizationProposal> ProposeAsync(CategorizationRequest request, CancellationToken cancellationToken);
    Task<string> CanonicalizeMerchantAsync(string merchantText, IReadOnlyList<MerchantOption> knownMerchants, CancellationToken cancellationToken);
}

public interface ICategoryCatalog
{
    Task<IReadOnlyList<CategoryEntry>> ActiveAsync(CancellationToken cancellationToken);
}

public interface IMerchantDirectory
{
    Task<IReadOnlyList<MerchantAliasEntry>> AliasesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MerchantOption>> MerchantsAsync(CancellationToken cancellationToken);
    Task<Guid> LinkAliasAsync(string folded, string displayName, CancellationToken cancellationToken);
}

public interface ICategorizationStore
{
    Task<CategorizationSubject?> GetSubjectAsync(Guid transactionId, CancellationToken cancellationToken);
    Task ApplyAsync(Guid transactionId, IReadOnlyList<CategorizedLineItem> items, CancellationToken cancellationToken);
    Task MarkFailedAsync(Guid transactionId, CancellationToken cancellationToken);
}

public enum ModelFailureKind { Transient = 0, Terminal = 1 }

public sealed class ModelCallException(ModelFailureKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public ModelFailureKind Kind { get; } = kind;
}

public static class MerchantScan
{
    public static IReadOnlyList<MerchantAliasEntry> Matches(string rawText, IReadOnlyList<MerchantAliasEntry> aliases, int limit);
}

public static class ProposalVerification
{
    public static bool TryResolve(
        string rawText, CategorizationProposal proposal,
        IReadOnlyCollection<string> offeredSlugs, IReadOnlyCollection<Guid> offeredMerchantIds,
        out IReadOnlyList<ResolvedLineItem> items, out string failure);
}
```

```csharp
namespace Noof.Ledger.Application.Jobs;

public interface IJobQueue
{
    Task<CategorizationJob?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken);
    Task<JobCompletionOutcome> SucceedAsync(Guid jobId, string workerId, CancellationToken cancellationToken);
    Task<JobCompletionOutcome> RetryAsync(Guid jobId, string workerId, DateTimeOffset runAfter, string error, CancellationToken cancellationToken);
    Task<JobCompletionOutcome> FailAsync(Guid jobId, string workerId, string error, CancellationToken cancellationToken);
    Task<int> ReleaseExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public enum JobCompletionOutcome { Applied, NotOwned }
```

```csharp
namespace Noof.Ledger.Application.Chat;

public interface IChatNotifier
{
    Task<int> SendAsync(long chatId, string text, CancellationToken cancellationToken);
    Task EditAsync(long chatId, int messageId, string text, CancellationToken cancellationToken);
}
```

```csharp
namespace Noof.Ledger.Application.Secrets;

public interface ISecretStore
{
    Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken);
    Task<SecretStatus> GetStatusAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, string plaintext, CancellationToken cancellationToken);
    Task<bool> TrySetIfMissingAsync(string key, string plaintext, CancellationToken cancellationToken);
}

public readonly record struct SecretStatus(SecretState State, DateTimeOffset? UpdatedAt);
public enum SecretState { Present = 0, Missing = 1, Unreadable = 2 }
public static class SecretKeys { public const string AnthropicApiKey = "anthropic-api-key"; /* ... */ }
```

```csharp
namespace Noof.Ledger.Domain;

public sealed class CategorizationJob
{
    public required Guid Id { get; init; }
    public required Guid TransactionId { get; init; }
    public required JobStatus Status { get; set; }
    public required int AttemptCount { get; set; }
    public required DateTimeOffset RunAfter { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public string? ClaimedBy { get; set; }
    public string? LastError { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; set; }
}

public enum JobStatus { Pending = 0, Claimed = 1, Succeeded = 2, Failed = 3 }
public static class MerchantName { public static string Fold(string raw); } // uppercase, whitespace-collapsed
public readonly record struct Money(decimal Amount, CurrencyCode Currency);
public readonly record struct CurrencyCode { public string Value { get; } /* ToString() => Value */ }
```

`TelegramChatNotifier` is already registered `AddSingleton<IChatNotifier, TelegramChatNotifier>()` (Phase 1A). `ISecretStore` is already `AddScoped<ISecretStore, EfSecretStore>()`. `TimeProvider.System` is already `AddSingleton`. This task does not touch any of those three lines.

---

## Locked design decisions

**1. The Anthropic key is checked *before* `ClaimAsync`, not after, as the plan's flow diagram literally shows it — this is a deliberate deviation, not an oversight.** `EfJobQueue.ClaimAsync` increments `attempt_count` as part of the same atomic `UPDATE` that claims the row (`attempt_count = attempt_count + 1`), and `IJobQueue` has no verb that undoes only that increment — `RetryAsync`/`FailAsync` never decrement it, they only compare it against `maxAttempts`. If the worker claimed first and only then discovered the key was missing, every tick until the operator pastes a key would burn one of the job's eight attempts; at `MaxAttempts = 8` and a five-second poll interval, **every job captured before the operator opens the settings page is permanently `Failed` within 40 seconds**, long before a human plausibly reacts to "I need to configure this." Checking `ISecretStore.GetStatusAsync(SecretKeys.AnthropicApiKey)` first costs nothing observable — `ReleaseExpiredLeasesAsync` still runs unconditionally before the check, so a crashed worker's leases keep recovering regardless of key state — and it means a job simply sits `Pending`, untouched, attempt count at zero, for as long as the key is missing. **Proof:** `A_missing_key_does_not_burn_the_attempt_budget` asserts `jobQueue.DidNotReceive().ClaimAsync(...)` when the key is `Missing` — since `ClaimAsync` is the *only* thing in this interface that increments `attempt_count`, proving it was never called is definitionally proof the count is unchanged.

**2. `ModelCallException.Kind` decides `RetryAsync` vs `FailAsync`; a `ProposalVerification.TryResolve` failure always goes straight to `FailAsync`, never `RetryAsync`.** A verification failure is a property of *this exact raw text* against *this exact offered schema* — the model reads the same message and is offered the same slugs on attempt two exactly as it was on attempt one, so nothing about retrying it changes the outcome. Spending up to seven more attempts (and seven more Anthropic calls, i.e. real money) to re-derive the same rejection is pure waste, and it also delays the operator's failure notification by up to 64 minutes for no benefit. **Proof:** `A_verification_failure_is_terminal_and_never_retried`.

**3. `NotOwned` means stop — no completion verb ever triggers another write once it comes back.** `SucceedAsync`/`RetryAsync`/`FailAsync` returning `NotOwned` means this worker's lease was already reclaimed and someone else now owns (or has already finished) the row; per `IJobQueue`'s own doc comment, the caller logs it and stops. Concretely: a `NotOwned` from `RetryAsync` or `FailAsync` must not trigger `ICategorizationStore.MarkFailedAsync` or `IChatNotifier.EditAsync` — both are gated behind the completion verb returning `Applied` first. A `NotOwned` from `SucceedAsync` (which only fires *after* `ApplyAsync` and the success `EditAsync` have already run — see the flow diagram) must not cause the worker to call `RetryAsync`/`FailAsync` as a fallback; it just logs and the tick ends. **Proof:** three tests, one per verb — `NotOwned_from_SucceedAsync_does_not_trigger_a_fallback_retry_or_fail`, `NotOwned_from_RetryAsync_does_not_mark_the_transaction_failed_or_edit_telegram`, `NotOwned_from_FailAsync_does_not_mark_the_transaction_failed_or_edit_telegram`.

**4. The host must survive anything — `RunTickAsync` swallows every non-cancellation exception, exactly like `TelegramPollingService.RunTickAsync`.** `.NET 10`'s `BackgroundServiceExceptionBehavior` defaults to `StopHost`: an exception escaping `ExecuteAsync` kills the whole application — Telegram capture included, not just categorisation. `RunTickAsync` wraps scope creation, the key check, the claim, and job processing in one `try/catch (Exception ex) when (ex is not OperationCanceledException)`, returning `CategorizationTickResult.Failed` and logging rather than throwing. This exact class of defect was caught in Phase 1A not by a unit test of the poller itself but by `LoopbackGuardTerminatesTests` (`tests/Noof.Ledger.Host.Tests/LoopbackGuardTerminatesTests.cs`) — a canary that starts the *real* host process and proves it stays up; a `BackgroundService` bug that stops the host would only ever show up as an unrelated test in a different file mysteriously timing out. **Proof:** `Every_dependency_throwing_is_reported_as_failed_without_throwing`, built the same way as `TelegramPollingServiceTests.A_DI_resolution_failure_while_creating_the_scope_is_reported_as_failed_without_throwing` — a hand-written `IServiceScopeFactory` whose `CreateScope()` itself throws.

**5. A Telegram edit failure never fails the job; `SucceedAsync` runs regardless.** By the time `EditAsync` is called, `ApplyAsync` has already committed the money and categories — that write is real and correct even if the message was deleted, the bot token rotated, or the network dropped between `ApplyAsync` and `EditAsync`. The edit is caught, logged at `Warning`, and the worker proceeds straight to `SucceedAsync`. When `subject.BotMessageId` is `null` (no message to edit — a job could in principle be created by a future non-Telegram capture path), `EditAsync` is never called at all, not called-and-caught. **Proof:** `A_Telegram_edit_failure_still_succeeds_the_job` and `A_null_BotMessageId_skips_the_edit_but_still_succeeds_the_job`.

**6. Merchant resolution order is exactly the contract's flow, including the "no second model call for a merchant already linked" property.** For each resolved line with `MerchantText` and no `KnownMerchantId`: fold the text with `MerchantName.Fold`, look it up in the alias dictionary built from `IMerchantDirectory.AliasesAsync()` (the *same* call already made once per job for `MerchantScan.Matches`, not fetched twice); on a hit, use that merchant id and call nothing else. On a miss, call `ICategorizer.CanonicalizeMerchantAsync` with `allMerchants` — the full canonical list, already fetched once at the top of the tick because `CategorizationRequest.AllMerchants` and the `offeredMerchantIds` verification set both need it, so there is no second read here — then `IMerchantDirectory.LinkAliasAsync`, and write the new entry into the **local** alias dictionary immediately so a second line in the *same* message naming the same new merchant also hits locally without a second model call. Canonicalisation is capped at `MaxCanonicalizationsPerJob`; lines past the cap keep their amount and category and carry no merchant. **Proof:** `A_known_alias_is_used_without_calling_the_model_to_canonicalize_it` (alias pre-seeded, `CanonicalizeMerchantAsync`/`LinkAliasAsync` never called) and `An_unknown_merchant_is_canonicalized_and_linked_exactly_once` (both called exactly once).

**7. Scope and lifetime.** `ICategorizationStore`, `ICategoryCatalog`, `IMerchantDirectory`, `IJobQueue` and `ICategorizer` are all registered `Scoped` — the first four are `DbContext`-bound, and `ICategorizer` composes with the already-`Scoped` `ISecretStore` (a `Scoped` service cannot be constructor-injected into anything with a longer lifetime, so `ICategorizer` must be `Scoped` too, not `Singleton`). `CategorizationWorker` itself is registered as the sole `BackgroundService` instance (effectively a singleton) and creates one `IServiceScope` per tick via `IServiceScopeFactory`, exactly like `TelegramPollingService.RunTickAsync`. `grep -rn "IJobQueue" src/` before this task returns only the interface file and `EfJobQueue.cs` — no `AddScoped`/`AddSingleton` anywhere — confirmed again immediately before writing this task (see Step 14's registration, which is this interface's first DI wiring in the whole solution). `EfJobQueue`'s `maxAttempts` constructor argument is supplied from `CategorizationWorkerOptions.MaxAttempts` (see decision 8) via an explicit factory registration, since a bare `int` cannot be resolved by type the way `AddScoped<TInterface, TImplementation>()` resolves `LedgerDbContext`/`TimeProvider`.

**8. The worker id.** `claimed_by` is `varchar(128)`. `CategorizationWorker.CreateWorkerId()` builds `{machineName (truncated to 64 chars)}-{Environment.ProcessId}-{8 hex chars}`: 64 (machine, capped) + 1 (`-`) + up to 10 digits (a 32-bit process id, worst case) + 1 (`-`) + 8 (a random suffix from `Guid.NewGuid().ToString("N")[..8]`) = **84 characters, worst case** — 44 characters of headroom below the column, and real machine names are almost always far under 64. The random suffix exists because a machine name plus a process id alone is not unique across a crash-and-immediately-restart cycle on the same box, where the OS can and does recycle a process id fast enough for two worker generations to theoretically collide inside the same lease window; the suffix removes that risk entirely rather than relying on timing. **Proof:** `CreateWorkerId_fits_the_128_character_claimed_by_column` and `CreateWorkerId_is_different_on_every_call`.

**9. Backoff: a separate schedule from `TelegramBackoff`, not a reuse of it — and no separate "outer tick" backoff either.** `TelegramBackoff.Compute` is `2^consecutiveFailures` seconds capped at **one minute**, tuned for a network poll that should recover almost immediately. Phase 1A's job-queue task already documents a *different*, wider schedule for `categorization_jobs` specifically: 30s base, doubling per attempt, reaching 64 minutes at attempt 8 under the default `MaxAttempts = 8` — reusing `TelegramBackoff`'s one-minute cap here would make attempts 3 through 8 all wait the same one minute, silently discarding the wider schedule the job-queue task already committed to and giving a struggling provider no real cooling-off period. `CategorizationWorkerOptions.ComputeBackoff(attemptCount)` implements the documented schedule directly: `min(BackoffCap, BackoffBase * 2^(attemptCount - 1))`. Separately, the *outer* `ExecuteAsync` delay (idle vs. a tick-level exception) does **not** get its own exponential backoff the way `TelegramPollingService` gives its outer loop one via `consecutiveFailures` — per-job retry timing is already fully owned by `run_after` inside the job queue itself (`ClaimAsync`'s `WHERE run_after <= @now` naturally skips a job that isn't due yet), so an outer exponential backoff here would be solving, at a second layer, a problem the queue already solves once. The outer loop uses a flat `options.PollInterval` for anything other than `Processed`. This also matters for `attempt_count` staying honest: Task 3 disables the Anthropic SDK's own retries (`MaxRetries = 0`), so one `ClaimAsync` is exactly one HTTP call to Anthropic — `attempt_count` never silently undercounts calls the SDK made behind the worker's back, which is exactly what makes this documented 30s→64m schedule (and the `MaxAttempts = 8` cap) trustworthy as *both* a cost bound and a retry-timing contract.

**10. `MaxAttempts` has exactly one source of truth: `CategorizationWorkerOptions.MaxAttempts`, read by both the worker's own "is this the last attempt" check and by the `EfJobQueue` constructor's `maxAttempts` argument.** `RetryAsync`'s outcome (`Applied`/`NotOwned`) does **not** distinguish "requeued to `Pending`" from "flipped to `Failed`" — both are `Applied`, because `EfJobQueue.RetryAsync`'s internal `CASE WHEN attempt_count >= @maxAttempts` decides that server-side and reports only whether the row was this worker's to touch. For the worker to know whether *this* `RetryAsync` call was the one that exhausted the budget (and therefore needs `MarkFailedAsync` + a failure notification), it re-evaluates the identical predicate locally — `job.AttemptCount >= options.MaxAttempts`, using the same `AttemptCount` `ClaimAsync` already returned — **before** calling `RetryAsync`. This only stays correct if the `maxAttempts` passed into `EfJobQueue`'s constructor is the literal same integer the worker compares against, so Program.cs constructs `EfJobQueue` from `CategorizationWorkerOptions.MaxAttempts` rather than a second, independently-configured value.

**11. `GetSubjectAsync` returning `null` is handled, not treated as a bug.** The interface's return type is `CategorizationSubject?` — nullable is part of the contract, not defensive paranoia. If the transaction the job points at is gone, `FailAsync` is called directly (retrying cannot make a missing row reappear), `MarkFailedAsync` is skipped for the reply-composition path (there is no `WalletName`/chat id to build a reply from) but is still invoked with `job.TransactionId` if the fail actually applied, so a would-be `Failed` job is at least consistent if the row somehow reappears later. **Proof:** `A_missing_subject_fails_the_job_without_ever_calling_ApplyAsync`.

**This task owns every DI registration this phase adds — including the two it does not itself consume.** `ISpendingReadModel`/`EfSpendingReadModel` and `ISecretProbe`/`AnthropicKeyProbe` are registered in the block below even though the dashboard and the Test button (Task 8) are what use them. The alternative was tried on paper and failed: three separate tasks each described the registration as somebody else's job, and had this plan shipped that way, the pages would have thrown at first render with a dependency-resolution error naming none of the three. One task touching `Program.cs` also means one merge point for it.

---

## CONTRACT GAP

- **Concrete constructor shapes for `AnthropicCategorizer`, `EfCategoryCatalog`, `EfMerchantDirectory`, `EfCategorizationStore` are not given anywhere in the plan's contract section** — only their interfaces are. Step 14's `Program.cs` registrations assume the same convention every other `Ef*` class in this codebase already follows (confirmed by reading `EfCaptureStore(LedgerDbContext db, TimeProvider timeProvider)` and `EfJobQueue(LedgerDbContext db, TimeProvider timeProvider, int maxAttempts)`): `EfCategoryCatalog(LedgerDbContext db)` (read-only, no clock needed), `EfMerchantDirectory(LedgerDbContext db, TimeProvider timeProvider)` and `EfCategorizationStore(LedgerDbContext db, TimeProvider timeProvider)` (both write, both need a clock for `created_at`/`updated_at`), and — settled by Task 3 as drafted, not assumed — `AnthropicCategorizer(IAnthropicClientFactory clientFactory, AnthropicOptions options)`, with `AnthropicClientFactory(ISecretStore, HttpClient, AnthropicOptions)` being the one type that reaches a plaintext key. If Tasks 1, 3 or 5 land with a different constructor shape, the fix is a one-line change to the matching `AddScoped<TInterface, TImplementation>()` (or a factory lambda, as already used for `IJobQueue`) in Step 14 — it does not change anything else in this task.
- ~~The flow diagram shows `ClaimAsync` before `GetStatusAsync`.~~ **Stale, and its premise was wrong.** The contract diagram was corrected while the plan was assembled and now reads `GetStatusAsync` → `ReleaseExpiredLeasesAsync` → `ClaimAsync`, which is decision 1's order and the reason for it: `ClaimAsync` increments `attempt_count` unconditionally and no verb undoes just that increment. The one remaining difference is that this task's code releases expired leases **before** reading the secret status. That is deliberate and not a deviation worth reordering: releasing a lease another worker abandoned is housekeeping that is correct whether or not this process has a key, and doing it first means a key pasted mid-outage finds a clean queue rather than one still holding dead claims.
- **Phase 1A's job-queue task (`docs/superpowers/plans/2026-09-21-phase1a-capture-and-storage.md`, Task 7) speculatively names a `Jobs:MaxAttempts` config key** for "whoever builds the worker." This task does not use that key — `MaxAttempts` lives on `CategorizationWorkerOptions` bound from a `Categorization` config section instead (decision 10), specifically so the same integer reaches both the worker's local exhaustion check and `EfJobQueue`'s constructor without two independently-configured values that could drift apart. Not a conflict with anything already built — that key was never implemented, only mentioned as a placeholder for this exact task.

---

- [ ] **Step 1: Add the `FakeTimeProvider` package reference to the Host test project**

`Microsoft.Extensions.TimeProvider.Testing` is already pinned in `Directory.Packages.props` (version `10.10.0`, added in Phase 1A Task 7) but `tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj` does not yet reference it — confirmed by reading the file (its only test packages are `AwesomeAssertions`, `Microsoft.AspNetCore.Mvc.Testing`, `NSubstitute`, `xunit.v3.mtp-v2`).

Edit `tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`:

```xml
  <ItemGroup>
    <PackageReference Include="AwesomeAssertions" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="xunit.v3.mtp-v2" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
  </ItemGroup>
```

Run: `dotnet restore`
Expected: restores with no errors.

- [ ] **Step 2: Write the failing tests for `CategorizationReply`**

Create `tests/Noof.Ledger.Host.Tests/CategorizationReplyTests.cs`:

```csharp
using AwesomeAssertions;
using Noof.Ledger.Domain;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

public class CategorizationReplyTests
{
    [Fact]
    public void Composes_the_success_text_for_a_single_item()
    {
        var lines = new[] { new CategorizationReply.ReplyLine("Coffee", new Money(3.50m, CurrencyCode.Eur), "Food & drink") };

        var text = CategorizationReply.ComposeSuccess("Cash", lines);

        text.Should().Be("Categorised — Cash\n• Coffee — 3.50 EUR (Food & drink)\n\nTotal: 3.50 EUR");
    }

    [Fact]
    public void Composes_the_success_text_across_two_currencies_with_totals_grouped_and_sorted()
    {
        var lines = new[]
        {
            new CategorizationReply.ReplyLine("Groceries", new Money(25m, CurrencyCode.Eur), "Groceries"),
            new CategorizationReply.ReplyLine("Taxi", new Money(1500m, CurrencyCode.Rsd), "Transport"),
        };

        var text = CategorizationReply.ComposeSuccess("Cash", lines);

        text.Should().Be(
            "Categorised — Cash\n" +
            "• Groceries — 25.00 EUR (Groceries)\n" +
            "• Taxi — 1500.00 RSD (Transport)\n\n" +
            "Total: 25.00 EUR, 1500.00 RSD");
    }

    [Fact]
    public void Sums_more_than_one_line_in_the_same_currency_into_one_total()
    {
        var lines = new[]
        {
            new CategorizationReply.ReplyLine("Bread", new Money(2.50m, CurrencyCode.Eur), "Groceries"),
            new CategorizationReply.ReplyLine("Milk", new Money(1.20m, CurrencyCode.Eur), "Groceries"),
        };

        var text = CategorizationReply.ComposeSuccess("Cash", lines);

        text.Should().EndWith("Total: 3.70 EUR");
    }

    [Fact]
    public void Composes_the_failure_text()
    {
        var text = CategorizationReply.ComposeFailure("Cash");

        text.Should().Be(
            "I couldn't categorise this one for Cash automatically. It's saved, nothing is lost, " +
            "but you'll need to sort it out by hand for now.");
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: build error — `The type or namespace name 'CategorizationReply' does not exist in the namespace 'Noof.Ledger.Host.Workers'` (the namespace itself does not exist yet either).

- [ ] **Step 3: Confirm the failure is the one expected**

Re-run the same command and actually read the output — it must be a "type not found" compile error, not a Postgres connection error or an unrelated failure in another test file. If it is anything else, stop and fix that first.

- [ ] **Step 4: Implement `CategorizationReply`**

Create `src/Noof.Ledger.Host/Workers/CategorizationReply.cs`:

```csharp
using System.Globalization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Workers;

public static class CategorizationReply
{
    public sealed record ReplyLine(string Description, Money Amount, string CategoryName);

    public static string ComposeSuccess(string walletName, IReadOnlyList<ReplyLine> lines)
    {
        var itemLines = lines.Select(FormatLine);

        var totals = lines
            .GroupBy(line => line.Amount.Currency)
            .OrderBy(group => group.Key.Value, StringComparer.Ordinal)
            .Select(group => $"{FormatAmount(group.Sum(line => line.Amount.Amount))} {group.Key}");

        return $"Categorised — {walletName}\n{string.Join('\n', itemLines)}\n\nTotal: {string.Join(", ", totals)}";
    }

    public static string ComposeFailure(string walletName) =>
        $"I couldn't categorise this one for {walletName} automatically. It's saved, nothing is lost, " +
        "but you'll need to sort it out by hand for now.";

    static string FormatLine(ReplyLine line) =>
        $"• {line.Description} — {FormatAmount(line.Amount.Amount)} {line.Amount.Currency} ({line.CategoryName})";

    static string FormatAmount(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
```

`CategorizationReply` composes text from a `Money`/`string` a caller already computed — it never receives model output directly, and it never will, per the global constraint that no number in user-facing text originates from a model.

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: the four `CategorizationReplyTests` facts pass.

- [ ] **Step 5: Write the failing tests for `CategorizationWorkerOptions`**

Create `tests/Noof.Ledger.Host.Tests/CategorizationWorkerOptionsTests.cs`:

```csharp
using AwesomeAssertions;
using Noof.Ledger.Host.Workers;

namespace Noof.Ledger.Host.Tests;

public class CategorizationWorkerOptionsTests
{
    [Fact]
    public void Defaults_match_the_documented_values()
    {
        var options = new CategorizationWorkerOptions();

        options.Lease.Should().Be(TimeSpan.FromMinutes(5));
        options.MaxAttempts.Should().Be(8);
        options.PollInterval.Should().Be(TimeSpan.FromSeconds(5));
        options.BackoffBase.Should().Be(TimeSpan.FromSeconds(30));
        options.BackoffCap.Should().Be(TimeSpan.FromMinutes(64));
        options.MerchantHintLimit.Should().Be(10);
        options.MaxCanonicalizationsPerJob.Should().Be(3);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    [InlineData(6, 960)]
    [InlineData(7, 1920)]
    [InlineData(8, 3840)]
    public void ComputeBackoff_follows_the_documented_30_second_doubling_schedule(int attemptCount, int expectedSeconds)
    {
        var options = new CategorizationWorkerOptions();

        var backoff = options.ComputeBackoff(attemptCount);

        backoff.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void ComputeBackoff_is_capped_even_when_MaxAttempts_is_raised_past_the_documented_schedule()
    {
        var options = new CategorizationWorkerOptions { BackoffCap = TimeSpan.FromMinutes(1) };

        var backoff = options.ComputeBackoff(attemptCount: 8);

        backoff.Should().Be(TimeSpan.FromMinutes(1), "the cap must actually truncate, not just happen to match the schedule at defaults");
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: build error — `The type or namespace name 'CategorizationWorkerOptions' could not be found`.

- [ ] **Step 6: Implement `CategorizationWorkerOptions`**

Create `src/Noof.Ledger.Host/Workers/CategorizationWorkerOptions.cs`:

```csharp
namespace Noof.Ledger.Host.Workers;

public sealed class CategorizationWorkerOptions
{
    // Several multiples of AnthropicOptions.Timeout's 90 seconds (Task 3), plus room for the DB
    // write and the Telegram edit, so a normal in-flight attempt never has its own lease reclaimed
    // out from under it by ReleaseExpiredLeasesAsync. Short enough that a genuinely crashed worker's
    // job is back in play within minutes rather than hours.
    public TimeSpan Lease { get; init; } = TimeSpan.FromMinutes(5);

    // Matches the schedule the Phase 1A job-queue task already documented (30s doubling reaches 64
    // minutes at attempt 8) and EfJobQueueTests' own default. Also feeds EfJobQueue's constructor
    // directly (see Program.cs) so the two never independently drift apart.
    public int MaxAttempts { get; init; } = 8;

    // Mirrors TelegramPollingService's idle poll interval: a captured message is typically
    // categorised within single-digit seconds without hammering ClaimAsync when the queue is empty.
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    // The base of the documented retry schedule (30s, 1m, 2m, 4m, ... doubling per attempt).
    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(30);

    // The ceiling the documented schedule reaches at the default MaxAttempts. Keeps growth bounded
    // if MaxAttempts is ever raised in configuration without this being revisited.
    public TimeSpan BackoffCap { get; init; } = TimeSpan.FromMinutes(64);

    // The exact limit the plan's flow diagram hardcodes for MerchantScan.Matches(rawText, aliases, 10).
    public int MerchantHintLimit { get; init; } = 10;

    // Each unknown merchant in one message costs its own model call (a knowing deviation from spec
    // section 11 - see "What this plan deliberately does NOT do"). Three is well past any real
    // message and bounds what a pathological one can spend. Lines past the cap keep their amount
    // and category and simply carry no merchant.
    public int MaxCanonicalizationsPerJob { get; init; } = 3;

    public TimeSpan ComputeBackoff(int attemptCount)
    {
        var exponent = Math.Max(0, attemptCount - 1);
        var seconds = Math.Min(BackoffCap.TotalSeconds, BackoffBase.TotalSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: `CategorizationWorkerOptionsTests` all pass (10 facts: 1 defaults + 8 theory rows + 1 cap).

- [ ] **Step 7: Write the failing tests for `CategorizationWorker`**

Create `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`:

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
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IJobQueue)).Returns(jobQueue);
        provider.GetService(typeof(ISecretStore)).Returns(secretStore);
        provider.GetService(typeof(ICategorizationStore)).Returns(store ?? Substitute.For<ICategorizationStore>());
        provider.GetService(typeof(ICategoryCatalog)).Returns(categoryCatalog ?? DefaultCategoryCatalog());
        provider.GetService(typeof(IMerchantDirectory)).Returns(merchantDirectory ?? DefaultMerchantDirectory());
        provider.GetService(typeof(ICategorizer)).Returns(categorizer ?? Substitute.For<ICategorizer>());
        provider.GetService(typeof(IChatNotifier)).Returns(notifier ?? Substitute.For<IChatNotifier>());

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
    public void CreateWorkerId_fits_the_128_character_claimed_by_column()
    {
        var id = CategorizationWorker.CreateWorkerId();

        id.Length.Should().BeLessOrEqualTo(128);
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
```

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: build error — `The type or namespace name 'CategorizationWorker' could not be found` and `The type or namespace name 'CategorizationTickResult' could not be found`.

- [ ] **Step 8: Confirm the failure is the one expected**

Re-run the same command. It must be the compile error named above — not a runtime failure, not a Postgres error (none of these tests touch a real database; every dependency is an NSubstitute fake).

- [ ] **Step 9: Implement `CategorizationWorker`**

Create `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Secrets;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Workers;

public enum CategorizationTickResult { Idle, Processed, Failed }

public sealed class CategorizationWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    CategorizationWorkerOptions options,
    string workerId,
    ILogger<CategorizationWorker> logger)
    : BackgroundService
{
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
            var secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();

            var now = timeProvider.GetUtcNow();
            await jobQueue.ReleaseExpiredLeasesAsync(now, cancellationToken);

            // Checked BEFORE claiming, deliberately out of the order the plan's flow diagram shows.
            // ClaimAsync increments attempt_count as part of the same UPDATE that claims the row, and
            // no verb undoes only that increment - claiming first and releasing on a missing key would
            // burn one of eight attempts on every tick until the operator pastes a key, and every job
            // captured before then would be permanently Failed within minutes.
            var keyStatus = await secretStore.GetStatusAsync(SecretKeys.AnthropicApiKey, cancellationToken);
            if (keyStatus.State is not SecretState.Present)
                return CategorizationTickResult.Idle;

            var job = await jobQueue.ClaimAsync(workerId, options.Lease, cancellationToken);
            if (job is null)
                return CategorizationTickResult.Idle;

            await ProcessClaimedJobAsync(scope, jobQueue, job, cancellationToken);
            return CategorizationTickResult.Processed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Categorization worker tick failed");
            return CategorizationTickResult.Failed;
        }
    }

    async Task ProcessClaimedJobAsync(
        IServiceScope scope, IJobQueue jobQueue, CategorizationJob job, CancellationToken cancellationToken)
    {
        var store = scope.ServiceProvider.GetRequiredService<ICategorizationStore>();
        var categoryCatalog = scope.ServiceProvider.GetRequiredService<ICategoryCatalog>();
        var merchantDirectory = scope.ServiceProvider.GetRequiredService<IMerchantDirectory>();
        var categorizer = scope.ServiceProvider.GetRequiredService<ICategorizer>();
        var notifier = scope.ServiceProvider.GetRequiredService<IChatNotifier>();

        CategorizationSubject? subject = null;

        try
        {
            subject = await store.GetSubjectAsync(job.TransactionId, cancellationToken);
            if (subject is not { } sub)
            {
                await FailTerminallyAsync(
                    jobQueue, store, notifier, job, null,
                    "the transaction this job points at no longer exists", cancellationToken);
                return;
            }

            var categories = await categoryCatalog.ActiveAsync(cancellationToken);
            var aliases = await merchantDirectory.AliasesAsync(cancellationToken);
            var hints = MerchantScan.Matches(sub.RawText, aliases, options.MerchantHintLimit)
                .DistinctBy(alias => alias.MerchantId)
                .Select(alias => new MerchantOption(alias.MerchantId, alias.DisplayName))
                .ToList();

            var allMerchants = await merchantDirectory.MerchantsAsync(cancellationToken);

            var request = new CategorizationRequest(
                sub.RawText,
                categories.Select(category => new CategoryOption(category.Slug, category.NameEn, category.NameRu, category.ParentSlug)).ToList(),
                hints,
                allMerchants);

            var proposal = await categorizer.ProposeAsync(request, cancellationToken);

            var offeredSlugs = categories.Select(category => category.Slug).ToHashSet();

            // The full directory, NOT just the hints. A model that called list_merchants answers with
            // an id it learned there, and validating against the short hint list would reject exactly
            // the answers that tool exists to produce - the tool would appear to work and every result
            // it influenced would fail verification.
            var offeredMerchantIds = allMerchants.Select(merchant => merchant.Id).ToHashSet();

            if (!ProposalVerification.TryResolve(sub.RawText, proposal, offeredSlugs, offeredMerchantIds, out var resolvedItems, out var failure))
            {
                await FailTerminallyAsync(jobQueue, store, notifier, job, subject, failure, cancellationToken);
                return;
            }

            var categoryNameBySlug = categories.ToDictionary(category => category.Slug, category => category.NameEn);
            var aliasByFolded = aliases.ToDictionary(alias => alias.Folded, alias => alias);
            var canonicalizations = 0;

            var categorizedItems = new List<CategorizedLineItem>(resolvedItems.Count);
            var replyLines = new List<CategorizationReply.ReplyLine>(resolvedItems.Count);

            foreach (var item in resolvedItems)
            {
                var merchantId = item.KnownMerchantId;

                if (merchantId is null && item.MerchantText is { Length: > 0 } merchantText)
                {
                    var folded = MerchantName.Fold(merchantText);

                    if (aliasByFolded.TryGetValue(folded, out var existingAlias))
                    {
                        merchantId = existingAlias.MerchantId;
                    }
                    else if (canonicalizations < options.MaxCanonicalizationsPerJob)
                    {
                        // allMerchants was already fetched for the request - not re-read here.
                        var displayName = await categorizer.CanonicalizeMerchantAsync(merchantText, allMerchants, cancellationToken);
                        var linkedId = await merchantDirectory.LinkAliasAsync(folded, displayName, cancellationToken);

                        // Written back locally so a second line naming the same new merchant hits
                        // the dictionary instead of paying for a second canonicalisation call.
                        aliasByFolded[folded] = new MerchantAliasEntry(folded, linkedId, displayName);
                        merchantId = linkedId;
                        canonicalizations++;
                    }
                    else
                    {
                        // Past the cap the line keeps its amount and category and simply has no
                        // merchant. Failing the job instead would throw away a correctly extracted
                        // bill over a field that is decoration, and the alias table stays clean.
                        logger.LogInformation(
                            "Job {JobId} reached the canonicalization cap of {Cap}; '{MerchantText}' was left unlinked",
                            job.Id, options.MaxCanonicalizationsPerJob, merchantText);
                    }
                }

                var categoryId = categories.First(category => category.Slug == item.CategorySlug).Id;
                categorizedItems.Add(new CategorizedLineItem(item.Description, item.Amount, categoryId, merchantId));
                replyLines.Add(new CategorizationReply.ReplyLine(item.Description, item.Amount, categoryNameBySlug[item.CategorySlug]));
            }

            await store.ApplyAsync(job.TransactionId, categorizedItems, cancellationToken);

            if (sub.BotMessageId is { } messageId)
            {
                try
                {
                    var text = CategorizationReply.ComposeSuccess(sub.WalletName, replyLines);
                    await notifier.EditAsync(sub.TelegramChatId, messageId, text, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The money is already committed by ApplyAsync above. A dropped edit - message
                    // deleted, token rotated, network blip - is cosmetic, not a reason to fail a job
                    // whose real work is done.
                    logger.LogWarning(ex,
                        "Failed to edit Telegram message {MessageId} for job {JobId}; the categorization itself already succeeded",
                        messageId, job.Id);
                }
            }

            var succeedOutcome = await jobQueue.SucceedAsync(job.Id, workerId, cancellationToken);
            if (succeedOutcome == JobCompletionOutcome.NotOwned)
                logger.LogWarning("Job {JobId} was already reclaimed by another worker; not retrying", job.Id);
        }
        catch (ModelCallException ex)
        {
            await HandleModelFailureAsync(jobQueue, store, notifier, job, subject, ex.Kind, ex.Message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything unmodeled here - a bug, an unexpected EF failure - is treated as Transient. The
            // attempt cap already bounds the damage: a persistent failure converges to Failed after
            // MaxAttempts instead of leaving the job Claimed for a full lease duration for no reason.
            await HandleModelFailureAsync(jobQueue, store, notifier, job, subject, ModelFailureKind.Transient, ex.Message, cancellationToken);
        }
    }

    async Task HandleModelFailureAsync(
        IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier,
        CategorizationJob job, CategorizationSubject? subject, ModelFailureKind kind, string error,
        CancellationToken cancellationToken)
    {
        if (kind == ModelFailureKind.Terminal)
        {
            await FailTerminallyAsync(jobQueue, store, notifier, job, subject, error, cancellationToken);
            return;
        }

        // The same predicate EfJobQueue.RetryAsync evaluates server-side - see decision 10. This only
        // stays correct because Program.cs feeds EfJobQueue the same CategorizationWorkerOptions.MaxAttempts.
        var isLastAttempt = job.AttemptCount >= options.MaxAttempts;
        var runAfter = timeProvider.GetUtcNow() + options.ComputeBackoff(job.AttemptCount);
        var outcome = await jobQueue.RetryAsync(job.Id, workerId, runAfter, error, cancellationToken);

        if (outcome == JobCompletionOutcome.Applied && isLastAttempt)
            await NotifyFailureAsync(store, notifier, subject, job.TransactionId, cancellationToken);
    }

    async Task FailTerminallyAsync(
        IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier,
        CategorizationJob job, CategorizationSubject? subject, string error, CancellationToken cancellationToken)
    {
        var outcome = await jobQueue.FailAsync(job.Id, workerId, error, cancellationToken);
        if (outcome == JobCompletionOutcome.Applied)
            await NotifyFailureAsync(store, notifier, subject, job.TransactionId, cancellationToken);
    }

    async Task NotifyFailureAsync(
        ICategorizationStore store, IChatNotifier notifier, CategorizationSubject? subject, Guid transactionId,
        CancellationToken cancellationToken)
    {
        await store.MarkFailedAsync(transactionId, cancellationToken);

        if (subject is not { BotMessageId: { } messageId } sub)
            return;

        try
        {
            var text = CategorizationReply.ComposeFailure(sub.WalletName);
            await notifier.EditAsync(sub.TelegramChatId, messageId, text, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to edit Telegram message {MessageId} to report a failed categorization for transaction {TransactionId}",
                messageId, transactionId);
        }
    }

    public static string CreateWorkerId()
    {
        // claimed_by is varchar(128). 64 (machine, capped) + 1 + up to 10 digits (a 32-bit process id,
        // worst case) + 1 + 8 (random suffix) = 84 characters, worst case - well inside the column. The
        // random suffix exists because a machine name plus a process id alone is not unique across a
        // crash-and-immediately-restart cycle, where the OS can recycle a process id fast enough for
        // two worker generations to collide inside the same lease window.
        var machine = Environment.MachineName.Length > 64 ? Environment.MachineName[..64] : Environment.MachineName;
        var suffix = Guid.NewGuid().ToString("N")[..8];

        return $"{machine}-{Environment.ProcessId}-{suffix}";
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: all `CategorizationWorkerTests` facts pass (21 facts), plus the earlier `CategorizationReplyTests` and `CategorizationWorkerOptionsTests` still green.

- [ ] **Step 10: Run the whole Host test project and confirm nothing else broke**

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: all green, including the pre-existing `TelegramHttpClientLoggingTests`, `LoopbackGuardTerminatesTests`, `BootTests`, etc.

- [ ] **Step 11: Wire `Program.cs`** (wiring code — exempt from TDD, but checked by the next step's test)

Edit `src/Noof.Ledger.Host/Program.cs`. Add these `using` directives near the top, alongside the existing ones:

```csharp
using Noof.Ledger.Ai;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Host.Workers;
using Noof.Ledger.Persistence.Categorization;
using Noof.Ledger.Persistence.Jobs;
using Noof.Ledger.Persistence.Reporting;
```

Add this block after the existing Telegram wiring (`builder.Services.AddHostedService<TelegramPollingService>();`) and before `var app = builder.Build();`:

```csharp
var anthropicOptions = new AnthropicOptions();
builder.Configuration.GetSection("Ai").Bind(anthropicOptions);
builder.Services.AddSingleton(anthropicOptions);

var categorizationOptions = new CategorizationWorkerOptions();
builder.Configuration.GetSection("Categorization").Bind(categorizationOptions);
builder.Services.AddSingleton(categorizationOptions);

// AddHttpClient registers IHttpClientFactory, never an HttpClient - AnthropicClientFactory takes a
// real client, so it has to be built through a lambda. Resolving HttpClient directly would fail at
// the first request with a message that names neither this line nor the factory.
builder.Services.AddHttpClient("anthropic");
builder.Services.AddScoped<IAnthropicClientFactory>(sp => new AnthropicClientFactory(
    sp.GetRequiredService<ISecretStore>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("anthropic"),
    sp.GetRequiredService<AnthropicOptions>()));

builder.Services.AddScoped<ICategorizer, AnthropicCategorizer>();
builder.Services.AddScoped<ICategoryCatalog, EfCategoryCatalog>();
builder.Services.AddScoped<IMerchantDirectory, EfMerchantDirectory>();
builder.Services.AddScoped<ICategorizationStore, EfCategorizationStore>();

// The dashboard's only way into the database (Task 6) and the Test button's only way to reach the
// model (Task 8). Both are consumed by Noof.Ledger.Web, which may never see a DbContext or an
// HttpClient - registering them here is what keeps that rule true at runtime as well as at compile
// time. ISecretProbe is registered as a collection because the page resolves IEnumerable<ISecretProbe>
// and matches on SecretKey; a second probe (Telegram's getMe) is a later addition, not a change here.
// EfSpendingReadModel takes the operator's current zone as a constructor argument, and nothing
// registers a TimeZoneInfo - a bare AddScoped<,>() here resolves fine at startup and then throws
// the first time the dashboard is opened. CaptureTimeZoneGuard.Resolve is the same call Program.cs
// already makes at boot to validate Capture:TimeZone, so there is one definition of "our zone".
builder.Services.AddScoped<ISpendingReadModel>(sp => new EfSpendingReadModel(
    sp.GetRequiredService<LedgerDbContext>(),
    sp.GetRequiredService<TimeProvider>(),
    CaptureTimeZoneGuard.Resolve(builder.Configuration["Capture:TimeZone"] ?? "Europe/Belgrade")));
builder.Services.AddScoped<ISecretProbe, AnthropicKeyProbe>();

// EfJobQueue's maxAttempts is not a separate config value: it comes straight from
// CategorizationWorkerOptions.MaxAttempts so the worker's own "is this the last attempt" check
// (CategorizationWorker.HandleModelFailureAsync) can never disagree with what the queue itself
// decides server-side.
builder.Services.AddScoped<IJobQueue>(sp => new EfJobQueue(
    sp.GetRequiredService<LedgerDbContext>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<CategorizationWorkerOptions>().MaxAttempts));

builder.Services.AddHostedService(sp => new CategorizationWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<CategorizationWorkerOptions>(),
    CategorizationWorker.CreateWorkerId(),
    sp.GetRequiredService<ILogger<CategorizationWorker>>()));
```

> If `AnthropicCategorizer`, `EfCategoryCatalog`, `EfMerchantDirectory` or `EfCategorizationStore` land from Tasks 1/3/5 with a constructor shape other than the conventional `(LedgerDbContext[, TimeProvider])`/`(ISecretStore, AnthropicOptions)` this task assumes (see `CONTRACT GAP`), swap the matching `AddScoped<TInterface, TImplementation>()` line for a factory lambda the same way `IJobQueue`'s registration already is — everything else in this task is unaffected.

- [ ] **Step 12: Write the confirming wiring tests**

Create `tests/Noof.Ledger.Host.Tests/CategorizationWiringTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Jobs;
using Noof.Ledger.Host.Workers;
using Noof.Ledger.Persistence.Jobs;

namespace Noof.Ledger.Host.Tests;

public class CategorizationWiringTests
{
    static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Mode", "Off");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Ledger",
                "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
        });

    [Fact]
    public void Every_scoped_categorization_port_resolves_without_touching_the_database()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IJobQueue>().Should().BeOfType<EfJobQueue>();
        scope.ServiceProvider.GetRequiredService<ICategorizationStore>();
        scope.ServiceProvider.GetRequiredService<ICategoryCatalog>();
        scope.ServiceProvider.GetRequiredService<IMerchantDirectory>();
        scope.ServiceProvider.GetRequiredService<ICategorizer>();
    }

    [Fact]
    public void CategorizationWorker_is_registered_as_a_hosted_service()
    {
        using var factory = Factory();

        factory.Services.GetServices<IHostedService>().Should().Contain(service => service is CategorizationWorker);
    }

    [Fact]
    public void CategorizationWorkerOptions_is_a_singleton_with_its_documented_defaults()
    {
        using var factory = Factory();

        var options = factory.Services.GetRequiredService<CategorizationWorkerOptions>();

        options.MaxAttempts.Should().Be(8);
        options.MerchantHintLimit.Should().Be(10);
        options.MaxCanonicalizationsPerJob.Should().Be(3);
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: all three `CategorizationWiringTests` facts pass. If any registration is missing or misnamed, this fails with `InvalidOperationException: Unable to resolve service for type '...'` naming exactly which one.

- [ ] **Step 13: Run the full solution**

Run: `dotnet test --solution NoofLedger.slnx`
Expected: all green — nothing in Telegram, Persistence, Domain or the rest of Host regressed.

- [ ] **Step 14: Commit**

```bash
git add tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj tests/Noof.Ledger.Host.Tests/CategorizationReplyTests.cs tests/Noof.Ledger.Host.Tests/CategorizationWorkerOptionsTests.cs tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs tests/Noof.Ledger.Host.Tests/CategorizationWiringTests.cs src/Noof.Ledger.Host/Workers/CategorizationReply.cs src/Noof.Ledger.Host/Workers/CategorizationWorkerOptions.cs src/Noof.Ledger.Host/Workers/CategorizationWorker.cs src/Noof.Ledger.Host/Program.cs
git commit -m "feat(host): CategorizationWorker claims, calls, verifies, writes, edits and completes categorization jobs"
```

---

### Task 8: The dashboard, and a Test button that never sees a secret

**Before you start — a prerequisite, and it is a real one:** this task consumes `ISpendingReadModel` (built in Task 6) and `ISecretProbe` (built in Task 3), both of which are **registered in DI by Task 7**, which owns every `Program.cs` change in this phase. Step 1 below verifies those registrations exist before you write a page against them; if they do not, stop and finish Task 7 rather than adding a second registration here.

- [ ] **Step 1: Confirm the prerequisites**

```bash
grep -n "ISpendingReadModel\|AddScoped<ISecretProbe\|AddSingleton<ISecretProbe\|AddTransient<ISecretProbe" src/Noof.Ledger.Host/Program.cs
```

If this returns nothing for either, stop — the earlier tasks must land first. Do not implement `EfSpendingReadModel` or `AnthropicKeyProbe` here.

Also confirm (read-only, no edit needed): `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs` already expects exactly `["Noof.Ledger.Application", "Noof.Ledger.Domain"]` for `Noof.Ledger.Web`'s project references and exactly `["Microsoft.AspNetCore.Components.Web", "Microsoft.AspNetCore.Components.Authorization"]` for its packages. Both are already true today. `ISpendingReadModel` (`Noof.Ledger.Application.Reporting`) and `ISecretProbe` (`Noof.Ledger.Application.Secrets`) are both `Application` types, so injecting them needs no `ProjectReference` or `PackageReference` change — confirmed by reading, not by building. `Noof.Ledger.Web.csproj` is **not modified** by this task.

**Files:**
- Modify: `src/Noof.Ledger.Web/Components/Pages/Home.razor`
- Modify: `src/Noof.Ledger.Web/Components/Pages/Settings/Secrets.razor`
- Modify: `src/Noof.Ledger.Web/wwwroot/app.css`
- Create: `tests/Noof.Ledger.Architecture.Tests/DashboardPageSourceTests.cs`
- Modify: `tests/Noof.Ledger.Architecture.Tests/SecretsPageSourceTests.cs`
- Create: `tests/Noof.Ledger.E2E.Tests/DashboardTests.cs`
- Modify: `tests/Noof.Ledger.E2E.Tests/SettingsSecretsTests.cs`
- Modify: `tests/Noof.Ledger.E2E.Tests/CookieModeHostFixture.cs`
- Not modified: `src/Noof.Ledger.Host/Program.cs` (DI registration is Tasks 3/6's job — see Step 1), `src/Noof.Ledger.Web/Noof.Ledger.Web.csproj`, `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs`

**Interfaces:**
- Consumes: `ISpendingReadModel`, `RecentTransaction`, `RecentLineItem`, `MonthSummary`, `MonthTotal` from `Noof.Ledger.Application.Reporting` (contract: Task 1; implementation `EfSpendingReadModel`: Task 6).
- Consumes: `ISecretProbe`, `ProbeResult` from `Noof.Ledger.Application.Secrets` (contract: Task 1; implementation `AnthropicKeyProbe`: Task 3). `ISecretStore`, `SecretStatus`, `SecretState`, `SecretKeys` (existing, Phase 1A).
- Produces: the dashboard at `GET /`; a Test button on `/settings/secrets` scoped to whichever `ISecretProbe` implementations DI resolves; `CookieModeHostFixture.ConnectionString` (new, additive — exposes the clone database for direct EF seeding, the way `ReadStoredSecretAsync` already exposes it for direct reads).

---

#### Locked design decisions

**1. Money and dates render through `CultureInfo.InvariantCulture`, explicitly, always — never the server's `CurrentCulture`.** This host runs on the operator's own machine, which `CurrencyCodeTests.Orders_ordinally_regardless_of_culture` already proves may be `ru-RU` or `sr-Latn-RS` — those cultures use `,` as the decimal separator and reorder date components. A bare `@item.Amount.Amount` interpolation or a `"C"` format specifier would render differently depending on which Windows locale happens to be active on the host machine, on a page this plan has no locale-specific fixture for. The rule: every number is formatted with `.ToString("N2", CultureInfo.InvariantCulture)` and a currency **code** suffix (`"250.00 RSD"`), never a currency **symbol** — `Money` is `decimal` + `CurrencyCode`, not a `RegionInfo`, so there is no correct symbol to ask .NET for. Every date is formatted with a fixed `"yyyy-MM-dd HH:mm"` pattern and `CultureInfo.InvariantCulture`, not a `"g"`/`"G"` standard format (those are culture-dependent by design). This is what keeps the E2E assertions in this task working identically on every machine that runs them, including CI and the operator's own `sr-Latn-RS` box — the alternative is a page that passes here and fails there.

**2. Each transaction's time is rendered in its own stored zone, never the browser's or the server's — decision P1-3.** `RecentTransaction.TimeZoneId` is an IANA id stamped at capture time (`docs/OPEN-QUESTIONS.md` P1-3: "the zone lives on the row"). The page resolves it with `TimeZoneInfo.FindSystemTimeZoneById` and converts with `TimeZoneInfo.ConvertTime` before formatting. No fallback or try/catch around a missing zone: `TimeZoneId` is written by capture from a known-good current-zone setting, so a failure to resolve it here is a genuine upstream bug this page should surface loudly, not mask — matching CLAUDE.md's "no defensive boilerplate for conditions that cannot occur."

**3. `Home.razor` stays static server-rendered — no `@rendermode`.** The file has no rendermode today and this task does not add one. The dashboard has nothing to hide (unlike `Secrets.razor`, which forces `prerender: false` specifically because prerendering could leak a fetched secret status), and it has no interactive control at all — the Test button lives on the Secrets page, not here. Rendering statically means `OnInitializedAsync` runs once, synchronously with the HTTP response, with no SignalR circuit to attach — which also removes the exact race Task 5's `SettingsSecretsTests` had to work around with `RetryUntilAsync` (a click landing before the circuit finishes attaching). The dashboard's E2E tests below need no such retry wrapper because there is no circuit-attachment window to race against.

**4. The month-totals table is grouped by currency in the markup, not just in the data.** `MonthSummary.Totals` is already a flat list of `(CategoryName, Currency, Amount)` rows that individually never mix currencies, but a flat table inviting the eye to add a column of mixed-currency numbers would misrepresent that. The page groups `Totals` by `Currency.Value` in `@code` and renders one `<table id="month-totals-{CURRENCY}">` per currency, so "never summed across currencies" is visible in the structure, not just true of the underlying numbers.

**5. Awaiting and failed transactions are rendered as distinct, explicit states, never as an empty item list.** `TransactionStatus.Captured` and `TransactionStatus.Failed` transactions have no line items yet (per the contract, `ApplyAsync` only ever runs on a successful proposal) — rendering their (empty) `Items` list as-is would look identical to "categorised into nothing," which is not a state that can occur and would read as a bug. The page switches on `Status` and shows an explicit sentence — "Awaiting categorisation" / "Categorisation failed" — instead of an empty `<ul>`.

**6. The Test button never touches `ISecretStore.GetAsync`.** `SecretsPageSourceTests.Reads_status_only_and_never_the_plaintext_secret` already asserts the page source never contains the substring `"GetAsync("`. The button is wired to `ISecretProbe.ProbeAsync` only, resolved by matching `IEnumerable<ISecretProbe>` against `row.Key` — a row with no matching probe renders no button at all, rather than a button that would always fail. This task adds a fourth fact to that same test file asserting the source contains `"ProbeAsync("` and still does not contain `"GetAsync("`, so the property is enforced structurally, not just by review.

**7. Only the probe's failure path is E2E-tested, not its success path.** A true success case needs a real, working Anthropic API key, and this is a public repository that must never hold one (CLAUDE.md §4, "Secrets — this is a public repo"). The E2E test instead saves an obviously-fake key, clicks Test, and asserts the resulting message (a) is shown and (b) never contains the fake key substring — proving the page renders `ProbeResult.Message` verbatim rather than building its own string that could echo `row.PendingValue`. This calls the real `api.anthropic.com` (the contract's `AnthropicKeyProbe` — `GET /v1/models`, "costs no tokens") because the E2E suite spins up the real host with real DI and there is no seam here to substitute a fake probe; the test guards itself with the same reachability-skip pattern `CookieModeHostFixture` already uses for a missing PostgreSQL instance, rather than assuming internet access.

**8. `Noof.Ledger.E2E.Tests` is still not registered in `NoofLedger.slnx`.** Every E2E task since Phase 1A has left it out, so `dotnet test --solution NoofLedger.slnx` never runs it — the exact command that does is `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`. This task does not change that (out of scope — it is not this task's `.slnx`/CI wiring to decide), but it is now carrying dashboard rendering and secret-probe-leak coverage, not just login/settings smoke coverage, which makes the omission a heavier bet each phase. **Flag for Task 9:** decide, explicitly, whether `Noof.Ledger.E2E.Tests` joins the solution (and what that does to a plain `dotnet test` run's runtime and its Playwright/browser-install dependency) or whether the omission is intentional and should be documented as such in `CLAUDE.md` rather than left implicit.

---

- [ ] **Step 2: Expose the clone database's connection string for direct seeding**

`DashboardTests` (Step 4) needs to write a categorised transaction straight into the clone database, the same way `ReadStoredSecretAsync` already reads one back. In `tests/Noof.Ledger.E2E.Tests/CookieModeHostFixture.cs`, add a property next to the existing `BaseUrl`/`CapturedOutputLines`:

```csharp
    public string BaseUrl => host.BaseUrl;

    public IReadOnlyList<string> CapturedOutputLines => host.CapturedOutputLines;

    // Lets a test seed rows directly into the same clone database the spawned host process reads
    // from - the dashboard has nothing to save through the UI itself, unlike the secrets page, so
    // this is the only way to get a categorised transaction in front of it without also building and
    // running the categorisation worker inside this test.
    public string ConnectionString => DatabaseSettings.For(cloneDatabaseName);
```

This is purely additive; nothing else in the file changes.

- [ ] **Step 3: Write the failing dashboard E2E tests**

Create `tests/Noof.Ledger.E2E.Tests/DashboardTests.cs`:

```csharp
using System.Globalization;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.E2E.Tests;

public sealed class DashboardTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    // From migration AddCaptureModel's seed SQL - present in every clone because the clone is
    // created FROM the already-migrated template database, not migrated fresh by this fixture
    // (CookieModeHostFixture starts the host with Database__MigrateOnStartup=false).
    static readonly Guid DefaultWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly Guid CoffeeCategoryId = new("00000000-0000-0000-0001-000000000017");

    [Fact]
    public async Task Dashboard_shows_a_categorised_transaction_and_its_month_total()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var cancellationToken = TestContext.Current.CancellationToken;
        var description = $"кофе {Guid.NewGuid():N}";
        var transactionId = Guid.NewGuid();
        var merchantId = Guid.NewGuid();
        var chatId = DateTimeOffset.UtcNow.Ticks;

        await using (var db = OpenDb())
        {
            db.Merchants.Add(new Merchant { Id = merchantId, DisplayName = "Kafeterija", Kind = MerchantKind.Retail });
            db.Transactions.Add(new Transaction
            {
                Id = transactionId,
                WalletId = DefaultWalletId,
                RawText = $"{description} 250 рсд",
                Status = TransactionStatus.Completed,
                TimeZoneId = "Europe/Belgrade",
                OccurredAt = DateTimeOffset.UtcNow,
                TelegramChatId = chatId,
                TelegramMessageId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.LineItems.Add(new LineItem
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                Description = description,
                Amount = new Money(250.00m, CurrencyCode.Rsd),
                CategoryId = CoffeeCategoryId,
                CategorizedBy = CategorizationAuthority.Model,
                MerchantId = merchantId,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var article = Page.Locator($"#txn-{transactionId}");
        var amountText = 250.00m.ToString("N2", CultureInfo.InvariantCulture);

        await Expect(article).ToContainTextAsync(description);
        await Expect(article).ToContainTextAsync("Kafeterija");
        await Expect(article).ToContainTextAsync("Main Wallet");
        await Expect(article).ToContainTextAsync(amountText);
        await AssertContainsCoffeeCategoryAsync(article);

        var monthTotals = Page.Locator("#month-totals-RSD");
        await Expect(monthTotals).ToContainTextAsync(amountText);
        await AssertContainsCoffeeCategoryAsync(monthTotals);
    }

    [Fact]
    public async Task Awaiting_and_failed_transactions_render_distinct_states_not_an_empty_result()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var cancellationToken = TestContext.Current.CancellationToken;
        var awaitingId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        var chatId = DateTimeOffset.UtcNow.Ticks;

        await using (var db = OpenDb())
        {
            db.Transactions.Add(NewTransaction(awaitingId, chatId, 1, TransactionStatus.Captured));
            db.Transactions.Add(NewTransaction(failedId, chatId, 2, TransactionStatus.Failed));
            await db.SaveChangesAsync(cancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator($"#txn-{awaitingId}")).ToContainTextAsync("Awaiting categorisation");
        await Expect(Page.Locator($"#txn-{failedId}")).ToContainTextAsync("Categorisation failed");
    }

    // CategoryName is Task 6's EfSpendingReadModel picking between the category's bilingual NameEn /
    // NameRu (P1-1) - nothing in the Task 1 contract or in docs/OPEN-QUESTIONS.md commits to which
    // one, so this test accepts either rather than baking in an assumption Task 6 hasn't made yet.
    static async Task AssertContainsCoffeeCategoryAsync(ILocator scope)
    {
        var text = await scope.TextContentAsync() ?? string.Empty;
        // The English name, not the Russian one: EfSpendingReadModel resolves categories.name_en
        // (Task 6), because the settled rule is that the interface is English and only model-authored
        // prose is Russian. Asserting "either name" would let a read model that silently switched
        // languages pass this test.
        text.Contains("Coffee", StringComparison.Ordinal)
            .Should().BeTrue("the coffee category's display name must appear, in whichever of NameEn/NameRu the read model chooses");
    }

    LedgerDbContext OpenDb() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    static Transaction NewTransaction(Guid id, long chatId, int messageId, TransactionStatus status) => new()
    {
        Id = id,
        WalletId = DefaultWalletId,
        RawText = $"seeded {status} transaction {id:N}",
        Status = status,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = DateTimeOffset.UtcNow,
        TelegramChatId = chatId,
        TelegramMessageId = messageId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    async Task SignInAsync()
    {
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForURLAsync("**/account/login*");
        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(fixture.BaseUrl + "/");
    }
}
```

> `TelegramChatId`/`TelegramMessageId` carries a unique index (`TransactionConfiguration`), so both seeded rows in the second test share one `chatId` derived from `DateTimeOffset.UtcNow.Ticks` (unique enough across runs) with distinct literal `messageId`s (`1`, `2`) rather than anything random that could collide with itself.

- [ ] **Step 4: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`

Expected: `LoginTests`, `SmokeTests`, `SettingsSecretsTests` still PASS (Step 2 was purely additive). Both new `DashboardTests` facts FAIL — `Microsoft.Playwright.PlaywrightException: Timeout ... exceeded` from `Expect(article).ToContainTextAsync(...)` / `Expect(Page.Locator($"#txn-{awaitingId}"))...`, because `Home.razor` still renders "Nothing to show yet." with no `#txn-*` elements at all.

- [ ] **Step 5: Write the failing Test-button E2E tests**

Add two facts and a helper to `tests/Noof.Ledger.E2E.Tests/SettingsSecretsTests.cs`, alongside the existing four facts (leave everything already there unchanged):

```csharp
    [Fact]
    public async Task Testing_an_invalid_Anthropic_key_reports_failure_without_echoing_it()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");
        if (!await AnthropicIsReachableAsync())
            Assert.Skip("api.anthropic.com is not reachable from this machine.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var fakeKey = $"sk-ant-invalid-{Guid.NewGuid():N}";
        var status = Page.Locator($"#status-{SecretKeys.AnthropicApiKey}");

        await RetryUntilAsync(async () =>
        {
            await Page.Locator($"#secret-{SecretKeys.AnthropicApiKey}").FillAsync(fakeKey);
            await Page.Locator($"#save-{SecretKeys.AnthropicApiKey}").ClickAsync();
            await Expect(status).ToContainTextAsync("Set", new() { Timeout = 2_000 });
        });

        var result = Page.Locator($"#test-result-{SecretKeys.AnthropicApiKey}");
        await Page.Locator($"#test-{SecretKeys.AnthropicApiKey}").ClickAsync();
        await Expect(result).ToBeVisibleAsync(new() { Timeout = 15_000 });

        var message = await result.TextContentAsync() ?? string.Empty;
        message.Should().NotBeEmpty();
        message.Should().NotContain(fakeKey, "the probe's failure message must never echo the key it was testing");
    }

    [Fact]
    public async Task A_secret_with_no_registered_probe_shows_no_Test_button()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/settings/secrets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Phase 1B ships a probe for the Anthropic key only - the Telegram equivalent is backlog
        // (see "What this plan deliberately does NOT do"). A row with no matching ISecretProbe must
        // stay silent about it rather than showing a button that would always fail.
        await Expect(Page.Locator($"#test-{SecretKeys.TelegramBotToken}")).Not.ToBeVisibleAsync();
    }

    static async Task<bool> AnthropicIsReachableAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync("https://api.anthropic.com/v1/models");
            return true;
        }
        catch
        {
            return false;
        }
    }
```

- [ ] **Step 6: Run it and watch it fail**

Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`

Expected: the two new `DashboardTests` facts still FAIL as in Step 4. `Testing_an_invalid_Anthropic_key_reports_failure_without_echoing_it` FAILS with a Playwright timeout from `Page.Locator($"#test-{SecretKeys.AnthropicApiKey}").ClickAsync()` — no such button exists yet. `A_secret_with_no_registered_probe_shows_no_Test_button` PASSES already (there is no `#test-*` button of any kind on the current page, so "not visible" is trivially true) — that is expected and will stay true after Step 9 too, since it is asserting an absence.

- [ ] **Step 7: Write the failing architecture (source) tests**

Create `tests/Noof.Ledger.Architecture.Tests/DashboardPageSourceTests.cs`:

```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class DashboardPageSourceTests
{
    static string SourceText() => File.ReadAllText(Path.Combine(
        RepoRoot.Find().FullName, "src", "Noof.Ledger.Web", "Components", "Pages", "Home.razor"));

    [Fact]
    public void Formats_every_amount_and_date_against_invariant_culture()
    {
        var source = SourceText();

        source.Should().Contain("CultureInfo.InvariantCulture",
            "money and dates must render the same way regardless of the host machine's OS locale (see CurrencyCodeTests.Orders_ordinally_regardless_of_culture)");
        source.Should().NotContain(".ToString(\"C\"",
            "a culture-formatted currency string depends on the server's locale for both the separator and the symbol - this page has a CurrencyCode, not a RegionInfo, to ask for one");
    }

    [Fact]
    public void Reads_spending_through_the_read_model_only()
    {
        SourceText().Should().Contain("ISpendingReadModel");
    }
}
```

Add one more fact to `tests/Noof.Ledger.Architecture.Tests/SecretsPageSourceTests.cs`, alongside the existing three (leave those untouched):

```csharp
    [Fact]
    public void Test_button_calls_the_probe_port_not_the_secret_store()
    {
        var source = SourceText();

        source.Should().Contain("ProbeAsync(",
            "the Test button must go through ISecretProbe, which structurally cannot hand back a plaintext secret");
        source.Should().NotContain("GetAsync(",
            "re-asserted here: wiring up the probe must not introduce a second path back to the plaintext secret");
    }
```

- [ ] **Step 8: Run the architecture tests and watch the new ones fail**

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`

Expected: the three existing `SecretsPageSourceTests` facts and every other architecture test still PASS. `DashboardPageSourceTests.Formats_every_amount_and_date_against_invariant_culture` and `.Reads_spending_through_the_read_model_only` FAIL — the current `Home.razor` contains neither `"CultureInfo.InvariantCulture"` nor `"ISpendingReadModel"`. `SecretsPageSourceTests.Test_button_calls_the_probe_port_not_the_secret_store` FAILS — the current `Secrets.razor` contains no `"ProbeAsync("`.

- [ ] **Step 9: Write the dashboard**

Replace `src/Noof.Ledger.Web/Components/Pages/Home.razor` in full:

```razor
@page "/"
@attribute [Authorize]
@using System.Globalization
@using Noof.Ledger.Application.Reporting
@using Noof.Ledger.Domain
@inject ISpendingReadModel ReadModel

<PageTitle>Ledger</PageTitle>

<h1>Ledger</h1>

<section>
    <h2>Recent transactions</h2>
    @if (recent.Count == 0)
    {
        <p>Nothing captured yet.</p>
    }
    else
    {
        @foreach (var transaction in recent)
        {
            <article id="txn-@transaction.Id" class="transaction @StatusClass(transaction.Status)">
                <header>
                    <span class="wallet">@transaction.WalletName</span>
                    <span class="when">@FormatOccurredAt(transaction)</span>
                    <span class="status">@DescribeStatus(transaction.Status)</span>
                </header>
                <p class="raw">@transaction.RawText</p>
                @if (transaction.Status == TransactionStatus.Captured)
                {
                    <p class="note">Awaiting categorisation - this message has not been read by the categoriser yet.</p>
                }
                else if (transaction.Status == TransactionStatus.Failed)
                {
                    <p class="note note-failed">Categorisation failed. Nothing was recorded for this message.</p>
                }
                else
                {
                    <ul>
                        @foreach (var item in transaction.Items)
                        {
                            <li>
                                <span class="category">@(item.CategoryName ?? "Uncategorised")</span>
                                <span class="description">@item.Description</span>
                                @if (item.MerchantName is not null)
                                {
                                    <span class="merchant">(@item.MerchantName)</span>
                                }
                                <span class="amount">@FormatMoney(item.Amount)</span>
                            </li>
                        }
                    </ul>
                }
            </article>
        }
    }
</section>

<section class="month-totals">
    <h2>This month (from @summaryFirstDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))</h2>
    @if (currencyGroups.Count == 0)
    {
        <p>No spending recorded this month.</p>
    }
    else
    {
        @foreach (var group in currencyGroups)
        {
            <h3>@group.Currency</h3>
            <table id="month-totals-@group.Currency">
                <tbody>
                    @foreach (var total in group.Totals)
                    {
                        <tr>
                            <td>@total.CategoryName</td>
                            <td class="amount">@total.Amount.ToString("N2", CultureInfo.InvariantCulture)</td>
                        </tr>
                    }
                </tbody>
            </table>
        }
    }
</section>

@code {
    IReadOnlyList<RecentTransaction> recent = [];
    IReadOnlyList<CurrencyGroup> currencyGroups = [];
    DateOnly summaryFirstDay;

    sealed record CurrencyGroup(string Currency, IReadOnlyList<MonthTotal> Totals);

    protected override async Task OnInitializedAsync()
    {
        recent = await ReadModel.RecentAsync(20, CancellationToken.None);

        var summary = await ReadModel.ThisMonthAsync(CancellationToken.None);
        summaryFirstDay = summary.FirstDay;
        currencyGroups =
        [
            .. summary.Totals
                .GroupBy(t => t.Currency.Value)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new CurrencyGroup(g.Key, [.. g.OrderBy(t => t.CategoryName, StringComparer.Ordinal)])),
        ];
    }

    static string FormatMoney(Money money) =>
        $"{money.Amount.ToString("N2", CultureInfo.InvariantCulture)} {money.Currency}";

    // The row's own time zone, not the browser's and not the server's - decision P1-3. TimeZoneId is
    // stamped from a known IANA id at capture time, so a failure to resolve it here is a genuine bug
    // upstream, not a condition this page should mask.
    static string FormatOccurredAt(RecentTransaction transaction)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(transaction.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(transaction.OccurredAt, zone);
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    static string DescribeStatus(TransactionStatus status) => status switch
    {
        TransactionStatus.Captured => "Awaiting categorisation",
        TransactionStatus.Completed => "Categorised",
        TransactionStatus.Failed => "Categorisation failed",
        _ => "Unknown",
    };

    static string StatusClass(TransactionStatus status) => status switch
    {
        TransactionStatus.Captured => "status-awaiting",
        TransactionStatus.Completed => "status-completed",
        TransactionStatus.Failed => "status-failed",
        _ => "status-unknown",
    };
}
```

Append to `src/Noof.Ledger.Web/wwwroot/app.css` (the file stays plain — this is layout and legibility only, no theme, no framework):

```css
.transaction {
    border: 1px solid #d0d0d0;
    border-radius: 4px;
    padding: 0.5rem 1rem;
    margin-bottom: 0.75rem;
}

.transaction .status {
    font-weight: 600;
}

.transaction.status-failed .status,
.note-failed {
    color: #b00020;
}

.transaction.status-awaiting .status {
    color: #8a6100;
}

.month-totals table {
    border-collapse: collapse;
    margin: 0 0 1rem;
}

.month-totals td {
    padding: 0.2rem 0.75rem;
}

.month-totals td.amount {
    text-align: right;
}

.test-ok {
    color: #146c2e;
}

.test-fail {
    color: #b00020;
}
```

> No `@rendermode` directive is added — see locked decision 3. `ISpendingReadModel` and everything it returns (`RecentTransaction`, `RecentLineItem`, `MonthSummary`, `MonthTotal`) live in `Noof.Ledger.Application.Reporting`, already reachable through the existing `ProjectReference` to `Noof.Ledger.Application` — no `.csproj` edit.

- [ ] **Step 10: Add the Test button**

Replace `src/Noof.Ledger.Web/Components/Pages/Settings/Secrets.razor` in full:

```razor
@page "/settings/secrets"
@attribute [Authorize]
@using Noof.Ledger.Application.Secrets
@inject ISecretStore SecretStore
@inject IEnumerable<ISecretProbe> Probes
@* Prerendering would run this component's code during the initial static HTTP response, letting a
   fetched secret status leak into the served HTML before the interactive circuit even exists - so
   this page never prerenders. *@
@rendermode @(new InteractiveServerRenderMode(prerender: false))

<PageTitle>Secrets</PageTitle>

<h1>Secrets</h1>

@foreach (var row in rows)
{
    <section>
        <h2>@row.Label</h2>
        <p id="status-@row.Key">@Describe(row.Status)</p>
        @if (row.Error is not null)
        {
            <p id="error-@row.Key">@row.Error</p>
        }
        <input id="secret-@row.Key" type="password" autocomplete="off" @bind="row.PendingValue" />
        <button id="save-@row.Key" type="button" @onclick="() => SaveAsync(row)">Save</button>
        @if (row.Probe is not null)
        {
            <button id="test-@row.Key" type="button" disabled="@row.Testing" @onclick="() => TestAsync(row)">
                @(row.Testing ? "Testing..." : "Test")
            </button>
            @if (row.TestOk is not null)
            {
                <p id="test-result-@row.Key" class="@(row.TestOk == true ? "test-ok" : "test-fail")">@row.TestMessage</p>
            }
        }
    </section>
}

@code {
    sealed class Row
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public SecretStatus Status { get; set; }
        public string PendingValue { get; set; } = string.Empty;
        public string? Error { get; set; }
        public ISecretProbe? Probe { get; set; }
        public bool Testing { get; set; }
        public bool? TestOk { get; set; }
        public string? TestMessage { get; set; }
    }

    readonly List<Row> rows =
    [
        new() { Key = SecretKeys.AnthropicApiKey, Label = "Anthropic API key" },
        new() { Key = SecretKeys.TelegramBotToken, Label = "Telegram bot token" },
        new() { Key = SecretKeys.TelegramOwnerChatId, Label = "Telegram owner chat id" },
    ];

    protected override async Task OnInitializedAsync()
    {
        foreach (var row in rows)
        {
            row.Status = await SecretStore.GetStatusAsync(row.Key, CancellationToken.None);
            row.Probe = Probes.SingleOrDefault(p => p.SecretKey == row.Key);
        }
    }

    async Task SaveAsync(Row row)
    {
        // Trim first: a leading space or trailing newline pasted alongside a token is invisible in
        // a password input and would otherwise be stored verbatim, breaking every call that uses it
        // (a bot token 404s; a chat id matches nothing) with only an opaque downstream error to show
        // for it. An empty result after trimming must be refused, not stored: for
        // telegram-owner-chat-id specifically, storing "" still leaves SecretState.Present, which
        // both rejects every chat AND leaves the first-message owner claim permanently disarmed --
        // silently, since Present looks the same as a real value everywhere else that reads it.
        var trimmed = row.PendingValue.Trim();
        if (trimmed.Length == 0)
        {
            row.Error = "Enter a value before saving.";
            return;
        }

        row.Error = null;
        await SecretStore.SetAsync(row.Key, trimmed, CancellationToken.None);
        row.PendingValue = string.Empty;
        row.Status = await SecretStore.GetStatusAsync(row.Key, CancellationToken.None);
    }

    // The probe is the only way this button can learn whether a stored key works: ISecretProbe
    // structurally cannot hand back a plaintext value, so there is nothing here for a future edit to
    // accidentally render. StateHasChanged before the await is what makes "Testing..." actually
    // appear during the round trip, rather than only after it completes.
    async Task TestAsync(Row row)
    {
        if (row.Probe is null)
            return;

        row.Testing = true;
        row.TestOk = null;
        row.TestMessage = null;
        StateHasChanged();

        var result = await row.Probe.ProbeAsync(CancellationToken.None);

        row.Testing = false;
        row.TestOk = result.Ok;
        row.TestMessage = result.Message;
    }

    static string Describe(SecretStatus status) => status.State switch
    {
        SecretState.Present => $"Set (updated {status.UpdatedAt?.ToString("u") ?? "unknown"})",
        SecretState.Missing => "Not set",
        SecretState.Unreadable => "Unreadable - re-enter this value",
        _ => "Unknown",
    };
}
```

> `row.Probe = Probes.SingleOrDefault(...)` uses `SingleOrDefault`, not `FirstOrDefault`: two probes claiming the same `SecretKey` is a DI wiring bug (Task 3's registration, not this page's business), and this makes that bug throw loudly during `OnInitializedAsync` instead of silently picking one. Whatever DI resolves for `Probes` today (Task 3 registers `AnthropicKeyProbe`, `SecretKey = SecretKeys.AnthropicApiKey`) is exactly what determines which rows get a button — the Telegram rows get none, matching "What this plan deliberately does NOT do."

- [ ] **Step 11: Run the architecture tests again**

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`

Expected: PASS — all `DashboardPageSourceTests` and `SecretsPageSourceTests` facts, and every pre-existing architecture test including `ProjectReferenceTests.Project_references_exactly_its_allowed_set` and `.Web_package_references_are_exactly_its_allowed_set` (both untouched by this task, confirming Step 1's claim that no reference or package changed).

- [ ] **Step 12: Run the E2E tests again**

Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`

Expected: PASS — `LoginTests`, `SmokeTests`, all six `SettingsSecretsTests` facts, and both `DashboardTests` facts. `Testing_an_invalid_Anthropic_key_reports_failure_without_echoing_it` may report SKIPPED instead, with the reachability message, on a machine with no route to `api.anthropic.com` — that is expected there and is not a failure.

- [ ] **Step 13: Confirm the rest of the suite is unaffected**

Run: `dotnet test --solution NoofLedger.slnx`

Expected: all green, no regressions. `Noof.Ledger.E2E.Tests` is not part of `NoofLedger.slnx` (see locked decision 8), so this run and Step 12 check disjoint things, same as every earlier E2E task in this project.

- [ ] **Step 14: Commit**

```bash
git add src/Noof.Ledger.Web/Components/Pages/Home.razor src/Noof.Ledger.Web/Components/Pages/Settings/Secrets.razor src/Noof.Ledger.Web/wwwroot/app.css tests/Noof.Ledger.Architecture.Tests/DashboardPageSourceTests.cs tests/Noof.Ledger.Architecture.Tests/SecretsPageSourceTests.cs tests/Noof.Ledger.E2E.Tests/DashboardTests.cs tests/Noof.Ledger.E2E.Tests/SettingsSecretsTests.cs tests/Noof.Ledger.E2E.Tests/CookieModeHostFixture.cs
git commit -m "feat(web): dashboard reads ISpendingReadModel, Anthropic key gets a Test button that never sees the secret"
```

---

## CONTRACT GAP

~~Which of a category's two names the read model puts in `CategoryName`.~~ **CLOSED: `NameEn`.** The contract now says so at `MonthTotal`, Task 6 resolves `categories.name_en`, and this task's E2E test asserts the English name rather than accepting either. The reasoning is the settled rule that the interface is English while only model-authored prose is Russian — `NameRu` exists so the model reads a Russian message well, not so the dashboard renders one. The original note is kept below because the hedge it describes was a real symptom of the gap.

**(original)** `RecentLineItem.CategoryName` and `MonthTotal.CategoryName` are both plain `string`/`string?`, already resolved by the read model — but neither the Task 1 contract nor `docs/OPEN-QUESTIONS.md` (P1-1: "bilingual `NameEn`/`NameRu`, renameable") states **which** of a category's two names `EfSpendingReadModel` (Task 6) is supposed to put there. This task's page treats `CategoryName` as an opaque already-decided string (correctly — that choice belongs to Task 6, not the page), but its E2E test had to hedge against both known seed values ("Coffee" / "Кофе") rather than asserting one, because Task 8 is drafted before Task 6 exists and cannot know which the eventual implementation will pick. Task 6 should make this choice deliberately (a fixed field, a per-user setting, or always both) and record it — this plan does not invent an answer for it.

---

### Task 9: Architecture rules, the phase acceptance, and the close

**Files:**
- Modify: `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs`
- Create: `tests/Noof.Ledger.Architecture.Tests/AiBoundaryTests.cs`
- Modify: `NoofLedger.slnx`
- Modify: `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs` — the flat path Task 7 actually creates, not a `Workers/` subfolder
- Modify: `CLAUDE.md`
- Modify: `docs/BACKLOG.md`

**Interfaces:**
- Consumes: `IJobQueue` (`Noof.Ledger.Application.Jobs`, five members: `ClaimAsync`, `SucceedAsync`, `RetryAsync`, `FailAsync`, `ReleaseExpiredLeasesAsync` — fixed by Phase 1A Task 7), `IChatNotifier` (`Noof.Ledger.Application.Chat`: `SendAsync(long, string, CancellationToken)`, `EditAsync(long, int, string, CancellationToken)` — fixed by Phase 1A Task 8), and every port in this plan's own contract section (`ICategorizer`, `ICategorizationStore`, `ProposalVerification`, `ModelCallException`). `CategorizationWorker` itself is consumed, not produced, here — its constructor and tick entry point are whatever the earlier task that builds it fixed; see the CONTRACT GAP note.
- Produces: no new production signatures. This task adds tests and documentation only — it is the phase's enforcement and closing task, not a feature.

This task adds no feature. It makes the phase's guarantees enforceable and proves the phase is done. It has five parts: architecture tests, the solution file, the phase acceptance, the close, and the closing review.

---

## Part 1 — Architecture tests

- [ ] **Step 1: The `Noof.Ledger.Ai` package set, exactly**

Add to `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs`, next to `Web_package_references_are_exactly_its_allowed_set` and `Telegram_package_references_are_exactly_its_allowed_set`:

```csharp
    [Fact]
    public void Ai_package_references_are_exactly_its_allowed_set()
    {
        Packages("Noof.Ledger.Ai").Should().BeEquivalentTo("Anthropic");
    }
```

> **Verifying the "one package" claim against the helper, not just the fact table.** `Packages(project)` (bottom of this file) reads `<PackageReference>` elements straight out of the `.csproj` XML with `XDocument.Descendants("PackageReference")` — it never touches NuGet's dependency graph or a lock file. `Microsoft.Extensions.AI.Abstractions`, `System.Net.ServerSentEvents` and `System.Text.Json` are dependencies **of the `Anthropic` package itself**, resolved by NuGet at restore time; they are never written as `<PackageReference>` lines in `Noof.Ledger.Ai.csproj` (confirmed by the plan's own fact table, sourced from the nuget.org registration API — see "Verified facts" §). So a purely XML-reading helper seeing exactly one entry, `Anthropic`, is not a simplification this test is getting away with — it is the literally correct exact set for this project's own `.csproj`, and `BeEquivalentTo` (same elements, no more, no fewer, order-independent) is the right assertion, matching how `Web_package_references_are_exactly_its_allowed_set` and `Telegram_package_references_are_exactly_its_allowed_set` already work.

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: **green**, not red. Task 1 of this plan already added the `Anthropic` `PackageReference` to `Noof.Ledger.Ai.csproj` to compile against the SDK at all; this fact is a lock on that state, the same non-red-first shape as `Domain_has_no_package_references` and `Application_has_no_package_references` already in this file — an assertion about a fact the implementation already satisfies, guarding it from regressing.

- [ ] **Step 2: `Noof.Ledger.Web` → `Noof.Ledger.Ai` — already covered, verified rather than duplicated**

`Project_references_exactly_its_allowed_set`'s `InlineData` for `Noof.Ledger.Web` is `("Noof.Ledger.Web", "Noof.Ledger.Application", "Noof.Ledger.Domain")`. `References("Noof.Ledger.Web")` returns every `<ProjectReference>` in `Web.csproj` by filename, and the assertion is `.Should().BeEquivalentTo(allowed)` — an **exact-set** match (same elements, same count, order-independent; AwesomeAssertions' `BeEquivalentTo` on a collection is not a subset check). `Noof.Ledger.Ai` is not in that `allowed` list, so the moment anyone adds `<ProjectReference Include="..\Noof.Ledger.Ai\Noof.Ledger.Ai.csproj" />` to `Web.csproj`, `References("Noof.Ledger.Web")` grows to three elements and this existing test fails. **No new test is needed for this rule — it is already enforced.** Do not add a second, redundant assertion for it. State this in the PR/commit body so a reviewer doesn't go looking for a missing test.

- [ ] **Step 3: The rule that is NOT yet covered — nothing outside `Noof.Ledger.Ai` touches the Anthropic SDK namespace**

Blocking `Web`'s project reference to `Ai` stops the most obvious route, but it is not the only one. `Noof.Ledger.Host` **is** allowed to reference `Noof.Ledger.Ai` (it has to, to run the worker), and a `ProjectReference` carries its target's own `PackageReference`s transitively at compile time by default. That means today, nothing stops `CategorizationWorker.cs` in `Noof.Ledger.Host` from writing `using Anthropic;` and calling the SDK directly instead of going through `ICategorizer` — which is exactly the seam spec §10 needs closed: the model must never reach a branch of control flow outside the one place that is allowed to hold it.

Create `tests/Noof.Ledger.Architecture.Tests/AiBoundaryTests.cs`:

```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class AiBoundaryTests
{
    [Fact]
    public void Only_Ai_references_the_Anthropic_SDK_namespace()
    {
        var srcRoot = Path.Combine(RepoRoot.Find().FullName, "src");
        var aiRoot = Path.Combine(srcRoot, "Noof.Ledger.Ai") + Path.DirectorySeparatorChar;

        var offenders = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.StartsWith(aiRoot, StringComparison.OrdinalIgnoreCase))
            .Where(file => ReferencesAnthropicSdk(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(srcRoot, file))
            .ToArray();

        // A project that references Noof.Ledger.Ai inherits its PackageReference to Anthropic
        // transitively at compile time, so "Web doesn't reference Ai" alone does not stop Host -
        // which legitimately references Ai to run the worker - from calling the SDK directly
        // instead of through ICategorizer. This is the rule that actually closes that gap.
        offenders.Should().BeEmpty(
            "every project outside Noof.Ledger.Ai must reach the model through ICategorizer, never the Anthropic SDK directly");
    }

    [Fact]
    public void Only_QuotedAmount_constructs_a_Money_inside_the_categorization_pipeline()
    {
        var root = RepoRoot.Find().FullName;
        string[] scannedRoots =
        [
            Path.Combine(root, "src", "Noof.Ledger.Ai"),
            Path.Combine(root, "src", "Noof.Ledger.Host", "Workers"),
        ];

        var offenders = scannedRoots
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(file => File.ReadAllText(file).Contains("new Money(", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        // ResolvedLineItem.Amount and CategorizedLineItem.Amount both carry forward the Money that
        // QuotedAmount.TryResolve (Noof.Ledger.Domain) already verified against the raw text. The
        // categorization pipeline must never mint a second one - that would be a figure reaching a
        // line item without ever being checked against what the user actually typed.
        offenders.Should().BeEmpty(
            "a Money in the categorization pipeline must come from QuotedAmount.TryResolve, never be constructed fresh");
    }

    static bool ReferencesAnthropicSdk(string source) =>
        source.Contains("using Anthropic;", StringComparison.Ordinal) ||
        source.Contains("Anthropic.", StringComparison.Ordinal);
}
```

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: **green**. Same non-red-first shape as Step 1 — both facts are true of the implementation this plan's earlier tasks already produced (the SDK call is confined to `Noof.Ledger.Ai`, and `ResolvedLineItem`/`CategorizedLineItem` only ever carry forward the `Money` `QuotedAmount.TryResolve` produced); these tests lock that in rather than driving it into existence. If either is red here, an earlier task in this plan drifted from its own contract — fix the drift, do not loosen the test.

> **Candidates considered and rejected for this step.**
>
> - **"No production file under `src/` contains `DateTimeOffset.UtcNow`."** This is real, and it is not hypothetical: `src/Noof.Ledger.Host/Cli/UserCommand.cs:64` (`CreatedAt = DateTimeOffset.UtcNow` when the `user set-password` verb creates the operator row) already violates it, verified by `grep -rn "DateTimeOffset.UtcNow" src/`. That makes it the *strongest* candidate in one sense — a test built from it would catch a real, standing violation, not just a theoretical one — but it is rejected from **this** step for two reasons. First, it has nothing to do with keeping the model out of places it must never reach, which is the concern this step exists to close; it is a general `TimeProvider` convention, a different rule entirely. Second, fixing it means threading a `TimeProvider` through `UserCommand.RunAsync` — a real code change to a file this plan's contract never lists, in a phase already large enough. Silently leaving a known violation out of a test that claims to guard the rule is worse than not writing the test, so it is **not added here**; it is written into `docs/BACKLOG.md` in Part 4 with the exact file and line so it is not lost.
> - **"`QuotedAmount.TryResolve` is the only place a `Money` may be constructed from model output," asserted across all of `src/`.** Rejected at that scope: `Money` is constructed legitimately throughout the domain from sources that have nothing to do with the model — wallet balances, FX rows, the seed data in `Noof.Ledger.Persistence`. A whole-`src` grep for `new Money(` would fail immediately on code this rule was never meant to touch, which is exactly the kind of test that gets deleted in frustration rather than fixed. Scoped to `Noof.Ledger.Ai` and `Noof.Ledger.Host/Workers` — the only two places a model-derived `Money` could legitimately appear — it becomes exactly the same shape of rule as the Anthropic-namespace check above and is genuinely cheap, so it is **kept**, just narrowed, as `Only_QuotedAmount_constructs_a_Money_inside_the_categorization_pipeline` above.

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: all `Noof.Ledger.Architecture.Tests` green, count grown by 3 (`Ai_package_references_are_exactly_its_allowed_set`, `Only_Ai_references_the_Anthropic_SDK_namespace`, `Only_QuotedAmount_constructs_a_Money_inside_the_categorization_pipeline`).

---

## Part 2 — The solution file

- [ ] **Step 4: Reconcile `NoofLedger.slnx` against what is actually on disk**

`tests/Noof.Ledger.E2E.Tests/` exists on disk and is **deliberately absent** from `NoofLedger.slnx` — this was decided in Phase 1A, not here: its own plan states plainly, at the point it adds `SettingsSecretsTests.cs`, *"`Noof.Ledger.E2E.Tests` is not part of `NoofLedger.slnx` — same as every other E2E task in this project"*. The reason still holds in 1B: it needs a published host, a real (or cloned) PostgreSQL database and a Playwright browser, all of which make it slow and environment-dependent in a way `dotnet test --solution` should never silently start paying on every inner-loop run. **Leave it out.** Do not add it.

What *does* need checking is whether every **unit/integration** test project on disk is actually wired into the solution. Rather than hard-coding "Task 2 added `Noof.Ledger.Ai.Tests`, so that's the only gap" — this task cannot see Tasks 1–8's final file list, and an Application-layer test project for the new pure logic (`MerchantScan`, `ProposalVerification`, `ModelCallException`) may or may not have landed as its own new project depending on how an earlier task chose to house it — reconcile mechanically instead:

```bash
comm -23 \
  <(find tests -maxdepth 1 -type d -name 'Noof.Ledger.*' ! -name 'Noof.Ledger.E2E.Tests' -printf '%f\n' | sort) \
  <(grep -oP '(?<=tests/)[^/]+(?=/)' NoofLedger.slnx | sort -u)
```

Expected output: **empty**. If anything prints, that project exists on disk but is missing from `NoofLedger.slnx` — add a `<Project Path="tests/<name>/<name>.csproj" />` line under the `/tests/` folder, alphabetically among the existing entries, for each one that prints.

- [ ] **Step 5: State, once, the commands that run everything**

So "all tests pass" has one unambiguous meaning for the rest of this task and for whoever reads this plan later:

- **The default inner-loop suite** (every project in `NoofLedger.slnx`, including `Noof.Ledger.Ai.Tests` added by Task 2 and anything Step 4 added): `dotnet test --solution NoofLedger.slnx`. This is what `ops/publish.ps1` itself runs (as `-c Release`) before it will produce a host at all. It **excludes** the opt-in live-model suite inside `Noof.Ledger.Ai.Tests` by design — Global Constraints and Task 3 are explicit that suite is skipped by default and never touches the network in the inner loop, so its absence from this command is not a gap, it is the point.
- **The browser suite**: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`, run separately, exactly as every prior E2E task in this project already does. It self-skips (`CookieModeHostFixture.DatabaseUnavailable`) when no PostgreSQL is reachable rather than failing the run outright, so it is safe to include in a "run everything before I claim this phase is done" checklist without a live database being a hard requirement to even attempt it — though for `dotnet test --solution NoofLedger.slnx` in the same step, Postgres-backed collections do require the real database from `ops/reset-database-auth.ps1`, same as every phase before this one.

"Everything" for this phase's close (Part 4) means both commands, run separately, both green.

---

## Part 3 — The phase acceptance

The spec's Phase 1 criterion (`docs/superpowers/specs/2026-09-19-noof-finance-design.md` §12), quoted verbatim:

> Boot with **zero secrets** → paste tokens into the settings page → send `кофе 250 рсд` → categorized expense on the dashboard. Pull the network cable → still saves → reconnect → it categorizes itself and the Telegram message updates.

Six clauses. Here is how each is proved:

| Clause | Proof |
|---|---|
| Boot with zero secrets | Automated — Phase 1A's `BootTests`/`AuthModeTests` already boot the host with no rows in `app_secret` and assert it comes up without throwing. Unchanged by this phase. |
| Paste tokens into the settings page | Automated (Playwright) — `SettingsSecretsTests` (Phase 1A) already proves a token typed into `/settings/secrets` is saved and readable back through the same DPAPI key ring the host uses. This phase's Task 8 extends that page with the Anthropic Test button (`ISecretProbe`); no new proof is owed here beyond what Task 8's own tests already cover. |
| Send `кофе 250 рсд` → categorized expense on the dashboard | Automated at two levels: the capture half (Phase 1A, `EfCaptureStore`) and the categorization half (this phase's `ProposalVerification`/`ICategorizationStore` tests, plus `ISpendingReadModel` reading it back). The manual checklist below is what proves the two halves are actually wired to the same live bot end to end — no automated test in this repository drives a real Telegram account. |
| Pull the network cable → still saves | **Automated, at the worker level** — Step 6 below. Simulated as a transient model-call failure, not a literal cable pull: `ICategorizer.ProposeAsync` throwing `ModelCallException(ModelFailureKind.Transient, ...)` is indistinguishable, from `CategorizationWorker`'s point of view, from the network actually being down — both are "the call did not complete" — and it is exactly what `AnthropicCategorizer` is contractually required to throw when the underlying `HttpRequestException`/timeout happens (per this plan's own "Verified facts" table and `ModelCallException`'s doc comment). |
| Reconnect → it categorizes itself | **Automated, at the worker level** — same test, second tick, `ICategorizer` now succeeds. |
| The Telegram message updates | **Faked, not real** — see the decision below. |

**The decision on "the Telegram message updates."** This clause needs a real Telegram bot and a real chat to prove completely, and this repository does not drive one in its automated suite (Phase 1A's own manual Step 40 says as much for the capture half). **`IChatNotifier` is faked with NSubstitute and the call to `EditAsync` is verified** — chat id, message id and reply text asserted exactly — the same pattern Phase 1A's own `TelegramChatNotifierTests` already uses for `EditAsync` in isolation. That proves the worker computes the right reply and calls the right method with the right arguments; it does not prove `api.telegram.org` actually renders the edit in a real chat. That residual — the one thing NSubstitute structurally cannot prove — is what the manual checklist below exists for, same division of labour as Phase 1A's Step 40.

- [ ] **Step 6: The automated worker-level acceptance test**

Add to `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`, the file Task 7 creates.

**Use that file's existing harness — do not build a second one.** Task 7's tests are substitute-based, not database-backed: `CreateWorker(IServiceScopeFactory scopeFactory, FakeTimeProvider time, CategorizationWorkerOptions? options = null)` with `ScopeFactoryFor(jobQueue, secretStore, store, categorizer, ...)` supplying NSubstitute doubles, and `KeyPresent()`/`KeyMissing()` for the secret. `Noof.Ledger.Host.Tests` has no PostgreSQL fixture at all — `PostgresFixture` lives in `Noof.Ledger.Persistence.Tests` and is not referenced from here — so a step written against a real database would not compile, and adding one would make the Host suite depend on a running server it currently does not need.

This step therefore proves the acceptance clause the way the rest of the file does: a substituted `ICategorizer` that throws a transient `ModelCallException` on the first tick and answers on the second, with `IJobQueue` and `ICategorizationStore` substitutes asserting that the transaction was never marked failed in between and that the edit went out exactly once. The durability half of the clause — that the capture survives with no model at all — is already proven against a real database by Phase 1A's `EfCaptureStore` tests; this step covers the recovery half:

```csharp
    [Fact]
    public async Task A_transient_model_failure_is_retried_and_the_captured_transaction_survives_until_it_recovers()
    {
        // Arrange: a transaction and its pending job already exist, exactly as EfCaptureStore
        // (Phase 1A) leaves them the moment "кофе 250 рсд" is captured - categorization has not
        // run yet. This stands in for "pull the network cable": the model call fails the same way
        // whether the cable is out or the API is briefly unreachable.
        var transactionId = await SeedCapturedTransactionAsync("кофе 250 рсд", chatId: 42L, botMessageId: 555);

        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<CategorizationProposal>>(_ => throw new ModelCallException(
                ModelFailureKind.Transient, "simulated network outage"));

        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(categorizer, notifier); // exactly as this file's other tests build it

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        // Assert: still saves. The captured transaction is untouched - not failed, not deleted -
        // and the job is due for another attempt, not abandoned.
        var afterFirstTick = await LoadTransactionAsync(transactionId);
        afterFirstTick.Status.Should().Be(TransactionStatus.Captured);
        afterFirstTick.Items.Should().BeEmpty("nothing was categorized yet - the raw capture alone must survive");
        await notifier.DidNotReceive().EditAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Act: reconnect. Same job, same worker, the model now answers.
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CategorizationProposal([
                new ProposedLineItem("Coffee", "250", "RSD", "food-drink", KnownMerchantId: null, MerchantQuote: null),
            ]));

        await AdvancePastRetryBackoffAsync(); // this file's existing helper for making a retried job due again
        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        // Assert: it categorizes itself, and the Telegram message updates - faked, per the decision
        // above; EditAsync being called with the computed figures is the automated half of that proof.
        var afterSecondTick = await LoadTransactionAsync(transactionId);
        afterSecondTick.Status.Should().Be(TransactionStatus.Completed);
        afterSecondTick.Items.Should().ContainSingle(i => i.Description == "Coffee");
        await notifier.Received(1).EditAsync(
            42L, 555, Arg.Is<string>(text => text.Contains("250") && text.Contains("RSD")),
            Arg.Any<CancellationToken>());
    }
```

> **CONTRACT GAP.** `CategorizationWorker`'s exact constructor parameters, its tick method's name (written here as `RunTickAsync`, following `TelegramPollingService`'s own naming from Phase 1A for consistency — not verified against this plan, since the task that builds the worker has not been drafted yet), `SeedCapturedTransactionAsync`, `LoadTransactionAsync`, `CreateWorker` and `AdvancePastRetryBackoffAsync` are whatever that earlier task's own test file already established. This step's job is the four assertions and the two-tick shape that prove the acceptance clauses — wire them into that file's real harness rather than this sketch's placeholder helper names.

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: green, alongside every other `CategorizationWorkerTests` case.

- [ ] **Step 7: The manual checklist — literal, numbered, what to see at each step**

Not automated. Run once, by the operator, against a real Telegram bot:

1. **Prerequisite — run this before anything else.** `dotnet run --project src/Noof.Ledger.Host -- user set-password noof`, and set a real password when prompted. *See:* password accepted, `Password set for 'noof'.` printed. **Do not enable `Auth:Mode=Cookie` before this step** — the existing row's password was set during Phase 0b verification and is not yours (`CLAUDE.md`, header note).
2. Confirm no secrets are stored: query `app_secret` on the target database, or trust a fresh database. *See:* zero rows, or a freshly created database.
3. Boot the host: `dotnet run --project src/Noof.Ledger.Host`. *See:* it starts without throwing; no Telegram polling starts yet (no token saved).
4. Sign in and open `/settings/secrets`. Paste a real Anthropic API key and a real Telegram bot token, one at a time, saving each. *See:* each field shows "set" status after save, never the plaintext back; the Anthropic field's Test button (this phase's Task 8) reports success.
5. Within ~5 seconds, confirm the bot starts polling (same check as Phase 1A Step 40: `getWebhookInfo` shows an empty `url`, or just proceed to the next step and see if it responds).
6. From your own Telegram account, send `кофе 250 рсд`. *See:* an immediate acknowledgement reply (Phase 1A's saved-not-yet-categorized text), then — within the poll interval — that same message **edited in place** to show a computed figure ("250 RSD", not a raw echo of your text) and a category.
7. Open the dashboard's home page. *See:* the same transaction listed as a categorized expense, in RSD, with the category shown in step 6.
8. **Pull the network cable** (or disable the network adapter) on the machine running the host. Send a second message, e.g. `такси 500 рсд`. *See:* the acknowledgement reply still arrives (Telegram long-polling and the acknowledgement write do not depend on the Anthropic API), but no categorized edit follows while the network is down.
9. **Reconnect** the network. *See:* within the poll interval, the second message's Telegram reply is edited with its computed figures, and the dashboard shows it as categorized — with no restart of the host required.

---

## Part 4 — The close

- [ ] **Step 8: Run the whole solution and the browser suite, record the actual numbers**

Run: `dotnet test --solution NoofLedger.slnx`
Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj`
Expected: both green. **Write down the actual `succeeded:` counts from the output of each** — they go into Step 10's `CLAUDE.md` edit and this task's own commit message. Do not state a pass count you have not seen; if either run is red, stop and fix the failure before continuing to Step 9.

- [ ] **Step 9: Run `ops/publish.ps1` and confirm the gate still produces a runnable host**

Run: `powershell -ExecutionPolicy Bypass -File ops/publish.ps1`
Expected: it builds in `Release`, runs `dotnet test --solution NoofLedger.slnx` itself (the script's own gate — this is the same suite Step 8 already ran; a second green run here is confirmation the script's gate is not silently broken, not new information), and produces `publish/Noof.Ledger.Host.dll`. Launch it once — `dotnet publish/Noof.Ledger.Host.dll` from the `publish/` directory — and confirm it starts and serves `/` before stopping it.

- [ ] **Step 10: Update `CLAUDE.md`'s status line**

> **CONTRACT GAP.** The prompt for this task asks to update the status line "the way Phase 1A's final task did." Checked directly: Phase 1A's actual final task (`docs/superpowers/plans/2026-09-21-phase1a-capture-and-storage.md`, Task 9, "Stop the connection string leaking parameter values into exception text") is a small unrelated security fix — it never touches `CLAUDE.md`, and no task anywhere in the 1A plan document does. `CLAUDE.md`'s status block still reads *"Phases 0 and 0b complete ... Next is Phase 1"* today, even though Phase 1A itself has already been merged (`git log`: `591ed58 Merge Phase 1A — capture and durable storage`). There is no precedent text to follow; the update below is written from scratch, in the voice and shape of the existing status block, and is the first thing in this project's history to actually perform this edit.

In `CLAUDE.md`, replace the status paragraph:

```markdown
> **Status:** spec approved (`docs/superpowers/specs/2026-09-19-noof-finance-design.md`); **Phases 0 and 0b complete** — solution, EF Core model and migrations, PostgreSQL money-storage gate, authentication in two modes, the `user set-password` verb, the loopback interlock and a Blazor Server shell. 136 solution tests plus 4 Playwright smoke tests, all green; `ops/publish.ps1` produces a runnable host. Next is Phase 1 (capture and categorisation). Rules below marked *(settled)* are direct user decisions and are not up for re-litigation.
```

with:

```markdown
> **Status:** spec approved (`docs/superpowers/specs/2026-09-19-noof-finance-design.md`); **Phases 0, 0b, 1A and 1B complete** — solution, EF Core model and migrations, PostgreSQL money-storage gate, authentication in two modes, the `user set-password` verb, the loopback interlock, a Blazor Server shell, Telegram capture with a durable queue, LLM categorisation with quote-and-verify amounts and a write-once merchant identity table, and a dashboard reading it all back through a read model. <SUCCEEDED-COUNT-FROM-STEP-8> solution tests plus <E2E-COUNT-FROM-STEP-8> Playwright E2E tests, all green; `ops/publish.ps1` produces a runnable host. Next is Phase 2 (money model — exact balances across all five currencies under `ru-RU` and `sr-Latn-RS`, and a proven backup restore). Rules below marked *(settled)* are direct user decisions and are not up for re-litigation.
```

Substitute `<SUCCEEDED-COUNT-FROM-STEP-8>` and `<E2E-COUNT-FROM-STEP-8>` with the actual numbers written down in Step 8 — never a guessed or remembered figure. Phase 2's description is taken verbatim from spec §12's phase table row `2 | Money model | Balances exact across all five currencies under ru-RU and sr-Latn-RS. A backup restored successfully at least once.` — the next row after Phase 1 in that table, since Phases 1A and 1B together are this repository's split of the spec's single Phase 1.

- [ ] **Step 11: `docs/OPEN-QUESTIONS.md` and `docs/BACKLOG.md` — checked, not assumed**

Checked directly against both files as they stand (not reproduced from memory of what this plan's own earlier sections claim):

- **`docs/OPEN-QUESTIONS.md`: no new entry.** Everything this phase defers is deferred *work* whose decision is already made (per `BACKLOG.md`'s own header, which draws that line), not an undecided *question* — so it belongs in `BACKLOG.md`, matching how this phase's own "What this plan deliberately does NOT do" section already frames every one of its deferrals. Do not add anything here.
- **The Telegram `getMe` probe.** `docs/BACKLOG.md` was grepped for `getme` (case-insensitive) and has no entry — despite this plan's own "What this plan deliberately does NOT do" section stating *"the Telegram equivalent is recorded in the backlog by that task"* (Task 8). **Add it now if Task 8 has not already added it by the time this task runs** — grep first, do not duplicate. Entry to add, matching this file's existing format (title, **Wanted**, **Why it is not scheduled**, one short closing paragraph):

  ```markdown
  ## A Test button for the Telegram bot token

  **Wanted.** The settings page's Anthropic key gets a Test button this phase (`ISecretProbe` /
  `AnthropicKeyProbe`, `GET /v1/models`, costs no tokens). The Telegram bot token has no equivalent -
  the spec (§9) names `getMe` for exactly this, and today the only way to learn a pasted Telegram
  token is bad is to watch the poller silently fail to start.

  **Why it is not scheduled.** Out of this phase's stated scope (Task 8 covers only the Anthropic
  key's Test button). `ISecretProbe` already exists as a port after this phase; a `TelegramKeyProbe`
  implementing it against `getMe` is a small, isolated addition with no schema or contract cost -
  ordinary work for any later phase.
  ```

- **The merchant merge inbox.** `docs/BACKLOG.md` was grepped for `merge`/`duplicate`/`alias` and has **no entry**, despite this phase's own "What this plan deliberately does NOT do" section claiming *"All three are in `docs/BACKLOG.md`"* (category management, per-line-item correction, and the merge inbox). Two of the three are there; **the merge inbox is not — that claim was wrong, and this step corrects it** rather than trusting it. Add:

  ```markdown
  ## Merchant merge inbox for near-duplicate aliases

  **Wanted.** Spec §11: "A `pg_trgm word_similarity` sweep surfaces near-duplicates in a merge inbox
  - one click merges retroactively and revertibly, and rejected pairs are remembered permanently."

  **Why it is not in Phase 1B.** The alias table this needs - write-once, `Fold()`-keyed, the sole
  authority on merchant identity - ships this phase (`IMerchantDirectory`). The merge inbox is a
  read/write UI over rows that table already produces correctly; nothing about it changes the
  schema. It is explicitly Phase 7 (Governance) work in the spec's phase table, alongside the
  recategorization batch UI it shares page furniture with.

  **Until then.** A wrong canonicalisation on first sighting is permanent under write-once (a
  named, accepted trade-off - see spec §13 risk 4), with no UI yet to correct it short of a manual
  database edit.
  ```

- **Per-line-item category correction, category management UI.** Both grepped, both present in `docs/BACKLOG.md` already, unchanged since before this phase. No edit needed — verified, not duplicated.

- [ ] **Step 12: Commit**

Two commits — code/tests, then docs — matching this plan's own convention of keeping a docs-only change separate from a behavioural one:

```bash
git add tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs tests/Noof.Ledger.Architecture.Tests/AiBoundaryTests.cs NoofLedger.slnx tests/Noof.Ledger.Host.Tests
git commit -m "$(cat <<'EOF'
test(architecture): lock the model behind ICategorizer and prove the phase's network-loss guarantee

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

```bash
git add CLAUDE.md docs/BACKLOG.md
git commit -m "$(cat <<'EOF'
docs: close Phase 1B — update status, record the getMe probe and merge inbox as backlog

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

## Part 5 — The closing review

- [ ] **Step 13: The Fable 5.1 review**

`CLAUDE.md` §1: *"The final review of a completed implementation runs on Fable 5.1 (`model: "fable"`), not on the family that wrote the code... This applies to the review that closes a phase or a plan."* This phase was written and implemented entirely by one model family; a reviewer from that same family tends to miss exactly what the implementer missed, and its agreement with the implementer is weak evidence the work is actually sound. Run this as an explicit, separate step — not folded into Step 8's test run.

Launch it as a subagent with `model: "fable"`, pointed at:
- The **whole diff** for Phase 1B (every commit from this plan's Task 1 through this task's Step 12) — not a summary of it.
- **The contract section** of this plan (`docs/superpowers/plans/2026-09-21-phase1b-categorisation-and-dashboard.md`, "The contract") — every implementation must match it exactly; a deviation is a defect in the code, not a reason to edit the contract retroactively.
- **This phase's own stated guarantees**: the acceptance criterion quoted in Part 3, the precedence rule (`User(4) > Rule(2) > Model(1) > None(0)` — the REAL `CategorizationAuthority` values in `src/Noof.Ledger.Domain/CategorizationAuthority.cs`, not the spec §11 ladder (`PinnedRule`, `Llm`, `Offline` were never built; `Llm` is spelled `Model`). Do not report the difference as a defect: it is Phase 1A's settled shape and the spec text is what is stale, spec §11), and "no number in user-facing output ever originates from a model" (`CLAUDE.md` §4).

**What the equivalent review found in Phase 1A, and why this reviewer should look hardest at the same shapes of bug here.** Phase 1A's own closing review (commits `ca3bac0` and `413d7b4`) was run the same way, on a different family, after same-family review had already passed the code. It found eight defects across two passes, none of which were "missing feature" — every one was a same-family blind spot in already-working-looking code:

1. **Unfenced concurrent writes.** `TelegramOwnerGate.ClaimAsync` and `EfCaptureStore.CaptureAsync` both had races where two callers could both believe they won. Phase 1B's `IMerchantDirectory.LinkAliasAsync` is contractually the *same shape* of write-once-first-writer-wins race, and `EfJobQueue`'s ownership-fenced `SucceedAsync`/`RetryAsync`/`FailAsync` are already proven correct by Phase 1A's own `A_stale_workers_late_retry_after_its_lease_was_reclaimed_does_not_resurrect_the_job` test — check `LinkAliasAsync` was actually built to the same standard, not just documented to be.
2. **Wrong time source.** `CapturedMessage.SentAt` was stamped from `timeProvider.GetUtcNow()` instead of Telegram's own `Message.Date` — a plausible-looking bug because both are "a timestamp," and only one is correct. Check `CategorizationWorker`'s own timestamps (job `run_after`, `updated_at`) the same way: `TimeProvider`, never `DateTimeOffset.UtcNow`, and never a value the model itself could have influenced.
3. **A boundary check disguised as a substring check.** `QuotedAmount`'s original verbatim check accepted `"500"` against raw text that said `"1500"` — a true substring, wrong boundary. `MerchantScan.Matches`' whole-word matching (this plan's contract) is the same class of hazard in new code: check it rejects a folded alias that is merely a substring of a longer word in the raw text, the same way `OccursAsWholeNumber` now does for amounts.
4. **Silent acceptance of bad input.** The settings page originally stored an empty secret silently instead of refusing it. `ProposalVerification.TryResolve`'s contract states partial acceptance is never offered — verify there is no code path where a malformed line is silently dropped rather than failing the whole proposal, and that an empty/whitespace merchant or description string cannot silently reach a database row.
5. **Resource lifecycle ordering.** Scope creation and DI resolution living outside the `try` in `TelegramPollingService`'s tick meant a resolution failure could kill the host. Check `CategorizationWorker`'s tick the same way — per the file table, "never take the host down" is its own stated job.
6. **A secret reaching an exception or log message through a more specific path than the obvious one was blocked.** The Telegram HTTP client leak was through a *nested* logging category a simple filter missed. Check that no prompt, tool-use payload, or error message built from a model response could carry `anthropic-api-key` or `telegram-bot-token` into a log, exception, or the Telegram reply text itself.

Additionally, weigh spec §13 risk 3 directly, which is specific to this phase and untested by anything above: **"Two retry layers multiply... a bad API key buys two hours of silence."** Confirm `ModelFailureKind.Terminal` (bad key, no credit) is never retried by `EfJobQueue` — only `Transient` is — and that the Anthropic SDK's own internal retry (noted in this plan's fact table) does not itself retry on what should be a terminal, single-shot failure.

Fix whatever the review finds as its own follow-up commits, in the same style as Phase 1A's `ca3bac0`/`413d7b4` — and, as those two did, amend this plan document in place for each fix, so a future re-run from scratch reproduces the patched code rather than rebuilding the same holes.

---
