---
paths:
  - "src/Noof.Ledger.Host/**"
  - "src/Noof.Ledger.Application/Diagnostics/**"
  - "src/Noof.Ledger.Persistence/Diagnostics/**"
  - "src/Noof.Ledger.Ai/Diagnostics/**"
  - "src/Noof.Ledger.Telegram/Diagnostics/**"
  - "**/Logging/**"
  - "**/Diagnostics/**"
  - "**/*Log.cs"
  - "**/appsettings*.json"
  - "src/Noof.Ledger.Web/Components/Pages/Settings/LogSettings.razor"
  - "src/Noof.Ledger.Web/Components/Pages/TransactionTrace.razor"
  - "src/Noof.Ledger.Web/Components/Pages/Diagnostics.razor"
  - "src/Noof.Ledger.Web/Components/Pages/DiagnosticsLogs.razor"
---

## CLAUDE.md §4 — Logging *(settled 2026-09-25, Phase 5)*

- **Serilog is the `Microsoft.Extensions.Logging` provider only** — referenced by `Noof.Ledger.Host`
  alone. Everywhere else logs through `ILogger<T>` with `[LoggerMessage]` source-generated methods;
  `CA1848`/`CA2254` are errors in `src` and an architecture test additionally bans a direct
  `.LogXxx(` call there.
- **`[LoggerMessage]` methods live in a sibling top-level `static partial class`, never a nested `Log`
  class** — a nested class does not compile here (`CS1109`/`CS0260`).
- Every log event gets a stable, pinned `EventId`. A `[LoggerMessage]` with no id is a gap the next
  person has to notice by hand.
- **Timing an operation goes through `IOperationTimer`** with a name from `TimedOperations`:
  `using var timing = timer.Start(logger, TimedOperations.X, expectedWait: ...)`, with an explicit
  `Stop(onlyIfSlow: ...)` on the success path so an idle call logs nothing; thresholds live in
  `Logging:SlowOperationMs`. Never `Stopwatch`, never a `GetUtcNow()` subtraction — `TimeProvider`'s
  `GetTimestamp`/`GetElapsedTime` end to end.
- **The database sink's minimum level is runtime state**, set on `/diagnostics/logs/settings` and
  persisted in `app_setting`. *(Decision (a), 2026-09-26, withdraws the earlier "capped at
  Information" rule.)* All six severities are offered, plus **Off** (nothing written to `app_log`).
  Off is not a `LogSeverity` — `IDatabaseLogLevel.Current`/`SetAsync` take `LogSeverity?`, `null`
  meaning Off — and is represented to Serilog by `LogLevelSwitches.Off`, a sentinel one past `Fatal`
  that no real event ever reaches; `RecomputeRoot`'s `Min()` naturally excludes it, so Off never
  lowers the root below the file/console floor. **Consequence the operator accepted rather than
  guarded against:** above Information (or Off), the database sink drops the Information-level stage
  events the trace page reads, so a transaction's trace page shows an inline notice
  (`#trace-log-level-notice`) linking back to Log settings whenever that is the case — it does not
  stop you choosing that level. The file and console sinks are static configuration instead —
  `Logging:File:MinimumLevel` (default `Debug`) and `Logging:Console:MinimumLevel` (default
  `Information`) — and the root level is `min(file, console, database-unless-Off)`.
  `Serilog:MinimumLevel:Default` is withdrawn *(decision (d), 2026-09-26)*: a value left under that
  key fails startup fast, naming the two keys above, rather than silently binding neither sink.
  `Serilog:MinimumLevel` now holds only `Override` (per-category); file retention is by file count
  only (`Logging:File:RetainedFileCountLimit`, `Logging:File:FileSizeLimitBytes`), never by days. A
  per-sink floor is `restrictedToMinimumLevel` or a `levelSwitch` on the `WriteTo` call — and an inner
  `LoggerConfiguration` reached through `WriteTo.Sink(innerLogger)` or any other direct
  `ILogEventSink.Emit` call **bypasses its own `MinimumLevel` entirely** (`SerilogInnerLoggerSinkTests`,
  against Serilog 4.4.0): `Logger.Emit` dispatches to the sink pipeline unconditionally, and only
  `ILogger.Write` — what `logger.Information(...)` and friends call — checks a logger's own floor
  first. `WriteTo.Logger(...)` (which calls `Write`) is the one variant that would honour it.
- **Database log retention, per level, lives only on `/diagnostics/logs/settings`** *(decision (c),
  2026-09-26)*, persisted in `app_setting` through `ILogRetentionSettings`/`LogRetentionDays` — never
  in `appsettings.json`. C# defaults (`LogRetentionDays.Default`): Verbose 1, Debug 1, Information 30,
  Warning 90, Error 90, Fatal 90 days; `EfLogRetention` reads the stored value, falling back to these.
  Changes on that page take effect only on **Save**, never on a field's own `@bind:after` — the same
  pattern as `Secrets.razor` — matching decision (b): no setting in this feature autosaves on change.
  `transaction_revisions` retention is explicitly not part of this — `docs/BACKLOG.md`.
