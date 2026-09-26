# noof-ledger — working agreement

Personal finance tracker. Telegram bot captures spending (text, voice, receipt photos), an LLM categorises it per line item, a local Blazor dashboard shows it across multiple wallets and currencies. C# / .NET 10, EF Core, strict TDD, local hosting, **public repo**.

> **Status:** Phases 0, 0b, 1A, 1B, 1C, 1D, 2, 3, 4 and 5 complete — full detail in `docs/STATUS.md`.
> Cross-currency conversion, transfers and receipt photos remain future phases. Rules
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
- **A wallet's balance is derived, never stored** *(settled 2026-09-24, Phase 4)* — details in
  `.claude/rules/database.md`.

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
- **Render modes are per-page and stay that way** — moved to `.claude/rules/web-ui.md`, which loads
  automatically when you touch `src/Noof.Ledger.Web/**` or any `.razor` file.
- **Nothing at startup may block on the database** *(settled 2026-09-25, Phase 5)*. The host starts —
  Kestrel, the sign-in page, `/healthz`, file logging — with PostgreSQL down; `IDatabaseGate` tracks
  `Waiting`/`Migrating`/`Ready`/`Failed` and every page shows a waiting banner until `Ready`. Every
  `BackgroundService` loop awaits `IDatabaseGate.WaitUntilReadyAsync` before its first iteration and
  catches every non-cancellation exception per tick — a loop that lets an exception escape faults the
  host's `BackgroundService` and stops the whole process with exit code 0, silently. This cost a
  critical review finding: `SecretSnapshotRefreshWorker` had no catch, so PostgreSQL going down *after*
  `Ready` took the host down outright even though startup itself tolerated it fine.

**Database**

Moved to `.claude/rules/database.md` — loads automatically when you touch Persistence source or
tests, `TestKit`, any `Migrations` folder, `ops/**`, `run.ps1`, or the backup code in Host.

**Logging** *(settled 2026-09-25, Phase 5)*

Moved to `.claude/rules/logging.md` — loads automatically when you touch a `Logging/` or
`Diagnostics/` folder, any `*Log.cs` file or test with `Log` in its name, `appsettings*.json`,
`Noof.Ledger.Host` or its tests, the trace tests, or the Log settings/Diagnostics/Trace razor pages.

**Testing**
- TDD: a failing test first, for all behaviour. Exempt: migrations, DTOs, `Program.cs` wiring.
- Test-writing details (log directories, `timestamptz` precision, no test-only config keys) are in
  `.claude/rules/tests-detail.md`; statically-rendered-page and silent-look-failure rules are in
  `.claude/rules/web-ui.md` — both load automatically under `tests/**` / `src/Noof.Ledger.Web/**`.
- **Watch the new test fail before you let it pass.** A guard that has never been seen red may be
  enforcing nothing — a grep that matches no file, a rule whose subject set is empty. Break the
  thing deliberately, see the failure name it, put it back.
- `global.json` must contain `{"test":{"runner":"Microsoft.Testing.Platform"}}` or `dotnet test` fails outright on SDK 10.0.204.
- The inner red-green loop never touches the network or a real model. Live model calls live in an opt-in suite that is skipped by default.
- **Database and E2E test projects run filtered to the classes a change touches, and in full once at
  the end of a phase** *(operator's decision, 2026-09-24)*. Both share a PostgreSQL server across
  worktrees, so an unfiltered run outside that one end-of-phase pass risks colliding with parallel
  work instead of catching anything the filtered run would not.

**Waiting on tests**
- A foreground command returns the moment it finishes; a `timeout` is only a ceiling, and on timeout
  Claude Code moves the command to the background (it keeps running, output goes to a file) — it is
  not killed.
- Step 1: run tests in the foreground with `timeout` ≈ p90 for that target. Step 2: if it timed out,
  wait ONCE, blocking, on its output file with `timeout` ≈ p99:
  `until grep -qE "Test run summary|error CS|Build FAILED" "<output file>"; do sleep 5; done`. Past
  p99 treat the run as hung: read the output tail, stop it, report. Never poll with `echo waiting` /
  `true` / bare `sleep` — each such call re-reads the whole context (≈97M input tokens in one week,
  measured); a hook refuses them.
- Measured from 14 days of runs incl. build (ms):

  | Target | Step 1 (≈p90) | Step 2 (≈p99) |
  |---|---|---|
  | Domain, Ai, Telegram, Architecture, Host | 30000 | 120000 |
  | Persistence, filtered | 60000 | 120000 |
  | Persistence, full | 180000 | 330000 |
  | Full solution | 150000 | 600000 |
  | E2E (no data yet — recalibrate) | 180000 | 600000 |

- Waiting for something you did not start (the shared test-database lock, another session): same
  rule — one blocking `until <condition>; do sleep 15; done` with timeout 600000.

**The model** *(settled)*

Moved to `.claude/rules/model.md` — loads automatically when you touch `src/Noof.Ledger.Ai/**`, its
tests, or the categorisation code that calls it.

**Bot text** *(settled 2026-09-23)*

Moved to `.claude/rules/bot-text.md` — loads automatically when you touch Telegram source/tests or
the code that builds the echo text.

**Secrets — this is a public repo**
- Secrets are encrypted in the database and entered through the UI. Never in `appsettings.json`, never in the repo, never in a log, an exception message, or an LLM prompt.
- `.gitignore` covers `publish/`, `artifacts/`, `*.db*`, secrets and any real receipt/voice/statement fixtures **before the first commit**.
- Test fixtures are synthetic. Real financial data never enters the repo.

## 5. Conventions

- Run `dotnet test` before claiming anything works. State the actual result; never assert success without having seen it.
- Prefer deterministic C# over an LLM call wherever both would work.
- When a decision is expensive to reverse (schema, storage encoding, a seam), stop and flag it rather than choosing quietly.

## 6. Closing a phase *(settled)*

When closing a phase, read and follow `docs/CLOSING-A-PHASE.md` *(settled)*.

Keep this file short: path-specific rules go in `.claude/rules/` with `paths:` frontmatter, anything longer in `docs/`.
A path-scoped rule loads when a matching file is read, not when a shell command touches one — after
`dotnet ef migrations add`, `run.ps1` or a scripted edit, read the file (or the rule) before relying on it.
