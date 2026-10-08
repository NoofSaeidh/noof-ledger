---
title: Filing an improvement the way /bug files a bug report
status: deferred
area: telegram
since: 2026-10-08
---
**Wanted.** Beside `/bug`, a way to file an improvement — an idea or a missing feature — from
Telegram, the moment it occurs to the operator, and find it later where bug reports are triaged.

**What it builds on.** Phase 8a's `/bug` (`docs/specs/2026-10-02-integrity-and-bug-reports-design.md`)
saves a report with the operator's text and the record it replies to, answers with the model's
explanation, and brings reports to triage through the `/bugs` page, `.\run.ps1 bugs export` and the
`/bugs` skill. Its store is already source-agnostic (R-2), so an improvement could be a second kind of
report rather than a second store.

**Open.** The command's name; whether an improvement gets the model's explanation and the record's
integrity findings at all, or only the text; and whether it shares the `/bugs` page and export or
gets its own.
