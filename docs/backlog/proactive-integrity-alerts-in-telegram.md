---
title: Tell the operator in Telegram when an integrity check finds something
status: deferred
area: telegram
since: 2026-10-02 (Phase 8a)
related: [loose-ends-phase-5-observability]
---
**What.** Today a finding is seen only when someone looks — the health tile, `/diagnostics/integrity`,
or `/health` in the bot. A proactive message would arrive when a Bug appears, or when something has
waited on the operator for a day. The general case — a push when any health check turns red — is
already parked in `loose-ends-phase-5-observability`; Integrity is one of those checks, so solving that
covers the tile turning red, but not a message naming the finding.

**Why it is not done.** Out of scope for Phase 8a: checks run on demand and nothing about them is stored
(IR-3), so knowing that a finding is *new* needs state the phase deliberately does not keep, and a
message per finding needs a rule against repeating itself.
