# Phase 8a — Integrity checks, explanations and bug reports (design)

**Status:** designed with the operator in conversation 2026-10-02 and approved ("начинай"); amended the
same day after the spec reviews (Fable 5.1 and a Codex adversarial review, run in parallel) with the
operator's answers to the decisions they raised (IR-10 – IR-12); amended again at implementation planning
(same day), with the plan reviews (Fable 5.1 and a Codex adversarial review, run in parallel) folded in —
see *Planning amendments* below; where it and an earlier paragraph differ, the amendment wins.

This is the first half of Phase 8 ("Integrity + explainer") in the phase table of
`2026-09-22-natural-language-capture.md`, which the operator split in two: **8a** finds what is wrong
(this spec) and **8b** analyses spending — a period summary in the dashboard and in the bot — with its
own spec. Letters rather than a renumbering, as with 0b and 1A–1D, so the existing references to later
phases (Phase 11's prompt tuning) stay true. It amends the original design's §10 (Tier 2 and Tier 3)
where the decisions below say so.

## Goal

The app finds its own inconsistencies: a handful of SQL checks, run on demand, each row a finding.
A finding that means our code wrote contradictory data turns the health tile red; one that waits on
the operator — something that never reached the ledger or never applied — turns it amber. A model
explains a finding on request, and when it judges the cause to be the app rather than the data it
offers to file a bug report. The operator files a bug report from Telegram with `/bug`, the bot
answers with an explanation, and every report reaches Claude Code for triage as Markdown — from the
dashboard, a CLI verb, or a skill.

## Decisions (operator's, 2026-10-02)

| # | Decision |
|---|---|
| IR-1 | **Phase 8 splits** into 8a (finding bugs) and 8b (spending analysis). Free-form questions about spending (*"сколько я потратил на кафе в сентябре?"*) are later than 8b; 8b is a ready-made summary. |
| IR-2 | **Two groups of findings, kept apart:** *Bugs* (our code made the data inconsistent) and *Waiting on you* (the operator has to act). The checks are the four of §1 — chosen by the agent at the operator's request ("сам думай"), approved as listed, regrouped by IR-10. |
| IR-3 | **Checks run on demand**, nothing stored: opening the page or a health run executes them; a finding disappears when the data or the code is fixed. |
| IR-4 | **The model explains, in English**, on request in the dashboard and in reply to `/bug` in the bot. |
| IR-5 | **No guarantee on the explanation's figures** — *"гарантии тут не сильно важны, это не ключевой функционал"*. The original §12 acceptance ("a fabricated number in a mocked response is rejected, not displayed") is dropped. Every figure the model is given is still computed and formatted by C#. |
| IR-6 | **The model may offer a bug report** (*"просто появляется диалог «хотите создать баг»"*): its answer says whether the cause looks like a bug in the app, which only shows or hides a *Create bug report* offer; the operator decides. A deliberate softening of §10's "the LLM never returns a branch of control flow" — nothing the model says changes a finding, a status or a health level. |
| IR-7 | **`/bug` in Telegram saves a report and answers with an explanation**; in the dashboard a report holds the operator's text, the record, the findings and the log lines as they were when it was filed. |
| IR-8 | **Reports reach triage three ways**, all in this phase: *Download open reports* on `/bugs`, `.\run.ps1 bugs export`, and a Claude Code skill `/bugs` that runs the verb and triages. |
| IR-9 | **Branching:** an aggregate branch `phase-8a`, cut from `phase-7` while Phase 7 is unmerged (its checks read Phase 7's tables), its draft PR stacked on `phase-7` and retargeted to `master` once Phase 7 merges. |
| IR-10 | **A job that failed is Waiting on you, not a Bug** (spec review; the operator left the choice to the agent): its causes are mostly outages, a missing key and unreadable input, and the bot has already said so. Bugs keep "Captured with no job", a real write-path hole. Classifying job errors to keep unexpected exceptions red would need a migration of the queue — not now. |
| IR-11 | **Phase 7's A-26 is amended**, wording only (spec review; the operator left the choice to the agent): a record that is neither `Failed` nor `Cancelled` holds `None`. Cancel has always kept the reason, so the `Cancelled` echo and Restore can show it. |
| IR-12 | **The `/bugs` skill sends reports — the operator's own financial data — to Claude Code's model**; invoking it is that choice. Accepted by the operator, as categorisation already sends the same data to the model. |

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

## Planning amendments (2026-10-02)

Taken while the implementation plan was written against the code, and from the plan reviews. The plan itself
is scratch in the git-ignored `.superpowers/`; these are the decisions it changed. The paragraph an amendment
changes is marked with its number.

| # | Amendment | Why |
|---|---|---|
| P-1 | The PR cut is 12 PRs, replacing §4's provisional nine: 1 → 1a (every Application contract of the phase) + 1b (I-1, I-2); 4 → 4a (the Markdown) + 4b (the table and store); 6 → 6a (`/bug`, Close report) + 6b (the worker); 7 follows 5. A PR may exceed the ~500-line guide. | PR 1 was ~1 300 lines and PR 6 spanned two leaf assemblies; 7 shares the E2E class and the demo writer with 5. |
| P-2 | One check per record: the checks run I-2 → I-1 → I-3 → I-4 and a record keeps only the earliest check's finding. I-1, I-3 and I-4 give at most one finding per record (I-1 lists every discrepancy as facts); I-2 one per record and condition. | The moved queries overlap — a moved entry is two `disagreeing` rows and a `misplaced` one; a charge on an income is both I-2 and I-1. A record shows its shape fault first, then any I-1 disagreement once that is fixed. |
| P-3 | I-1's queries are reshaped, not re-ruled: rows per record, wallet, currency and role, a transfer's fee as a per-record sum of its `Fee` lines. The write-path test calls I-1 **and I-2** and expects no finding from either. | The shape clauses of `brokenTransfers` moved to I-2 and must stay in the oracle; a `LEFT JOIN` on fee lines multiplied rows. |
| P-4 | A record's clock is its last activity — the latest of its `created_at`, its jobs' `updated_at` and its revisions' `created_at`. I-3's 10 minutes and I-4's 24 hours are measured from it; a clock ahead of now is never a finding and reads as age 0. | `transactions` has no `updated_at`; a Restore, a reply or a failed retry restarts the wait, so a record just touched is never flagged at once. |
| P-5 | A record ever cancelled is never I-3. `Captured` with a `Cancel` revision, no `Pending`, `Claimed` or `Failed` job, not awaiting confirmation and idle over a day, it is I-4's fourth case: restored, but nothing will process it — Cancel or a correction reply answers it. | Restore queues no work, so the app's own cancels (a duplicate receipt, a non-money receipt kind) followed by the operator's Restore would turn the tile red for no fault of the code. |
| P-6 | I-4's failed job: "later" is by `created_at`; the record has no `Pending` or `Claimed` job; the finding names the latest such job, its last error's first line cut to 500 characters. "The amount the record holds": its transfer's `From`, else its receipt's total above zero, else its `Principal` lines summed per currency, else none. | The spec left these open. |
| P-7 | A fifth fact kind, `SinceFact`, holds the instant a wait started; C# formats the age as of now on the dashboard and as of the snapshot in a report. `DateFact` holds a date. | None of the four kinds carries an age that keeps its meaning in a stored snapshot. |
| P-8 | The Integrity check reads `1 bug found` / `N bugs found`, `1 waiting on you` / `N waiting on you`, `No findings`; while the database is not ready it answers `Waiting for the database` (a Warning) without a query. Order: after Receipts. | As the Migrations check does: a health run never touches a database that is not ready. |
| P-9 | `bug_reports.number` is `GENERATED ALWAYS AS IDENTITY`; `transaction_id` is a `RESTRICT` foreign key; check constraints tie the Telegram ids to the source, `closed_at` to the status and `Done` to an explanation. A redelivered `/bug` is looked up before inserting; the unique index is the race backstop. | A report is evidence, like a revision; a redelivery burns no number. |
| P-10 | The record's summary is composed by Persistence and also holds the record's raw text and its revisions, URL-stripped — wallet and category names included. | The trace page's summary lacks the failure reason and is unstripped; the explainer is given the raw text and the revisions. |
| P-11 | No lease on a report: the worker reads the oldest due one; one host runs one worker, and a second process explaining a report twice is accepted. Replaces §4's "the worker's claim". | The table has no claim columns; a crash mid-explain leaves the report `Pending`. |
| P-12 | The reply's wording where the spec left it open: an unlinked report says `1 finding in the ledger` / `N findings in the ledger`; a snapshot whose findings could not be collected says `Integrity findings could not be collected`; the explanation is cut to 3 500 characters; a `Failed` explanation's reply has no button. | Telegram's 4 096-character limit would otherwise fail the send forever. |
| P-13 | Every reply to a `/bug` — `Bug report #N saved.` and the explanation — is sent even when that message was deleted. A failed send is retried every tick, with a Warning once per report and Debug after. | A deleted `/bug` would otherwise block delivery forever; an unreachable chat must not write a Warning every 15 s. |
| P-14 | Close report confirms itself: the owner's press is answered, closes the report and edits the reply to add `Bug report #N closed.` without the button. A stranger's press gets no answer at all. | Without feedback the operator cannot tell a press worked. |
| P-15 | `/bug` has its own matcher: `/bug`, `/bug <text>`, `/bug@name`, `/bug@name <text>`, any case, the text over several lines. `/bugfix 500` and `/bugs` are captured as spending; a photo captioned `/bug` stays a receipt. | `/health`'s matcher is exact; photos are dispatched before text. |
| P-16 | `setMyCommands` registers `health` and `bug` only when the token is new and an owner exists, as for `/health` today: a bot claimed later gets its menu at the next restart. | Unchanged behaviour; deferred to the backlog at the close. |
| P-17 | The CLI verb is `bugs export [--all] [--output <directory>]`. Any arguments starting with `bugs` are the verb — a bad one prints its usage and exits 2, never starting the web host. With nothing to export it says so, writes no file and exits 0. Its failure lines use an ASCII ` - `. | `.\run.ps1 bugs export` passes its own output folder; an em dash prints as `?` in a Windows console's code page. |
| P-18 | Explain → Create is not driven by E2E: the page's flow is a Web class tested from Host.Tests with a fake explainer and store. E2E covers the page with a seeded finding, Explain with no model key ("Couldn't explain this right now"), `/bugs` with Close/Reopen and the download, and the Integrity row. | The published E2E host cannot take a fake explainer, and no test-only configuration key may exist. |
| P-19 | The demo seeds no Bug: one Waiting-on-you finding — a failed exchange waiting for its amount, dated by the real clock — and the records that could wait on the operator are touched at seeding, so the demo tile is amber (`Integrity — 1 waiting on you`) whatever the day. Replaces §4's "its violation seeded by raw SQL". | A permanently red tile in the main screenshot is wrong (the operator left the choice to the agent). The Bugs section is shown by tests. |
| P-20 | The skill carries `disable-model-invocation: true`, so only the operator's `/bugs` starts it; `CLAUDE.md`'s `noof_ledger` rule names it as the explicit request to read the ledger through the verb. | The model could otherwise start the skill, and read the real ledger, on its own. |
| P-21 | A refused account (401/402/403 — a revoked key, no credit) spends no attempt: the worker pauses explaining for five minutes, and the report stays `Pending`. | Plan review: every categorisation worker already does this; three 401s would otherwise end each report `Failed`, though the key may be fixed an hour later. |
| P-22 | A report's log lines hold no Telegram id: a `…ChatId`, `…MessageId` or `…FileId` property, and its value wherever it appears in the message or exception, read `[telegram id]`. | Plan review: existing events log `to bot message {BotMessageId}` and `from chat {ChatId}`, and `SecretRedactor` leaves numbers alone. |
| P-23 | A report reads its revisions straight from `transaction_revisions` — time, kind, and the instruction or the status change — not through the trace page's reader; no snapshot names are shown. Replaces §3's "their names resolved to today's". | Plan review: that reader throws on a transfer whose fee is in another currency than its leg — an I-2 fault, the very record a report is about — and would make that report and the whole export unreadable. |

## 1. Checks

Each check is one SQL query; each row it returns is one finding. A defect is a finding of one check
only (P-2).

### Bugs — any finding means our code wrote contradictory data (`Failing`)

**I-1 · Postings disagree with their sources.** A transaction's `entries`, per wallet, currency and
role, differ from those derived independently from its `line_items`, `charges` and `transfers`
(`disagreeing` in `Entries_always_agree_with_the_lines_they_are_summed_from`); an entry sits on a
wallet that is neither the record's nor its transfer's destination (`misplaced`); or a charge is
mispriced — its `charged_amount` differs from its `Principal` lines in its currency at `rate_used`, or
an expense's charge fees differ from its `Fee` lines in the wallet's currency (the pricing clauses of
`mispricedCharges`). Facts: the wallet, currency and role; expected and actual amounts. The shape
clauses of `brokenTransfers` and `mispricedCharges` belong to I-2. The queries move into production
code; the write-path test then calls I-1 and expects no finding, keeping its guards that entries,
transfers and charges exist — so its oracle stays the one production code uses, and stays independent
of `LedgerPostings`, which writes the entries (P-3).

**I-2 · A record's facts do not match its kind.** Any of:
- a `Transfer` without a `transfers` row, or a `transfers` row on a record of another kind;
- a `Transfer` with a `Principal` line;
- a `BalanceCheck` without a `balance_checks` row, or one on a record of another kind;
- a `charges` row on a record that is not an `Expense`, or in its wallet's own currency; an expense's
  `Fee` line in a currency other than its wallet's;
- `fee_leg` set without exactly one `Fee` line on the record, or a `Fee` line on a transfer whose
  `fee_leg` is null;
- a transfer leg whose currency differs from its wallet's;
- `balance_checks.wallet_id` or `transfers.from_wallet_id` differing from `transactions.wallet_id`;
- `failure_reason` other than `None` on a record that is `Captured` or `Completed` (IR-11).

Facts: which condition, and the values that disagree; one finding per record and condition.

**I-3 · Stuck in the pipeline.** A record `Captured` for more than 10 minutes with no `Pending`,
`Claimed` or `Failed` job, unless it is a receipt or slip awaiting confirmation. Facts: the status, the
age, and the kinds of its jobs (P-4, P-5).

**Awaiting confirmation** is `IsAwaitingConfirmationAsync`, never a SQL copy: a `Vision` receipt (a
fiscal QR receipt never waits) with no `CategorizeReceipt` job, or a `Vision` exchange slip with no
`RecordExchange` job, no `Initial`, `Correction` or `Edit` revision, reason `None`, and stored evidence
that assesses as `Hold` (Phase 7's A-7 and A-8). A check's SQL selects candidates — a `Vision` receipt
or slip without that job — and keeps those the C# predicate answers true for. The predicate ignores
status; I-3 and I-4 add their own.

### Waiting on you — the operator has to act (`Warning`)

**I-4 · Not applied.** Older than 24 hours, with no `Pending` or `Claimed` job:
- a `Failed` record — with a reason, the bot asked for something (the received amount, a wallet, a
  slip's figure) and no reply came; with `None`, its first reading failed (an unreadable photo, an
  outage, a missing key);
- a `Captured` receipt or slip awaiting confirmation ("Record anyway" not pressed) — Cancel is the
  operator's answer and clears the finding; Restore brings it back;
- a `Failed` job — a correction, re-read or transcription that never applied — on a record neither
  `Failed` nor `Cancelled`, with no later `Succeeded` job on that record.

Cancel clears every I-4 finding on its record. Facts: the reason, or the job kind and its last error's
first line; the age; the date and the amount the record holds, when it holds one (P-4, P-5, P-6).

### Shape

`Noof.Ledger.Application` (`Diagnostics/Integrity/`), contracts only:

- `IntegrityCheck` (enum: `PostingsDisagree`, `FactsMismatchKind`, `StuckInPipeline`, `NotApplied`)
  and `IntegrityGroup` (`Bug`, `WaitingOnYou`).
- `IntegrityFinding` — `Check`, `Group`, optional `TransactionId`, `WalletId`, `JobId`, and `Facts`.
- `IntegrityFact` — a named, typed fact: `MoneyFact(decimal, CurrencyCode)`, `DateFact`,
  `TextFact`, `CountFact` (P-7). No `double`, no pre-formatted amount; C# formats at display.
- `IIntegrityChecks` — `FindAllAsync(ct)` and `FindForTransactionAsync(transactionId, ct)` (the same
  queries filtered to one record, for `/bug`).

`Noof.Ledger.Persistence`: one internal class per check, sharing an internal interface (`Check`,
`Group`, `FindAsync(scope, ct)`), its SQL beside it as `BalanceSql` keeps its own; `EfIntegrityChecks`
runs them one at a time. "Now" comes from `TimeProvider`.

**Health.** `IntegrityHealthCheck : ISystemHealthCheck` (Persistence, registered by
`AddNoofPersistence`) runs every check: any Bug → `Failing`, "N bugs found"; only Waiting-on-you →
`Warning`, "N waiting on you"; none → `Ok`. The Home tile, `/diagnostics` and the bot's `/health` get
it unchanged, with the existing 30 s cache and 5 s timeout; a timeout or an exception is reported the
way `SystemHealth` reports any check's (P-8).

**Boundary.** `HealthCheckBoundaryTests.No_health_check_reaches_a_model` already fails when an
`ISystemHealthCheck` names `IChatClient` or the model stack; it is extended to the files implementing
the integrity checks (a second declaration pattern) and to `IFindingExplainer`. Persistence cannot
reference Ai anyway (`ProjectReferenceTests`); the rule is what still holds if a check ever moves.

## 2. Explanations

`IFindingExplainer` (Application): `ExplainAsync(ExplanationRequest, ct)` →
`Explanation(string Text, bool LooksLikeBug)`, or a failure the caller renders as "Couldn't explain
this right now" (logged as a Warning, without the model's text).

`ExplanationRequest` carries, as text C# composed:
- each finding with its check's fixed English description (what it catches and what it usually
  means) and its facts, formatted by C#;
- for `/bug`: the operator's text and the record's summary (kind, date, wallet or both legs, lines,
  amounts, status, failure reason) (P-10).

A `/bug` without a record sends at most the first 20 findings, in check order, and says how many there
were in total.

`ChatFindingExplainer` (`Noof.Ledger.Ai`) answers through the forced tool `write_explanation`,
`strict: true`, one round, `ChatToolMode.RequireSpecific` — as `read_receipt` does. Arguments:
`{ text, looks_like_bug }`. The system instructions ask for a short English answer (what is wrong, the
likely cause, what to do — e.g. "reply to the echo with the amount", or "this is a bug — file it"),
and `looks_like_bug = true` only when the cause is the app's code rather than the operator's data or
a misread phrase. Same `IChatClientFactory`, same single model.

Every free-text field that leaves C# — the operator's `/bug` text, a record's `raw_text`, its
revisions' raw text and instructions, line descriptions, a job's last error, log messages — passes
through `FiscalVerificationUrl.StripUrl` before it reaches the explainer, a `bug_reports` column or the
Markdown (CLAUDE.md §4 Secrets). A job's last error enters as its first line only. The prompt holds
what categorisation already sends — amounts, dates, categories, merchant and wallet names, the
record's text — plus the check's description, the operator's words and that first line; never a
Telegram id or a file id.

The model is called only when the operator presses *Explain* or sends `/bug`.

## 3. Bug reports

### `bug_reports`

| Column | |
|---|---|
| `id uuid` PK | |
| `number integer` | identity, shown as `#12` |
| `created_at timestamptz` | |
| `source integer` | `Telegram = 0`, `Dashboard = 1` |
| `text text null` | the operator's words, URL-stripped |
| `transaction_id uuid null` FK | the record the report is about |
| `telegram_chat_id bigint null`, `telegram_message_id integer null` | the `/bug` message, to reply to |
| `status integer` | `Open = 0`, `Closed = 1`; `closed_at timestamptz null` |
| `snapshot_at timestamptz null` | when findings, log lines and the summary were collected; null until then |
| `record_summary text null` | the record's summary as C# composed it for the explainer, URL-stripped |
| `findings jsonb null` | the findings when the snapshot was taken |
| `log_lines jsonb null` | log rows when the snapshot was taken — already redacted by `SecretRedactor` |
| `collection_failures text null` | what could not be collected, e.g. `findings: check failed (TimeoutException)`; null when complete |
| `explanation_state integer` | `Pending = 0`, `Done = 1`, `Failed = 2`; `explanation_attempts integer`, `explanation_next_at timestamptz` |
| `explanation text null`, `looks_like_bug boolean null` | |
| `reply_message_id integer null` | the bot's reply carrying the explanation; null until delivered |

`(telegram_chat_id, telegram_message_id)` is unique where set (P-9).

Findings disappear once fixed and log rows by their level's retention (configurable; Debug defaults to
one day, and database logging may be Off), hence the snapshots; a missing log line is not evidence that
nothing happened. `log_lines` holds the linked record's rows, newest first, at most 200, each message
and property cut to 2 000 characters, with the number omitted; an unlinked report holds the
Warning-and-above rows of the hour before it was filed, under the same limits (P-22). Snapshots stay with the
report, open or closed. Revisions are never deleted and are read live, their names resolved to
today's (P-23). One migration in this phase, this table.

### `/bug` in Telegram

- Owner only: recognised beside `/health`, before the owner gate's claim and before the reply branch (a
  reply would otherwise become a `Correct` job), and authorised by `TelegramOwnerGate.IsOwnerAsync`,
  never `IsAllowedAsync` — `/bug` never claims an unowned bot; anyone else gets silence. Never creates a
  transaction; `/bug@<anything>` too; added to `setMyCommands` beside `/health` (P-15, P-16).
