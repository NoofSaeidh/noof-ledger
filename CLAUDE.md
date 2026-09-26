# noof-ledger — working agreement

Personal finance tracker. Telegram bot captures spending (text, voice, receipt photos), an LLM categorises it per line item, a local Blazor dashboard shows it across multiple wallets and currencies. C# / .NET 10, EF Core, strict TDD, local hosting, **public repo**.

> **Status:** spec approved (`docs/superpowers/specs/2026-09-24-money-model.md`,
> `docs/superpowers/specs/2026-09-24-observability-design.md`); **Phases 0, 0b, 1A, 1B, 1C, 1D, 2, 3, 4
> and 5 complete** — solution, EF Core model and migrations, PostgreSQL money-storage gate, cookie
> authentication as the sole mode, the `user set-password` verb, the loopback interlock, a Blazor
> Server shell, Telegram capture with a durable queue, natural-language capture, voice notes
> transcribed by Groq's whisper-large-v3, the money model (every wallet's balance — opening balance,
> minus spending, plus income, re-anchored by the operator's own balance statements — is exact in all
> five currencies (EUR, RSD, USD, RUB, KZT) under `ru-RU` and `sr-Latn-RS`), and now observability: the
> host waits indefinitely for PostgreSQL instead of exiting when it is down, every page shows a waiting
> banner until the database gate is `Ready`, Serilog logs to a rolling file and to the `app_log` table
> with secrets redacted before every sink, a transaction's whole path from message to echo (and its
> revision history) is visible on one trace page, and system health is on a dashboard tile, on
> `/diagnostics` and behind the bot's owner-only `/health` command. Health checks are now our own
> `ISystemHealthCheck` seam (no `Microsoft.Extensions.Diagnostics.HealthChecks`), each owned and
> registered by the assembly that owns what it checks, run one at a time under a 5 s cooperative
> timeout; a check that throws or times out shows only its exception type. Every model, speech,
> Telegram, worker and health-check call is timed through `IOperationTimer`, logged as Debug or, over
> its own slow threshold (`Logging:SlowOperationMs`), a Warning. The database log sink's minimum level
> is runtime state — Verbose/Debug/Information, switched on the Logs page, persisted in `app_setting`,
> never above Information — while the file and console sinks each keep their own static floor,
> `Logging:File:MinimumLevel` and `Logging:Console:MinimumLevel`; a transaction's
> trace page links straight into the Logs page at Debug for that transaction. Transactions carry a `Kind`
> (`Expense`/`Income`/`BalanceCheck`); expense and income transactions own signed double-entry-lite
> `entries`; a balance statement is a `balance_checks` checkpoint; a wallet's balance is computed by the
> `wallet_balances` SQL view and read back by `IBalanceReadModel`, never stored. The model's answer tool
> is `record_transaction` (was `record_spending`) and now names a wallet and, for a balance statement,
> the stated amount. `/wallets` manages wallets on the dashboard; income and balance statements are
> ordinary messages to the bot. The app also backs itself up daily — `pg_dump -Fc` into
> `%LOCALAPPDATA%\NoofLedger\backups`, the newest 14 kept, every run logged to `backup_runs` — and
> `ops/restore-check.ps1` proves a dump restores to the same balances, checked so far against a
> template clone; the one check against the live ledger itself is the operator's to run
> (`ops/RUNBOOK.md`). **1271 solution tests — 1260 passing, 11 live-only tests skipped, none failing** —
> the Playwright browser tests are in the solution now, so `dotnet test --solution` runs them too and
> needs Chromium present. Live suites stay skipped unless `NOOF_LEDGER_LIVE_ANTHROPIC_KEY` /
> `NOOF_LEDGER_LIVE_GROQ_KEY` + `NOOF_LEDGER_LIVE_VOICE_FILE` are set; `ops/publish.ps1` produces a
> runnable host. **`run.ps1` in the repo root is the one entry point for launching and operating the
> app** (`.\run.ps1 help`) — `dotnet run` and the published exe now behave the same. Cross-currency
> conversion, transfers and receipt photos remain future phases. Rules
> below marked *(settled)* are direct user decisions and are not up for re-litigation.
>
> Deferred **decisions** live in `docs/OPEN-QUESTIONS.md`; deferred **work** lives in `docs/BACKLOG.md`. Check both before proposing something as missing.
>
> **`noof_ledger` holds the operator's real credentials now.** Never run tests, experiments or manual checks against it, or call the live model, without an explicit request. Tests use the `noof_ledger_test_template` clones only.

