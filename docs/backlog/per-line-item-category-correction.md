---
title: Per-line-item category correction
status: deferred
area: web
---
**Wanted.** "No, that one was actually Transport" on a single line item.

**Why it is not scheduled.** The only recategorisation UI the spec names is `/recategorize`, which is
for a later governance phase (the original design's Phase 7; the delivered Phase 7 became currency
exchange) and is a bulk, merchant-rule-driven operation — not a one-row fix. That leaves every phase
until then with no way to correct a single miscategorised item.

**Cost already paid.** `CategorizationAuthority.User = 4` ships in Phase 1's enum and its integer value
is pinned by a test, so the precedence guard already knows a human outranks the model. The retrofit is
a page, not a migration.

**Partly taken by Phase 2 (2026-09-22).** A reply to the bot's echo (*"это не еда, а подарок"*) now
corrects a record through the model. A one-click category change on a single line item in the
dashboard is still this item.
