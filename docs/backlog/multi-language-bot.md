---
title: Multi-language bot
status: deferred
area: telegram
since: 2026-09-23
---
**Wanted.** The bot's own text (the echo, buttons, prompts) in more than one language.

**Why it is not scheduled.** English only for now, by the operator's 2026-09-23 review (P2-5, D-D) —
multi-language was never budgeted into Phase 2, and shipping one language today costs nothing toward
shipping more later. The operator may still write to the bot in any language; only the bot's own
output is fixed to English (`Category.NameEn` in the echo, `RecordEcho`'s constants, the button
labels).

**What it would take.** Resource strings per language instead of literals in `RecordEcho.cs` and
`RecordActionButtons.cs`, and a language setting for the operator to choose from — stored, not
configuration, the same shape as the currency default above. `Category.NameRu` already exists in the
schema and is unused by the bot today, so the category half of this is data that is already there.