---

## 1. Model and effort policy

**Default to the cheapest model that can do the job correctly.** Escalate on demonstrated need, not on suspicion that a task might be hard. Cost discipline is a standing requirement, not a preference.

| Tier | Use for | Examples |
|---|---|---|
| **haiku** | Mechanical, deterministic, verifiable-at-a-glance work | Running tests and reporting pass/fail · running builds · file/dir listings · grep sweeps · renames · formatting · dependency version lookups · reading a log for a known string · scaffolding boilerplate from an explicit template |
| **sonnet** | Analysis, review, and most implementation | Writing and modifying code · reading and explaining a subsystem · code review · test authoring · adversarial fact-checking · research against known docs · debugging a localised failure |
| **opus** | Only genuinely hard reasoning with wide blast radius | Cross-cutting architecture decisions · synthesising many conflicting sources · designing a schema or seam that is expensive to reverse · diagnosing a bug that resisted sonnet |

**Effort levels.** Do not use high or xhigh effort for small tasks. `low` for mechanical work, `medium` for ordinary implementation, `high`/`xhigh` reserved for the architecture- and correctness-critical reasoning that justifies opus in the first place.

**The final review of a completed implementation runs on Fable 5.1** (`model: "fable"`), not on the family that wrote the code. Models in one family share blind spots: a reviewer drawn from the same family tends to miss exactly what the implementer missed, and agreement between them is weak evidence. A different family is the cheapest independence available. This applies to the review that closes a phase or a plan — per-task reviews stay on sonnet.

**Anti-patterns — do not do these:**
- Running a test suite on opus. That is a haiku task; the model is not what makes tests pass.
- Using xhigh effort to rename a variable, fix a typo, or add a using directive.
- Escalating to opus because a task *sounds* important. Blast radius and reversibility decide the tier, not topic gravity.
- Re-running expensive work that is already cached or already done. Check first.

## 2. Subagent usage

**Delegate aggressively, and delegate downward.** Any task that is self-contained, produces a summarisable result, and does not need this conversation's full context should go to a subagent on the cheapest adequate tier.

- **Always delegate:** multi-file searches, "find where X is defined", test runs, build verification, reading a large file to answer one question, independent research, per-file review passes.
- **Parallelise:** independent work goes out in one message as multiple subagents, not sequentially.
- **Workflows:** for fan-out work, set `model` per stage — haiku for mechanical stages, sonnet for research and verification, opus only for final synthesis. Never let a whole workflow inherit opus by default.
- **Keep the conclusion, not the transcript.** A subagent's job is to return the answer, not to dump file contents back into the main context.

## 3. Code style

**Write simple code that explains itself. Comments are a last resort, not a habit.**

- **Avoid comments.** If a comment explains *what* the code does, delete it and fix the names instead. The only comments worth writing explain *why* — a non-obvious constraint, a workaround with a link, a deliberate deviation that would otherwise look like a bug.
- **No XML doc blocks** on private or internal members. No `#region`. No commented-out code — git remembers it.
- **Names carry the meaning.** A well-named method needs no header comment. If you cannot name it clearly, the method is doing too much.
- **Small and focused.** Short methods, one reason to change per class. A long file is a design signal, not a formatting problem.

**Use modern C# — this targets .NET 10, so write like it:**

- File-scoped namespaces · primary constructors · `record` and `readonly record struct` for values · collection expressions (`[.. items]`) · target-typed `new`
- Pattern matching and switch expressions over `if`/`else` chains · `is null` / `is not null`
- `required` and `init` over constructor telescoping · raw string literals for multi-line text and JSON
- Nullable reference types are on. The null-forgiving `!` operator needs a reason.
- `async`/`await` end to end; no `.Result`, no `.Wait()`
- LINQ where it reads better than a loop; a loop where LINQ does not

