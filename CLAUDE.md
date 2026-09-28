# noof-ledger — working agreement

Personal finance tracker. Telegram bot captures spending (text, voice, receipt photos), an LLM categorises it per line item, a local Blazor dashboard shows it across multiple wallets and currencies. C# / .NET 10, EF Core, strict TDD, local hosting, **public repo**.

> **Status:** Phases 0, 0b, 1A, 1B, 1C, 1D, 2, 3, 4, 5 and 6 complete — full detail in `docs/STATUS.md`.
> Cross-currency conversion, transfers, exchange-office slips and editing receipt lines remain future
> phases. Rules below marked *(settled)* are direct user decisions and are not up for re-litigation.
>
> Deferred **decisions** live in `docs/decisions/`; deferred **work** lives in `docs/backlog/`. Check both before proposing something as missing.
>
> **`noof_ledger` holds the operator's real credentials now.** Never run tests, experiments or manual checks against it, or call the live model, without an explicit request. Tests use the `noof_ledger_test_template` clones only.

---

## 1. Model and effort policy

**The kind of work picks the model, named on every dispatch** — Agent `model`, Workflow per-stage
`model` *(operator's decision, 2026-09-28)*. Sonnet proved weak wherever judgment or self-checking
was needed (it reported "0 references left" with one remaining, and over-built a hook under an
adversarial review), so cost is kept down by handing mechanical work down, not by defaulting to the
cheapest tier.

| Model | Use for |
|---|---|
| **opus** | Anything with judgment: implementing behaviour · design and planning · triaging review findings · investigations that end in a conclusion or recommendation · rules and docs that encode decisions · verifying a subagent's factual claims |
| **sonnet** | Mechanical work with a fully specified recipe: moving/splitting files by a given scheme · sweeping references · renames · boilerplate from an explicit template. A recipe that leaves a decision open is judgment — opus. |
| **haiku** | Running tests or builds and reporting pass/fail · reading a log for a known string · listings |

The operator's global default model is Opus, so a dispatch without `model` runs on Opus: fine for
judgment work, but sonnet and haiku work must name its model. Reviews use the models the review
policy below names.

**Effort levels.** Opus at `medium`; `high`/`xhigh` only for architecture- and correctness-critical
reasoning, never for small tasks. `low` for mechanical work.

**Review policy — three tiers, three cadences, don't substitute one for another:**
- **Per task** (inside a phase or plan): review runs on opus (effort medium). No Codex, no Fable per task.
- **Per pull request: one Codex review**, a model family different from the implementer, run from the
  PR branch with the Codex CLI — the plugin's `/codex:review`/`/codex:adversarial-review` slash
  commands cannot be invoked by an agent. Never enable the plugin's stop-time review gate; it would
  review on every Stop and burn the quota.
  - `codex review --base <base-branch> -c model_reasoning_effort="medium"` for a mechanical PR — the
    CLI rejects a PROMPT combined with `--base`, so this form takes no prompt.
  - For a PR that makes design choices, use the plugin's companion script instead, which supports
    both a base branch and a focus prompt: `node <path> adversarial-review --wait --base
    <base-branch> "<focus>"` (challenge the approach, assumptions, trade-offs, failure modes — not
    just defects); default to adversarial when unsure. Resolve `<path>` with Glob on
    `~/.claude/plugins/cache/openai-codex/codex/*/scripts/codex-companion.mjs` — the version
    directory changes on update, never hard-code it.
  - Run in the foreground with a 600000 ms timeout. Findings are judged, not obeyed: fix what's
    confirmed, reply in the PR description to what's rejected and why.
  - Codex refuses on usage limit → don't wait for the window; fall back to an opus review and say so
    in the PR description.
- **Fable 5.1 runs twice per phase** *(operator's decision, 2026-09-28)* — never per PR, never per
  fix round. Models in one family share blind spots; a different family is the cheapest independence
  available.
  - **At planning**, once, after the phase's spec and plan are written and before implementation
    starts. It reviews the decision, not code: alternatives considered, risks and failure modes,
    conflicts with *(settled)* rules and `docs/decisions/`, and whether the PR cut is right.
    Its findings are triaged like any review.
  - **At the end**, closing the phase or a batch of PRs.
  - **Trial, 3 phases** *(operator, 2026-09-28)*: both Fable reviews run in parallel with a Codex
    adversarial review of the same scope — closing: the companion script above with `--base <base>`;
    planning: `codex exec -s read-only "<prompt naming the spec and plan paths, even under the ignored
    .superpowers/, asking for an adversarial review of the decision>"`. An opus pass merges both lists
    (deduped, each tagged both / Fable only / Codex only), then triages. The phase's closing notes
    record one tally line per review (confirmed findings by tag); after the third phase the operator keeps both or drops one.
- **Review findings are triaged, not all fixed** *(operator's decision, 2026-09-28)*, for Copilot and
  Codex alike: fix a critical finding (real bug, wrong money/balance, data loss, secret leak,
  security hole, broken build/test, *(settled)*-rule violation); reply with a sentence of reasoning
  and don't change code for a non-critical one (style, naming, nits, speculative hardening,
  preference); ask the operator first on anything expensive (new design, migration, another topic,
  roughly >~50 lines) instead of starting it, proposing a backlog entry — judged by this
  single-operator local app's real risk, not completeness.
- **Copilot is optional** — its quota runs out, so CI and the per-PR Codex review are the gate and
  nothing waits for Copilot. When a Copilot review does arrive, handle it as below.
- **Copilot fix rounds:** one round, one commit, opus-only review (effort medium), push — at most 2
  rounds per PR, then list what's left for the operator. No
  Codex/Fable for a Copilot round unless it touches money, secrets, a migration or the public
  surface and a stronger review is judged necessary — say why in the PR. Resolve only the threads
  you replied to or fixed, by id, never "resolve all unresolved" (hides new comments).

**Anti-patterns — do not do these:**
- Running a test suite on opus — including by dispatching it without `model`. That is a haiku task;
  the model is not what makes tests pass.
- Using xhigh effort to rename a variable, fix a typo, or add a using directive.
- Choosing the model by how important a task *sounds*. A rename in money code is still sonnet; a
  judgment call in a small doc is still opus. The kind of work decides.
- Re-running expensive work that is already cached or already done. Check first.
- A Fable review per fix round or per Copilot round — this cost ~5 hours on PR #3.

## 2. Subagent usage

**Delegate aggressively, and delegate downward.** Any task that is self-contained, produces a summarisable result, and does not need this conversation's full context should go to a subagent on the model §1 names for that kind of work.

- **Always delegate:** multi-file searches, "find where X is defined", test runs, build verification, reading a large file to answer one question, independent research, per-file review passes.
- **Parallelise:** independent work goes out in one message as multiple subagents, not sequentially.
- **Workflows:** for fan-out work, set `model` per stage by §1 — haiku for test/build stages, sonnet for recipe-driven mechanical stages, opus for research, verification and synthesis. A mechanical stage left without `model` inherits Opus.
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
  not rejection. Do not re-add verbatim checks or sanity bounds — `docs/decisions/p2-1-quote-and-verify-removed.md`.
- **A wallet's balance is derived, never stored** *(settled 2026-09-24, Phase 4)* — details in
  `.claude/rules/database.md`.
- **A receipt's amounts and date never come from the model** *(settled 2026-09-25, Phase 6)* — the
  fiscal QR or the vision fallback fixes them; details in `.claude/rules/receipts.md`.

**Architecture**
- Projects are split: `Domain` ← `Application` ← (`Persistence` · `Ai` · `Fx` · `Receipts` · `Telegram` · `Web`) ← `Host`.
- `Noof.Ledger.Web` is UI only — no `DbContext`, no EF types, no `HttpClient`, no `Program.cs`. Enforced by `DisableTransitiveProjectReferences` plus an architecture test, because project references are transitive at compile time and a convention alone will not hold.
- `Noof.Ledger.Domain` has zero NuGet references. Asserted by a test.
- **A new NuGet package or ported code updates `THIRD-PARTY-NOTICES.md` in the same commit**, after
  checking its licence — `.claude/rules/dependencies.md`, which loads on `Directory.Packages.props`/`*.csproj`.
- Do not add MediatR, AutoMapper, generic repositories over `DbContext`, or CQRS scaffolding.
- **Minimum accessibility.** A type is `internal` unless another assembly names it; a member is
  `private` unless something outside its type calls it. Tests reach internals through
  `InternalsVisibleTo`, never by widening `src` — and substituting an internal interface needs
  `InternalsVisibleTo("DynamicProxyGenAssembly2")` on the declaring assembly as well, or
  NSubstitute fails at runtime. `PublicSurfaceTests` checks each assembly's allowlist, one file per
  assembly under `tests/Noof.Ledger.Architecture.Tests/PublicSurface/`; widening it is an edit to
  that assembly's file, which is the point.
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
  | Domain, Ai, Telegram, Receipts, Architecture, Host | 30000 | 120000 |
  | Persistence, filtered | 60000 | 120000 |
  | Persistence, full (≈13 min since Phase 6) | 600000 | 600000 |
  | Full solution (13–15 min since Phase 6) | 600000 | 600000 |
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
- **A fiscal receipt's verification URL, or its `vl` payload, is never logged** *(settled 2026-09-25,
  Phase 6)* — a log line or exception message names at most the `vl` value's first 8 characters.
- **...and never in a model prompt either** *(settled 2026-09-27)* — a full verification URL or `vl`
  payload never reaches `record_transaction`, `categorize_receipt` or a correction's prompt.
  `FiscalVerificationUrl.StripUrl` is the one place that strips it.

## 5. Conventions

- Run `dotnet test` before claiming anything works. State the actual result; never assert success without having seen it.
- Prefer deterministic C# over an LLM call wherever both would work.
- When a decision is expensive to reverse (schema, storage encoding, a seam), stop and flag it rather than choosing quietly.
- **Screenshots stay current** *(settled 2026-09-26)*. A change to anything the operator sees — a
  page, or a message the bot sends — adds mock data for anything new (`tools/Noof.Ledger.Demo`), runs
  `.\run.ps1 screenshots`, commits the changed images in `docs/screenshots/` with the change, and sends
  them to the operator (the command lists them; phone-sized copies are in `artifacts/screenshots/`).
  A new page or bot reply gets a screen or a scene added. Never from `noof_ledger`.

**Pull requests are small** *(operator's decision, 2026-09-28)*
- One topic per PR — a phase is a series of PRs, not one; the plan cuts it into PR-sized tasks up
  front and names the PR boundaries.
- Not too small either: a PR is one coherent change a reviewer reads in one sitting. Related edits
  go together — e.g. several rules about how agents work are one PR, never a PR per paragraph of
  CLAUDE.md. A follow-up that belongs to an open PR goes into that PR, not a new one.
- Stop and propose a split (operator decides) past ~500 changed lines excluding generated files
  (migrations' `.Designer.cs`, the model snapshot, `schema.expected.sql`), or past one of §4's
  split assemblies per PR (e.g. touching both Persistence and Web).
- Mechanical moves/renames get their own PR, separate from behaviour changes.
- Independent PRs branch from `master`; a PR needing another's changes is stacked on it (base = that
  branch), never merged into it.
- Shared hot files (backlog entries, per-assembly allowlists) are split across PRs so parallel work
  doesn't conflict.

**A PR says when it is ready** *(operator's decision, 2026-09-28)*
- Open every PR as a draft (`gh pr create --draft`) and keep it draft while anything is still in
  progress (implementation, tests, Codex triage, Copilot rounds).
- Only when done: `gh pr ready <n>` plus one PR comment starting "Ready to merge" with one line per
  check (tests run and result, Codex review triaged, Copilot rounds done if it reviewed) and, for a stacked PR,
  "merge after #N".
- A PR that needs more work after that goes back to draft (`gh pr ready <n> --undo`). The operator
  merges only non-draft PRs.
- `gh` posts as the operator's account, so every comment, review reply or PR body an agent writes
  ends with the line `🤖 Written by Claude Code (<model>)` (PR bodies keep the "Generated with Claude
  Code" footer). Never edit or sign a comment the operator wrote.

**Waiting on a PR** *(operator's decision, 2026-09-28)*
- After pushing to a PR branch, run `.\run.ps1 pr-wait <n>` once in the foreground (600000 ms
  timeout) instead of polling `gh` yourself — it blocks on CI internally and returns once. A red CI
  check keeps the PR in draft until it is fixed and pushed again.
- CI is the gate; it waits for a Copilot review only when Copilot is actually requested on the PR
  (the operator's Copilot quota runs out, so most PRs never get one) — otherwise it says so and
  doesn't wait on it. Exit 2 (timeout) → run it once more at most.
- Handle every comment it lists in ONE round, per the Copilot-fix-round rule in §1.

## 6. Closing a phase *(settled)*

When closing a phase, read and follow `docs/CLOSING-A-PHASE.md` *(settled)*.

Keep this file short: path-specific rules go in `.claude/rules/` with `paths:` frontmatter, anything longer in `docs/`.
A path-scoped rule loads when a matching file is read, not when a shell command touches one — after
`dotnet ef migrations add`, `run.ps1` or a scripted edit, read the file (or the rule) before relying on it.

## 7. Shell and search

- Locate files with the Glob tool, search content with Grep, read with Read — not shell
  `find`/`grep`/`cat` pipelines; each shell process costs time on Windows.
- Never `find` from the disk root or the home directory — a hook refuses it (an orphaned
  `find / -iname X` once held 11.6M handles for hours and slowed every process spawn on the machine).
- NuGet package API: `~/.nuget/packages/<id-lowercase>/<version>/lib/<tfm>/` holds the DLL and XML
  docs; get source with `ilspycmd` (a global dotnet tool). No disk-wide search.
- The app's own data lives in `%LOCALAPPDATA%\NoofLedger\`: `backups\`/`manual-backups\`, `logs\`,
  `demo\`, `dp-keys\` (Data Protection keys — secret) and `db.connection` (secret). Search these
  freely; never read `dp-keys\` or `db.connection` without an explicit request.
- A command that hits its timeout keeps running in the background — stop what you started (KillShell
  / `Stop-Process`) before you finish; never leave it running.
- Windows Git Bash `find` is unix find — never pipe into `find /c`; use `grep -c` instead.