- **As a reply** to a record's echo or Edit prompt (`FindByBotMessageAsync`) or to the operator's
  message that captured it (`FindByUserMessageAsync`), the report is linked to that record.
  **Otherwise** it has no record. The text after `/bug` is optional.
- The handler only saves the report — text, record, message ids — and answers `Bug report #12 saved.`
  A redelivered `/bug` finds its report by `(telegram_chat_id, telegram_message_id)` and answers with
  the same number; it never files a second one.
- **`BugReportExplanationWorker`** (Host) takes open `Pending` reports whose `explanation_next_at` has
  passed, oldest first, only while `IModelProvider.IsConfiguredAsync` holds — an unconfigured model
  leaves them `Pending` without spending an attempt. On a report's first attempt it takes the snapshot
  — findings (`FindForTransactionAsync` for a linked report, otherwise `FindAllAsync`), log lines and
  the record summary — each part best-effort: a part that fails is named in `collection_failures` and
  the report goes on without it, never with an empty findings list in its place. It explains the
  snapshot, not today's data, and stores the answer (`Done`). A failed model call counts an attempt and
  waits `CategorizationWorkerOptions.ComputeBackoff`; three → `Failed` (P-11, P-21).
- **Delivery** is separate: a Telegram report that is `Done` or `Failed` with no `reply_message_id` is
  sent as a reply to the `/bug` message — the explanation and a line `2 findings on this record` /
  `No integrity findings`, or `Couldn't explain it — the report is saved.` When `looks_like_bug` is
  false it carries an inline **Close report** button. A failed send is retried on the next tick without
  calling the model and spends no attempt; a crash between sending and storing `reply_message_id` may
  send it twice, which is accepted. A report closed first is neither explained nor delivered. A reply
  with inline buttons is a new `IChatNotifier` method; `SendAsync` and `AskAsync` cannot do it (P-12, P-13).