**Public services go through an interface**, registered by the assembly's own `AddNoofXxx`. The
exceptions are simple helpers with no state and nothing to substitute — `MerchantName.Fold`,
`SecretKeys` — plus the composition-root `AddNoofXxx` registration classes themselves and
`LedgerConnectionString` (read before any `IServiceCollection` exists, so there is nothing to
register it into) — named because they are exceptions, not a licence to invent more by analogy.

**Don't:** defensive boilerplate for conditions that cannot occur · ceremony that exists to look enterprise.

## 4. Engineering rules *(settled)*

**Money**
- Money is `decimal` + `Currency` in the domain. Never `double`, never `float`. Every method signature, test, component and report sees a `decimal`.
- Reports, totals and balances are computed by C#; the LLM only phrases figures C# computed.
- **Capture is the exception** *(settled 2026-09-22)*: the model interprets amounts and dates from
  natural speech with no validation layer. The safety is the echo in Telegram plus cancel and correct,
  not rejection. Do not re-add verbatim checks or sanity bounds — `docs/OPEN-QUESTIONS.md` P2-1.
- **A wallet's balance is derived, never stored** *(settled 2026-09-24, Phase 4)*. No column anywhere
  holds a running balance. It is the latest `balance_checks` checkpoint for that wallet and currency
  plus the sum of `entries` after it (no checkpoint → the sum of all entries), computed by the
  `wallet_balances` SQL view and read back through `IBalanceReadModel`. A checkpoint's recorded
  `computed_before` is history for the echo, not a balance anything reads back as current — never add
  a cached-balance column "for speed" without re-deriving it from entries on every write; that is
  exactly the drift this model exists to prevent.

**Architecture**
- Projects are split: `Domain` ← `Application` ← (`Persistence` · `Ai` · `Fx` · `Receipts` · `Telegram` · `Web`) ← `Host`.
- `Noof.Ledger.Web` is UI only — no `DbContext`, no EF types, no `HttpClient`, no `Program.cs`. Enforced by `DisableTransitiveProjectReferences` plus an architecture test, because project references are transitive at compile time and a convention alone will not hold.
- `Noof.Ledger.Domain` has zero NuGet references. Asserted by a test.
- Do not add MediatR, AutoMapper, generic repositories over `DbContext`, or CQRS scaffolding.
- **Minimum accessibility.** A type is `internal` unless another assembly names it; a member is
  `private` unless something outside its type calls it. Tests reach internals through
  `InternalsVisibleTo`, never by widening `src` — and substituting an internal interface needs
  `InternalsVisibleTo("DynamicProxyGenAssembly2")` on the declaring assembly as well, or
  NSubstitute fails at runtime. `PublicSurfaceTests` holds each assembly's allowlist; widening it
  is an edit to that file, which is the point.
- **Each assembly registers its own services**, exposing one `AddNoofXxx(this IServiceCollection)`
  the Host calls. `Program.cs` names no implementation type.
- **Render modes are per-page and stay that way.** The sign-in page is a real form POST, because
  `SignInAsync` needs an `HttpContext`. So `MainLayout` renders statically, MudBlazor's popover,
  dialog and snackbar providers cannot work from there, and nothing may use a popover, dialog,
  snackbar, tooltip or menu. Feedback is an inline `MudAlert`. Ways out are costed in
  `docs/BACKLOG.md`; taking one is a decision, not a convenience.
- **Nothing at startup may block on the database** *(settled 2026-09-25, Phase 5)*. The host starts —
  Kestrel, the sign-in page, `/healthz`, file logging — with PostgreSQL down; `IDatabaseGate` tracks
  `Waiting`/`Migrating`/`Ready`/`Failed` and every page shows a waiting banner until `Ready`. Every
  `BackgroundService` loop awaits `IDatabaseGate.WaitUntilReadyAsync` before its first iteration and
  catches every non-cancellation exception per tick — a loop that lets an exception escape faults the
  host's `BackgroundService` and stops the whole process with exit code 0, silently. This cost a
  critical review finding: `SecretSnapshotRefreshWorker` had no catch, so PostgreSQL going down *after*
  `Ready` took the host down outright even though startup itself tolerated it fine.

