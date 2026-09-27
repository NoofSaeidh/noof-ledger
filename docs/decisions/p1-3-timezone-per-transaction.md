---
id: P1-3
title: "Timezone for \"today\" / \"this month\""
status: decided
date: 2026-09-21
phase: Phase 1
---

**Decision:** Per-transaction. The zone is stored on the row, not assumed globally.

## The zone lives on the row

Storage stays `DateTimeOffset` → `timestamptz`; that was already settled. What is new is that
bucketing into a local day must not depend on the machine's clock. Telegram does not report the
sender's zone, so the row's zone is stamped at capture time from a current-zone setting (default
`Europe/Belgrade`) that the user can change when they travel. History then stays honest: a spend made
in Belgrade keeps its Belgrade day even after the setting moves.

`time_zone text` (IANA id) on the transaction, in the first migration. Retrofitting it means
re-bucketing history against an assumption nobody wrote down.