- **Close report** is its own callback, `bug:close:<number>`, routed before `RecordActionButtons` and
  authorised by `IsOwnerAsync`; a malformed or repeated press does nothing (P-14).
- The worker awaits `IDatabaseGate.WaitUntilReadyAsync` and catches every non-cancellation exception
  per tick (CLAUDE.md §4).
- A report is its own queue rather than a `categorization_jobs` kind: that queue is keyed by a
  transaction, and a report may have none.

### In the dashboard

- **`/diagnostics/integrity`** — interactive server page: *Bugs* and *Waiting on you*, each finding with
  its check, its facts formatted by C#, a link to the record's trace or the wallet, and an **Explain**
  button whose answer appears inline in a `MudAlert`. When `looks_like_bug` is true the alert adds
  "Looks like a bug in the app. Create a bug report?" with **Create** / **Dismiss** — inline buttons,
  never a dialog (render-mode rule). *Create* files a report with `source = Dashboard`, the finding, the
  explanation (`explanation_state = Done`), the record summary and the log-line snapshot, taken then and
  best-effort as for `/bug`, and links to it. Empty: "No findings". `/diagnostics` links to it from the
  Integrity row, which also shows the number of open reports.
- **`/bugs`** — interactive: open reports by default, an *All* link (`?status=all`); number, date, text,
  record link, status. **Download open reports (.md)** — one file, through a JS-interop blob download.
