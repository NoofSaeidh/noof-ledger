# Phase 5 — Observability (design)

**Status:** approved in conversation 2026-09-24/25 (sections 1–5), implementation authorised immediately
after writing. Supersedes the logging parts of `2026-09-19-noof-finance-design.md` §10 where they differ
(no custom sink, no separate connection, no pre-migration DDL — operator's decision).

## Goal

Stop PostgreSQL and start the app → it says "waiting for database", logs every attempt to a file, does not
exit, and comes up by itself when PostgreSQL starts. Every transaction's path from message to echo, and its
later edits, is visible on one page. System health is visible on the dashboard, on `/diagnostics` and from
the bot's `/health` command. Killing the DB log sink degrades the log viewer to a file tail and turns the
Log sink check amber.

## Decisions (operator's, 2026-09-24/25)

| # | Decision |
|---|---|
| O-1 | The host waits for PostgreSQL **indefinitely**; it never exits on its own because the database is missing. |
| O-2 | Log files live in a folder configured by `Logging:File:Directory` in `appsettings.json`, default `%LOCALAPPDATA%\NoofLedger\logs`; daily roll, 14 files kept, 50 MB cap per file. |
| O-3 | **Serilog is only the provider** behind `Microsoft.Extensions.Logging`. All code logs through `ILogger<T>` with `[LoggerMessage]` source-generated methods. Serilog packages are referenced by `Noof.Ledger.Host` only. |
| O-4 | The database sink is the ready-made `Serilog.Sinks.Postgresql.Alternative`, not a custom sink. No separate connection isolation; the log table is created by an ordinary EF migration. |
| O-5 | The most important thing logged is the **transaction**: its path (Received ▸ Transcribed ▸ Categorized ▸ Persisted ▸ Replied, with timings and errors) plus its revision history, on one page. |
| O-6 | Health: a tile on the dashboard **and** `/diagnostics`. |
| O-7 | `/health` bot command, **owner only**; never claims ownership. |

## 1. Startup and waiting for the database

- `IDatabaseGate` (Application) exposes the database state: `Waiting`, `Migrating`, `Ready`, `Failed`, a
  human-readable `Detail`, and `WaitUntilReadyAsync(CancellationToken)`. The Host owns the mutable
  implementation.
- `Program.cs` no longer awaits `MigrateNoofDatabaseAsync` before `app.Run()`. The host always starts:
  Kestrel, the sign-in page, `/healthz` and the file log work with no database.
- `DatabaseStartupService` (Host, `BackgroundService`, registered before every worker) loops:
  - opens a connection; if `Database:MigrateOnStartup` (default `true`) runs `MigrateAsync`; sets `Ready`;
  - connection-level failure (socket, refused, timeout, `NpgsqlException` with no SQL state) → `Waiting`,
    Warning log, retry with backoff 2 s, 4 s, 8 s, 16 s, 30 s, 30 s … forever. The first 10 attempts are each
    logged; after that every 10th;
  - the database answered but migration failed (a `PostgresException` or any other non-connection error) →
    `Failed`, Error log with the exception, retry every 5 minutes;
  - cancellation on shutdown ends the loop quietly.
- Every worker (`CategorizationWorker`, `TranscriptionWorker`, `BackupWorker`, `TelegramPollingService`,
  and the new `LogRetentionWorker`) awaits `gate.WaitUntilReadyAsync(stoppingToken)` before its loop.
- UI: while not `Ready`, the sign-in page shows an inline `MudAlert` instead of the form —
  "Waiting for the database…" (`Waiting`/`Migrating`) or "Database migration failed — see the log file."
  (`Failed`). A signed-in user sees the same banner on every page instead of data. No live refresh.
- `/healthz` stays liveness: always 200.
- Setting the PostgreSQL service to Automatic is the operator's, documented in `ops/RUNBOOK.md`.

## 2. Logging

- Packages (Host only): `Serilog.AspNetCore`, `Serilog.Sinks.File`, `Serilog.Sinks.Postgresql.Alternative`
  (≥ 4.3.0, net10.0, Npgsql ≥ 10). Versions pinned in `Directory.Packages.props` if the repo uses central
  package management, otherwise in `Noof.Ledger.Host.csproj`.
- First line of `Main` (after the CLI verb branch): a bootstrap logger (`CreateBootstrapLogger()`) with the
  file sink. The directory is read from `appsettings.json` / environment via a plain `ConfigurationBuilder`
  (`Logging:File:Directory`, environment variables expanded, default above). `UseSerilog((ctx, services,
  cfg) => …)` then reconfigures it with: console, file (same settings), and the PostgreSQL sink.
- PostgreSQL sink: table `app_log` (schema `public`), created by an EF migration; the sink's auto-create is
  off. Columns:
  `id bigint identity PK`, `logged_at timestamptz not null`, `level smallint not null` (Serilog
  `LogEventLevel` numeric value), `source text null` (`SourceContext`), `message text not null` (rendered),
  `template text not null`, `exception text null`, `transaction_id uuid null` (property `TransactionId`),
  `properties jsonb null`. Indexes: `(logged_at)`, `(transaction_id)`, `(level, logged_at)`.
- The DB sink only receives events while `IDatabaseGate.State == Ready` (a filter on its sub-logger); before
  that, events go to file and console only. Batches lost while PostgreSQL is down are lost from the table
  only — the file is the durable copy.
- Sink failure signal: a failure listener (Serilog `ILoggingFailureListener`, or `SelfLog` if the sink does
  not support listeners) feeds `ILogSinkStatus` (`RecordFailure(DateTimeOffset)`, `LastFailureAt`), used by
  the Log sink health check.
- `[LoggerMessage]` everywhere in `src`: every existing `logger.LogXxx(...)` call (19 in
  `TelegramPollingService`, `BackupWorker`, `CategorizationWorker`, `TranscriptionWorker`) is converted.
  `CA1848` and `CA2254` are errors for `src`. An architecture test additionally fails on any direct
  `.LogTrace(`/`.LogDebug(`/`.LogInformation(`/`.LogWarning(`/`.LogError(`/`.LogCritical(` call in `src`.
  `Program.cs`'s existing `LogCritical` for the loopback guard is converted too.
- **Transaction path events.** Each stage opens `logger.BeginScope(new Dictionary<string, object>
  { ["TransactionId"] = id })` (a helper `TransactionLogScope.Begin(logger, id)` in Application) and writes
  one event with a `Stage` property. Stable event ids:

  | EventId | Stage | Where | Key properties |
  |---|---|---|---|
  | 5001 | `Received` | Telegram capture (text or voice) | `CaptureKind`, `ChatId` |
  | 5002 | `Transcribed` | `TranscriptionWorker` | `DurationSeconds`, `Characters` |
  | 5003 | `Categorized` | `CategorizationWorker` after the model answered | `Kind`, `WalletId`, `Summary` (C#-formatted amounts and categories) |
  | 5004 | `Persisted` | after line items / entries / checkpoint are saved | `Kind` |
  | 5005 | `Replied` | after the echo is sent or edited | `BotMessageId` |
  | 5009 | `StageFailed` | any stage's failure | `FailedStage`, exception |

  Event ids, stage names and property names live in one Application type (`TransactionStages`) so the
  trace reader and the writers cannot drift.
- **Secrets.** Primary protection is not logging them (true today). Plus:
  1. `RemoveAllLoggers()` on every `HttpClient` stays (existing tests).
  2. `SecretRedactor`: applied before every sink (file, console, DB). Replaces every known secret value
     with `***` in string property values (recursively), in the exception text, and hence in the rendered
     message. Known values: every value in `ISecretStore` (snapshot refreshed on `Ready` and every 5
     minutes; values shorter than 8 characters are ignored) plus the database password from the connection
     string (available at bootstrap).
  3. A sentinel test (section 5).
- **Retention.** `LogRetentionWorker` (Host): first run 60 s after `Ready`, then every 6 h, through
  `ILogRetention.PruneAsync(DateTimeOffset now, CancellationToken)` (Application; Persistence
  implementation with `ExecuteDeleteAsync`): Verbose/Debug older than 7 days, Information older than 90
  days, Warning/Error/Fatal older than 730 days. Files are pruned by the file sink.
- `logs/` is added to `.gitignore`.

## 3. Health

- Standard `Microsoft.Extensions.Diagnostics.HealthChecks`; each assembly registers its checks in its own
  `AddNoofXxx`. `ISystemHealth` (Application) is the single read seam:
  `Task<SystemHealthReport> GetAsync(bool fresh, CancellationToken)` — `fresh: false` returns a result at
  most 30 s old, `fresh: true` runs the checks now. `SystemHealthReport` = overall `HealthLevel`
  (`Ok`/`Warning`/`Failing`) + ordered `IReadOnlyList<HealthItem(string Name, HealthLevel Level, string
  Summary, DateTimeOffset CheckedAt)>`. Web and Telegram depend only on `ISystemHealth`, never on
  `HealthCheckService`.
- Checks (Ok / Warning / Failing):

  | Name | Ok | Warning | Failing | Lives in |
  |---|---|---|---|---|
  | Database | gate `Ready` | `Waiting`/`Migrating` — "Offline — waiting for PostgreSQL" | `Failed` | Host |
  | Migrations | none pending | — | pending after `Ready` | Persistence |
  | Telegram | last successful poll < 2 min | network failure — "Offline — this is normal" | 401 (token rejected) · token secret missing | Telegram |
  | AI keys | Anthropic and Groq keys present | — | a key missing | Host |
  | Backup | last success < 26 h | stale, or last run failed | — | Host |
  | Disk | > 5 GB free on the log and backup drives | 1–5 GB | < 1 GB | Host |
  | Log sink | no sink failure in the last 10 min | failures in the last 10 min — "Logs are in the file only" | — | Host |

  Checks other than Database report Warning "Waiting for the database" while the gate is not `Ready`.
- `IPollingHeartbeat` (Application) — `RecordSuccess(DateTimeOffset)`, `RecordFailure(DateTimeOffset,
  PollFailure)` with `PollFailure` = `Network` | `Unauthorized` | `Other`; written by
  `TelegramPollingService`, read by the Telegram check.
- The Backup rule currently in `Home.razor` (`BackupIsStale()`/`DescribeBackup()`) moves into the Backup
  check; the dashboard stops computing it.
- The LLM plays no part in health: an architecture test asserts that no health check type references
  `Noof.Ledger.Ai` or `Microsoft.Extensions.AI`.
- FX freshness arrives with the FX phase.

### `/health` in Telegram

- `TelegramUpdateRouter` intercepts a message whose text is `/health` or `/health@<anything>` (trimmed,
  case-insensitive) before capture. It never creates a transaction.
- Authorisation: `TelegramOwnerGate.IsOwnerAsync(chatId, ct)` — `true` only when the owner secret is
  `Present` and equals the chat id. It never claims ownership. Anyone else, or no owner yet → no reply, no
  write, a Debug log without the message text.
- Reply: `ISystemHealth.GetAsync(fresh: true)` formatted by `HealthReplyFormatter` (Telegram), plain text,
  English:

  ```
  Health: 1 warning
  ✅ Database — ready
  ⚠️ Backup — last success 31 h ago
  ```

  Header: `all good` / `N warning(s)` / `N failing` (failing wins when both). One line per check, `✅` / `⚠️`
  / `❌`, name, em dash, summary.
- When polling starts and the owner is known, the bot calls `setMyCommands` with `/health` ("System
  health") scoped to `BotCommandScopeChat(ownerChatId)`. No owner → skipped until the next start.
- While the database is down the bot is silent (the owner check and polling need the database).
- Proactive alerts in Telegram → `docs/BACKLOG.md`.

## 4. UI

- NavBar: **Diagnostics** item.
- Dashboard tile (`id="health-tile"`): an inline `MudAlert` with the overall level; one line per
  non-Ok check, or "All systems normal"; links to `/diagnostics`. Replaces `database-unavailable` and
  `backup-status`. No popover, tooltip, dialog, snackbar or menu.
- `/diagnostics`: table of all checks (level, name, summary, checked at); a "Logs" link per non-Ok row to
  `/diagnostics/logs?source=<relevant category>`; a "Transaction id" field that opens the trace page.
- `/diagnostics/logs`: paged `QuickGrid`, 100 rows per page, newest first, **no `Virtualize`**. Filters:
  minimum level, from/to time, text (`ILIKE` on `message`), source, transaction id. A row expands to the
  exception and the `properties` JSON. Data through `ILogQuery` (Application; Persistence implementation).
  Rows with a transaction id link to its trace.
  - File-tail mode when the Log sink check is not Ok or the gate is not `Ready`: the last 500 lines of the
    newest log file via `ILogFileTail` (Application; Host implementation), a banner "Database log
    unavailable — showing the log file", text filter only.
- `/transactions/{id:guid}/trace`: stage strip (Received ▸ Transcribed ▸ Categorized ▸ Persisted ▸
  Replied; done green, failed red, not reached grey; Transcribed shows "—" for text captures); a timeline
  `+0 ms / +312 ms / +1.4 s` relative to Received, one line per event with its key data; **History** — the
  rows of `transaction_revisions` for the transaction (time, change kind, changed fields). If the log rows
  have been pruned: "Trace expired (logs older than 90 days)"; History still shows. Data through
  `ITransactionTrace` (Application; Persistence implementation).
- All pages require sign-in and follow the render-mode rule. With the gate not `Ready` they show the
  section 1 banner, except `/diagnostics/logs` in file-tail mode.

## 5. Testing

- TDD; every new guard seen red once.
- Fast: gate and backoff on `FakeTimeProvider`; each health check; `HealthReplyFormatter`; router +
  `IsOwnerAsync` (owner → reply; stranger → silence; no owner → silence and `TrySetIfMissingAsync` never
  called; `/health@bot`; ordinary text still captured); `setMyCommands` scope on the captured HTTP body;
  `SecretRedactor`.
- Architecture: `Serilog*` referenced only by Host; no direct `LogXxx(` calls in `src`; health checks do not
  reference AI.
- Database (clone of the test template, filtered to the new classes): sink writes `transaction_id` and
  `properties`; retention prunes by level and keeps fresh rows; `ILogQuery` filters and paging;
  `ITransactionTrace`; Migrations and Backup checks.
- Sentinel: a host on a clone, a failure path whose exception message carries a fake secret registered in
  the secret store; the test reads the **written file and `app_log`** and asserts the marker is absent.
- E2E (Playwright): host on an unreachable database with `MigrateOnStartup=true` stays up, sign-in page
  shows waiting, the log file has the attempts; the health tile; `/diagnostics/logs` filter and file-tail
  fallback; the trace page for a seeded transaction.
- Manual (operator, `ops/RUNBOOK.md`): stop PostgreSQL → start host → "waiting" + file lines → start
  PostgreSQL → host comes up → `/health` in the bot.

## Out of scope

Tier 2 correctness SQL and Tier 3 LLM explanation (Phase 8); FX freshness check; proactive Telegram alerts;
a transactions list page; making the PostgreSQL service Automatic.
