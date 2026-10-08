---
id: P8a-1
title: "Phase 8a integrity checks, explanations and bug reports decisions (2026-10-02)"
status: decided
date: 2026-10-02
phase: Phase 8a
related: [p7-1-transfers-and-exchange-decisions, p5-1-observability-decisions, duplicate-looking-transactions]
---

Full design in `docs/specs/2026-10-02-integrity-and-bug-reports-design.md`; the rules that bind future
work live in `CLAUDE.md` (the `/bugs` skill as the one way an agent reads `noof_ledger`; a new enum's `0`
is `Unknown`) and `.claude/rules/` (`model.md`: the model explains and never decides; `database.md`: the
integrity checks are the write path's independent oracle). The operator's twelve decisions (IR-1..IR-12),
the last three after the spec reviews:

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

## Departures from the original design

The original design (`docs/specs/2026-09-19-noof-finance-design.md`, §10 and §12) described integrity and
the explainer as Tier 2 and Tier 3 of self-diagnostics:

- **"The LLM never returns a branch of control flow" is softened for one offer** (IR-6): the explanation
  says whether the cause looks like a bug in the app, which only shows or hides *Create bug report* in the
  dashboard and the *Close report* button in the bot. Nothing it says changes a finding, a status or a
  health level.
- **§12's acceptance "a fabricated number in a mocked response is rejected, not displayed" is dropped**
  (IR-5). Every figure the model is given is still computed and formatted by C#.
- **Tier 2's candidate list is cut to four checks**: transfer legs netting (cross-currency legs cannot
  net), an uncategorized expense line (the write path cannot produce one), a missing FX snapshot (none
  exists since Phase 7), balance against reconciliation (a checkpoint absorbing a back-dated record is
  rule M6 working), a duplicate-looking transaction (`docs/backlog/duplicate-looking-transactions.md`), and
  lines not summing to a receipt's total (accepted through "Record anyway") are not checks.
- **The explainer answers in English**, not Russian (IR-4; the bot's own text is English, `bot-text.md`).
- **The boundary is a source-scanning test, not an ArchUnitNET rule**:
  `HealthCheckBoundaryTests.No_health_check_reaches_a_model` covers health checks and integrity checks alike
  and forbids `IFindingExplainer` with the model stack.

## Planning amendments (2026-10-02)

Taken while the implementation plan was written against the code, and folded into the spec the same day;
copied here verbatim from the spec's own table.

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

P-1's twelve PRs did not survive: the operator moved to fewer PRs per phase, cut by stage (2026-10-02,
`CLAUDE.md` §5), and the phase shipped as four stage PRs into `phase-8a` — #61 (A: the contracts, the four
checks and the Integrity health check; planned 1a, 1b, 2), #62 (B: the explainer, the Markdown and the report
store; 3, 4a, 4b), #63 (C: `/bug`, *Close report* and the explanation worker; 6a, 6b) and #64 (D: the
integrity page, the bug report pages and the export verb; 5, 7, 8). The closing review's fixes and the closing
documentation were committed straight to the branch.

## Review amendments (2026-10-03)

The operator's decisions from the review of the stage PRs, folded into the spec the same day; copied here
verbatim from the spec's own table. R-2 is the phase's second migration, `BugReportsReplyTo`, since
`AddBugReports` was already applied to the shared test template.

| # | Amendment | Supersedes |
|---|---|---|
| R-1 | **An enum's `0` is `Unknown`.** Every enum this phase adds — `IntegrityCheck`, `IntegrityGroup`, `BugReportSource`, `BugReportStatus`, `BugExplanationState` — gives `0` to `Unknown`, never to a real value, so a value nobody set never reads as a real one. The values stored in `bug_reports` shift accordingly, and so do the check constraints that name them. Enums stored before Phase 8a keep their values (`docs/backlog/zero-value-for-existing-enums.md`). A rule in CLAUDE.md §3 carries it forward. | §1 *Shape*'s `IntegrityCheck` and `IntegrityGroup` value lists; §3's `bug_reports` rows `source` (`Telegram = 0`, `Dashboard = 1`), `status` (`Open = 0`, `Closed = 1`) and `explanation_state` (`Pending = 0`, `Done = 1`, `Failed = 2`); the values P-9's check constraints name. |
| R-2 | **The report store does not know where a report came from.** One `FileAsync(NewBugReport)` replaces the Telegram and dashboard filing methods. `NewBugReport` carries the source, an opaque reply address (`ReplyTo`) that only the source's own layer writes and reads, the text, the record, and — for a dashboard report — the finding and its explanation. Filing is idempotent per (source, reply address). Delivery is likewise source-agnostic: a delivery carries the source and the reply address, and the stored reply reference is opaque. In `bug_reports`, `telegram_chat_id` and `telegram_message_id` give way to one `reply_to text` column with a unique index on (source, `reply_to`) where it is set, and the reply message id becomes an opaque reference. This lands as a second migration of the phase, because `AddBugReports` is already applied to the shared test template and an applied migration is never edited (`.claude/rules/database.md`). A new reporting source adds an enum value and its own layer, no schema change. | §3's `bug_reports` rows `telegram_chat_id`/`telegram_message_id` and `reply_message_id integer null`, and its "`(telegram_chat_id, telegram_message_id)` is unique where set" and "One migration in this phase, this table"; P-9's check constraints tying the Telegram ids to the source; §3 *`/bug` in Telegram*: the "message ids" the handler saves, the redelivery lookup by `(telegram_chat_id, telegram_message_id)`, and *Delivery*'s "no `reply_message_id`"; §4 row 4's store operations. |

## Changed by the closing review (2026-10-08)

