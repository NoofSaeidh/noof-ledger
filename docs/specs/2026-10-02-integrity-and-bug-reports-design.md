# Phase 8a — Integrity checks, explanations and bug reports (design)

**Status:** designed with the operator in conversation 2026-10-02; awaiting the operator's review of
this written spec.

This is the first half of Phase 8 ("Integrity + explainer") in the phase table of
`2026-09-22-natural-language-capture.md`, which the operator split in two: **8a** finds what is wrong
(this spec) and **8b** analyses spending — a period summary in the dashboard and in the bot — with its
own spec. Letters rather than a renumbering, as with 0b and 1A–1D, so the existing references to later
phases (Phase 11's prompt tuning) stay true. It amends the original design's §10 (Tier 2 and Tier 3)
where the decisions below say so.

## Goal

The app finds its own inconsistencies: a handful of SQL checks, run on demand, each row a finding.
A finding that means our code wrote contradictory data turns the health tile red; one that means
money silently never reached the ledger turns it amber. A model explains a finding on request, and
when it judges the cause to be the app rather than the data it offers to file a bug report. The
operator files a bug report from Telegram with `/bug`, the bot answers with an explanation, and every
report reaches Claude Code for triage as Markdown — from the dashboard, a CLI verb, or a skill.

## Decisions (operator's, 2026-10-02)

| # | Decision |
|---|---|
| IR-1 | **Phase 8 splits** into 8a (finding bugs) and 8b (spending analysis). Free-form questions about spending (*"сколько я потратил на кафе в сентябре?"*) are later than 8b; 8b is a ready-made summary. |
| IR-2 | **Two groups of findings, kept apart:** *Bugs* (our code made the data inconsistent) and *Waiting on you* (the operator's data needs an action). The checks are the four of §1 — chosen by the agent at the operator's request ("сам думай"), approved as listed. |
| IR-3 | **Checks run on demand**, nothing stored: opening the page or a health run executes them; a finding disappears when the data or the code is fixed. |
| IR-4 | **The model explains, in English**, on request in the dashboard and in reply to `/bug` in the bot. |
| IR-5 | **No guarantee on the explanation's figures** — *"гарантии тут не сильно важны, это не ключевой функционал"*. The original §12 acceptance ("a fabricated number in a mocked response is rejected, not displayed") is dropped. Every figure the model is given is still computed and formatted by C#. |
| IR-6 | **The model may offer a bug report** (*"просто появляется диалог «хотите создать баг»"*): its answer says whether the cause looks like a bug in the app, which only shows or hides a *Create bug report* offer; the operator decides. A deliberate softening of §10's "the LLM never returns a branch of control flow" — nothing the model says changes a finding, a status or a health level. |
| IR-7 | **`/bug` in Telegram saves a report and answers with an explanation**; in the dashboard a report holds the operator's text, the record, the findings and the log lines as they were when it was filed. |
| IR-8 | **Reports reach triage three ways**, all in this phase: *Download open reports* on `/bugs`, `.\run.ps1 bugs export`, and a Claude Code skill `/bugs` that runs the verb and triages. |
| IR-9 | **Branching:** an aggregate branch `phase-8a`, cut from `phase-7` while Phase 7 is unmerged (its checks read Phase 7's tables), its draft PR stacked on `phase-7` and retargeted to `master` once Phase 7 merges. |

### Departures from the original design, and why

- **§10's "LLM never returns a branch of control flow"** is softened by IR-6, for the offer only.
- **§12's fabricated-number acceptance** is dropped (IR-5).
- **Tier 2's candidate list** is cut to four checks (§1). Dropped: *transfer legs net* (cross-currency
  legs cannot net; a transfer's entries agreeing with its row is part of I-1), *uncategorized expense
  line* (the write path cannot produce one), *missing FX snapshot* (no FX snapshot exists since Phase
  7), *balance vs reconciliation* (a checkpoint absorbing a back-dated record is rule M6 working as
  designed, and flagging it would fire on every remembered coffee), *duplicate-looking transaction*
  (needs a "not a duplicate" table and UI — `docs/backlog/duplicate-looking-transactions.md`), and
  *lines don't sum to the receipt's total* (the operator already accepted such a receipt through
  "Record anyway").

## 1. Checks

Each check is one SQL query; each row it returns is one finding.

### Bugs — any finding means our code wrote contradictory data (`Failing`)

**I-1 · Postings disagree with their sources.** A transaction's `entries`, per wallet, currency and
role, differ from those derived independently from its `line_items`, `charges` and `transfers` (the
derivation in `Entries_always_agree_with_the_lines_they_are_summed_from` and its sibling queries
`misplaced`, `brokenTransfers` and `mispricedCharges`, moved into production code). Facts: the
wallet, currency and role; expected and actual amounts. The write-path test then calls I-1 and
expects no finding, so its oracle stays the one production code uses — and stays independent of
`LedgerPostings`, which writes the entries.

**I-2 · A record's facts do not match its kind.** Any of:
- a `Transfer` without a `transfers` row, or a `transfers` row on a record of another kind;
- a `BalanceCheck` without a `balance_checks` row, or one on a record of another kind;
- a `charges` row on a record that is not an `Expense`;
- `fee_leg` set without exactly one `Fee` line on the record, or a `Fee` line on a transfer whose
  `fee_leg` is null;
- a transfer leg whose currency differs from its wallet's;
- `balance_checks.wallet_id` or `transfers.from_wallet_id` differing from `transactions.wallet_id`;
- `failure_reason` other than `None` on a record whose status is not `Failed`.

Facts: which condition, and the values that disagree.

**I-3 · Stuck in the pipeline.** Either:
- a record `Captured` for more than 10 minutes with no `Pending` or `Claimed` job, unless it is a
  receipt or slip awaiting confirmation;
- a job that exhausted its attempts (`Failed` in `categorization_jobs`).

Facts: the status, the age, the job kind and its last error's first line.

"Awaiting confirmation" is the predicate `IsAwaitingConfirmationAsync` implements in C# (fiscal: no
`CategorizeReceipt` job; exchange: Phase 7's A-7). I-3 and I-4 express it in SQL; a test seeds every
awaiting and not-awaiting case and asserts the SQL and the C# predicate agree.

### Waiting on you — money that never reached the ledger (`Warning`)

**I-4 · Not in the ledger.** Older than 24 hours:
- a `Failed` record whose `failure_reason` is not `None` — the bot asked for something (the received
  amount, a wallet, a slip's figure) and no reply came;
- a receipt or slip awaiting confirmation ("Record anyway" not pressed).

A `Failed` record with reason `None` is a failed job, already a Bug under I-3. Facts: the reason, the
age, the date and the amount the record holds, when it holds one.

### Shape

`Noof.Ledger.Application` (`Diagnostics/Integrity/`), contracts only:

- `IntegrityCheck` (enum: `PostingsDisagree`, `FactsMismatchKind`, `StuckInPipeline`,
  `NotInLedger`) and `IntegrityGroup` (`Bug`, `WaitingOnYou`).
- `IntegrityFinding` — `Check`, `Group`, optional `TransactionId`, `WalletId`, `JobId`, and `Facts`.
- `IntegrityFact` — a named, typed fact: `MoneyFact(decimal, CurrencyCode)`, `DateFact`,
  `TextFact`, `CountFact`. No `double`, no pre-formatted amount; C# formats at display.
- `IIntegrityChecks` — `FindAllAsync(ct)` and `FindForTransactionAsync(transactionId, ct)` (the same
  queries filtered to one record, for `/bug`).

`Noof.Ledger.Persistence`: one internal class per check, sharing an internal interface (`Check`,
`Group`, `FindAsync(scope, ct)`), its SQL beside it as `BalanceSql` keeps its own; `EfIntegrityChecks`
runs them one at a time. "Now" comes from `TimeProvider`.

**Health.** `IntegrityHealthCheck : ISystemHealthCheck` (Persistence, registered by
`AddNoofPersistence`) runs every check: any Bug → `Failing`, "N bugs found"; only Waiting-on-you →
`Warning`, "N waiting on you"; none → `Ok`. The Home tile, `/diagnostics` and the bot's `/health` get
it unchanged, with the existing 30 s cache and 5 s timeout; a timeout or an exception is reported the
way `SystemHealth` reports any check's.

**Boundary.** `HealthCheckBoundaryTests` is extended to the files implementing the integrity checks,
and a new rule — promised by §10, never written — fails when a health check or an integrity check
references `Noof.Ledger.Ai`, `IChatClient` or `IFindingExplainer`. Persistence cannot reference Ai
anyway (`ProjectReferenceTests`); the rule is what still holds if a check ever moves.

## 2. Explanations

`IFindingExplainer` (Application): `ExplainAsync(ExplanationRequest, ct)` →
`Explanation(string Text, bool LooksLikeBug)`, or a failure the caller renders as "Couldn't explain
this right now" (logged as a Warning, without the model's text).

`ExplanationRequest` carries, as text C# composed:
- each finding with its check's fixed English description (what it catches and what it usually
  means) and its facts, formatted by C#;
- for `/bug`: the operator's text and the record's summary (kind, date, wallet or both legs, lines,
  amounts, status, failure reason).

A `/bug` without a record sends at most the first 20 findings, in check order, and says how many there
were in total.

`ChatFindingExplainer` (`Noof.Ledger.Ai`) answers through the forced tool `write_explanation`,
`strict: true`, one round, `ChatToolMode.RequireSpecific` — as `read_receipt` does. Arguments:
`{ text, looks_like_bug }`. The system instructions ask for a short English answer (what is wrong, the
likely cause, what to do — e.g. "reply to the echo with the amount", or "this is a bug — file it"),
and `looks_like_bug = true` only when the cause is the app's code rather than the operator's data or
a misread phrase. Same `IChatClientFactory`, same single model.

Everything sent passes through `FiscalVerificationUrl.StripUrl` first, the operator's `/bug` text
included (CLAUDE.md §4 Secrets). The prompt holds only what categorisation already sends: amounts,
dates, categories, merchant and wallet names.

The model is called only when the operator presses *Explain* or sends `/bug`.

## 3. Bug reports

### `bug_reports`

| Column | |
|---|---|
| `id uuid` PK | |
| `number integer` | identity, shown as `#12` |
| `created_at timestamptz` | |
| `source integer` | `Telegram = 0`, `Dashboard = 1` |
| `text text null` | the operator's words |
| `transaction_id uuid null` FK | the record the report is about |
| `telegram_chat_id bigint null`, `telegram_message_id integer null` | the `/bug` message, to reply to |
| `status integer` | `Open = 0`, `Closed = 1`; `closed_at timestamptz null` |
| `explanation_state integer` | `Pending = 0`, `Done = 1`, `Failed = 2`; `explanation_attempts integer` |
| `explanation text null`, `looks_like_bug boolean null` | |
| `findings jsonb` | the findings when the report was filed |
| `log_lines jsonb` | the record's `app_log` rows when the report was filed — already redacted by `SecretRedactor` |

Findings disappear once fixed and Debug log rows after 7 days, hence the snapshots; revisions are never
deleted and are read live. One migration in this phase, this table.

### `/bug` in Telegram

- Owner only, through `TelegramOwnerGate`, as `/health`: anyone else gets silence. Intercepted by
  `TelegramUpdateRouter` before capture, never creates a transaction; `/bug@<anything>` too; added to
  `setMyCommands` beside `/health`.
- **As a reply to an echo**, the report is linked to that echo's record, found the way a correction
  finds it. **Otherwise** it has no record. The text after `/bug` is optional.
- The bot saves the report with its findings and log-line snapshots and answers at once:
  `Bug report #12 saved.`
- **`BugReportExplanationWorker`** (Host) picks up `Pending` reports, oldest first: runs the checks
  (`FindForTransactionAsync` for a linked report, otherwise `FindAllAsync`), calls the explainer, stores
  the answer, and replies to the `/bug` message with the explanation and a line `2 findings on this
  record` / `No integrity findings`. When `looks_like_bug` is false it adds an inline button
  **Close report** (owner-only callback). Three failed attempts → `Failed`, and the bot replies
  `Couldn't explain it — the report is saved.` The worker awaits `IDatabaseGate.WaitUntilReadyAsync`
  and catches every non-cancellation exception per tick (CLAUDE.md §4).
- A report is its own queue rather than a `categorization_jobs` kind: that queue is keyed by a
  transaction, and a report may have none.

### In the dashboard

- **`/diagnostics/integrity`** — interactive server page: *Bugs* and *Waiting on you*, each finding with
  its check, its facts formatted by C#, a link to the record's trace or the wallet, and an **Explain**
  button whose answer appears inline in a `MudAlert`. When `looks_like_bug` is true the alert adds
  "Looks like a bug in the app. Create a bug report?" with **Create** / **Dismiss** — inline buttons,
  never a dialog (render-mode rule). *Create* files a report with `source = Dashboard`, the finding, the
  explanation (`explanation_state = Done`) and the log-line snapshot, and links to it. Empty: "No
  findings". `/diagnostics` links to it from the Integrity row, which also shows the number of open
  reports.
- **`/bugs`** — interactive: open reports by default, an *All* link (`?status=all`); number, date, text,
  record link, status. **Download open reports (.md)** — one file, through a JS-interop blob download.
- **`/bugs/{number}`** — the text and the explanation; the findings then and the record's findings now;
  the trace link; the snapshotted log lines; **Close / Reopen**; **Copy as Markdown** (clipboard via
  JS interop) with the line "Contains your data — never paste it into a public issue."
- NavBar: **Bugs**, beside Diagnostics.

### Markdown and triage

- `IBugReportMarkdown` (Application; implemented in Persistence beside the store) renders one report
  or several: header (number, date, source, status), the operator's text, the record's summary and its
  revision history, the findings then and now, the explanation, the log lines. The download, *Copy as
  Markdown* and the CLI verb all use it.
- **`.\run.ps1 bugs export [--all]`** — a Host verb beside `user set-password`: reads the reports
  (open by default) and writes `artifacts/bug-reports/<yyyy-MM-dd-HHmm>.md` (git-ignored), printing the
  path. Read-only; works with the app stopped; waits for nothing but the database. Closing a report
  stays the operator's, in the dashboard.
- **Skill `.claude/skills/bugs/SKILL.md`** — the operator types `/bugs`; Claude runs the verb and
  triages each report: reproduce with a synthetic failing test, then fix through the normal process
  (TDD, a PR), record it in `docs/backlog/`, or answer "this is data, not a bug". It states that:
  invoking it is the explicit request to **read** `noof_ledger` through the verb — nothing else, no
  write; an amount, merchant or date from a report never enters a fixture, commit, PR or issue — the
  reproduction is rewritten synthetic; the export file stays under `artifacts/`.

## 4. Delivery

**Branch (IR-9).** `phase-8a` from `phase-7`; draft PR `phase-8a → phase-7`, retargeted to `master`
when Phase 7 merges, out of draft only once 8a is finalised. This spec and its amendments are
committed straight to `phase-8a`; every PR below targets it.

The plan fixes the cut; provisionally:

| # | PR | Assemblies |
|---|---|---|
| 1 | Finding contracts, the four checks, `IntegrityHealthCheck`, the write-path test moved onto I-1, the boundary rule | Application · Persistence · Architecture |
| 2 | `IFindingExplainer`, `ChatFindingExplainer` and `write_explanation` | Application · Ai |
| 3 | `bug_reports`: migration, store, snapshots, `IBugReportMarkdown` | Application · Persistence |
| 4 | `/diagnostics/integrity` with Explain → Create; demo data; screenshots | Web · Demo |
| 5 | `/bug`, `BugReportExplanationWorker`, *Close report*; bot scenes | Telegram · Host · Demo |
| 6 | `/bugs`, `/bugs/{number}`, Copy and Download; screenshots | Web · Demo |
| 7 | `.\run.ps1 bugs export` and the `/bugs` skill | Host · ops |
| 8 | Closing per `docs/CLOSING-A-PHASE.md`, the phase table updated for 8a/8b | docs |

**Reviews** (CLAUDE.md §1): Fable 5.1 with a parallel Codex adversarial review on this spec, on the
plan, and at the close; one Codex review per PR; opus at medium effort per task.

## Testing

TDD, a failing test first, each new guard seen red. No test touches `noof_ledger`, the network or a
live model.

- **Checks** (Persistence, template clones, filtered): per check, a seeded violation → exactly one
  finding, clean data → none; every I-2 condition seeded once; I-3 on a receipt awaiting confirmation →
  no finding; the SQL and C# awaiting-confirmation predicates agree; `FindForTransactionAsync` sees only
  its record; `IntegrityHealthCheck` levels and summaries.
- **Architecture:** the boundary rule, seen red against a check that names `IChatClient`.
- **Explainer** (Ai): the tool schema and `tool_choice` on the captured HTTP body; `looks_like_bug`
  read; a fiscal URL in the operator's text absent from the body.
- **Bot:** `/bug` from the owner and from a stranger, as a reply to an echo and not, `/bug@bot`;
  ordinary text still captured; the worker's success, failure after three attempts, the gate wait, a
  tick that throws; *Close report* from the owner only.
- **Markdown:** a rendered synthetic report against a literal expected text.
- **E2E (Playwright):** Explain and Create on a fake explainer; `/bugs` with Close/Reopen and the
  download; the Integrity row on `/diagnostics`.
- **CLI verb:** writes the file under `artifacts/bug-reports/` from a template clone.
- The full suite once, at the end of the phase.

## Acceptance

1. A seeded entry that disagrees with its line → exactly one I-1 finding; the health tile is red and
   `/health` says `1 bug found`. Removing it → no finding, the tile green.
2. A `Failed` exchange waiting for its received amount for over a day → one I-4 finding, the tile amber.
3. *Explain* on a finding shows the model's answer; with `looks_like_bug`, *Create* files a report that
   `/bugs` lists.
4. `/bug сумма не та` as a reply to an echo → `Bug report #N saved.`, then a reply with the
   explanation and the record's findings; the report on `/bugs/{N}` links the record and holds its log
   lines.
5. `.\run.ps1 bugs export` writes a Markdown file holding that report; *Download open reports* gives
   the same text.

## Out of scope

Spending analysis (Phase 8b); free-form questions about spending; duplicate-looking transactions
(`docs/backlog/duplicate-looking-transactions.md`); storing findings or their history; dismissing a
finding; proactive alerts in Telegram; posting reports anywhere outside the machine (the repository is
public); voice `/bug`.