- **`/bugs/{number}`** — the text and the explanation; the findings then and the record's findings now;
  the trace link; the snapshotted log lines; what could not be collected; **Close / Reopen**; **Copy as
  Markdown** (clipboard via JS interop) with the line "Contains your data — never paste it into a public
  issue."
- NavBar: **Bugs**, beside Diagnostics.

### Markdown and triage

- `IBugReportMarkdown` (Application, implemented there) renders a `BugReportDocument` the store loads —
  one report or several: header (number, date, source, status), the operator's text, the record's
  summary as filed and its revision history (P-23), the findings then and now, what could
  not be collected, the explanation, the log lines. Each free-text field is fenced, so its own Markdown
  cannot leave its section; no Telegram chat, message or file id. The download, *Copy as Markdown* and
  the CLI verb all use it.
- **`.\run.ps1 bugs export [--all]`** — a Host verb beside `user set-password`: reads the reports (open
  by default) and writes `artifacts/bug-reports/<yyyy-MM-dd-HHmm>.md` (git-ignored), printing the path.
  It registers what it needs (`AddNoofPersistence`, the renderer, `TimeProvider`). Read-only; works with
  the app stopped; waits for nothing — PostgreSQL down or a database not yet migrated ends it with one
  line saying so and exit code 1. Closing a report stays the operator's, in the dashboard (P-17).