Fable 5.1 and Codex (adversarial) reviewed `phase-8a` against `master` at the close (`docs/REVIEWS.md`, trial
tally). One finding changed the phase's design: a transfer's `Fee` line in a currency other than its fee leg's
wallet passed every check — I-1 derives the fee leg's principal and its fee entry from the same line, so they
agreed — and is now an I-2 condition, listed in the spec's I-2 beside P-23, which already called it an I-2 fault
(Codex; Fable's F2 touched it). The other fixes changed behaviour, not the design, each committed straight to the
branch with its test seen red first: a bug-report reply the chat accepted but whose reference failed to store was
sent again on every tick and held back later replies — the worker now remembers the sent reference and retries
only the write, each delivery is its own failure boundary, and a reply address the chat cannot read warns once
(EventId 1910) and is not retried (Codex, with Fable's malformed-address minor); a `null` element in a stored
JSON column reads as not collected instead of failing the report page and the export; the `/bug` matcher matches
the command alone, with a match timeout, because it runs over any sender's text before the owner gate; Explain
after Create keeps the created report, so a second Explain can no longer file a second report; `download.js`
revokes the object URL after the click is handled; the `/bugs` skill reads the verb's last line. Deferred: the
integrity checks' cost on a large ledger (`docs/backlog/integrity-checks-at-scale.md`), and a finding's Trace link
opening a trace page that cannot render a transfer whose fee is in another currency than its leg
(`docs/backlog/trace-page-fee-in-another-currency.md`). Replied and left as they are: fiscal links in non-canonical
forms (already `docs/backlog/fiscal-link-redaction-forms.md`, the operator's choice), nested log properties (no
event logs a destructured object), `/bug` staying silent while the model key is missing (P-21 as designed;
`/diagnostics` shows the AI keys), and no browser test for a stranger's `/bug` getting silence.

## Decisions made during implementation, worth knowing before revisiting this code

- **One run, one snapshot** (#61). `EfIntegrityChecks` runs every check inside one `REPEATABLE READ`
  transaction, begun when none is open and joined when the caller has one, so a "Record anyway" or a Cancel
  committed mid-run cannot turn a held receipt into a false Bug.
- **The awaiting-confirmation predicate stays in C#** (#61). I-3 and I-4 narrow candidates in SQL by status and
  ask `IReceiptStore.IsAwaitingConfirmationAsync` record by record, checking the token before each call, rather
  than keeping a second copy of the predicate in SQL; the cost is in `docs/backlog/integrity-checks-at-scale.md`.
- **Every sink strips its own input** (#62). The explainer, the report store and the Markdown each call
  `FiscalVerificationUrl.StripUrl`, field by field and line by line, whatever the others did; stripping twice
  changes nothing. Free text is stripped before it is cut, so a cut never leaves half a link.
- **A stored report never fails to read** (#62, and the closing review). A JSON column that cannot be read back —
  one holding a `null` element included — reads as not collected, and a closed report without its closing time
  prints `Closed`, so one damaged row never fails the export.
- **A failed filing detaches only its own row** (#62). The scoped `LedgerDbContext` is shared — a dashboard circuit
  keeps its own — so a row left `Added` would be inserted by the next `SaveChanges`; a redelivered `/bug` racing
  the unique index (23505) is resolved the same way and reads back the winner's number.
- **Only an exception from the explainer spends an attempt** (#63). A database fault is a failed tick (1901) with
  the report left `Pending`; an account refusal pauses explaining for five minutes (P-21). Explaining and delivery
  are separate failure boundaries, and an answer whose write failed is kept in memory and written on the next tick
  without asking the model again.
- **A delivered reply is not sent again by the worker** (the closing review, replacing #63's accepted resend).
  The sent reference is kept per report until it is stored, and a delivered report leaves the in-memory "already
  warned" set. A crash between the send and the write can still send it twice.
- **A stranger's `/bug` or *Close report* press never claims an unowned bot** (#63): both are matched before the
  owner gate and get no answer. Editing an old message into `/bug` goes through the ordinary edit path and may
  claim it, as any stranger's message already does.
- **One gate per page** (#64). `OneAtATime` serialises a page's load and its actions over the circuit's one
  `LedgerDbContext`, and swallows a failure that arrives after the page is gone, so an abandoned action cannot end
  the circuit.
- **The export verb builds the Release output `.\run.ps1 start` runs from** (#64), so it is run before changing
  code in a checkout whose host is running; it clears the host's logging providers so its one line on stdout is
  not interleaved with EF Core's.
- **Deferred to the backlog at the close:** classifying job errors so an unexpected exception stays red
  (`docs/backlog/classifying-job-errors-to-keep-bugs-red.md`, IR-10), free-form questions about spending
  (`docs/backlog/free-form-questions-about-spending.md`, IR-1), proactive integrity alerts
  (`docs/backlog/proactive-integrity-alerts-in-telegram.md`), bot commands for a bot claimed after start-up
  (`docs/backlog/bot-commands-for-a-bot-claimed-later.md`, P-16), voice `/bug`
  (`docs/backlog/voice-bug-reports.md`), the checks at scale (`docs/backlog/integrity-checks-at-scale.md`), and
  the trace page for a fee in another currency (`docs/backlog/trace-page-fee-in-another-currency.md`). From the
  operator's review of the stage PRs (2026-10-03), in `docs/backlog/`: `provider-specific-code-in-its-own-project.md`,
  `explicit-access-modifiers.md`, `background-services-dashboard.md`, `zero-value-for-existing-enums.md` and
  `fiscal-link-redaction-forms.md`.
- **Deliberately not done, and not backlog:** storing findings or their history (IR-3 — a finding
  disappears when the data or the code is fixed), dismissing a finding (Cancel clears a Waiting-on-you one),
  posting reports anywhere outside the machine (the repository is public).
