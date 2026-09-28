---
id: P5-3
title: "Log settings (2026-09-26, operator decisions, binding)"
status: decided
date: 2026-09-26
phase: Phase 5
related: [p5-2-phase5-followups]
---

These five decisions were made directly by the operator and supersede the Phase 5 rules they touch
(`.claude/rules/logging.md` reflects (a)–(d); O-11 in `p5-2-phase5-followups.md` is superseded by (a)).

| # | Decision | Reasoning |
|---|---|---|
| (a) | The database log level offers every `LogSeverity` plus **Off** (nothing written to `app_log`) — the earlier "capped at Information" rule (O-11) is withdrawn. Above Information, or at Off, the trace page shows an inline `MudAlert` (`#trace-log-level-notice`) saying stage events are not recorded at the current level, linking to Log settings | The operator wanted the choice available and was willing to accept the stated consequence rather than have the UI refuse it; surfacing the consequence on the one page it actually affects is cheaper and more honest than a blanket cap |
| (b) | Settings on the new Log settings page are saved by an explicit **Save** button, not on a field's own change — the same pattern `Secrets.razor` already uses | A field that persists itself the moment you touch it is not obvious from looking at it — the Logs page's former inline "Record to database from" select did exactly that, silently, which is what prompted this decision |
| (c) | Per-level database retention (days) moved from `appsettings.json` (`Logging:Retention:Days`) onto the Log settings screen, persisted in `app_setting` via `ILogRetentionSettings`/`LogRetentionDays`, with C# defaults Verbose 1, Debug 1, Information 30, Warning 90, Error 90, Fatal 90 | A day count an operator wants to change on the fly (loosen Debug briefly, tighten Warning) belongs next to the level it retains, not in a file that needs a restart; the defaults also come down from two years to ninety days for Warning/Error/Fatal, since two years of a personal ledger's warnings was never a deliberate choice, just what shipped first |
| (d) | File and console sinks stay static, in `appsettings.json`, under explicit per-sink keys (`Logging:File:{Directory,MinimumLevel,FileSizeLimitBytes,RetainedFileCountLimit}`, `Logging:Console:MinimumLevel`); file retention stays by file count only; `Serilog:MinimumLevel:Default` is withdrawn and fails startup fast if present | Implemented in commit `4c87d5c`, ahead of (a)–(c)/(e); see `.claude/rules/logging.md` for the mechanics |
| (e) | `transaction_revisions` retention is explicitly **not** built now | It is append-only by an enforced trigger (`UPDATE`/`DELETE`/`TRUNCATE` all refused) — any retention job would need that trigger relaxed first, which is a bigger decision than this feature's scope; tracked in `docs/backlog/loose-ends-phase-5-observability.md` instead of decided here |