**Database**
- **After `dotnet ef migrations add`, convert the migration `.cs` file to a file-scoped namespace.** `IDE0161` is an error here, so it **fails the build** until you do. Its `.Designer.cs` and `LedgerDbContextModelSnapshot.cs` carry `// <auto-generated />`, which exempts them from code-style analyzers — leave those exactly as EF emits them. Converting them is churn that EF overwrites on the next scaffold anyway. Only the migration file is hand-edited (it carries the raw SQL EF cannot express), and only it is checked.
- EF Core with migrations from the first commit. The database must be creatable from empty and upgradeable in one mechanism.
- **Never call `EnsureCreated()`** — anywhere, including test helpers. It bypasses migrations and permanently poisons that database for `Migrate()`.
- **Never edit a migration that has already been applied anywhere — add a new one.** EF records a
  migration as applied by id, so SQL appended to an applied migration never runs on that database
  and never will. This already cost a real guarantee: commit `01b4961` added the
  `merchant_aliases_no_truncate` trigger to an existing migration, and `noof_ledger` and the test
  template went on for a phase without the TRUNCATE guard the README promised, while every freshly
  created database had it.
- **After adding a migration, run `.\run.ps1 update-test-template`** (or the command it runs, in
  `ops/RUNBOOK.md` under "After adding a migration"). The E2E suite clones the template and fails on
  a stale one; never run `dotnet ef database update` bare or with `--connection` — both resolve
  `noof_ledger` — point it at the template via `NOOF_LEDGER_EF_CONNECTION` instead.
- **`transaction_revisions` is append-only** (a trigger refuses `UPDATE`/`DELETE`/`TRUNCATE`). Any
  code that changes a record writes a revision inside the same database transaction, through
  `RevisionLog.AppendAsync`.
- Tests run against a real database, never the EF InMemory provider.
- **An external process never receives a secret as an argument** *(settled 2026-09-24, Phase 4)*.
  `BackupWorker`'s `pg_dump` is the first production code in this repo to shell out to another
  process; its connection password goes through `ProcessStartInfo.Environment["PGPASSWORD"]` only —
  never `ArgumentList`, a log line, or a recorded `backup_runs.error`. Any future external process
  (another database tool, a future export) follows the same rule: `UseShellExecute = false`,
  `ArgumentList` for arguments, environment variables for anything that must not appear in a process
  list or a log.
- **`wallet_balances` reads `transactions`, `entries` and `balance_checks`.** A migration that alters
  or drops a column any of those three still expose to the view must `DROP VIEW wallet_balances`
  first and re-create it in the same migration, or the migration fails on the dependency.
  `schema.expected.sql` never shows views or triggers — `WalletBalancesViewTests` is their detector.

**Logging** *(settled 2026-09-25, Phase 5)*
- **Serilog is the `Microsoft.Extensions.Logging` provider only** — referenced by `Noof.Ledger.Host`
  alone. Everywhere else logs through `ILogger<T>` with `[LoggerMessage]` source-generated methods;
  `CA1848`/`CA2254` are errors in `src` and an architecture test additionally bans a direct
  `.LogXxx(` call there.
- **`[LoggerMessage]` methods live in a sibling top-level `static partial class`, never a nested `Log`
  class** — a nested class does not compile here (`CS1109`/`CS0260`).
- Every log event gets a stable, pinned `EventId`. A `[LoggerMessage]` with no id is a gap the next
  person has to notice by hand.
- **Timing an operation goes through `IOperationTimer`** with a name from `TimedOperations`:
  `using var timing = timer.Start(logger, TimedOperations.X, expectedWait: ...)`, with an explicit
  `Stop(onlyIfSlow: ...)` on the success path so an idle call logs nothing; thresholds live in
  `Logging:SlowOperationMs`. Never `Stopwatch`, never a `GetUtcNow()` subtraction — `TimeProvider`'s
  `GetTimestamp`/`GetElapsedTime` end to end.
