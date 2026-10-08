# Phase 8b — Monthly spending summary (design)

**Status:** designed with the operator in conversation 2026-10-02 – 2026-10-08, section by section, and
approved with the agent's decisions A-1 – A-3; amended the same day after the spec reviews (Fable 5.1
and a Codex adversarial review, run in parallel) — the operator's SS-19, A-4 – A-9 and the changes they
brought are folded into the sections below.

This is the second half of Phase 8, split off by 8a's IR-1
(`2026-10-02-integrity-and-bug-reports-design.md`): **8b is a ready-made spending summary in the
dashboard and in the bot.** Free-form questions about spending come later. It also settles the rate
source deferred in `docs/decisions/q4-fx-rate-source.md` for this use, and adds the first conversion
between currencies the app does — for reporting only; nothing converted is stored, and no balance or
record changes.

## Goal

Once a month, and whenever asked, the operator sees where the money went, whether that is more or less
than usual, and how much it was in total — per wallet in the wallet's own currency, and across all
wallets in EUR or RSD. The dashboard has a `/summary` page; the bot answers `/summary` and sends the
finished month's summary on the 1st by itself. Home shows the net worth in one currency. A model
comments on a summary on request; every figure is computed by C#.

## Decisions (operator's)

| # | Decision |
|---|---|
| SS-1 | The summary answers three questions: **where the money went, more or less than usual, how much in total.** |
| SS-2 | **Rates come from an external mid-rate archive**: `open.er-api.com/v6/latest/EUR` (Q4's default), fetched daily into an append-only `fx_rates` table. **Each amount is converted at its own day's rate.** No back-fill: there is no real history to convert. |
| SS-3 | **The calendar month is the only period** in 8b. A summary over an arbitrary date range goes to the backlog — the operator was unsure such a range needs a comparison at all. |
| SS-4 | A month is compared with **the previous month and the average of the three before it**. |
| SS-5 | The current, unfinished month is compared **over the same day-of-month window**, clamped to each month's end; **a finished month is compared whole, with whole months.** |
| SS-6 | Telegram: **`/summary` on request, and an automatic summary of the finished month on the 1st.** |
| SS-7 | **The text is a C# template; a model comment is behind an *Explain* button**, in the dashboard and in the bot alike. Bot text stays English (`.claude/rules/bot-text.md`). |
| SS-8 | A **new page `/summary`**; Home's *This month* links to it. |
| SS-9 | Contents: **categories with their comparison, top merchants, largest records.** |
| SS-10 | **Spent, received and net** (net = received − spent). |
| SS-11 | The converted total is shown in **EUR or RSD only.** |
| SS-12 | **The reporting currency is a setting** (EUR or RSD, default EUR), changed on its own small settings page. It decides Home's net worth, the automatic summary and what a summary opens in; the page's toggle and the bot's buttons change only the view at hand. |
| SS-13 | **Home shows the net worth** — every balance converted to the reporting currency. |
| SS-14 | **Approach A:** SQL returns flat rows; a pure C# calculator converts and sums. Rejected: converting in a SQL view; storing an EUR equivalent per entry. |
| SS-15 | **First per wallet, in the wallet's own currency, then summed** (*"сначала все траты в одной валюте в одном кошельке и оно именно и считается, а уже после это суммируется вместе"*): a wallet's spending is what actually left it — a foreign purchase counts as the bank's charge, not the purchase currency. Rates are used to add wallets together. The dashboard shows each wallet and the total. |
| SS-16 | A wallet's summary shows the money **moved to and from other wallets on lines of their own**, apart from spent and received; the all-wallets view leaves movements out. |
| SS-17 | **What an exchange loses against mid-market is ignored** — only explicit fees count as spending. Tracking that spread goes to the backlog. |
| SS-18 | The Telegram summary is **the total plus one line per wallet**, no wallet buttons. |
| SS-19 | **A fiscal refund reduces spending** in its categories instead of counting as received (spec review). Today a `Refund` receipt becomes an `Income` record, so a returned purchase stayed in its category's spending and showed again under *Received*. Home's *This month* follows the same rule. Net is unchanged. |

### Decisions taken by the agent

A-1 – A-3 were approved by the operator with the spec; A-4 – A-9 come from the spec reviews.

| # | Decision | Why |
|---|---|---|
| A-1 | **Home's *This month* adopts SS-15's rule** too: a foreign purchase counts in its wallet's currency, as the charge, not in the purchase currency. This supersedes the Phase 7 spec's §4 sentence "A foreign spending counts in the currency it was bought in; its charge moves the wallet's balance, not the statistics", and the comment on `ISpendingReadModel` saying the same. | Otherwise Home's *This month* and `/summary` for the same month disagree whenever there is a foreign purchase. Today `EfSpendingReadModel` sums `line_items` by the line's own currency, so a 30 USD purchase on a KZT wallet shows as 30 USD plus a 156 KZT fee instead of 15 756 KZT. |
| A-2 | A record counts unless it is `Cancelled` — the rule *This month* already uses — not only once `Completed`, as the balance view does. | A record still being categorised has its amount; leaving it out would make a summary jump when categorisation finishes. The summary is not a balance. |
| A-3 | The automatic summary goes out **from 09:00** in the configured zone (`Capture:TimeZone`, the one *This month* uses) on or after the 1st, and only for **the last finished month**; a month with nothing counted is skipped silently. | Not at midnight; after the PC was off for days, one message, not a burst; no empty message the day 8b is installed. |
| A-4 | **A line in a currency other than its wallet's and with no charge** — a foreign purchase with no terms set, a fiscal receipt in a foreign currency, any foreign-currency income — **is converted to the wallet's currency at the day's mid rate and marked "≈"**. With no rate for it, it goes to a *Not converted* block. | It happens routinely (Phase 7: A-24, A-28; income is never charged), and a wallet's figures must stay in one currency for comparisons and rankings to mean anything. |
| A-5 | **The average's history starts at the scope's first month with anything counted**; a later month with nothing counted counts as zero; the first month counts as it is, even if it began mid-month. | A gap month is a real zero, not missing data; the start must come from the whole history, not the four months the rows cover. |
| A-6 | **Percentages only against a positive figure.** Spent, received and a category get a % when the comparison figure is above zero, "new" when it is zero and this month's is not; net gets the signed change, never a %. | A % against zero is undefined, and against a negative net it points the wrong way. |
| A-7 | **The automatic summary waits for the month's records to settle**: the host has been up for at least 10 minutes and no categorisation job is queued or running for a record dated in that month. Still unsettled 24 hours after it first could have gone, it is sent anyway, saying some records are still being processed. | After a night off across the month's end, the summary would otherwise go out before the queued messages were read and categorised, and never be revisited. |
| A-8 | **Explain in the bot runs off the update loop**: the press is answered at once and the request goes to an in-memory queue drained by a worker. Waiting presses are lost on restart, which is accepted. | Telegram updates are handled one at a time; a 20–40 s model call there holds every capture behind it, and an exception would retry the update as poison — a second paid call. |
| A-9 | **Explain explains what is shown**: the bot sends the summary message's own text; the page captures its summary text at the press and drops an answer that arrives after the view changed. | Rebuilding from month and currency days later would explain other figures than the ones on screen. |

## 1. Rate archive

**Source.** `GET https://open.er-api.com/v6/latest/EUR`, no key. Verified 2026-10-08 against
`exchangerate-api.com/docs/free`: rates update once a day; requesting hourly or daily stays under the
rate limit (an excess gets HTTP 429, lifted after 20 minutes); the payload carries `result`,
`time_last_update_unix`, `time_next_update_unix`, `time_eol_unix`, `base_code` and `rates`.
**Attribution is required** on the pages that use the rates: `<a href="https://www.exchangerate-api.com">Rates
By Exchange Rate API</a>`, which may be discreet.

**Table** (Persistence, a migration):

| column | |
|---|---|
| `currency varchar(3)` | one of `CurrencyCode.Supported` other than EUR |
| `as_of_date date` | the UTC date of the payload's `time_last_update_unix` |
| `source text` | `open.er-api` |
| `units_per_eur numeric(24,12)` | how many units of `currency` one EUR buys; `CHECK (units_per_eur > 0)` |
| `fetched_at timestamptz` | |

Key `(currency, as_of_date, source)`; append-only — a fetch that repeats a day inserts nothing
(`ON CONFLICT DO NOTHING`). Only the currencies the app supports are stored. `as_of_date` is a UTC date
and a record's `occurred_on` a local one; near midnight a record may find the previous day's rate,
which is harmless.

**Validation.** A snapshot is stored only whole, and only if `result` is `success`, `base_code` is
`EUR`, every supported currency is present with a rate above zero that fits the column, and
`time_last_update_unix` is not in the future. Anything else stores nothing and logs a Warning; since
nothing invalid is ever stored, no stored rate needs superseding. A non-zero `time_eol_unix` (the
endpoint announcing its end) logs a Warning once per process.

**Code.**
- `IFxRateSource` (Application) → `FxRateSnapshot(DateOnly AsOfDate, string Source, IReadOnlyDictionary<CurrencyCode, decimal> UnitsPerEur)`;
  `OpenErApiRateSource` (`Noof.Ledger.Fx`, the empty project that already exists) over a named
  `HttpClient`, registered by `AddNoofFx()` as `ReceiptsRegistration` registers its client. Fx's csproj
  gains `Microsoft.Extensions.Http`, which Receipts already uses.
- `IFxRateStore` (Application, implemented in Persistence): `AppendAsync(snapshot)`,
  `GetAsync(currencies, from, to)` — the rates in that range plus, per currency, the latest before
  `from` and the earliest after `to`.
- `FxRateWorker` (Host): every 6 hours, the first tick once `IDatabaseGate` is ready; each tick's
  exception is caught and logged; the call is timed through `IOperationTimer`.
- `FxRatesHealthCheck : ISystemHealthCheck` (Persistence, which owns the table): newest rate ≤ 3 days
  old → Ok; older → Warning, "Exchange rates N days old — normal while offline"; none → Warning,
  "No exchange rates yet".

**Lookup.** `RateTable` (Application, pure, built from `GetAsync`'s rows) converts an amount of
currency X on day *d* to Y as `amount / upe(X, d) × upe(Y, d)`, with `upe(EUR) = 1`; X to X needs no
rate. `upe(X, d)` is the latest rate on or before *d*, failing that the earliest after *d*. A
conversion is **approximate ("≈")** when the rate used is from after *d* or more than 3 days before it.
Either X or Y without any rate → the amount cannot be converted (§2). Conversion keeps full `decimal`
precision; rounding happens only when a figure is formatted, each figure on its own, so displayed parts
may differ from their displayed total by a minor unit — the page says so in its footer.

## 2. Calculation

**Rows.** One SQL query (Persistence) returns, for a date range — the month asked for and the three
before it — with `status <> Cancelled` (A-2), bucketed by `occurred_on`:

- each **line item** that counts: Principal lines of expenses and incomes, Fee lines of expenses and
  transfers — the predicate *This month* uses today — with its transaction, kind, role, category,
  merchant, description, amount, currency and day, and **the wallet it belongs to**: the
  transaction's wallet, except a transfer's Fee line, which belongs to the `fee_leg` wallet (`From` →
  `from_wallet_id`, `To` → `to_wallet_id`), as `LedgerPostings` posts it; and whether the record is
  **a refund** — an `Income` record with a `receipts` row of `receipt_kind = Refund` (SS-19);
- for an expense line in a currency that has a `charges` row, that row's `charged_amount`;
- each **transfer**: its day, `from_wallet_id`/`from_amount`/`from_currency`,
  `to_wallet_id`/`to_amount`/`to_currency`, `fee_leg` and the fee amount;
- **the scope's history start** (A-5): the first month, over the whole history, with a counted line in
  the scope.

**Per wallet, in the wallet's currency** (`MonthlySummaryCalculator`, Application, pure):

- **Spent:** an expense's Principal lines in the wallet's currency as they are; the lines of a foreign
  currency with a charge are replaced by the charge, split across them in proportion to their amounts,
  the rounding remainder (to the currency's minor unit) on the largest line, so the parts add up to the
  charge exactly. Plus every Fee line belonging to this wallet — an expense's foreign-purchase fee, a
  transfer's fee on its paying leg. A foreign line with no charge is converted per A-4. **Minus a
  refund's Principal lines**, each in its own category (SS-19), so a category's spending can be
  negative in a month whose refunds exceed its purchases.
- **Received:** Principal lines of incomes other than refunds (a foreign-currency income per A-4).
- **Net** = received − spent.
- **Moved out / Moved in** (SS-16): a transfer's principals, as `LedgerPostings` posts them — moved out
  = `from_amount` − the fee when the fee is on `From`, moved in = `to_amount` + the fee when it is on
  `To`. So a wallet's moved amount and its transfer fee together equal its leg as stored. Not part of
  spent, received or net.

**All wallets.** Each wallet's per-record amounts are converted to the chosen currency at their own
day's rate, then added. Movements are left out — they are not converted and summed to zero, since
across currencies they would not cancel at archive rates. An exchange's loss against mid-market is not
counted (SS-17).

**Not converted.** An amount that cannot be converted (no rate for its currency or the target's) is
listed per currency in a *Not converted* block, with the reason, and left out of the totals, the
comparisons, the highlights and the ranked lists. With no rates at all, the all-wallets view is that
block and nothing else.

**Comparison** (SS-4, SS-5). **The current month** counts its records up to today, and each earlier
month's window is its day 1 to `min(today's day, its last day)`. **A finished month** is compared with
whole earlier months. *vs previous month* uses *m − 1*; *vs average* the mean of *m − 1 … m − 3*,
counting only months from the scope's history start (A-5) — a month after the start with nothing
counted counts as zero — shown as "average of N months"; with N = 0 there is no comparison.
Percentages per A-6. Converted comparisons convert each amount at its own day, as above.

**Contents** (SS-9):
- **Highlights:** the three categories with the largest absolute change against the average (against
  the previous month when there is no average), ties by name, each one sentence from a C# template —
  e.g. `Groceries: 180.10 EUR, 40.00 more than your 3-month average.`
- **Categories:** amount, previous month, average, change.
- **Top merchants:** the five with the most spent — sum and number of records.
- **Largest records:** the five records with the largest spent amount — the sum of the record's spent
  lines in the scope, its charge and fee included; a transfer's fee is a record here too. Each is
  labelled with its merchant, or else its first line's description, and links to its trace.
- **Wallets** (all-wallets view): one line each — spent and received in its currency.

**Service.** `IMonthlySummaryService.BuildAsync(DateOnly firstDayOfMonth, SummaryScope scope,
CurrencyCode currency, CancellationToken)` (Application) → `MonthlySummary`; `SummaryScope` is all
wallets or one wallet (a wallet's summary ignores `currency`). "Now" and the local month come from
`TimeProvider` and the zone `EfSpendingReadModel` already uses. **`MonthlySummaryText`** (Application)
is the one plain-text rendering of a `MonthlySummary`: the bot's message, the automatic summary, and
what both *Explain* buttons send to the model, so the three cannot drift.

**Home's *This month*** follows the same per-wallet rule (A-1) and the refund rule (SS-19):
`ISpendingReadModel.ThisMonthAsync` returns the charge in the wallet's currency instead of the purchase
currency, and a refund as less spending, not as received. Still per currency, never converted.

**Net worth** (SS-13). `NetWorthCalculator` (Application, pure) converts every balance
`IBalanceReadModel` returns — archived wallets and a wallet's balances in other currencies included,
since that is money held — at the latest rate on or before today, and adds them; a currency with no
rate is left out and named.

## 3. Dashboard

**`/summary?month=YYYY-MM&wallet=all|<id>&currency=EUR|RSD`**, all optional — the current month, all
wallets and the reporting currency by default. The URL holds the whole view, so Home, the bot and the
browser history open exactly what was seen. Render mode `InteractiveServer` without prerendering, like
`/transactions` (`.claude/rules/web-ui.md`); no popover, menu or dialog.

- Header: ‹ month › — no step past the current month.
- Wallets as chips: *All wallets* · each active wallet · an archived wallet only when it has something
  in the month. EUR | RSD buttons only under *All wallets*.
- Totals: spent, received, net, each with *vs previous month* and *vs average of N months* (A-6); a
  wallet adds *Moved out* / *Moved in*.
- Highlights; categories table; top merchants; largest records (links to the trace); under *All
  wallets*, a line per wallet; the *Not converted* block when there is one.
- **Explain** (§5): the answer below the button in a `MudAlert`, a spinner while it runs.
- Footer: the range of rate dates used ("Rates of 3 – 7 Oct"), "≈" explained when used, that rounded
  parts may differ from totals by a minor unit, and the attribution link (§1).

**Home.** *This month* links to `/summary`. The Balances block gains **Total ≈ N EUR · rates of 7 Oct**
in the reporting currency, with the attribution link; a currency without a rate reads "excludes KZT —
no rate yet".

**`/settings/reporting`** (SS-12): *Reporting currency* EUR | RSD, saved to `app_setting` key
`reporting.currency` through `IReportingCurrencySetting` (Application; `EfReportingCurrencySetting` in
Persistence, as `IDatabaseLogLevelStore` is), linked from `NavBar` next to the secrets settings.

## 4. Telegram

**`/summary`**: the message is `/summary` or `/summary@<bot>`, any case, anything after it ignored —
recognised before the reply and capture branches, as `/health` is, and authorised by `IsOwnerAsync`
(never `IsAllowedAsync`, so it never claims ownership); a stranger gets no answer. Added to the bot's
command list with `/health` and `/bug`. It answers with the current month, all wallets, in the
reporting currency, rendered by `MonthlySummaryText` in English:

```
October 2026 · 1–8 Oct · EUR
Spent 412.30 (vs Sep 1–8 +12%, vs 3-month avg −5%)
Received 2,100.00 · Net +1,687.70 (vs Sep 1–8 +310.00)
• Groceries: 180.10 EUR, 40.00 more than your 3-month average.
Categories: Groceries 180.10 · Cafés 95.00 · … · Other 22.40
Top merchants: Maxi 120.40 (6) · …
Largest: 89.00 Gigatron · …
Wallets: Wise −250.00 / +2,100.00 EUR · Cash RSD −14,200 RSD · …
Rates of 3–7 Oct · exchangerate-api.com
```

**Length.** The renderer is budgeted, not merely capped: every name is cut to 24 characters with "…";
lists are capped — eight categories then *Other*, three merchants, three records, six wallets then
"+K more on the dashboard"; if the text is still over 3 800 characters, sections are dropped from the
bottom (largest records, then merchants) and a last hard cut ends with "…". A test renders the caps
with maximum-length names, and one renders an over-budget summary to see the reduction.

**Buttons:** `‹ Sep` · `Oct ›` (none past the current month) · `EUR`/`RSD` (the other one) ·
`Explain`. Month and currency **edit the same message**; *Explain* sends a **new** message (§5) and
leaves the summary as it is. Callback data `sum:v:2026-10:EUR` / `sum:x` (well under Telegram's 64
bytes), routed before `RecordActionHandler`, as the bug-report buttons are, and authorised by
`IsOwnerAsync`; a stranger's press gets no answer. `IChatNotifier` gains a send and an edit that take
text and a keyboard of `ChatButton(Label, Data)` rows — today's `EditAsync` takes only an echo.

**Automatic summary** (SS-6, A-3, A-7). `MonthlySummaryWorker` (Host) ticks hourly, after
`IDatabaseGate`, catching each tick's exception. When it is past 09:00 in the configured zone, the last
finished month is not the `app_setting` marker `summary.last-auto-month`, and that month's records have
settled (A-7), it renders that month in the reporting currency and sends it to the owner chat
(`SecretKeys.TelegramOwnerChatId`), then moves the marker. A month with nothing counted moves the marker
without sending. The first run after installation sends the last finished month at once if it has
anything counted. A failed send leaves the marker, so the next tick tries again — a Warning the first
time for that month, Debug after; a crash between the send and the marker may send twice, which is
accepted. No bot token or owner chat id → the tick does nothing, logged at Debug. The marker is read
and written through `IAutoSummaryMarker` (Application; implemented in Persistence beside the reporting
setting).

## 5. Explain

`ISummaryExplainer` (Application): `ExplainAsync(SummaryExplanationRequest, ct)` → the comment's text,
or a failure the caller shows as **"Couldn't explain this right now"** and logs as a Warning without
the model's text. `SummaryExplanationRequest` holds only text. `ChatSummaryExplainer`
(`Noof.Ledger.Ai`) answers through the forced tool `write_summary_comment { text }`, `strict: true`,
one round — as 8a's `write_explanation`.

The request is the summary as `MonthlySummaryText` rendered it, the one on screen (A-9): figures
formatted by C#, the names of categories, merchants and wallets and line descriptions — all of which
categorisation already sends. Never a record's raw message, a URL or a secret. The instructions ask for
three to five English sentences on what stands out. As 8a's IR-5, the comment's figures are not
checked.

The model is called only on *Explain*, never cached. **In the bot** (A-8) the press is answered at once
("Explaining…") and the summary message's text is queued to `SummaryExplanationWorker` (Host, after
`IDatabaseGate`, catching per item), which calls the model and sends the comment as a new message, cut
to 3 500 characters; a refused account (401/402/403) or any failure answers "Couldn't explain this
right now". **On the page** the summary text is captured at the press, and an answer that arrives after
the view changed is dropped.

## 6. Delivery

**Branch.** An aggregate branch `phase-8b` cut from `phase-8a` while 8a is unmerged (its draft PR
stacked on `phase-8a`, retargeted to `master` once 8a merges), from `master` otherwise. This spec and
its amendments, review fixes and the closing are committed straight to it (CLAUDE.md §5); the PRs are
cut by stage, not by assembly. Provisionally:

| # | PR — stage | Assemblies | After |
|---|---|---|---|
| 1 | **Rates and the calculation:** `fx_rates` and its migration, `IFxRateStore`, `OpenErApiRateSource` and its validation, `AddNoofFx`, `FxRateWorker`, `FxRatesHealthCheck`; `IReportingCurrencySetting` and `IAutoSummaryMarker` with their stores; the rows query, `RateTable`, `MonthlySummaryCalculator`, `MonthlySummaryText`, `IMonthlySummaryService`, `NetWorthCalculator`; *This month* per wallet (A-1) and with refunds (SS-19), with the Home screenshots it changes; `ISummaryExplainer`, `ChatSummaryExplainer`, `write_summary_comment` | Application · Persistence · Fx · Ai · Host · Demo | — |
| 2 | **The pages:** `/summary`, Home's net worth and link, `/settings/reporting`; demo data and screenshots | Web · Demo | 1 |
| 3 | **The bot:** `/summary`, its buttons, the notifier's keyboard methods, `SummaryExplanationWorker`, `MonthlySummaryWorker`; bot scenes | Application · Telegram · Host · Demo | 1 |

The closing, per `docs/CLOSING-A-PHASE.md`, goes straight to the branch: Q4 updated with SS-2 and the
Phase 7 spec's §4 sentence marked superseded by A-1. PR 1 is the large one; the plan may split it in
two (the rate archive, then the calculation) if its size makes it hard to follow.

**Backlog**, written with this spec: a summary over an arbitrary date range (SS-3); what exchanges lose
against mid-market, and comparing venues (SS-17).

**Reviews** (CLAUDE.md §1): Fable 5.1 and a Codex adversarial review in parallel ran on this spec
(their findings are folded in above); both run again on the plan and at the close. One Codex review
per PR; opus at medium effort per task.

## Testing

TDD, each new guard seen red. No test touches `noof_ledger`, the network or a live model.

- **Calculator** (unit): a wallet's spent, received and net; a refund lowering its category's spending,
  absent from received, net unchanged; a foreign charge split across lines, the
  parts adding up exactly; a foreign line with no charge and a foreign income converted with "≈", and
  without a rate in *Not converted*; fees on both kinds; a transfer fee on each leg landing on its
  wallet, with spent + moved equal to the stored leg; the current month's window and its clamp (31 Jan
  → 28 Feb); a finished February against whole January; the average over fewer than three months, with
  a gap month as zero, and over none; percentages against zero and a negative net; conversion at each
  day's rate and the "≈" rule; highlights and their ties; largest records with a charge and a transfer
  fee; net worth with an archived wallet and a currency without a rate.
- **Text** (unit): `MonthlySummaryText` at its caps with maximum-length names stays under 3 800
  characters; an over-budget summary drops sections in order.
- **Fx** (unit): parsing a synthetic payload; `result: error`, another `base_code`, a missing currency,
  a zero or negative rate and a future timestamp store nothing; a non-zero `time_eol_unix` warns.
- **Persistence** (template clones, filtered): append is idempotent and the positivity constraint
  holds; the range lookup's edges; the rows query per kind and status, a transfer fee's wallet per
  `fee_leg`, a refund flagged, the history start; *This month* with a foreign purchase (A-1) and a
  refund (SS-19); the health check's levels;
  the reporting setting and the marker.
- **Host** (`FakeTimeProvider`, `RunTickAsync`): `FxRateWorker` stores a snapshot and survives a
  failing source; `MonthlySummaryWorker` before 09:00, on the 1st, catching up days later, waiting on a
  queued job and sending after 24 hours anyway, an empty month, a failed send logging once, no owner;
  `SummaryExplanationWorker` sends the comment and the failure text.
- **Telegram:** `/summary` matching and a stranger ignored; routing `sum:` callbacks, a stranger's press
  ignored; edit in place; Explain answered at once and queued.
- **Ai:** `ChatSummaryExplainer` against a fake `IChatClient`; a live check only in the opt-in suite.
- **E2E:** `/summary` on demo data, switching wallet and currency; Explain without a model key shows
  "Couldn't explain this right now"; `/settings/reporting` saves.

**Demo and screenshots** (CLAUDE.md §5): four months of mock history and synthetic rates for them;
screens of `/summary` (all wallets and one wallet, desktop and phone), Home with the net worth and
`/settings/reporting`; bot scenes for `/summary`, Explain and the automatic summary.
