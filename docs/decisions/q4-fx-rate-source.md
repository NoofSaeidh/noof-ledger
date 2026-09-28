---
id: Q4
title: "Canonical mid-rate source for RSD: `open.er-api.com` for all five currencies, or add NBS *srednji kurs* for RSD?"
status: deferred
phase: Phase 7 (was 5)
---

**Default taken:** `open.er-api.com` for all five.

**Decide by:** Phase 7 (was 5).

## Why the default is safe to defer

The two providers disagree on RSD by **0.09%** while the spread being measured is 0.5–0.6%, so
provider choice is roughly a sixth of the signal. `Source` is stamped on every rate row and the
archive is append-only, so adding NBS later is a new source, not a migration.
