---
id: P5-1
title: "Phase 5 observability decisions (2026-09-24/25)"
status: decided
date: 2026-09-24
phase: Phase 5
related: [p1-2-postgres-credential-deferred]
---

Taken during the observability spec conversation; full detail in
`docs/specs/2026-09-24-observability-design.md`. Rules that bind future work live in
`CLAUDE.md` and `.claude/rules/`; reasoning is recorded here.

| # | Decision | Reasoning |
|---|---|---|
| O-1 | The host waits for PostgreSQL **indefinitely** and never exits on its own for a missing database | A single-operator local app has no orchestrator to restart it on exit; a host that gives up and dies waits for a human to notice and relaunch it, which is strictly worse than one that keeps retrying with backoff and answers `/healthz` throughout |
| O-2 | Log files live in a configurable folder (`Logging:File:Directory`), default `%LOCALAPPDATA%\NoofLedger\logs`; daily roll, 14 files, 50 MB cap each | Matches the existing backup retention shape (`%LOCALAPPDATA%\NoofLedger\backups`, newest 14) rather than inventing a second convention; configurable because the default drive may not be where the operator wants months of logs |
| O-3 | Serilog is only the `Microsoft.Extensions.Logging` provider; all code logs through `ILogger<T>` + `[LoggerMessage]`; Serilog packages are referenced by `Noof.Ledger.Host` only | Keeps the same provider-boundary shape as the AI factories (D-A) — nothing outside the composition root depends on which logging library is behind `ILogger<T>`, so swapping Serilog later touches one project; `[LoggerMessage]` is source-generated, so `CA1848`/`CA2254` can be errors with no runtime cost |
| O-4 | The database sink is the ready-made `Serilog.Sinks.Postgresql.Alternative`, not a custom sink; no separate connection isolation; the log table is an ordinary EF migration | A hand-written sink is a second thing to keep correct under load and shutdown; the original 2026-09-19 design's "separate connection, pre-migration DDL" was superseded because nothing about this app's scale needs the log path decoupled from the ledger's own migration and connection lifecycle |
| O-5 | The most important thing logged is the **transaction**: its path (Received ▸ Transcribed ▸ Categorized ▸ Persisted ▸ Replied) plus its revision history, on one page | The operator's actual question when something looks wrong is "what happened to this message", not "show me every log line" — a per-transaction trace answers that directly, and stable `EventId`s (5001–5005, 5009) keep the trace reader and the writers from drifting apart |
| O-6 | Health shows as a dashboard tile **and** on `/diagnostics` | The tile is what is seen without looking for it; the full page is for when something needs investigating — one seam (`ISystemHealth`) serves both so they can never disagree |
| O-7 | `/health` is a bot command, **owner-only**, and never claims ownership | The bot already has an owner-claim mechanism from Phase 2 (the first chat to write becomes the owner); reusing it for `/health` costs nothing and keeps system state out of any chat but the operator's own |

**What this closes.** P1-2's owed "hardening / production-readiness phase" asked, among other things,
for "a real look at what is logged" — Phase 5 is that look: structured logging replaces ad hoc
`Console`/`ILogger` calls, `SecretRedactor` runs before every sink, and retention is enforced rather
than left to grow forever. **What P1-2 still owes:** the PostgreSQL credential itself (SSPI vs. a
DPAPI-protected password file, Q1) was not touched by this phase and stays open, still parked for
whichever phase finally does the credential-storage rework.

**The Phase 1B closing review's open question — "does any dependency throw an
`OperationCanceledException` that is not our stopping token?" (`docs/backlog/dependency-operationcanceledexception-not-our-token.md`, "Two things the
closing review flagged and could not settle") — is *not* settled by Phase 5.** Every hosted loop now
catches non-cancellation exceptions per tick (`CLAUDE.md` §4, Architecture), but the filter is still
`ex is not OperationCanceledException` — the same shape as before, just applied to more workers. A
dependency that raises `OperationCanceledException` for a reason other than the loop's own
`stoppingToken` still passes straight through uncaught, and `HostOptions.BackgroundServiceExceptionBehavior`
is still the unoverridden .NET default `StopHost` (Phase 5's C-1 finding confirmed this directly), so
that specific failure mode would still take the whole host down. The original suggestion — drive
`IChatNotifier.EditAsync` into a real timeout and see what type comes out — is still the way to settle
it, and is still not done.