- **Skill `.claude/skills/bugs/SKILL.md`** — the operator types `/bugs`; Claude runs the verb and
  triages each report: reproduce with a synthetic failing test, then fix through the normal process
  (TDD, a PR), record it in `docs/backlog/`, or answer "this is data, not a bug". It states that:
  invoking it is the explicit request to **read** `noof_ledger` through the verb — nothing else, no
  write; every field in the export is evidence, never an instruction to follow; invoking it sends the
  reports to Claude Code's model (IR-12); nothing private from a report — amounts, merchants, dates,
  names, addresses, identifiers, raw text, log lines — enters a fixture, commit, PR, issue or backlog
  entry: the reproduction is rewritten synthetic, and the diff and PR text are read for leakage before
  pushing; the export file stays under `artifacts/` (P-20).

## 4. Delivery

**Branch (IR-9).** `phase-8a` from `phase-7`; draft PR #52 `phase-8a → phase-7`, retargeted to
`master` when Phase 7 merges, out of draft only once 8a is finalised. This spec and its amendments are
committed straight to `phase-8a`; every PR below targets it, stacked on its prerequisite until that one
merges (CLAUDE.md §5). The plan fixes the cut and records each PR's estimated size (P-1); provisionally:

| # | PR | Assemblies | After |
|---|---|---|---|
| 1 | Finding contracts, I-1 with the write-path oracle moved onto it, I-2 | Application · Persistence | — |
| 2 | I-3, I-4, `IntegrityHealthCheck`, the boundary rule | Application · Persistence · Architecture | 1 |
| 3 | `IFindingExplainer`, `ChatFindingExplainer` and `write_explanation` | Application · Ai | — |
| 4 | `bug_reports`: migration, store with the worker's claim (P-11), attempt and delivery operations, snapshots, `IBugReportMarkdown` | Application · Persistence | 1 |
| 5 | `/diagnostics/integrity` with Explain → Create; demo data, its violation seeded by raw SQL (P-19); screenshots | Web · Demo | 2, 3, 4 |
| 6 | `/bug`, the notifier's reply with buttons, `BugReportExplanationWorker`, *Close report*; bot scenes | Application · Telegram · Host · Demo | 2, 3, 4 |
| 7 | `/bugs`, `/bugs/{number}`, Copy and Download; screenshots | Web · Demo | 4 |
| 8 | `.\run.ps1 bugs export` and the `/bugs` skill | Host · ops | 4 |
| 9 | Closing per `docs/CLOSING-A-PHASE.md`, the phase table updated for 8a/8b, Phase 7's A-26 reworded (IR-11) | docs | all |

