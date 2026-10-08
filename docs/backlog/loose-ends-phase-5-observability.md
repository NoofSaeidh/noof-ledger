---
title: Loose ends from Phase 5 (observability)
status: deferred
area: host
since: 2026-09-25
---
Recorded 2026-09-25 closing Phase 5. The critical and important findings from the Fable 5.1 closing
review were fixed on-branch; these are the minors the operator chose to park rather than fix, plus
two items the spec explicitly deferred.

**Proactive Telegram alerts when a health check turns red.** Today the operator only learns of a
failing check by opening the dashboard, `/diagnostics`, or asking the bot's `/health` — nothing pushes
a message when a check's state changes. Named out of scope by the observability spec (§"Out of
scope"); it would want a debounce (a flapping check should not spam) and a decision about which
checks are worth a push at all.

**M-1 — `SelfLog.Enable` attributes every non-connection Serilog self-log line to the database sink.**
**Closed 2026-09-26 (Phase 6), cross-host half; the message-type half remains, see below.**
`LoggingSetup.cs` still records *any* Serilog internal error that doesn't name a specific host:port (a
locked log file, a console write failure, not only a PostgreSQL batch failure) as a Log sink failure,
so the check can say "logs are in the file only" for the wrong reason. The other half of this item -
`SelfLog` being a process-global listener while `Configure` runs per host, so an orphaned host (the
throwaway one `WebApplicationFactory`'s `HostFactoryResolver` builds and never disposes) could feed a
later, unrelated host's `ILogSinkStatus` - is fixed: `SelfLogOwnership` (Phase 6 final tidy-up, item 3)
tracks the current claim and drops any "Failed to connect to `<host:port>`" message that names a
*different* connection than the current claim's own, closing the cross-host case
`SelfLogSinkFailureTests` flaked on. What remains is purely the message-type gap: a non-connection
failure (naming no host:port at all) still defaults to attributing to whoever is current. Fix: attach
to the PostgreSQL sink's own failure listener if `Serilog.Sinks.Postgresql.Alternative` exposes one,
otherwise filter `SelfLog` text by that sink's type name before recording a failure.

**Fixed (Phase 6 second re-review, R2-1) — `SelfLogOwnership`'s host:port comparison was wrong for any
connection string that named a host rather than an IP literal.** The old `HostPortOf` built the identity
from `NpgsqlConnectionStringBuilder.Host`+`Port` verbatim (`localhost:5432`), but Npgsql's own failure
message always names the *resolved* IP endpoint (`Failed to connect to 127.0.0.1:5432`), never the
configured host name — verified directly against Npgsql 10.0.3. `IdentitiesOf` now resolves the identity
the way Npgsql renders it: an IP literal (v4 or v6, `IPAddress.TryParse`) is used as-is — an IPv6 literal
renders bracketed, `[::1]:5432` — and any other host is resolved once per claim through
`Dns.GetHostAddresses`, keeping every resulting `IPEndPoint`; a Unix-socket path or a failed resolution
carries no identity and falls back to defaulting every failure to the current claim, same as a message
naming no connection at all. `SelfLogOwnershipTests` covers a host name resolving to loopback, an IPv6
literal host, and a foreign endpoint still being rejected.

**New from the same fix.** The endpoint identity is resolved once, in `Claim`, at process startup — not
re-resolved for the life of the process. A remote database host whose DNS answer changes while the host
is running would keep comparing against the address it resolved to at startup, so its failures would be
dropped again, the same silent gap M-1 exists to close. Acceptable for this local-hosting deployment —
`ops/reset-database-auth.ps1` writes `127.0.0.1`, an IP literal with no DNS involved at all — revisit if
the database ever moves off-box to a host name whose address can actually change underneath a running
process.

**M-3 — three near-identical registration entry points for one folder.**
`DiagnosticsRegistration.AddNoofDiagnostics`, `DiagnosticsHostRegistration.AddNoofDiagnosticsHost` and
`HostDiagnosticsRegistration.AddNoofHostDiagnostics(connectionString)` are all called from `Program.cs`
and all live under `Noof.Ledger.Host/Diagnostics`. Harmless today; collapsing them into one
`AddNoofHostDiagnostics(connectionString)` is ordinary tidying whenever that folder is touched next.

**M-10 — the Database health check reads the gate, not PostgreSQL, so it can say "Ready" while
PostgreSQL is down.** `DatabaseHealthCheck` reports whatever `IDatabaseGate.State` was when it last
changed; once `Ready`, it never re-probes, so an outage that starts *after* startup shows "Database —
Ready" in green next to Migrations, Backup and Telegram failing with "Check failed (NpgsqlException)
— see logs" underneath.
Spec-conformant (the design says gate `Ready` → Ok) but confusing to read. A live `SELECT 1` with a
short timeout on every check would be better and is cheap; not built because the gate's own workers
already recover on their own, so nothing operationally depends on this check being live.

**M-11 — `/health`'s command registration is not retried, and `/health@otherbot` is also accepted.**
`setMyCommands` (which registers `/health` in the owner's chat) runs only when the bot token changes;
a transient network failure at that moment is logged and never retried until the next restart. Separately,
the router accepts `/health@anyname`, not just `/health@<this bot's own username>` — harmless for a
single-owner DM bot where nothing else is listening, so left as is rather than plumbing the bot's own
username through for a check that changes nothing observable.

**Paths deliberately left untimed (Phase 5, task V12).** `IOperationTimer` covers the model, speech,
Telegram, worker and health paths named in the verbose logging design, but not: `EfCaptureStore`
(the initial message-received write); the Cancel/Edit/Restore button path (`RecordActionHandler`,
`CorrectionHandler`, `EfRecordEditor`); a separate timing for the revision-append inside
`RevisionLog.AppendAsync` (it is covered only as part of whichever timing wraps its caller); and
model token usage (`IOperationTimer` measures wall-clock time, not tokens — a separate metric).
Add any of these if a slow path shows up that the existing timings do not explain.

**Npgsql does not log through the app's `ILoggerFactory` — checked once, not guarded (Phase 5, V9/V12;
decision O-15 in `docs/decisions/p5-2-phase5-followups.md`).** At database level Debug, after a real
`DbContext` round trip, no `app_log` row carried a source starting with `Npgsql`, so no
`Serilog:MinimumLevel:Override:Npgsql` entry exists. A test asserting "0 rows" could never go red, so
none was kept. After an EF Core or Npgsql upgrade, repeat the check by hand (set the database level
to Debug, load any page that reads the database, query `app_log` for an `Npgsql` source); if rows
appear, adding an override is a decision, not a silent flood.

**Timing rows inside the trace timeline itself were considered and rejected for Phase 5.** Timing
events carry a `TransactionId` but no `Stage`, and `/transactions/{id}/trace` is built only from
Stage rows (`EfTransactionTrace`) — mixing timing rows into that view would clutter the one page meant
to answer "what happened to this message" at a glance, and timing rows only exist at all once the
database log level is at Debug. Instead the trace page links to `/diagnostics/logs` pre-filtered to
the transaction at Debug ("Timings and debug log"). Revisit only if the linked-page detour turns out
to be a real friction point in practice.

**`GroqHttpClientLoggingTests` does not exist.** `AnthropicHttpClientLoggingTests` and
`TelegramHttpClientLoggingTests` each prove `RemoveAllLoggers()` keeps that provider's `HttpClient`
from leaking request/response bodies (which could carry secrets) into the log pipeline; Groq's speech
`HttpClient` has no equivalent test, even though `GroqRegistration` applies the same
`RemoveAllLoggers()` call. Low risk today (nothing in the Groq request path carries an app secret),
but the gap is real and cheap to close whenever `Noof.Ledger.Ai/Groq` is next touched.

**FX freshness check arrives with the FX phase.** The observability spec named this out of scope
because there is no FX rate source yet (`docs/decisions/q4-fx-rate-source.md`) — nothing to check the freshness
of. Add it alongside whichever phase builds currency conversion.

**Playwright cannot drive a native `datetime-local` widget in headless Chromium**, so
`/diagnostics/logs`'s From/To filter is verified through the query layer and the page's markup, not
through a browser interaction test. `FillAsync` sets the DOM value but fires no event Blazor Server's
`@bind:event="oninput"` receives — confirmed with a debug span that stayed empty across `oninput`,
`onchange`, a plain `@bind`, and an explicit bubbling `dispatchEvent`. Coverage instead: `EfLogQueryTests`
proves the SQL/EF filtering (inclusive both ends), and `DiagnosticsPageSourceTests` proves the page
renders `#logs-filter-from`/`#logs-filter-to` and wires them into the query. Revisit if a future
Playwright or Chromium release fixes native datetime input dispatch.

**An intermittent Host auth-flow test flake**, seen twice across roughly six `Host.Tests` runs during
the phase's closing fix pass, never reproduced on an immediate retry, and not connected to any finding
fixed in this phase (a clean 292/292 pass bracketed each sighting). The failing test's own name was not
captured — the run's tail buffer held only request-log noise by the time it was checked. Worth
instrumenting the next time it is seen live rather than chasing from this description.

### Test-infrastructure flakes seen at the Phase 5 close

- **Clone `CREATE`/`DROP DATABASE` timeouts in full runs — closed 2026-09-27 (Phase 6).**
  `CookieModeHostFixture.DropCloneAsync` and the Database-tagged Host.Tests classes hit 120 s
  `DatabaseSettings.ExecuteAdminDdlAsync` read timeouts: once at the Phase 5 close, then 11–23 entries
  per full run during Phase 6 — with a second checkout's suite running, and later with none. Raising
  the admin timeouts to 120 s did not help, and a concurrent checkout was only an amplifier. Sampling
  `pg_stat_activity` during a run showed the drops waiting on `IPC/CheckpointDone` and
  `ProcSignalBarrier` while a single checkpoint took over two minutes. The cause: every `DROP DATABASE`
  forces a checkpoint and waits for it, and under PostgreSQL's default `WAL_LOG` strategy each clone's
  ~300 files go through shared buffers, so every checkpoint had to fsync every clone created since the
  previous one. With 40 live clones, one `CHECKPOINT` took 42.6 s under `WAL_LOG` and 0.17 s under
  `FILE_COPY`, and creating the 40 cost about the same (33 s vs 36 s). `DatabaseSettings` now creates
  every test database `STRATEGY FILE_COPY` (commit `3b04301`, guarded by
  `DatabaseSettingsCloneStrategyTests`). `FILE_COPY` is slower for a clone dropped within seconds —
  a short-lived create/write/drop benchmark favoured `WAL_LOG` (38.5 s vs 62.7 s), because dropping a
  clone forgets its queued fsyncs — but a full run keeps Persistence's clones alive until collection
  teardown, and the whole-run outcome is what was measured: checkpoint sync time over a full run fell
  from 515 s to 42 s, and the slowest sampled DDL from the 120 s ceiling to 4 s. Two consecutive full
  `dotnet test --solution` runs then passed first time. The cost is time: Persistence takes 13 min alone
  and 14–15 min inside a full run under `FILE_COPY`, against about 12 min under `WAL_LOG` on the same
  clean server — the per-test database strategy entry above is where to win that back.
  The advisory lock around clone DDL that was once proposed here is not needed. Leftover
  clones from interrupted runs are harmless but still worth `.\run.ps1 clean-test-dbs`: 200 had built
  up, mostly from these timed-out drops.
- **`DatabaseLogLevelDbTests.A_stored_Off_level_drops_the_next_hosts_own_startup_burst...` failed once
  in five runs** (2026-09-26, same slow server): EF's "No migrations were applied" row reached
  `app_log` despite a stored Off. `ReadyGatedBufferSink` gives the stored-level load 5 s
  (`LoadTimeout`) before it flushes the startup buffer at the compiled-in default, so a load slower
  than that lets the startup burst through. It passed on an immediate rerun and was not changed in
  Phase 6. It failed once more on 2026-09-26 in a full run with the checkpoint stalls above, and
  passed in every run after the `FILE_COPY` fix. It recurred on 2026-09-27 in the full run after
  Copilot round 7, on a healthy server under full-suite load, with the same row, then passed 3 of 3
  when re-run alone. So this is a real timing race, not server degradation: the buffer times out to
  the compiled-in default before the stored level arrives. The fix is to make `ReadyGatedBufferSink`
  wait for the stored level, or for a definite "none stored", instead of flushing at a 5 s timeout.
  Through Phase 8a (2026-10) it failed on every local `.\run.ps1 test db`, which stops that run before the Demo
  database classes (`run-test-db-filter-reaches-only-persistence.md`); CI does not run it.
- **`SecretRedactionSentinelTests` hits an `IOException` in its cleanup — closed 2026-09-26 (Phase 6)**,
  not its assertions:
  `Directory.Delete(logDirectory)` in the `finally` runs while the host's file sink still holds
  `noof-ledger-<date>.log`. Seen once at the Phase 5 close; on 2026-09-26 it reproduced 2 runs in 3
  when the class ran together with `SecretRedactorTests` straight through the xUnit exe, and passed
  alone and in every full `run.ps1 test all`. Parked by the operator as test-only (no effect on the
  running app); the fix is to dispose the factory before deleting, or retry the delete briefly.
  Phase 6 took the second option: the `finally` now deletes through `TestHostLogging.DeleteBestEffortAsync`, the
  same bounded retry `HostProcess.DeleteBestEffortAsync` uses for the out-of-process E2E host.
- **Configuration-added Serilog sinks did not reproduce through `WebApplicationFactory`** while they did
  in an isolated logger (follow-up review I-1). `ReadFrom.Configuration` is gone, so the risk is closed,
  but the reason the hosted repro stayed silent was never found.

### Cadence and timeout constants still in code — deferred 2026-09-26

PR #1's review asked for hard-coded paths to move to `appsettings.json`; paths, file-log limits,
per-level retention, every backup setting and the DataProtection key directory did. These stayed in
C# on purpose, because they are how often or how long, not where or how much, and nobody has needed
to change one: the worker poll intervals (`SecretSnapshotRefreshWorker`, `LogRetentionWorker`), the
health checks' staleness windows (Disk, Backup, Log sink, Telegram), `TelegramBackoff`'s caps, the
5 s per-check timeout in `SystemHealth`, and `pg_dump`'s `PGCONNECT_TIMEOUT`. Move one when a real
reason to tune it appears, through the options pattern the rest already use.

Phase 6 added its own three, swept for the Phase 5 convention pass and left in code for the same
reason: the `suf-purs` `HttpClient`'s 15 s timeout to the Tax Administration (`ReceiptsRegistration`),
the 5 s deferral `CategorizationWorker.TryRouteToReceiptAsync` waits before re-checking a receipt
that is still being extracted (`ReceiptExtractionPendingDelay`), and the 40-line cap on a receipt's
detailed echo (`RecordEcho.MaxDetailedReceiptLines`, a display decision, not a resource setting).

### `transaction_revisions` retention — deferred 2026-09-26 (decision (e))

Log settings (`/diagnostics/logs/settings`) gives the operator per-level retention for `app_log`, but
deliberately not for `transaction_revisions` — the append-only history a trace page's History section
reads. That table has no retention at all today: every revision, forever. Doing this properly needs,
first, the row-level trigger (`transaction_revisions_append_only_guard`, which refuses `UPDATE` and
`DELETE`; a sibling `transaction_revisions_no_truncate` separately refuses `TRUNCATE`) relaxed to
allow a retention job's own `DELETE`, which is a bigger decision than this feature's scope: it is the
one guarantee that table currently makes, and loosening it for one more caller is not something to do
as a side effect of a settings screen. Revisit alongside a real reason to prune old revisions (disk
growth becomes a real problem, or a GDPR-shaped request to actually forget something).
(The drift `.claude/rules/database.md` warns about — a guard trigger added to an already-applied
migration never taking effect on `noof_ledger` or the test template — was `merchant_aliases_no_truncate`
in commit `01b4961`, a different table; that story does not apply to `transaction_revisions`, whose
guards shipped in their own migration from the start.)

### Copilot review items parked by the operator — 2026-09-26

Copilot's second pass on PR #1 raised five items. The template-borne secret leak was fixed (a
library logging an interpolated string puts the text in the message template itself;
`SecretRedactor` now redacts the template too). Two described decisions the operator had already
made (Debug retention of 1 day; no file-tail fallback on the Logs page). The operator parked the
other two as not mattering in real use:

- **Secrets shorter than 8 characters are never redacted** (`SecretSnapshot.MinimumSecretLength`).
  Deliberate: a short value such as `1234` would be replaced everywhere it occurs in a log. The only
  secret this can realistically hit is a short database password — keep that password at 8+
  characters and the gap is closed. Lowering the cutoff just for the database password is the fix if
  that is ever not an option.
- **Most `WebApplicationFactory` fixtures still use the real DataProtection key ring**
  (`%LOCALAPPDATA%\NoofLedger\dp-keys`); only three call `UseTempKeyRingDirectory()`. A fixture only
  writes there when no valid default key exists — about once per 90-day key lifetime — and a key it
  creates is a valid member of the same DPAPI-protected ring, so the running app is unaffected. The
  fix mirrors the log directory: apply `TestHostDataProtection` everywhere and add an architecture
  guard like `TestHostLogDirectoryTests`.