- **The database sink's minimum level is runtime state**, set from the Logs page, persisted in
  `app_setting`, and capped at Information (Warning+ would empty the trace page, which reads
  Information-level stage events). The file and console sinks are static configuration instead —
  `Logging:File:MinimumLevel` (default `Debug`) and `Logging:Console:MinimumLevel` (default
  `Information`) — and the root level is `min(file, console, database)`. `Serilog:MinimumLevel:Default`
  is withdrawn *(decision (d), 2026-09-26)*: a value left under that key fails startup fast, naming
  the two keys above, rather than silently binding neither sink. `Serilog:MinimumLevel` now holds only
  `Override` (per-category); file retention is by file count only (`Logging:File:RetainedFileCountLimit`,
  `Logging:File:FileSizeLimitBytes`), never by days. A per-sink floor is `restrictedToMinimumLevel`
  or a `levelSwitch` on the `WriteTo` call — and an inner `LoggerConfiguration` reached through
  `WriteTo.Sink(innerLogger)` or any other direct `ILogEventSink.Emit` call **bypasses its own
  `MinimumLevel` entirely** (`SerilogInnerLoggerSinkTests`, against Serilog 4.4.0): `Logger.Emit`
  dispatches to the sink pipeline unconditionally, and only `ILogger.Write` — what
  `logger.Information(...)` and friends call — checks a logger's own floor first. `WriteTo.Logger(...)`
  (which calls `Write`) is the one variant that would honour it.

**Testing**
- TDD: a failing test first, for all behaviour. Exempt: migrations, DTOs, `Program.cs` wiring.
- **Test hosts never write into the operator's real log directory** (Phase 5).
  Every `WebApplicationFactory<Program>` and E2E host fixture must point `Logging:File:Directory` at a
  per-fixture temp directory (the `TestHostLogging` helpers, guarded by `TestHostLogDirectoryTests`) —
  before this, test runs wrote files straight into `%LOCALAPPDATA%\NoofLedger\logs` and could evict the
  operator's own logs under the 14-file retention cap. `Host.Tests` runs with
  `parallelizeTestCollections=false` because Serilog's logger is a shared static.
- **A statically rendered page's state is read once, at render** — an E2E fixture waiting for
  something to change (the database gate, a background job) must wait for the actual rendered marker
  (the sign-in page rendering *without* `id="database-waiting"`), never for a bare HTTP 200; a page can
  200 while still showing what it rendered before the change.
- **No production configuration key exists solely so a test can flip a code path** *(settled
  2026-09-25, Phase 5)*. A `Diagnostics:ForceLogSinkFailureForTests` hook was added, then removed in
  review, in favour of driving the real failure (an unreachable sink) from the test itself.
- **A look that fails silently needs a test that reads what the app serves, not the source.** The
  theme once emitted `font-family: 'system-ui, -apple-system, ...'` — one quoted name no machine
  has — and every page rendered in Times New Roman while all 482 tests passed. `ShellSourceTests`
  and `ThemeTests` are that detector; a source-text assertion could not have been.
- **Watch the new test fail before you let it pass.** A guard that has never been seen red may be
  enforcing nothing — a grep that matches no file, a rule whose subject set is empty. Break the
  thing deliberately, see the failure name it, put it back.
- **Never seed a test with `DateTimeOffset.UtcNow` and then assert exact equality against a value
  read back from PostgreSQL.** `timestamptz` keeps microseconds; a .NET tick is 100ns. A timestamp
  whose final tick digit is non-zero is truncated on the round trip, so the assertion fails most
  runs but not all — the worst kind of flake. Seed from a fixed literal, or compare with
  `BeCloseTo`. This shipped twice before it was caught.