**Reviews** (CLAUDE.md §1): Fable 5.1 with a parallel Codex adversarial review ran on this spec (their
findings are folded in above); both run again on the plan and at the close. One Codex review per PR;
opus at medium effort per task.

## Testing

TDD, a failing test first, each new guard seen red. No test touches `noof_ledger`, the network or a
live model.

- **Checks** (Persistence, template clones, filtered): per I-1 branch (`disagreeing` — a missing, an
  extra and a wrong-amount entry, a transfer fee on the wrong leg; `misplaced`; each pricing clause) and
  per I-2, I-3 and I-4 condition, a seeded violation → exactly one finding of that check, clean data →
  none; Failed → Cancel → Restore gives no Bug; a held receipt and a held slip → Cancel → Restore give
  an I-4 finding only while `Captured`; a failed exchange waiting for its amount → one I-4 finding and no
  Bug; a failed correction followed by a successful one → none; awaiting confirmation — a fiscal QR
  receipt with no job, a held vision receipt, a held, an incomplete and a ready slip, a slip cancelled
  before extraction, an applied slip, one with a `RecordExchange` job; `FindForTransactionAsync` sees
  only its record; `IntegrityHealthCheck` levels and summaries.
- **Architecture:** the extended boundary rule, seen red against a check that names `IChatClient`.
- **Explainer** (Ai): the tool schema and `tool_choice` on the captured HTTP body; `looks_like_bug`
  read; a fiscal URL in the operator's text, the record's raw text and a revision absent from the body.
