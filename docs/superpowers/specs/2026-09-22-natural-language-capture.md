# Natural-language capture — design

**Status:** approved by the operator 2026-09-22. Amends `2026-09-19-noof-finance-design.md` §8 and §12.

## Why this exists

Quote-and-verify made the model copy the amount out of the message character for character, and C#
rejected anything it could not find as digits in the text. That rejects exactly how people talk:
*"купил вчера штуку евро"* has no digits in it, and neither does most of what speech recognition
produces (*"двести пятьдесят"*). Voice is the capture path that matters most, so the guard was
blocking the main use case to prevent a failure the operator would rather fix by hand.

The operator's decision, in their words: *"не надо делать валидацию. просто в чат тг должен
присылаться распознанный вариант. и его можно уже руками поправить. или написать как обработать. но
главное его можно отменить и изменить. это важнее guarda"*.

So the safety moves from **rejecting model output** to **showing it and making it cheap to undo**.

## Decisions

**D1 — The model interprets; C# does not second-guess it.** The model returns the amount as a number
(`"1000"` for *штуку*), the currency, the date, the category and the merchant name. There is no
verbatim check, no evidence span, no sanity bound. `QuotedAmount` and the verbatim merchant check are
removed. C# still *parses* what comes back (a decimal string with `InvariantCulture`, a slug looked up
to a category id) — mapping, not validation. A response that does not parse at all is a failed job,
as today.

**D2 — The date is the model's call too.** The user turn tells the model today's local date and
weekday in the capture time zone. The model returns `occurred_on` as an ISO date, or omits it when the
message names no day. "Today" is the local date of the message's **send** time (Telegram's
`message.Date`), never of processing time — a message that sat in the offline queue overnight must not
move a day.

**D3 — `occurred_on` is a local date column; `occurred_at` stays the send instant.** `transactions`
gains `occurred_on date not null`, set at capture to the local date of `occurred_at` in the row's own
`time_zone_id`, and overwritten by the model's date. Backfilled by the migration. The dashboard buckets
by `occurred_on`. No precision column: when `occurred_on` equals the local date of `occurred_at`, the
time of day is known; otherwise only the day is. A backdated purchase never gets an invented midnight.

**D4 — The bot echoes what it recorded, with buttons.** When categorisation finishes, the bot edits
its acknowledgement into the recognised result — each line, the date when it is not today, the total
— with an inline keyboard: **Отменить** · **Изменить**. Figures are rendered by C# from the stored
rows, so the echo shows exactly what is in the database.

**D5 — Cancel is deterministic and reversible.** *Отменить* sets `TransactionStatus.Cancelled`; no
model call, works offline. The read model excludes cancelled rows. The button becomes **Вернуть**,
which restores the previous status.

> **Note, operator, 2026-09-23.** The labels above are now **Cancel** / **Edit** / **Restore**, and
> the bot writes English only (multi-language is deferred — `docs/BACKLOG.md`). The operator may
> still write to the bot in any language; only the bot's own output changed. See
> `docs/OPEN-QUESTIONS.md` P2-5.

**D6 — Three ways to change a record, all converging on one correction job:**
1. **Reply** to the bot's echo with free text — *"нет, 1500"*, *"это было позавчера"*, *"это подарок"*.
2. **Изменить** — the bot asks what to change with a `ForceReply` prompt; the answer is route 1.
3. **Edit the original message** in Telegram — the edited text replaces `raw_text` and the message is
   re-interpreted from scratch.

A correction job hands the model the original message, the current record and the instruction, and
asks for the complete corrected record. The result replaces the model-authored line items, as a first
categorisation does today; `categorized_by = User` rows keep their precedence.

**D7 — Every change is kept.** `transaction_revisions` is append-only: one row per state the
transaction has had (initial, correction, edit of the original, cancel, restore), holding the
instruction text when there was one and a JSON snapshot of the line items and date. Rolling back to a
revision from the UI is not in this phase; the history exists so it can be.

**D8 — The rule in CLAUDE.md changes shape, not strength.** *"No number in user-facing output
originates from a model"* stays for reports, totals, balances and any explainer: those are computed by
C#. For **capture**, the model interprets the user's own words, and the user sees and can undo the
result. That is the whole contract.

## Scope of the phase

In: D1–D8, text messages only, all tests against a faked model and the test database template.

Out, each with a home:
- **Voice** — the next phase (see the phase table below). Needs a speech-to-text decision (no STT at
  Anthropic; a hosted service or local whisper — a price and privacy question).
- **Editing in the dashboard** — `docs/BACKLOG.md`.
- **Rollback to an earlier revision** — `docs/BACKLOG.md`.
- **Prompt tuning against the live model** — the final phase, each live run only with the operator's
  explicit permission.

## Phases, renumbered

| # | Phase | Delivers |
|---|---|---|
| **2** | Natural-language capture | *"купил вчера штуку евро"* → 1000 EUR dated yesterday, echoed with Отменить/Изменить; a reply *"нет, 1500"* corrects it; every state kept. |
| **3** | Voice | A Telegram voice note goes through speech-to-text and then the same pipeline. |
| 4 | Money model | Was Phase 2: exact balances across five currencies under `ru-RU` and `sr-Latn-RS`; a backup restored at least once. |
| 5 | Observability | Was 3. |
| 6 | Receipts | Was 4 (rich capture), minus voice. |
| 7 | Currency exchange | Was 5. |
| 8 | Integrity + explainer | Was 6. |
| 9 | Governance | Was 7. |
| 10 | Operations | Was 8. |
| **11** | Live calibration | A corpus of real phrasings run against the live model; system instructions tuned. |
