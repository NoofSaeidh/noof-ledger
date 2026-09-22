# noof-ledger — working agreement

Personal finance tracker. Telegram bot captures spending (text, voice, receipt photos), an LLM categorises it per line item, a local Blazor dashboard shows it across multiple wallets and currencies. C# / .NET 10, EF Core, strict TDD, local hosting, **public repo**.

> **Status:** spec approved (`docs/superpowers/specs/2026-09-19-noof-finance-design.md`); **Phases 0, 0b, 1A and 1B complete** — solution, EF Core model and migrations, PostgreSQL money-storage gate, authentication in two modes, the `user set-password` verb, the loopback interlock, a Blazor Server shell, Telegram capture with a durable queue, LLM categorisation with quote-and-verify amounts and a write-once merchant identity table, and a dashboard reading it all back through a read model. 408 solution tests plus 12 Playwright E2E tests, all green; `ops/publish.ps1` produces a runnable host. Next is Phase 2 (money model — exact balances across all five currencies under `ru-RU` and `sr-Latn-RS`, and a proven backup restore). Rules below marked *(settled)* are direct user decisions and are not up for re-litigation.
>
> Deferred **decisions** live in `docs/OPEN-QUESTIONS.md`; deferred **work** lives in `docs/BACKLOG.md`. Check both before proposing something as missing.
>
> **Before enabling `Auth:Mode=Cookie`, the operator must run `user set-password noof`** — the existing row's password was set during Phase 0b verification and is not theirs.

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

**Don't:** defensive boilerplate for conditions that cannot occur · abstractions with one implementation and no second in sight · ceremony that exists to look enterprise.

## 4. Engineering rules *(settled)*

**Money**
- Money is `decimal` + `Currency` in the domain. Never `double`, never `float`. Every method signature, test, component and report sees a `decimal`.
- No number in user-facing output ever originates from a model. The LLM phrases figures that C# computed.

**Architecture**
- Projects are split: `Domain` ← `Application` ← (`Persistence` · `Ai` · `Fx` · `Receipts` · `Telegram` · `Web`) ← `Host`.
- `Noof.Ledger.Web` is UI only — no `DbContext`, no EF types, no `HttpClient`, no `Program.cs`. Enforced by `DisableTransitiveProjectReferences` plus an architecture test, because project references are transitive at compile time and a convention alone will not hold.
- `Noof.Ledger.Domain` has zero NuGet references. Asserted by a test.
- Do not add MediatR, AutoMapper, generic repositories over `DbContext`, or CQRS scaffolding.

**Database**
- **After `dotnet ef migrations add`, convert the migration `.cs` file to a file-scoped namespace.** `IDE0161` is an error here, so it **fails the build** until you do. Its `.Designer.cs` and `LedgerDbContextModelSnapshot.cs` carry `// <auto-generated />`, which exempts them from code-style analyzers — leave those exactly as EF emits them. Converting them is churn that EF overwrites on the next scaffold anyway. Only the migration file is hand-edited (it carries the raw SQL EF cannot express), and only it is checked.
- EF Core with migrations from the first commit. The database must be creatable from empty and upgradeable in one mechanism.
- **Never call `EnsureCreated()`** — anywhere, including test helpers. It bypasses migrations and permanently poisons that database for `Migrate()`.
- Tests run against a real database, never the EF InMemory provider.

**Testing**
- TDD: a failing test first, for all behaviour. Exempt: migrations, DTOs, `Program.cs` wiring.
- `global.json` must contain `{"test":{"runner":"Microsoft.Testing.Platform"}}` or `dotnet test` fails outright on SDK 10.0.204.
- The inner red-green loop never touches the network or a real model. Live model calls live in an opt-in suite that is skipped by default.

**Secrets — this is a public repo**
- Secrets are encrypted in the database and entered through the UI. Never in `appsettings.json`, never in the repo, never in a log, an exception message, or an LLM prompt.
- `.gitignore` covers `publish/`, `artifacts/`, `*.db*`, secrets and any real receipt/voice/statement fixtures **before the first commit**.
- Test fixtures are synthetic. Real financial data never enters the repo.

## 5. Conventions

- Run `dotnet test` before claiming anything works. State the actual result; never assert success without having seen it.
- Prefer deterministic C# over an LLM call wherever both would work.
- When a decision is expensive to reverse (schema, storage encoding, a seam), stop and flag it rather than choosing quietly.
