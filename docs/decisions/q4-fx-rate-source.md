---
id: Q4
title: "Canonical mid-rate source for RSD: `open.er-api.com` for all five currencies, or add NBS *srednji kurs* for RSD?"
status: deferred
phase: none set (was 5, then 7)
---

**Default taken:** `open.er-api.com` for all five.

**Decide by:** no phase set — Phase 7 deferred it again (was 5, then 7; the 2026-10-02 note below).

**2026-10-02, Phase 7 closed without an external rate source.** The operator scoped the rate archive
and the spread insight out of Phase 7 (T-1, `docs/decisions/p7-1-transfers-and-exchange-decisions.md`):
a foreign-currency spending is converted at each wallet's own terms set on `/wallets` (T-6), and a
transfer's rate is the one the operator said or the one its two amounts imply. No mid-rate is fetched or
stored, so this question stays deferred, with no phase named for it.

## Why the default is safe to defer

The two providers disagree on RSD by **0.09%** while the spread being measured is 0.5–0.6%, so
provider choice is roughly a sixth of the signal. `Source` is stamped on every rate row and the
archive is append-only, so adding NBS later is a new source, not a migration.