- `global.json` must contain `{"test":{"runner":"Microsoft.Testing.Platform"}}` or `dotnet test` fails outright on SDK 10.0.204.
- The inner red-green loop never touches the network or a real model. Live model calls live in an opt-in suite that is skipped by default.
- **Database and E2E test projects run filtered to the classes a change touches, and in full once at
  the end of a phase** *(operator's decision, 2026-09-24)*. Both share a PostgreSQL server across
  worktrees, so an unfiltered run outside that one end-of-phase pass risks colliding with parallel
  work instead of catching anything the filtered run would not.

**The model** *(settled)*
- Reached through **`Microsoft.Extensions.AI`'s `IChatClient`** — the Anthropic factory calls
  `AsIChatClient(options.Model, options.MaxTokens)` on the SDK client — not the SDK's native
  `Messages.Create`. Operator's decision.
- **The answer is a forced tool call with `strict: true`, not structured outputs** *(settled
  2026-09-23, operator's preference)*. `record_transaction`'s arguments are the answer (renamed from
  `record_spending` in Phase 4: it now records income and balance statements too, and names a `kind`,
  an optional `wallet_id`, and — for `kind = "balance"` — the stated `balance_amount`); strictness is a
  provider-neutral marker (`StrictTool.Marker()`), translated to the wire's own `"Strict"` key inside
  `Noof.Ledger.Ai/Anthropic/`, and `ChatToolMode.RequireAny`/`RequireSpecific` becomes `tool_choice`.
  Assert both on the captured HTTP body, not from documentation. (Phase 1B had used
  `output_config.format`; that was an agent's choice, not the operator's.)
- Forced tool use is unsupported on Claude Opus 5.5, Fable 5.1 and Mythos 5.1 — `docs/OPEN-QUESTIONS.md` P3-2.
- **Amounts are JSON numbers read straight into `decimal`** — from the argument's `JsonElement`,
  never via `double`.
- **Never set temperature.** It is `[Obsolete]` in the SDK and therefore a compile error here.
  Determinism comes from the schema's enums.
- **Nothing depends on an AI provider except its factory**: `IChatClientFactory` for the model and
  `ISpeechToTextClientFactory` for speech, each implemented in its own folder under
  `src/Noof.Ledger.Ai/<Provider>/` *(D-A, 2026-09-23; speech P3-1)*. Asserted by `AiBoundaryTests`.
  `ISpeechToTextClient` is experimental (`MEAI001`), and the warning is suppressed in
  `Noof.Ledger.Ai` and its tests only.
- **The tool loop runs through `FunctionInvokingChatClient`** *(D-B)*, with a guard
  `DelegatingChatClient` below it that re-forces `record_transaction` on the follow-up request: FICC
  resets a required `ToolMode` after the first round and strips every tool declaration on its own
  last iteration — verified by decompiling, not by its docs.

**Bot text** *(settled 2026-09-23)*
- The bot writes English only, including the category name shown in the echo (`Category.NameEn`).
  Multi-language is deferred — `docs/BACKLOG.md`. The operator may still write to the bot in any
  language; only the bot's own output is English.
- Identifiers and comments use English action names — `Cancel`/`Edit`/`Restore` — never the Russian
  labels the UI used to show.

**Secrets — this is a public repo**
- Secrets are encrypted in the database and entered through the UI. Never in `appsettings.json`, never in the repo, never in a log, an exception message, or an LLM prompt.
- `.gitignore` covers `publish/`, `artifacts/`, `*.db*`, secrets and any real receipt/voice/statement fixtures **before the first commit**.
- Test fixtures are synthetic. Real financial data never enters the repo.

## 5. Conventions

- Run `dotnet test` before claiming anything works. State the actual result; never assert success without having seen it.
- Prefer deterministic C# over an LLM call wherever both would work.
- When a decision is expensive to reverse (schema, storage encoding, a seam), stop and flag it rather than choosing quietly.

## 6. Closing a phase *(settled)*

A phase is not finished when its tests pass. It is finished when the next person — or the next
agent, with none of this conversation — can pick it up without rediscovering what it cost.

- **Write down what outlived the phase.** A rule that will bind future work goes in this file. A
  decision and its reasoning goes in `docs/OPEN-QUESTIONS.md`. Work deliberately not done goes in
  `docs/BACKLOG.md` with enough reasoning that nobody re-proposes it as new. A repeatable procedure
  goes in `ops/RUNBOOK.md` or a skill. If it changes how someone should work, it is not optional.
- **Only what generalises.** A defect fixed inside the phase is in the commit that fixed it; that
  is where it belongs. Promote a lesson here only when it would otherwise be paid for twice —
  the PostgreSQL microsecond flake earned its line by shipping twice before anyone noticed.
- **Keep this file short.** It is read in full at the start of every session, so length is a tax on
  every single one. Anything that runs past a short paragraph belongs in its own document under
  `docs/`, linked from here in one line. Prefer deleting a rule the code now enforces by itself: a
  test that fails is worth more than a paragraph that asks nicely.
- **Correct what has gone stale**, starting with the status block and `README.md`. A public README
  that understates the project by two phases, or claims a guarantee the code stopped providing, is
  worse than no README — someone trusts it.
- **Leave nothing uncommitted.** Working tree clean, every documentation change committed alongside
  the work it describes, and the branch integrated or explicitly left open by the operator's choice.