- **Bot:** `/bug` from the owner, from a stranger and on an unowned bot (not claimed); as a reply to an
  echo, to an Edit prompt, to the captured message, and not; `/bug@bot`; a redelivered `/bug` → one
  report; ordinary text and replies still captured or corrected; the worker's snapshot with a failing
  part, success, failure after three attempts, a failed send retried without a model call, an
  unconfigured model, the gate wait, a tick that throws; *Close report* from the owner only, malformed
  and repeated.
- **Markdown** (Application, no database): a synthetic `BugReportDocument` against a literal expected
  text; fiscal URLs stripped, fields fenced, no Telegram ids.
- **E2E (Playwright):** Explain and Create on a fake explainer (P-18); `/bugs` with Close/Reopen and the
  download; the Integrity row on `/diagnostics`.
- **CLI verb:** writes the file under `artifacts/bug-reports/` from a template clone; PostgreSQL down and
  an unmigrated database → one line and exit code 1.
- The full suite once, at the end of the phase.

## Acceptance

1. A seeded entry that disagrees with its line → exactly one I-1 finding; the health tile is red and
   `/health` says `1 bug found`. Removing it → no finding, the tile green.
2. A `Failed` exchange waiting for its received amount for over a day → one I-4 finding, the tile amber,
   and no Bug.
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
finding (Cancel clears a Waiting-on-you one); classifying job errors to keep unexpected exceptions red
(IR-10); proactive alerts in Telegram; posting reports anywhere outside the machine (the repository is
public); voice `/bug`.
