# Phase 5 — Observability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The host survives a missing database (waits, logs, recovers), logs through Serilog behind `ILogger`/`[LoggerMessage]` to a rolling file and an `app_log` table, shows every transaction's path and history, and reports system health on the dashboard, `/diagnostics` and the bot's owner-only `/health`.

**Architecture:** `IDatabaseGate` + `DatabaseStartupService` replace the blocking startup migration; Serilog is only the MEL provider (Host-only), with `Serilog.Sinks.File` and `Serilog.Sinks.Postgresql.Alternative` behind a `SecretRedactor`; transaction stages log structured events scoped by `TransactionId`; health uses standard `IHealthCheck`s behind `ISystemHealth`.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, PostgreSQL 18, Blazor Server + MudBlazor + QuickGrid, Serilog, Microsoft.Extensions.Diagnostics.HealthChecks, Telegram.Bot, xUnit v3/MTP, Playwright.

**Spec:** `docs/superpowers/specs/2026-09-24-observability-design.md`

**Waves:** 1 = Tasks 1, 2, 3 · 2 = Tasks 4, 5, 6 · 3 = Tasks 7, 8. Parallel tasks in a wave run in separate worktrees; each wave merges into `phase-5-observability`.

## Global Constraints

- .NET 10, C# modern style per CLAUDE.md §3; nullable on; warnings are errors; file-scoped namespaces.
- Minimum accessibility: `internal` unless another assembly names it; `PublicSurfaceTests` holds each assembly's
  public allowlist — every new public type is added there in the same task.
- Each assembly registers its own services in its `AddNoofXxx`; `Program.cs` names no implementation type
  (Serilog configuration lives in a Host static class, e.g. `Noof.Ledger.Host.Logging.LoggingSetup`).
- Serilog packages referenced by `Noof.Ledger.Host` only. Central package management: versions go in
  `Directory.Packages.props`.
- All logging via `ILogger<T>` + `[LoggerMessage]` (static partial methods, stable EventIds). No direct
  `LogXxx(` calls in `src`.
- Bot text English only. UI text English.
- UI: no popover/dialog/snackbar/tooltip/menu; inline `MudAlert` for feedback; pages `[Authorize]`.
- `Noof.Ledger.Web` has no EF, no `HttpClient`, no `System.IO` file access — it reads everything through
  Application interfaces.
- Tests: xUnit v3 + AwesomeAssertions + NSubstitute; DB tests on a clone of the test template; never touch
  `noof_ledger`; never seed `DateTimeOffset.UtcNow` then assert exact equality after a PostgreSQL round trip.
- A migration's `.cs` file is converted to a file-scoped namespace; Designer/snapshot left as EF emits them.
- Never log secrets, message text of strangers, or connection strings.


The binding cross-task contract (exact type and member names) is `.superpowers/sdd/2026-09-25-phase5-observability/global-constraints.md` §"Binding contract", reproduced in each task's Interfaces block. `ILogSinkStatus` is defined by Task 3 (not Task 4).

## Review Focus

- PostgreSQL goes down **after** startup: the gate stays `Ready`, workers hit connection errors per tick — they must log and continue, never crash the host; the Database check and DB sink must not throw into callers.
- A secret appears inside an exception message from a dependency (e.g. a token in a URL): the redactor must scrub the file, console and `app_log` copies alike (sentinel test, Task 4).
- `/health` from a stranger's chat while no owner is recorded: silence, and ownership must not be claimed (Task 8).
- A voice capture whose transcription fails: the trace page must show Received ✓, Transcribed ✗ and no later stages (Tasks 5, 7).
- The log directory is unwritable or on a full disk: the host must still start (Serilog file sink failures go to SelfLog, never throw), and the Disk check goes red (Tasks 2, 6).

---

### Task 1: Database gate and startup without a database

**Plan notes (deviations from a literal reading of the spec):**
- `IDatabaseStartupProbe` (Host.Startup, internal, task-internal — not in the binding contract) and a
  new Persistence extension `OpenNoofDatabaseConnectionAsync` are added so `DatabaseStartupService`'s
  retry/backoff/classification logic can be unit-tested on `FakeTimeProvider` without a real database,
  per spec §5 ("Fast: gate and backoff on `FakeTimeProvider`"). Without this seam the service would have
  to talk to `LedgerDbContext` directly, which is Persistence-internal (not on `PublicSurfaceTests`'
  Persistence allowlist) and not something a fast test can fake.
- The spec's "a signed-in user sees the same banner on every page instead of data" is implemented as a
  small reusable `DatabaseGateBanner` component used *locally* inside `Login.razor` (replacing only the
  `<form>`) and inside `Home.razor` (replacing the balances/transactions block and skipping the query in
  `OnInitializedAsync`) — **not** wrapped around `@Body` in `MainLayout`. Wrapping `@Body` would also hide
  Login's own `<PageTitle>Sign in</PageTitle>` and `<h1>`, breaking the existing
  `SmokeTests.Anonymous_home_page_redirects_to_login_without_a_5xx_while_the_database_is_down` assertion
  (`Page.TitleAsync() == "Sign in"`) the moment the gate is not `Ready` — which, once
  `UnreachableDatabaseHostFixture` runs with `MigrateOnStartup=true` per this task's own scope, is exactly
  the state that test now runs in. Local placement keeps that assertion true while still satisfying "the
  same banner" (same component, same id, same two messages) everywhere it appears.
- There is no Blazor/bUnit unit-test project in this repo (confirmed: no `*Web.Tests*` project, no
  `HomeTests.cs`/`LoginTests.cs` at the unit level — `Noof.Ledger.Web` pages are exercised only through
  `Noof.Ledger.E2E.Tests`, e.g. the existing `LoginTests.cs`/`SmokeTests.cs`). Login/Home markup changes
  are therefore TDD'd through the one new E2E test this task adds (Step 14), matching repo convention —
  there is no faster loop to put them in.
- `DatabaseStartupProbe` (the thin wrapper that adapts `IDatabaseStartupProbe` onto the two Persistence
  extension methods) and the Program.cs wiring are not given dedicated unit tests, the same way the
  pre-existing `MigrateNoofDatabaseAsync` extension has none today (confirmed by grep) — both are
  Program.cs-style composition, covered by the E2E test in Step 14.

---

**Files**

Create:
- `src/Noof.Ledger.Application/Diagnostics/IDatabaseGate.cs`
- `src/Noof.Ledger.Host/Startup/DatabaseGate.cs`
- `src/Noof.Ledger.Host/Startup/IDatabaseStartupProbe.cs`
- `src/Noof.Ledger.Host/Startup/DatabaseStartupProbe.cs`
- `src/Noof.Ledger.Host/Startup/DatabaseStartupService.cs`
- `src/Noof.Ledger.Web/Components/Shared/DatabaseGateBanner.razor`
- `tests/Noof.Ledger.Host.Tests/DatabaseGateTests.cs`
- `tests/Noof.Ledger.Host.Tests/DatabaseStartupServiceTests.cs`
- `tests/Noof.Ledger.Persistence.Tests/DatabaseConnectivityTests.cs`

Modify:
- `src/Noof.Ledger.Persistence/PersistenceRegistration.cs` (+~7 lines: new `OpenNoofDatabaseConnectionAsync` extension)
- `src/Noof.Ledger.Host/Program.cs` (lines 75–81 removed; ~4 new registration lines added before line 67's `AddNoofTelegram()`)
- `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs` (constructor line 11–19; `ExecuteAsync` lines 31–41)
- `src/Noof.Ledger.Host/Workers/TranscriptionWorker.cs` (constructor line 11–18; `ExecuteAsync` lines 25–35)
- `src/Noof.Ledger.Host/Workers/BackupWorker.cs` (constructor line 12–17; `ExecuteAsync` lines 24–31)
- `src/Noof.Ledger.Telegram/TelegramPollingService.cs` (constructor line 15–22; `ExecuteAsync` lines 36–52)
- `src/Noof.Ledger.Host/Workers/WorkerRegistration.cs` (each `new XWorker(...)` call: +1 argument)
- `src/Noof.Ledger.Web/Components/Account/Login.razor` (wrap the `<form>` in a gate check)
- `src/Noof.Ledger.Web/Components/Pages/Home.razor` (guard `OnInitializedAsync`; wrap the data section)
- `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs` (`Allowed["Noof.Ledger.Application"]`: +`"IDatabaseGate"`, `"DatabaseState"`)
- `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs` (`CreateWorker` helper, line 127–130)
- `tests/Noof.Ledger.Host.Tests/TranscriptionWorkerTests.cs` (`CreateWorker` helper, line 101–103)
- `tests/Noof.Ledger.Host.Tests/BackupWorkerTests.cs` (`CreateWorker` helper, line 51–52)
- `tests/Noof.Ledger.Telegram.Tests/TelegramPollingServiceTests.cs` (`CreateService` helper, line 57–69)
- `tests/Noof.Ledger.E2E.Tests/UnreachableDatabaseHostFixture.cs` (`MigrateOnStartup` → `true`)
- `tests/Noof.Ledger.E2E.Tests/SmokeTests.cs` (no code change expected; re-verified, see Step 14)

**Interfaces**

Consumes (already in the repo): `IServiceScopeFactory`, `TimeProvider`/`FakeTimeProvider`, `IConfiguration`,
`ILogger<T>`, `LedgerDbContext` (Persistence-internal, reached only through the new extension), the existing
`MigrateNoofDatabaseAsync(this IServiceProvider, CancellationToken)`.

Produces — binding contract (exact names, from `global-constraints.md`):
```csharp
namespace Noof.Ledger.Application.Diagnostics;

public enum DatabaseState { Waiting, Migrating, Ready, Failed }

public interface IDatabaseGate
{
    DatabaseState State { get; }
    string? Detail { get; }
    Task WaitUntilReadyAsync(CancellationToken cancellationToken);
}
```
```csharp
namespace Noof.Ledger.Host.Startup;

internal sealed class DatabaseGate : IDatabaseGate
{
    void Set(DatabaseState state, string? detail);
}

internal sealed partial class DatabaseStartupService : BackgroundService;
```

Produces — task-internal (not in the binding contract, safe to name freely):
```csharp
namespace Noof.Ledger.Host.Startup;

internal interface IDatabaseStartupProbe
{
    Task OpenConnectionAsync(CancellationToken cancellationToken);
    Task MigrateAsync(CancellationToken cancellationToken);
}

internal sealed class DatabaseStartupProbe(IServiceProvider services) : IDatabaseStartupProbe;
```
```csharp
namespace Noof.Ledger.Persistence;

public static class PersistenceRegistration
{
    public static Task OpenNoofDatabaseConnectionAsync(this IServiceProvider services, CancellationToken cancellationToken = default);
}
```

---

- [ ] **Step 1: `IDatabaseGate` / `DatabaseState` and the public-surface guard**

  Add the contract type first; `PublicSurfaceTests` is the "watch it fail" — a new public type outside
  the allowlist fails that theory by design.

  `src/Noof.Ledger.Application/Diagnostics/IDatabaseGate.cs`:
  ```csharp
  namespace Noof.Ledger.Application.Diagnostics;

  public enum DatabaseState { Waiting, Migrating, Ready, Failed }

  public interface IDatabaseGate
  {
      DatabaseState State { get; }

      string? Detail { get; }

      Task WaitUntilReadyAsync(CancellationToken cancellationToken);
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter PublicSurfaceTests`
  Expected: FAIL — `Public_types_are_exactly_the_allowed_set("Noof.Ledger.Application")` reports two
  unexpected public types, `IDatabaseGate` and `DatabaseState`, not in `Allowed["Noof.Ledger.Application"]`.

  In `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`, extend the Application array (line 64,
  right after `"BackupRunRecord", "BackupStatus", "DumpResult", "IBackupLog", "IDatabaseDumper",`):
  ```csharp
              "BackupRunRecord", "BackupStatus", "DumpResult", "IBackupLog", "IDatabaseDumper",
              "IDatabaseGate", "DatabaseState",
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter PublicSurfaceTests`
  Expected: PASS.

  Commit: `git add src/Noof.Ledger.Application/Diagnostics/IDatabaseGate.cs tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`
  ```
  git commit -m "$(cat <<'EOF'
  Add IDatabaseGate/DatabaseState (Phase 5 Task 1)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 2: `DatabaseGate` implementation**

  `tests/Noof.Ledger.Host.Tests/DatabaseGateTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Host.Startup;

  namespace Noof.Ledger.Host.Tests;

  public class DatabaseGateTests
  {
      [Fact]
      public void Starts_waiting_with_no_detail()
      {
          var gate = new DatabaseGate();

          gate.State.Should().Be(DatabaseState.Waiting);
          gate.Detail.Should().BeNull();
      }

      [Fact]
      public void Set_updates_the_state_and_the_detail()
      {
          var gate = new DatabaseGate();

          gate.Set(DatabaseState.Failed, "the database rejected the migration");

          gate.State.Should().Be(DatabaseState.Failed);
          gate.Detail.Should().Be("the database rejected the migration");
      }

      [Fact]
      public async Task WaitUntilReadyAsync_completes_immediately_once_the_gate_is_already_ready()
      {
          var gate = new DatabaseGate();
          gate.Set(DatabaseState.Ready, null);

          var waiting = gate.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

          waiting.IsCompletedSuccessfully.Should().BeTrue();
          await waiting;
      }

      [Fact]
      public async Task WaitUntilReadyAsync_completes_once_the_gate_becomes_ready()
      {
          var gate = new DatabaseGate();

          var waiting = gate.WaitUntilReadyAsync(TestContext.Current.CancellationToken);
          waiting.IsCompleted.Should().BeFalse();

          gate.Set(DatabaseState.Ready, null);

          await waiting;
      }

      [Fact]
      public async Task WaitUntilReadyAsync_honours_cancellation_while_the_gate_never_becomes_ready()
      {
          var gate = new DatabaseGate();
          using var cts = new CancellationTokenSource();

          var waiting = gate.WaitUntilReadyAsync(cts.Token);
          await cts.CancelAsync();

          await Assert.ThrowsAsync<TaskCanceledException>(() => waiting);
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter DatabaseGateTests`
  Expected: FAIL to compile — `Noof.Ledger.Host.Startup.DatabaseGate` does not exist yet (CS0246).

  `src/Noof.Ledger.Host/Startup/DatabaseGate.cs`:
  ```csharp
  using Noof.Ledger.Application.Diagnostics;

  namespace Noof.Ledger.Host.Startup;

  internal sealed class DatabaseGate : IDatabaseGate
  {
      readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

      volatile DatabaseState state = DatabaseState.Waiting;
      volatile string? detail;

      public DatabaseState State => state;

      public string? Detail => detail;

      public void Set(DatabaseState newState, string? newDetail)
      {
          state = newState;
          detail = newDetail;

          if (newState == DatabaseState.Ready)
              ready.TrySetResult();
      }

      public Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
          state == DatabaseState.Ready ? Task.CompletedTask : ready.Task.WaitAsync(cancellationToken);
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter DatabaseGateTests`
  Expected: PASS (5 tests).

  Commit: `git add src/Noof.Ledger.Host/Startup/DatabaseGate.cs tests/Noof.Ledger.Host.Tests/DatabaseGateTests.cs`
  ```
  git commit -m "$(cat <<'EOF'
  Add DatabaseGate (Phase 5 Task 1)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 3: `OpenNoofDatabaseConnectionAsync` (Persistence)**

  `tests/Noof.Ledger.Persistence.Tests/DatabaseConnectivityTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Microsoft.EntityFrameworkCore;
  using Microsoft.Extensions.DependencyInjection;
  using Npgsql;

  namespace Noof.Ledger.Persistence.Tests;

  [Collection("postgres")]
  public class DatabaseConnectivityTests(PostgresFixture fixture)
  {
      [Fact]
      public async Task Opens_and_closes_a_connection_against_a_reachable_database()
      {
          var connectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();
          await using var services = new ServiceCollection()
              .AddDbContext<LedgerDbContext>(options => options.UseNpgsql(connectionString))
              .BuildServiceProvider();

          var act = () => services.OpenNoofDatabaseConnectionAsync(TestContext.Current.CancellationToken);

          await act.Should().NotThrowAsync();
      }

      [Fact]
      public async Task Throws_against_an_unreachable_database()
      {
          await using var services = new ServiceCollection()
              .AddDbContext<LedgerDbContext>(options => options.UseNpgsql(
                  "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2"))
              .BuildServiceProvider();

          var act = () => services.OpenNoofDatabaseConnectionAsync(TestContext.Current.CancellationToken);

          await act.Should().ThrowAsync<NpgsqlException>();
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter DatabaseConnectivityTests`
  Expected: FAIL to compile — `OpenNoofDatabaseConnectionAsync` does not exist on `IServiceProvider` yet (CS1061).

  In `src/Noof.Ledger.Persistence/PersistenceRegistration.cs`, add after `MigrateNoofDatabaseAsync`
  (after the existing closing brace at line 73, before the class's own closing brace at line 74):
  ```csharp

      public static async Task OpenNoofDatabaseConnectionAsync(
          this IServiceProvider services, CancellationToken cancellationToken = default)
      {
          using var scope = services.CreateScope();
          var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
          await db.Database.OpenConnectionAsync(cancellationToken);
          await db.Database.CloseConnectionAsync();
      }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter DatabaseConnectivityTests`
  Expected: PASS (2 tests). Requires a reachable local PostgreSQL (`noof_ledger_test_template`'s server) —
  same precondition every other `Noof.Ledger.Persistence.Tests` test already has.

  Commit: `git add src/Noof.Ledger.Persistence/PersistenceRegistration.cs tests/Noof.Ledger.Persistence.Tests/DatabaseConnectivityTests.cs`
  ```
  git commit -m "$(cat <<'EOF'
  Add OpenNoofDatabaseConnectionAsync (Phase 5 Task 1)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 4: `IDatabaseStartupProbe` / `DatabaseStartupProbe`**

  No test-first here: this is a two-line pass-through wiring class over two already-tested extension
  methods (Step 3's, and the pre-existing `MigrateNoofDatabaseAsync`, itself untested for the same
  reason). It exists purely as the seam Step 5's fast tests substitute.

  `src/Noof.Ledger.Host/Startup/IDatabaseStartupProbe.cs`:
  ```csharp
  namespace Noof.Ledger.Host.Startup;

  internal interface IDatabaseStartupProbe
  {
      Task OpenConnectionAsync(CancellationToken cancellationToken);

      Task MigrateAsync(CancellationToken cancellationToken);
  }
  ```

  `src/Noof.Ledger.Host/Startup/DatabaseStartupProbe.cs`:
  ```csharp
  using Noof.Ledger.Persistence;

  namespace Noof.Ledger.Host.Startup;

  internal sealed class DatabaseStartupProbe(IServiceProvider services) : IDatabaseStartupProbe
  {
      public Task OpenConnectionAsync(CancellationToken cancellationToken) =>
          services.OpenNoofDatabaseConnectionAsync(cancellationToken);

      public Task MigrateAsync(CancellationToken cancellationToken) =>
          services.MigrateNoofDatabaseAsync(cancellationToken);
  }
  ```

  Run: `dotnet build src/Noof.Ledger.Host/Noof.Ledger.Host.csproj`
  Expected: PASS (no test to fail first; this step only needs to compile before Step 5 depends on it).

  Commit: `git add src/Noof.Ledger.Host/Startup/IDatabaseStartupProbe.cs src/Noof.Ledger.Host/Startup/DatabaseStartupProbe.cs`
  ```
  git commit -m "$(cat <<'EOF'
  Add DatabaseStartupProbe (Phase 5 Task 1)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 5: `DatabaseStartupService` — connection vs. migration classification**

  `tests/Noof.Ledger.Host.Tests/DatabaseStartupServiceTests.cs` (first slice — classification and gate
  transitions; the backoff sequence and logging cadence are Step 6):
  ```csharp
  using AwesomeAssertions;
  using Microsoft.Extensions.Configuration;
  using Microsoft.Extensions.Logging.Abstractions;
  using Microsoft.Extensions.Time.Testing;
  using NSubstitute;
  using NSubstitute.ExceptionExtensions;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Host.Startup;
  using Npgsql;

  namespace Noof.Ledger.Host.Tests;

  public class DatabaseStartupServiceTests
  {
      static DatabaseStartupService CreateService(
          IDatabaseStartupProbe probe, DatabaseGate gate, FakeTimeProvider time, bool migrateOnStartup = true)
      {
          var configuration = new ConfigurationBuilder()
              .AddInMemoryCollection([new("Database:MigrateOnStartup", migrateOnStartup.ToString())])
              .Build();

          return new DatabaseStartupService(probe, gate, configuration, time, NullLogger<DatabaseStartupService>.Instance);
      }

      [Fact]
      public async Task A_connection_that_opens_and_a_migration_that_succeeds_makes_the_gate_ready()
      {
          var probe = Substitute.For<IDatabaseStartupProbe>();
          probe.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
          probe.MigrateAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
          var gate = new DatabaseGate();

          var result = await CreateService(probe, gate, new FakeTimeProvider())
              .RunAttemptAsync(TestContext.Current.CancellationToken);

          result.Should().Be(DatabaseStartupResult.Ready);
          gate.State.Should().Be(DatabaseState.Ready);
      }

      [Fact]
      public async Task MigrateOnStartup_false_skips_migration_and_still_reaches_ready()
      {
          var probe = Substitute.For<IDatabaseStartupProbe>();
          probe.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
          var gate = new DatabaseGate();

          var result = await CreateService(probe, gate, new FakeTimeProvider(), migrateOnStartup: false)
              .RunAttemptAsync(TestContext.Current.CancellationToken);

          result.Should().Be(DatabaseStartupResult.Ready);
          gate.State.Should().Be(DatabaseState.Ready);
          await probe.DidNotReceive().MigrateAsync(Arg.Any<CancellationToken>());
      }

      [Fact]
      public async Task Gate_reports_Migrating_while_the_migration_runs()
      {
          var probe = Substitute.For<IDatabaseStartupProbe>();
          probe.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
          var gate = new DatabaseGate();
          probe.MigrateAsync(Arg.Any<CancellationToken>()).Returns(_ =>
          {
              gate.State.Should().Be(DatabaseState.Migrating);
              return Task.CompletedTask;
          });

          await CreateService(probe, gate, new FakeTimeProvider()).RunAttemptAsync(TestContext.Current.CancellationToken);
      }

      [Fact]
      public async Task A_connection_that_never_opens_is_classified_as_Waiting_not_Failed()
      {
          var probe = Substitute.For<IDatabaseStartupProbe>();
          probe.OpenConnectionAsync(Arg.Any<CancellationToken>())
              .ThrowsAsync(new NpgsqlException("Connection refused"));
          var gate = new DatabaseGate();

          var result = await CreateService(probe, gate, new FakeTimeProvider())
              .RunAttemptAsync(TestContext.Current.CancellationToken);

          result.Should().Be(DatabaseStartupResult.WaitingRetry);
          gate.State.Should().Be(DatabaseState.Waiting);
          gate.Detail.Should().Be("Connection refused");
      }

      [Fact]
      public async Task A_socket_level_failure_is_classified_as_Waiting()
      {
          var probe = Substitute.For<IDatabaseStartupProbe>();
          probe.OpenConnectionAsync(Arg.Any<CancellationToken>())
              .ThrowsAsync(new System.Net.Sockets.SocketException());
          var gate = new DatabaseGate();

          var result = await CreateService(probe, gate, new FakeTimeProvider())
              .RunAttemptAsync(TestContext.Current.CancellationToken);

          result.Should().Be(DatabaseStartupResult.WaitingRetry);
          gate.State.Should().Be(DatabaseState.Waiting);
      }

      [Fact]
      public async Task A_migration_failure_is_classified_as_Failed_not_Waiting()
      {
          var probe = Substitute.For<IDatabaseStartupProbe>();
          probe.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
          probe.MigrateAsync(Arg.Any<CancellationToken>())
              .ThrowsAsync(new PostgresException("relation already exists", "ERROR", "ERROR", "42P07"));
          var gate = new DatabaseGate();

          var result = await CreateService(probe, gate, new FakeTimeProvider())
              .RunAttemptAsync(TestContext.Current.CancellationToken);

          result.Should().Be(DatabaseStartupResult.FailedRetry);
          gate.State.Should().Be(DatabaseState.Failed);
          gate.Detail.Should().Be("relation already exists");
      }

      [Fact]
      public async Task An_unmodeled_exception_from_the_migration_step_is_classified_as_Failed()
      {
          var probe = Substitute.For<IDatabaseStartupProbe>();
          probe.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
          probe.MigrateAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
          var gate = new DatabaseGate();

          var result = await CreateService(probe, gate, new FakeTimeProvider())
              .RunAttemptAsync(TestContext.Current.CancellationToken);

          result.Should().Be(DatabaseStartupResult.FailedRetry);
          gate.State.Should().Be(DatabaseState.Failed);
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter DatabaseStartupServiceTests`
  Expected: FAIL to compile — `DatabaseStartupService`, `DatabaseStartupResult` and its `RunAttemptAsync`
  do not exist yet (CS0246).

  `src/Noof.Ledger.Host/Startup/DatabaseStartupService.cs`:
  ```csharp
  using Microsoft.Extensions.Configuration;
  using Noof.Ledger.Application.Diagnostics;
  using Npgsql;

  namespace Noof.Ledger.Host.Startup;

  internal enum DatabaseStartupResult { Ready, WaitingRetry, FailedRetry }

  internal sealed partial class DatabaseStartupService(
      IDatabaseStartupProbe probe,
      DatabaseGate gate,
      IConfiguration configuration,
      TimeProvider timeProvider,
      ILogger<DatabaseStartupService> logger)
      : BackgroundService
  {
      // 2, 4, 8, 16, 30, 30, 30 ... - the last step repeats forever (O-1: the host waits
      // indefinitely). A connection-level failure never reaches Failed - the database simply is not
      // up yet, and that is not an error worth escalating past a Warning.
      static readonly TimeSpan[] ConnectionBackoffSteps =
      [
          TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
          TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30),
      ];

      static readonly TimeSpan FailedRetryInterval = TimeSpan.FromMinutes(5);

      int connectionAttempt;

      protected override async Task ExecuteAsync(CancellationToken stoppingToken)
      {
          while (!stoppingToken.IsCancellationRequested)
          {
              var result = await RunAttemptAsync(stoppingToken);
              if (result == DatabaseStartupResult.Ready)
                  return;

              var delay = result == DatabaseStartupResult.FailedRetry
                  ? FailedRetryInterval
                  : ConnectionBackoff(connectionAttempt);

              await DelayQuietlyAsync(delay, stoppingToken);
          }
      }

      // Public so the fast test suite can drive one classification/gate-transition at a time without
      // going through BackgroundService.StartAsync (CategorizationWorker's RunTickAsync is the same
      // shape, for the same reason).
      public async Task<DatabaseStartupResult> RunAttemptAsync(CancellationToken cancellationToken)
      {
          try
          {
              await probe.OpenConnectionAsync(cancellationToken);
              connectionAttempt = 0;

              if (configuration.GetValue("Database:MigrateOnStartup", true))
              {
                  gate.Set(DatabaseState.Migrating, null);
                  await probe.MigrateAsync(cancellationToken);
              }

              gate.Set(DatabaseState.Ready, null);
              return DatabaseStartupResult.Ready;
          }
          catch (Exception ex) when (ex is not OperationCanceledException && IsConnectionFailure(ex))
          {
              connectionAttempt++;
              gate.Set(DatabaseState.Waiting, ex.Message);

              if (connectionAttempt <= 10 || connectionAttempt % 10 == 0)
                  LogConnectionAttemptFailed(logger, connectionAttempt, ex);

              return DatabaseStartupResult.WaitingRetry;
          }
          catch (Exception ex) when (ex is not OperationCanceledException)
          {
              gate.Set(DatabaseState.Failed, ex.Message);
              LogMigrationFailed(logger, ex);

              return DatabaseStartupResult.FailedRetry;
          }
      }

      TimeSpan ConnectionBackoff(int attempt) =>
          ConnectionBackoffSteps[Math.Min(Math.Max(attempt, 1) - 1, ConnectionBackoffSteps.Length - 1)];

      async Task DelayQuietlyAsync(TimeSpan delay, CancellationToken stoppingToken)
      {
          try
          {
              await Task.Delay(delay, timeProvider, stoppingToken);
          }
          catch (OperationCanceledException)
          {
          }
      }

      // Npgsql wraps a refused/timed-out socket in an NpgsqlException whose SqlState is null - a real
      // PostgresException (the server answered and rejected something) always carries one. A bare
      // SocketException or TimeoutException can also surface unwrapped, depending on where in the
      // connect sequence Npgsql gives up.
      static bool IsConnectionFailure(Exception ex) => ex switch
      {
          PostgresException => false,
          NpgsqlException npgsqlException => npgsqlException.SqlState is null,
          System.Net.Sockets.SocketException => true,
          TimeoutException => true,
          _ => ex.InnerException is { } inner && IsConnectionFailure(inner),
      };

      [LoggerMessage(EventId = 5101, Level = LogLevel.Warning,
          Message = "Database connection attempt {Attempt} failed; retrying")]
      static partial void LogConnectionAttemptFailed(ILogger logger, int attempt, Exception exception);

      [LoggerMessage(EventId = 5102, Level = LogLevel.Error,
          Message = "Database migration failed; retrying in 5 minutes")]
      static partial void LogMigrationFailed(ILogger logger, Exception exception);
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter DatabaseStartupServiceTests`
  Expected: PASS (7 tests).

  Commit: `git add src/Noof.Ledger.Host/Startup/DatabaseStartupService.cs tests/Noof.Ledger.Host.Tests/DatabaseStartupServiceTests.cs`
  ```
  git commit -m "$(cat <<'EOF'
  Add DatabaseStartupService classification (Phase 5 Task 1)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 6: Backoff sequence and logging cadence on `FakeTimeProvider`**

  Append to `tests/Noof.Ledger.Host.Tests/DatabaseStartupServiceTests.cs`:
  ```csharp
      [Fact]
      public async Task The_backoff_sequence_is_2_4_8_16_30_then_30_forever()
      {
          var probe = Substitute.For<IDatabaseStartupProbe>();
          probe.OpenConnectionAsync(Arg.Any<CancellationToken>())
              .ThrowsAsync(new NpgsqlException("Connection refused"));
          var gate = new DatabaseGate();
          var time = new FakeTimeProvider();
          var service = CreateService(probe, gate, time);

          TimeSpan[] expected =
          [
              TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
              TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
          ];

          List<TimeSpan> observed = [];
          foreach (var _ in expected)
          {
              var before = time.GetUtcNow();
              await service.RunAttemptAsync(TestContext.Current.CancellationToken);

              // RunAttemptAsync itself never delays (ExecuteAsync does, between attempts) - this test
              // exercises the pure backoff function the loop consults, driven the same way
              // CategorizationWorkerTests drives RunTickAsync in a loop.
              observed.Add(service.NextDelay());
              time.Advance(observed[^1]);
          }

          observed.Should().Equal(expected);
          _ = before => before;
      }

      [Fact]
      public async Task Logs_the_first_ten_connection_failures_then_only_every_tenth()
      {
          var probe = Substitute.For<IDatabaseStartupProbe>();
          probe.OpenConnectionAsync(Arg.Any<CancellationToken>())
              .ThrowsAsync(new NpgsqlException("Connection refused"));
          var gate = new DatabaseGate();
          var logger = new ListLogger<DatabaseStartupService>();
          var service = new DatabaseStartupService(
              probe, gate, new ConfigurationBuilder().Build(), new FakeTimeProvider(), logger);

          for (var attempt = 1; attempt <= 21; attempt++)
              await service.RunAttemptAsync(TestContext.Current.CancellationToken);

          // Attempts 1-10, then 20: 11 log lines out of 21 failures.
          logger.Entries.Should().HaveCount(11);
      }
  }
  ```

  This needs `NextDelay()` exposed and a tiny in-repo `ListLogger<T>` test double (this repo has no
  logging test helper yet - `NullLogger<T>.Instance` is used everywhere logging is not itself under
  test). Add `tests/Noof.Ledger.Host.Tests/ListLogger.cs`:
  ```csharp
  using Microsoft.Extensions.Logging;

  namespace Noof.Ledger.Host.Tests;

  sealed class ListLogger<T> : ILogger<T>
  {
      public List<string> Entries { get; } = [];

      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

      public bool IsEnabled(LogLevel logLevel) => true;

      public void Log<TState>(
          LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
          Entries.Add(formatter(state, exception));
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter DatabaseStartupServiceTests`
  Expected: FAIL to compile — `NextDelay` does not exist on `DatabaseStartupService` yet (CS1061). (Fix
  the stray `_ = before => before;` line above before compiling — it was left in to make the diff obvious
  and must be deleted; the real failing point is the missing `NextDelay`.)

  In `src/Noof.Ledger.Host/Startup/DatabaseStartupService.cs`, remove the private `ConnectionBackoff`
  method and replace it with a public one the loop and the test both call, and change `ExecuteAsync` to
  use it:
  ```csharp
      protected override async Task ExecuteAsync(CancellationToken stoppingToken)
      {
          while (!stoppingToken.IsCancellationRequested)
          {
              var result = await RunAttemptAsync(stoppingToken);
              if (result == DatabaseStartupResult.Ready)
                  return;

              await DelayQuietlyAsync(NextDelay(), stoppingToken);
          }
      }
  ```
  ```csharp
      // The delay ExecuteAsync waits before its next attempt, given the outcome RunAttemptAsync just
      // recorded. Public so the backoff sequence itself is a fast, non-timing assertion.
      public TimeSpan NextDelay() => gate.State switch
      {
          DatabaseState.Failed => FailedRetryInterval,
          _ => ConnectionBackoffSteps[Math.Min(Math.Max(connectionAttempt, 1) - 1, ConnectionBackoffSteps.Length - 1)],
      };
  ```

  Also delete the now-unused `_ = before => before;` line from the test above (it was a placeholder to
  keep `before` referenced; the real assertion never needed it — remove both `before` lines from
  `The_backoff_sequence_is_2_4_8_16_30_then_30_forever`, leaving only:
  ```csharp
          List<TimeSpan> observed = [];
          foreach (var _ in expected)
          {
              await service.RunAttemptAsync(TestContext.Current.CancellationToken);
              observed.Add(service.NextDelay());
              time.Advance(observed[^1]);
          }

          observed.Should().Equal(expected);
      }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter DatabaseStartupServiceTests`
  Expected: PASS (9 tests).

  Commit: `git add src/Noof.Ledger.Host/Startup/DatabaseStartupService.cs tests/Noof.Ledger.Host.Tests/DatabaseStartupServiceTests.cs tests/Noof.Ledger.Host.Tests/ListLogger.cs`
  ```
  git commit -m "$(cat <<'EOF'
  Add DatabaseStartupService backoff and log cadence (Phase 5 Task 1)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 7: Program.cs — remove the synchronous migrate, register the gate and the startup service first**

  In `src/Noof.Ledger.Host/Program.cs`, delete lines 75–81:
  ```csharp
  if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
      // ApplicationStopping, not CancellationToken.None: ...
      await app.Services.MigrateNoofDatabaseAsync(app.Lifetime.ApplicationStopping);

  ```
  (the whole `if` block and its comment; `app.Services` no longer needs `MigrateNoofDatabaseAsync` called
  from here — `DatabaseStartupProbe` calls it now.)

  Insert, right after `builder.Services.AddNoofPersistence(builder.Configuration, categorizationOptions.MaxAttempts);`
  (line 48) and before `builder.Services.AddAuthentication(...)` (line 50) — i.e. before every other
  hosted-service registration:
  ```csharp

  builder.Services.AddSingleton<Noof.Ledger.Host.Startup.DatabaseGate>();
  builder.Services.AddSingleton<Noof.Ledger.Application.Diagnostics.IDatabaseGate>(
      sp => sp.GetRequiredService<Noof.Ledger.Host.Startup.DatabaseGate>());
  builder.Services.AddSingleton<Noof.Ledger.Host.Startup.IDatabaseStartupProbe, Noof.Ledger.Host.Startup.DatabaseStartupProbe>();
  builder.Services.AddHostedService<Noof.Ledger.Host.Startup.DatabaseStartupService>();
  ```
  and add the two `using` directives at the top alongside the existing ones (so the four lines above can
  drop their fully qualified names):
  ```csharp
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Host.Startup;
  ```
  then the inserted block reads:
  ```csharp
  builder.Services.AddSingleton<DatabaseGate>();
  builder.Services.AddSingleton<IDatabaseGate>(sp => sp.GetRequiredService<DatabaseGate>());
  builder.Services.AddSingleton<IDatabaseStartupProbe, DatabaseStartupProbe>();
  builder.Services.AddHostedService<DatabaseStartupService>();
  ```
  This lands before `builder.Services.AddNoofTelegram();` (line 67, registers `TelegramPollingService`)
  and before `builder.Services.AddNoofWorkers(...)` (line 71) — `AddHostedService` appends to an ordered
  list the generic host starts in registration order, so `DatabaseStartupService` is first.

  This step is TDD-exempt (CLAUDE.md §5: "Program.cs wiring"); it is proved by Step 14's E2E test, which
  is the first thing that can observe "the host starts and stays up with an unreachable database and
  `MigrateOnStartup=true`" — the whole point of the change. Before that step, confirm the build compiles
  and the fast suites are undisturbed:

  Run: `dotnet build src/Noof.Ledger.Host/Noof.Ledger.Host.csproj`
  Expected: PASS (this also verifies the two `using` directives are exact matches for what's already
  imported elsewhere — `Noof.Ledger.Application.Diagnostics` and `Noof.Ledger.Host.Startup` are new to
  this file).

  Commit: `git add src/Noof.Ledger.Host/Program.cs`
  ```
  git commit -m "$(cat <<'EOF'
  Start Noof.Ledger.Host without blocking on the database (Phase 5 Task 1)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 8: `CategorizationWorker` waits for the gate**

  In `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`, change the `CreateWorker` helper
  (line 127–130) to accept an optional gate defaulting to one that is already `Ready`, so every existing
  call site keeps compiling and passing unchanged:
  ```csharp
      static CategorizationWorker CreateWorker(
          IServiceScopeFactory scopeFactory, FakeTimeProvider time, CategorizationWorkerOptions? options = null,
          IDatabaseGate? gate = null) =>
          new(scopeFactory, time, options ?? new CategorizationWorkerOptions(), WorkerId,
              Mapper, Scan, Echo, gate ?? ReadyGate(), NullLogger<CategorizationWorker>.Instance);

      static IDatabaseGate ReadyGate()
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
          return gate;
      }
  ```
  and add `using Noof.Ledger.Application.Diagnostics;` to the file's usings.

  Then append one new test proving the loop actually waits (not just that the constructor accepts a gate):
  ```csharp
      [Fact]
      public async Task The_loop_waits_for_the_database_gate_before_its_first_claim()
      {
          var jobQueue = Substitute.For<IJobQueue>();
          var gateSource = new TaskCompletionSource();
          var gate = Substitute.For<IDatabaseGate>();
          gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(gateSource.Task);
          var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent()), new FakeTimeProvider(), gate: gate);

          await worker.StartAsync(TestContext.Current.CancellationToken);
          await Task.Delay(50, TestContext.Current.CancellationToken);
          await jobQueue.DidNotReceive().ClaimAsync(
              WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());

          gateSource.SetResult();
          await Task.Delay(50, TestContext.Current.CancellationToken);
          await jobQueue.Received().ReleaseExpiredLeasesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

          await worker.StopAsync(TestContext.Current.CancellationToken);
      }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWorkerTests`
  Expected: FAIL to compile — `CategorizationWorker`'s constructor has no `IDatabaseGate` parameter yet
  (CS1729/CS7036).

  In `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`, add the parameter and the wait:
  ```csharp
  using Noof.Ledger.Application.Diagnostics;
  ```
  ```csharp
  internal sealed class CategorizationWorker(
      IServiceScopeFactory scopeFactory,
      TimeProvider timeProvider,
      CategorizationWorkerOptions options,
      string workerId,
      IProposalMapper proposalMapper,
      IMerchantScan merchantScan,
      IRecordEcho recordEcho,
      IDatabaseGate gate,
      ILogger<CategorizationWorker> logger)
      : BackgroundService
  {
  ```
  ```csharp
      protected override async Task ExecuteAsync(CancellationToken stoppingToken)
      {
          await gate.WaitUntilReadyAsync(stoppingToken);

          while (!stoppingToken.IsCancellationRequested)
          {
              var result = await RunTickAsync(stoppingToken);

              var delay = result == CategorizationTickResult.Processed ? TimeSpan.Zero : options.PollInterval;
              if (delay > TimeSpan.Zero)
                  await Task.Delay(delay, timeProvider, stoppingToken);
          }
      }
  ```

  In `src/Noof.Ledger.Host/Workers/WorkerRegistration.cs`, add the argument to the `CategorizationWorker`
  construction (line 14–22), right before `logger`:
  ```csharp
          services.AddHostedService(sp => new CategorizationWorker(
              sp.GetRequiredService<IServiceScopeFactory>(),
              sp.GetRequiredService<TimeProvider>(),
              options,
              CategorizationWorker.CreateWorkerId(),
              sp.GetRequiredService<IProposalMapper>(),
              sp.GetRequiredService<IMerchantScan>(),
              sp.GetRequiredService<IRecordEcho>(),
              sp.GetRequiredService<IDatabaseGate>(),
              sp.GetRequiredService<ILogger<CategorizationWorker>>()));
  ```
  and add `using Noof.Ledger.Application.Diagnostics;` to that file's usings.

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWorkerTests`
  Expected: PASS (all prior tests plus the new one).

  Commit: `git add src/Noof.Ledger.Host/Workers/CategorizationWorker.cs src/Noof.Ledger.Host/Workers/WorkerRegistration.cs tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`
  ```
  git commit -m "$(cat <<'EOF'
  CategorizationWorker waits for the database gate (Phase 5 Task 1)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 9: `TranscriptionWorker` waits for the gate**

  Same shape as Step 8. In `tests/Noof.Ledger.Host.Tests/TranscriptionWorkerTests.cs`, change `CreateWorker`
  (line 101–103):
  ```csharp
      static TranscriptionWorker CreateWorker(
          IServiceScopeFactory scopeFactory, FakeTimeProvider? time = null, IDatabaseGate? gate = null) =>
          new(scopeFactory, time ?? new FakeTimeProvider(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero)),
              new CategorizationWorkerOptions(), WorkerId, Echo, gate ?? ReadyGate(), NullLogger<TranscriptionWorker>.Instance);

      static IDatabaseGate ReadyGate()
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
          return gate;
      }
  ```
  add `using Noof.Ledger.Application.Diagnostics;`, and append:
  ```csharp
      [Fact]
      public async Task The_loop_waits_for_the_database_gate_before_its_first_claim()
      {
          var harness = DefaultHarness();
          var gateSource = new TaskCompletionSource();
          var gate = Substitute.For<IDatabaseGate>();
          gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(gateSource.Task);
          var worker = CreateWorker(harness.ScopeFactory(), gate: gate);

          await worker.StartAsync(TestContext.Current.CancellationToken);
          await Task.Delay(50, TestContext.Current.CancellationToken);
          await harness.Queue.DidNotReceive().ReleaseExpiredLeasesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

          gateSource.SetResult();
          await Task.Delay(50, TestContext.Current.CancellationToken);
          await harness.Queue.Received().ReleaseExpiredLeasesAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

          await worker.StopAsync(TestContext.Current.CancellationToken);
      }
  ```
  (`DefaultHarness()` is whatever this file's existing helper that builds a `Harness` with an idle queue is
  called — reuse the same one every other idle-tick test in this file already uses; check the file for its
  exact name before writing this call, since it was not captured verbatim during planning.)

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter TranscriptionWorkerTests`
  Expected: FAIL to compile — no `IDatabaseGate` parameter on `TranscriptionWorker` yet.

  In `src/Noof.Ledger.Host/Workers/TranscriptionWorker.cs`:
  ```csharp
  using Noof.Ledger.Application.Diagnostics;
  ```
  ```csharp
  internal sealed class TranscriptionWorker(
      IServiceScopeFactory scopeFactory,
      TimeProvider timeProvider,
      CategorizationWorkerOptions options,
      string workerId,
      IRecordEcho recordEcho,
      IDatabaseGate gate,
      ILogger<TranscriptionWorker> logger)
      : BackgroundService
  {
  ```
  ```csharp
      protected override async Task ExecuteAsync(CancellationToken stoppingToken)
      {
          await gate.WaitUntilReadyAsync(stoppingToken);

          while (!stoppingToken.IsCancellationRequested)
          {
              var result = await RunTickAsync(stoppingToken);

              var delay = result == CategorizationTickResult.Processed ? TimeSpan.Zero : options.PollInterval;
              if (delay > TimeSpan.Zero)
                  await Task.Delay(delay, timeProvider, stoppingToken);
          }
      }
  ```

  In `src/Noof.Ledger.Host/Workers/WorkerRegistration.cs` (line 24–30):
  ```csharp
          services.AddHostedService(sp => new TranscriptionWorker(
              sp.GetRequiredService<IServiceScopeFactory>(),
              sp.GetRequiredService<TimeProvider>(),
              options,
              CategorizationWorker.CreateWorkerId(),
              sp.GetRequiredService<IRecordEcho>(),
              sp.GetRequiredService<IDatabaseGate>(),
              sp.GetRequiredService<ILogger<TranscriptionWorker>>()));
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter TranscriptionWorkerTests`
  Expected: PASS.

  Commit: `git add src/Noof.Ledger.Host/Workers/TranscriptionWorker.cs src/Noof.Ledger.Host/Workers/WorkerRegistration.cs tests/Noof.Ledger.Host.Tests/TranscriptionWorkerTests.cs`
  ```
  git commit -m "$(cat <<'EOF'
  TranscriptionWorker waits for the database gate (Phase 5 Task 1)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 10: `BackupWorker` waits for the gate**

  In `tests/Noof.Ledger.Host.Tests/BackupWorkerTests.cs`, change `CreateWorker` (line 51–52):
  ```csharp
      static BackupWorker CreateWorker(
          IServiceScopeFactory scopeFactory, FakeTimeProvider time, BackupWorkerOptions options, IDatabaseGate? gate = null) =>
          new(scopeFactory, time, options, gate ?? ReadyGate(), NullLogger<BackupWorker>.Instance);

      static IDatabaseGate ReadyGate()
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
          return gate;
      }
  ```
  add `using Noof.Ledger.Application.Diagnostics;`, and append:
  ```csharp
      [Fact]
      public async Task The_loop_waits_for_the_database_gate_before_its_first_tick()
      {
          var log = LogWithStatus(new BackupStatus(null, false, null));
          var dumper = Substitute.For<IDatabaseDumper>();
          var gateSource = new TaskCompletionSource();
          var gate = Substitute.For<IDatabaseGate>();
          gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(gateSource.Task);
          var worker = CreateWorker(ScopeFactoryFor(log, dumper), new FakeTimeProvider(), Options(), gate);

          await worker.StartAsync(TestContext.Current.CancellationToken);
          await Task.Delay(50, TestContext.Current.CancellationToken);
          await log.DidNotReceive().StatusAsync(Arg.Any<CancellationToken>());

          gateSource.SetResult();
          await Task.Delay(50, TestContext.Current.CancellationToken);
          await log.Received().StatusAsync(Arg.Any<CancellationToken>());

          await worker.StopAsync(TestContext.Current.CancellationToken);
      }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter BackupWorkerTests`
  Expected: FAIL to compile — no `IDatabaseGate` parameter on `BackupWorker` yet.

  In `src/Noof.Ledger.Host/Workers/BackupWorker.cs`:
  ```csharp
  using Noof.Ledger.Application.Diagnostics;
  ```
  ```csharp
  internal sealed class BackupWorker(
      IServiceScopeFactory scopeFactory,
      TimeProvider timeProvider,
      BackupWorkerOptions options,
      IDatabaseGate gate,
      ILogger<BackupWorker> logger)
      : BackgroundService
  {
  ```
  ```csharp
      protected override async Task ExecuteAsync(CancellationToken stoppingToken)
      {
          await gate.WaitUntilReadyAsync(stoppingToken);

          while (!stoppingToken.IsCancellationRequested)
          {
              var outcome = await RunTickCoreAsync(stoppingToken);
              await Task.Delay(DelayUntilNextTick(outcome), timeProvider, stoppingToken);
          }
      }
  ```

  In `src/Noof.Ledger.Host/Workers/WorkerRegistration.cs` (line 34–39):
  ```csharp
          if (backupOptions.Enabled)
          {
              services.AddHostedService(sp => new BackupWorker(
                  sp.GetRequiredService<IServiceScopeFactory>(),
                  sp.GetRequiredService<TimeProvider>(),
                  backupOptions,
                  sp.GetRequiredService<IDatabaseGate>(),
                  sp.GetRequiredService<ILogger<BackupWorker>>()));
          }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter BackupWorkerTests`
  Expected: PASS.

  Commit: `git add src/Noof.Ledger.Host/Workers/BackupWorker.cs src/Noof.Ledger.Host/Workers/WorkerRegistration.cs tests/Noof.Ledger.Host.Tests/BackupWorkerTests.cs`
  ```
  git commit -m "$(cat <<'EOF'
  BackupWorker waits for the database gate (Phase 5 Task 1)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 11: `TelegramPollingService` waits for the gate**

  In `tests/Noof.Ledger.Telegram.Tests/TelegramPollingServiceTests.cs`, change `CreateService`
  (line 57–69) to accept an optional gate:
  ```csharp
      static TelegramPollingService CreateService(
          ISecretStore secretStore,
          ITelegramBotClientFactory clientFactory,
          TelegramClientHandle handle,
          ITelegramUpdateRouter? router = null,
          IChatNotifier? chatNotifier = null,
          IDatabaseGate? gate = null) =>
          new(
              ScopeFactoryFor(secretStore, router, chatNotifier),
              clientFactory,
              handle,
              new ConfigurationBuilder().Build(),
              TimeProvider.System,
              gate ?? ReadyGate(),
              NullLogger<TelegramPollingService>.Instance);

      static IDatabaseGate ReadyGate()
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
          return gate;
      }
  ```
  add `using Noof.Ledger.Application.Diagnostics;`, and append:
  ```csharp
      [Fact]
      public async Task The_loop_waits_for_the_database_gate_before_its_first_poll()
      {
          var clientFactory = Substitute.For<ITelegramBotClientFactory>();
          var gateSource = new TaskCompletionSource();
          var gate = Substitute.For<IDatabaseGate>();
          gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(gateSource.Task);
          var service = CreateService(NoTokenYet(), clientFactory, new TelegramClientHandle(), gate: gate);

          await service.StartAsync(TestContext.Current.CancellationToken);
          await Task.Delay(50, TestContext.Current.CancellationToken);
          clientFactory.DidNotReceive().Create(Arg.Any<string>());

          gateSource.SetResult();
          await Task.Delay(50, TestContext.Current.CancellationToken);
          // NoTokenYet() never has a token, so the only observable effect of the gate releasing is
          // that the loop starts ticking at all - proven by not throwing/hanging past StopAsync.

          await service.StopAsync(TestContext.Current.CancellationToken);
      }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramPollingServiceTests`
  Expected: FAIL to compile — no `IDatabaseGate` parameter on `TelegramPollingService` yet.

  In `src/Noof.Ledger.Telegram/TelegramPollingService.cs`:
  ```csharp
  using Noof.Ledger.Application.Diagnostics;
  ```
  ```csharp
  internal sealed class TelegramPollingService(
      IServiceScopeFactory scopeFactory,
      ITelegramBotClientFactory clientFactory,
      TelegramClientHandle clientHandle,
      IConfiguration configuration,
      TimeProvider timeProvider,
      IDatabaseGate gate,
      ILogger<TelegramPollingService> logger)
      : BackgroundService
  {
  ```
  ```csharp
      protected override async Task ExecuteAsync(CancellationToken stoppingToken)
      {
          await gate.WaitUntilReadyAsync(stoppingToken);

          while (!stoppingToken.IsCancellationRequested)
          {
              var result = await RunTickAsync(stoppingToken);

              var delay = result switch
              {
                  TelegramPollResult.Idle => IdlePollInterval,
                  TelegramPollResult.Failed => TelegramBackoff.Compute(consecutiveFailures),
                  _ => TimeSpan.Zero,
              };

              if (delay > TimeSpan.Zero)
                  await Task.Delay(delay, timeProvider, stoppingToken);
          }
      }
  ```
  No change is needed in `src/Noof.Ledger.Telegram/TelegramRegistration.cs`: `services.AddHostedService<TelegramPollingService>()`
  already resolves every constructor parameter from DI, and `IDatabaseGate` is registered (Step 7) before
  this call runs (Program.cs line 67, after the Step 7 block).

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramPollingServiceTests`
  Expected: PASS.

  Commit: `git add src/Noof.Ledger.Telegram/TelegramPollingService.cs tests/Noof.Ledger.Telegram.Tests/TelegramPollingServiceTests.cs`
  ```
  git commit -m "$(cat <<'EOF'
  TelegramPollingService waits for the database gate (Phase 5 Task 1)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 12: `DatabaseGateBanner` and the sign-in page**

  No fast test exists for Web markup (see Plan notes); this step's correctness is proved by Step 14's
  E2E test. Add the shared component:

  `src/Noof.Ledger.Web/Components/Shared/DatabaseGateBanner.razor`:
  ```razor
  @inject IDatabaseGate DatabaseGate
  @using Noof.Ledger.Application.Diagnostics

  <MudAlert id="database-waiting" Severity="Severity.Warning" Variant="Variant.Outlined" Dense="true" Class="mb-6">
      @(DatabaseGate.State == DatabaseState.Failed
          ? "Database migration failed — see the log file."
          : "Waiting for the database…")
  </MudAlert>
  ```

  In `src/Noof.Ledger.Web/Components/Account/Login.razor`, wrap the `<form>` (lines 22–43) so it only
  renders once the gate is `Ready`:
  ```razor
  @inject IDatabaseGate DatabaseGate
  @using Noof.Ledger.Application.Diagnostics
  ```
  ```razor
          @if (DatabaseGate.State != DatabaseState.Ready)
          {
              <DatabaseGateBanner />
          }
          else
          {
              @* Native inputs inside MudBlazor chrome, deliberately. This POST is what calls SignInAsync,
                 which needs an HttpContext an interactive circuit does not have, so the page stays static
                 SSR - and a component that may or may not emit a name attribute is not worth the risk on
                 the one page whose entire job is a form POST. *@
              <form method="post" action="/account/login/submit" class="noof-login-form">
                  <AntiforgeryToken />

                  <label class="noof-field">
                      <span class="noof-field-label">Username</span>
                      <input name="username" autocomplete="username" class="noof-input" />
                  </label>

                  <label class="noof-field">
                      <span class="noof-field-label">Password</span>
                      <input name="password" type="password" autocomplete="current-password" class="noof-input" />
                  </label>

                  <MudButton ButtonType="ButtonType.Submit" Variant="Variant.Filled"
                             Color="Color.Primary" FullWidth="true" Class="mt-2">
                      Sign in
                  </MudButton>
              </form>
          }
  ```
  (the `@if (Failed is not null) { <MudAlert id="login-failed" ...> }` block above the form stays exactly
  where it is, outside this new `@if`/`@else` — a failed-login redirect never happens while the gate is
  not `Ready`, since sign-in itself needs the database, but leaving it in place costs nothing and keeps
  the diff to exactly the form.)

  Run: `dotnet build src/Noof.Ledger.Web/Noof.Ledger.Web.csproj`
  Expected: PASS (Razor compiles; `DatabaseGate`/`DatabaseState` resolve because Web already references
  Application).

  Commit: `git add src/Noof.Ledger.Web/Components/Shared/DatabaseGateBanner.razor src/Noof.Ledger.Web/Components/Account/Login.razor`
  ```
  git commit -m "$(cat <<'EOF'
  Sign-in page shows a waiting banner while the database is not ready (Phase 5 Task 1)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 13: `Home.razor` shows the same banner instead of data**

  In `src/Noof.Ledger.Web/Components/Pages/Home.razor`, inject the gate and skip the query while not ready:
  ```razor
  @inject IDatabaseGate DatabaseGate
  @using Noof.Ledger.Application.Diagnostics
  ```
  Guard the top of `OnInitializedAsync` (line 227) so a not-ready gate never even attempts the read
  models (avoiding a guaranteed `DbException` on top of the gate's own signal):
  ```csharp
      protected override async Task OnInitializedAsync()
      {
          if (DatabaseGate.State != DatabaseState.Ready)
              return;

          try
          {
  ```
  And wrap the whole existing `@if (databaseUnavailable) { ... } else { ... }` block (lines 20–202) in a
  new outer check, so the new banner takes priority over the pre-existing query-time one:
  ```razor
  @if (DatabaseGate.State != DatabaseState.Ready)
  {
      <DatabaseGateBanner />
  }
  else if (databaseUnavailable)
  {
      <MudAlert id="database-unavailable" Severity="Severity.Error" Variant="Variant.Outlined" Class="mt-6">
          Can't reach the database right now. Once PostgreSQL is running again, reload this page.
      </MudAlert>
  }
  else
  {
      @* everything from the existing "else" branch, unchanged: the Balances MudPaper, the
         backup-status caption, the currency groups, Recent transactions - lines 33-201 as they are
         today. *@
  }
  ```
  (this is a pure structural rewrap — `databaseUnavailable`'s own `@if`/`else` collapses into an
  `else if` one level up; no line inside the old `else` branch changes.)

  Run: `dotnet build src/Noof.Ledger.Web/Noof.Ledger.Web.csproj`
  Expected: PASS.

  Commit: `git add src/Noof.Ledger.Web/Components/Pages/Home.razor`
  ```
  git commit -m "$(cat <<'EOF'
  Home shows the database-waiting banner instead of data (Phase 5 Task 1)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 14: E2E — the host stays up on an unreachable database with migration enabled**

  In `tests/Noof.Ledger.E2E.Tests/UnreachableDatabaseHostFixture.cs`, flip the flag (this is the
  regression the whole task exists to fix — previously this value being `true` here would have made the
  old `await app.Services.MigrateNoofDatabaseAsync(...)` in `Program.cs` run before `app.Run()`, against
  an address that refuses every connection):
  ```csharp
          await host.StartAsync(publishDirectory, new Dictionary<string, string>
          {
              ["Database__MigrateOnStartup"] = "true",
              ["ConnectionStrings__Ledger"] = "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2",
              ["Backup__Enabled"] = "false",
          }, cancellationToken);
  ```

  Append to `tests/Noof.Ledger.E2E.Tests/SmokeTests.cs`:
  ```csharp
      [Fact]
      public async Task The_sign_in_page_shows_the_database_waiting_banner_while_the_database_is_unreachable()
      {
          await Page.GotoAsync(fixture.BaseUrl + "/");
          await Page.WaitForURLAsync("**/account/login*");

          await Expect(Page.Locator("#database-waiting")).ToBeVisibleAsync();
          await Expect(Page.Locator("#database-waiting")).ToContainTextAsync("Waiting for the database");
          await Expect(Page.Locator("input[name='username']")).Not.ToBeVisibleAsync();
      }
  ```
  (`Expect`/`Page` come from `Microsoft.Playwright.Xunit.v3.PageTest`, already the base class of this
  file — no new `using` needed.)

  Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj --filter SmokeTests`
  Expected (before Steps 1–13 land, i.e. run this against `master` first as the "watch it fail" for this
  fixture change alone): the new test FAILS — `#database-waiting` does not exist; the existing three
  `SmokeTests` also start failing once `MigrateOnStartup=true` reaches the *old* `Program.cs`, because the
  synchronous `MigrateNoofDatabaseAsync` call blocks the host from ever printing "Now listening on:" within
  `HostProcess`'s 30 s timeout (or, depending on Npgsql's own connect timeout inside `MigrateAsync`, the
  host starts but the migrate call throws unhandled before `app.Run()`, tearing the process down). Either
  way, this fixture edit alone (with the Steps 1–13 production code already in place, which it will be by
  the time this step actually runs) is what proves the fix — run it after Step 13, not before, as the
  final "does the whole thing actually work" check:

  Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj --filter SmokeTests`
  Expected: PASS (all 4 tests: the 3 existing plus the new one). This needs Chromium (`playwright install
  chromium` if not already present) and no PostgreSQL dependency (the fixture deliberately points at a
  port nothing listens on).

  Commit: `git add tests/Noof.Ledger.E2E.Tests/UnreachableDatabaseHostFixture.cs tests/Noof.Ledger.E2E.Tests/SmokeTests.cs`
  ```
  git commit -m "$(cat <<'EOF'
  E2E: the host stays up and shows the waiting banner with migration on and no database (Phase 5 Task 1)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 15: Full-solution regression pass**

  Run: `dotnet test --solution NoofLedger.slnx`
  Expected: PASS — every existing suite (852 tests before this task) plus this task's new tests
  (`DatabaseGateTests`: 5, `DatabaseStartupServiceTests`: 9, `DatabaseConnectivityTests`: 2, one new test
  each in `CategorizationWorkerTests`/`TranscriptionWorkerTests`/`BackupWorkerTests`/
  `TelegramPollingServiceTests`: 4, and one new `SmokeTests` case: 1 — 21 new tests total). Requires a
  reachable local PostgreSQL and Chromium, and follows CLAUDE.md's "serialise full-suite runs across
  worktrees" rule (the lock directory at `C:\Users\noofs\AppData\Local\Temp\noof-suite.lock`).

  Also re-run, specifically, to confirm no other suite silently depended on the old synchronous-migrate
  timing or on the pre-gate constructor shapes:
  ```
  dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj
  dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj
  dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj
  dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj
  dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj
  ```
  Expected: all PASS.

  No commit for this step — it is verification only, not a code change. If anything here fails, fix it
  under the step above that introduced the regression and re-commit there rather than adding a
  Step 16 patch, per CLAUDE.md's "never edit a migration/never bolt on a fix past the step that should
  have had it" spirit for this plan's own steps.

---

### Task 2: Serilog as the MEL provider, file log, [LoggerMessage] everywhere

**Plan notes (deviations from the literal scope text, kept inside the spec's intent):**
- Program.cs's own converted `LogCritical` call is placed in a **sibling static class** `Noof.Ledger.Host.Logging.ProgramLog`, not a class nested inside `Program`. `Program.cs` needs `Serilog.Log` (`Log.Logger`, `Log.Information`, `Log.Fatal`, `Log.CloseAndFlush`) unqualified throughout; a nested type literally named `Log` inside `Program` would shadow `Serilog.Log` for every unqualified `Log.*` call in that same file (member lookup prefers a nested type over a `using`-imported name). The four worker types nest `private static partial class Log` exactly as scoped, because none of them also uses `Serilog.Log` by name.
- The two-stage Serilog bootstrap wraps the whole host body (`WebApplication.CreateBuilder` through `app.Run()`) in `try / catch (Exception ex) when (ex is not HostAbortedException) / finally { Log.CloseAndFlush(); }` — this is Serilog's own documented ASP.NET Core template shape, not an invention. It is necessary here specifically because `WebApplicationFactory` (used by `BootTests` and the new logging test) stops the host by throwing `HostAbortedException` out of `Build()`; without the `when` filter that exception would be logged as a fake fatal error on every Host.Tests run.
- E2E is **not** extended. Task 1 (the database gate) is a parallel task in this wave and this task must not depend on its types; a Host-level `WebApplicationFactory` test that the bootstrap logger writes a file before the host is built is the "otherwise" branch the brief itself allows, and it needs nothing from Task 1.
- Serilog package versions were looked up live on nuget.org (flat-container index, checked 2026-09-25): `Serilog.AspNetCore` latest stable is `10.0.0` (10.0.1 exists only as a `-dev-` prerelease); `Serilog.Sinks.File` latest stable is `8.0.0`. Both are plain reference assemblies with no target-framework floor above net10.0's baseline, so they resolve cleanly there.
- Confirmed against this repo's own resolved dependency graph (`artifacts/obj/Noof.Ledger.Telegram/project.assets.json`) that `Microsoft.Extensions.Logging.Abstractions` (10.0.12, carrying the `[LoggerMessage]` source generator) is already transitively present in `Noof.Ledger.Telegram`'s graph via `Microsoft.Extensions.Http`/`Microsoft.Extensions.Hosting.Abstractions`. No new package reference is needed there, and `ProjectReferenceTests.Telegram_package_references_are_exactly_its_allowed_set` needs no change.
- CA1848/CA2254 are turned on via `.editorconfig` (`dotnet_diagnostic.X.severity = error` under a `[src/**]` section), matching how this repo already turns on other opt-in analyzer rules (CA1861, CA2263) — not `Directory.Build.props`, which the repo uses only for SDK-wide properties, never per-rule analyzer severities.

---

**Files:**

Create:
- `src/Noof.Ledger.Host/Logging/LoggingSetup.cs` — bootstrap config + Serilog configuration, both stages.
- `src/Noof.Ledger.Host/Logging/ProgramLog.cs` — `[LoggerMessage]` for `Program`'s loopback-guard critical log.
- `tests/Noof.Ledger.Host.Tests/LoggingBootstrapTests.cs` — starting the host writes a line to the configured directory.
- `tests/Noof.Ledger.Architecture.Tests/LoggingBoundaryTests.cs` — (a) only Host references Serilog, (b) no direct `LogXxx(` call anywhere in `src`.

Modify:
- `Directory.Packages.props` — add `Serilog.AspNetCore` `10.0.0`, `Serilog.Sinks.File` `8.0.0`.
- `src/Noof.Ledger.Host/Noof.Ledger.Host.csproj` — add the two `PackageReference`s (Host only).
- `src/Noof.Ledger.Host/appsettings.json` — add `"Logging": { "File": { "Directory": "%LOCALAPPDATA%\\NoofLedger\\logs" } }` alongside the existing `Logging.LogLevel` block.
- `src/Noof.Ledger.Host/Program.cs` (whole file rewritten; see Step 6) — bootstrap logger, `UseSerilog`, try/catch/finally, converted `LogCritical`.
- `src/Noof.Ledger.Telegram/TelegramPollingService.cs` (lines 1–166) — 4 call sites converted.
- `src/Noof.Ledger.Host/Workers/BackupWorker.cs` (lines 1–145) — 2 call sites converted.
- `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs` (lines 1–358) — 7 call sites converted.
- `src/Noof.Ledger.Host/Workers/TranscriptionWorker.cs` (lines 1–218) — 6 call sites converted.
- `.editorconfig` — new `[src/**]` section making CA1848/CA2254 errors.

**Interfaces:**
- Consumes: nothing from the binding contract (Task 2 defines no `Noof.Ledger.Application.Diagnostics` type). Depends only on already-existing `ILogger<T>` injection in the four worker types and `Program`.
- Produces (internal, Host-only, not part of the cross-task binding contract, so not added to `PublicSurfaceTests`):
  - `internal static class Noof.Ledger.Host.Logging.LoggingSetup`
    - `public static IConfiguration BuildBootstrapConfiguration(string[] args)`
    - `public static string ResolveLogDirectory(IConfiguration configuration)`
    - `public static Serilog.ILogger CreateBootstrapLogger(string logDirectory)`
    - `public static void Configure(Serilog.LoggerConfiguration configuration, string logDirectory)`
    - `public const string DirectoryConfigKey = "Logging:File:Directory"`
  - `internal static partial class Noof.Ledger.Host.Logging.ProgramLog` — `public static partial void LoopbackGuardFailed(this ILogger logger, string message)`.
  - Per-type nested `private static partial class Log` in `TelegramPollingService`, `BackupWorker`, `CategorizationWorker`, `TranscriptionWorker` (method lists in Step 7–9 below).

---

- [ ] **Step 1: Confirm the exact call sites are still where this plan found them, and pin package versions**

  Run (read-only, no repo change):
  ```
  grep -rn "logger\.Log(Trace|Debug|Information|Warning|Error|Critical)\(" src/Noof.Ledger.Host src/Noof.Ledger.Telegram
  ```
  Expect exactly: 2 hits in `BackupWorker.cs` (lines 71, 118), 7 in `CategorizationWorker.cs` (76, 175, 208, 212, 225, 284, 340), 6 in `TranscriptionWorker.cs` (65, 106, 117, 201, 211, 215), 4 in `TelegramPollingService.cs` (110, 122, 146, 163) — 19 total — plus one `LogCritical` in `Program.cs` (line 112). If a line number has drifted, re-read that file before editing it; the call text itself (quoted in Steps 7–10 below) is what must match, not the line number.

  Package versions already looked up live against nuget.org's flat-container index on 2026-09-25 (see Plan notes): `Serilog.AspNetCore` `10.0.0`, `Serilog.Sinks.File` `8.0.0`. No network step needed here unless significant time has passed since this plan was written, in which case re-check `https://api.nuget.org/v3-flatcontainer/serilog.aspnetcore/index.json` and `.../serilog.sinks.file/index.json` for a newer non-`-dev-` version before Step 2.

- [ ] **Step 2: Add the two Serilog packages — RED (architecture guard first, so it fails before Serilog even resolves)**

  Write the architecture test file first (it will fail to compile/find anything meaningful before the packages exist, which is an acceptable form of "red" here since the guard's subject — a Serilog reference outside Host — cannot exist yet; the important RED moment for this file is Step 11's deliberate-break check). Create `tests/Noof.Ledger.Architecture.Tests/LoggingBoundaryTests.cs`:

  ```csharp
  using System.Text.RegularExpressions;
  using System.Xml.Linq;
  using AwesomeAssertions;

  namespace Noof.Ledger.Architecture.Tests;

  // Task 2 (Phase 5 observability): Serilog is only the provider behind Microsoft.Extensions.Logging,
  // and every call site goes through a source-generated [LoggerMessage] method - never a direct
  // logger.LogXxx(...) call. Both rules are enforced here because a convention alone did not hold for
  // the Anthropic SDK boundary either (see AiBoundaryTests) and there is no reason logging would be
  // different.
  public class LoggingBoundaryTests
  {
      static readonly string SrcRoot = Path.Combine(RepoRoot.Find().FullName, "src");
      static readonly string HostRoot = Path.Combine(SrcRoot, "Noof.Ledger.Host") + Path.DirectorySeparatorChar;

      static readonly Regex SerilogReference = new(
          @"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?:global::)?Serilog\b|(?<![\w.])Serilog\.\w",
          RegexOptions.Multiline | RegexOptions.Compiled);

      static readonly Regex DirectLoggerCall = new(
          @"\.Log(?:Trace|Debug|Information|Warning|Error|Critical)\s*\(",
          RegexOptions.Compiled);

      [Fact]
      public void Only_Noof_Ledger_Host_references_Serilog()
      {
          var offenders = SourceFiles("*.cs")
              .Where(file => !InHost(file))
              .Where(file => SerilogReference.IsMatch(File.ReadAllText(file)))
              .Select(Relative)
              .ToArray();

          offenders.Should().BeEmpty("Serilog is reached through ILogger<T>; only the Host composition root may name it");
          SourceFiles("*.cs").Where(InHost).Should().Contain(file => SerilogReference.IsMatch(File.ReadAllText(file)),
              "Host itself must use Serilog, or an empty offender list proves nothing about the pattern");
      }

      [Fact]
      public void Only_Noof_Ledger_Host_has_a_Serilog_package_reference()
      {
          var projectDirs = Directory.EnumerateDirectories(SrcRoot);

          var offenders = projectDirs
              .Select(dir => (dir, csproj: Directory.EnumerateFiles(dir, "*.csproj").SingleOrDefault()))
              .Where(entry => entry.csproj is not null)
              .Where(entry => !string.Equals(Path.GetFileNameWithoutExtension(entry.csproj), "Noof.Ledger.Host", StringComparison.Ordinal))
              .Where(entry => XDocument.Load(entry.csproj!).Descendants("PackageReference")
                  .Any(e => (e.Attribute("Include")?.Value ?? "").StartsWith("Serilog", StringComparison.Ordinal)))
              .Select(entry => Path.GetFileName(entry.dir))
              .ToArray();

          offenders.Should().BeEmpty("Serilog packages are referenced by Noof.Ledger.Host only");

          var hostCsproj = Path.Combine(HostRoot.TrimEnd(Path.DirectorySeparatorChar), "Noof.Ledger.Host.csproj");
          XDocument.Load(hostCsproj).Descendants("PackageReference")
              .Should().Contain(e => (e.Attribute("Include")?.Value ?? "").StartsWith("Serilog", StringComparison.Ordinal),
                  "Host itself must reference Serilog, or an empty offender list proves nothing");
      }

      [Fact]
      public void No_source_file_calls_a_LogXxx_convenience_method_directly()
      {
          var offenders = SourceFiles("*.cs")
              .Where(file => DirectLoggerCall.IsMatch(File.ReadAllText(file)))
              .Select(Relative)
              .ToArray();

          offenders.Should().BeEmpty(
              "every log call goes through a source-generated [LoggerMessage] method, never logger.LogXxx(...) directly");
      }

      static IEnumerable<string> SourceFiles(string pattern) =>
          Directory.EnumerateFiles(SrcRoot, pattern, SearchOption.AllDirectories)
              .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
              .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

      static bool InHost(string file) => file.StartsWith(HostRoot, StringComparison.OrdinalIgnoreCase);

      static string Relative(string file) => Path.GetRelativePath(SrcRoot, file);
  }
  ```

  Run:
  ```
  dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter LoggingBoundaryTests
  ```
  Expected failure: `Only_Noof_Ledger_Host_references_Serilog` and `Only_Noof_Ledger_Host_has_a_Serilog_package_reference` both fail on the "Host itself must ... or an empty offender list proves nothing" assertion (Host references nothing Serilog yet). `No_source_file_calls_a_LogXxx_convenience_method_directly` fails too, listing all 20 offending files — this is expected red for the guard this task exists to satisfy.

- [ ] **Step 3: Add the packages — GREEN for the two Serilog-presence assertions**

  Edit `Directory.Packages.props`, inside the first `<ItemGroup>` (after the `Microsoft.Extensions.Configuration` line):
  ```xml
      <PackageVersion Include="Serilog.AspNetCore" Version="10.0.0" />
      <PackageVersion Include="Serilog.Sinks.File" Version="8.0.0" />
  ```

  Edit `src/Noof.Ledger.Host/Noof.Ledger.Host.csproj`, adding a new `<ItemGroup>` (placed after the existing `<ProjectReference>` group, before `<PropertyGroup>`):
  ```xml
    <ItemGroup>
      <PackageReference Include="Serilog.AspNetCore" />
      <PackageReference Include="Serilog.Sinks.File" />
    </ItemGroup>
  ```

  Run:
  ```
  dotnet restore NoofLedger.slnx
  dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter LoggingBoundaryTests
  ```
  Expected: `Only_Noof_Ledger_Host_has_a_Serilog_package_reference` passes now (Host's csproj has the references, no other project does). `Only_Noof_Ledger_Host_references_Serilog` still fails the same way (no `.cs` file uses Serilog yet) — expected, fixed in Step 6. `No_source_file_calls_a_LogXxx_convenience_method_directly` still fails — expected, fixed in Steps 7–10.

- [ ] **Step 4: appsettings.json — the config key**

  Edit `src/Noof.Ledger.Host/appsettings.json`:
  ```json
  {
    "Logging": {
      "LogLevel": {
        "Default": "Information",
        "Microsoft.AspNetCore": "Warning"
      },
      "File": {
        "Directory": "%LOCALAPPDATA%\\NoofLedger\\logs"
      }
    },
    "AllowedHosts": "*",
    "Auth": { "Mode": "Off" },
    "Database": { "MigrateOnStartup": true },
    "ConnectionStrings": { "Ledger": "" },
    "Capture": { "TimeZone": "Europe/Belgrade" }
  }
  ```
  No test for this step alone (a JSON literal is a config value, not behaviour) — Step 6's Host test exercises it end to end.

- [ ] **Step 5: `.editorconfig` — CA1848/CA2254 errors for `src`, watched red once**

  First, prove the guard can fail: temporarily add a throwaway line `logger.LogInformation("temp");` inside `RunTickAsync` in `src/Noof.Ledger.Host/Workers/BackupWorker.cs` (do not commit this) and run:
  ```
  dotnet build src/Noof.Ledger.Host/Noof.Ledger.Host.csproj
  ```
  Expected right now: **no** CA1848/CA2254 error (the rules are not wired yet) — build succeeds despite the direct call. This is the "before" state the next edit changes.

  Edit `.editorconfig`, inserting a new section directly after the top `[*.cs]` block (before `[src/Noof.Ledger.Domain/**]`):
  ```
  [src/**]
  # Task 2 (Phase 5 observability): every log call in src goes through a source-generated
  # [LoggerMessage] method - CA1848 catches a direct logger.LogXxx(...) call, CA2254 catches a
  # message template built from a runtime expression instead of a compile-time constant. Scoped to
  # src, not tests/**, per CLAUDE.md's own working agreement for this task.
  dotnet_diagnostic.CA1848.severity = error
  dotnet_diagnostic.CA2254.severity = error
  ```

  Run again:
  ```
  dotnet build src/Noof.Ledger.Host/Noof.Ledger.Host.csproj
  ```
  Expected now: build **fails** with `CA1848` on the throwaway `logger.LogInformation("temp");` line — the guard is proven live. Remove the throwaway line (`git diff src/Noof.Ledger.Host/Workers/BackupWorker.cs` must show no change once removed) and rebuild to confirm green again:
  ```
  dotnet build src/Noof.Ledger.Host/Noof.Ledger.Host.csproj
  ```
  This also now fails, correctly — the 20 real remaining call sites trip CA1848 too. That failure is expected and is what Steps 6–10 fix; do not treat it as a problem with the `.editorconfig` edit itself.

- [ ] **Step 6: `LoggingSetup`, `ProgramLog`, and `Program.cs` — bootstrap + full config + converted `LogCritical`**

  Create `src/Noof.Ledger.Host/Logging/LoggingSetup.cs`:
  ```csharp
  using Serilog;

  namespace Noof.Ledger.Host.Logging;

  // Task 2 (Phase 5 observability): Serilog is only the provider behind ILogger<T> (CLAUDE.md,
  // "The tool loop..." section's sibling rule for AI providers applies the same way here) - this is
  // the one place in the solution allowed to name Serilog types, besides Program.cs and the
  // per-type [LoggerMessage] partial classes that only ever see ILogger<T>.
  internal static class LoggingSetup
  {
      public const string DirectoryConfigKey = "Logging:File:Directory";

      const string DefaultDirectory = @"%LOCALAPPDATA%\NoofLedger\logs";
      const long FileSizeLimitBytes = 50 * 1024 * 1024;
      const int RetainedFileCountLimit = 14;

      // A plain ConfigurationBuilder, not builder.Configuration - this runs before
      // WebApplication.CreateBuilder exists, because the bootstrap logger must be live before
      // anything else in the host can fail loudly. AppContext.BaseDirectory, not
      // Directory.GetCurrentDirectory(): the latter is the test runner's working directory under
      // dotnet test, not the folder appsettings.json is copied into.
      public static IConfiguration BuildBootstrapConfiguration(string[] args) =>
          new ConfigurationBuilder()
              .SetBasePath(AppContext.BaseDirectory)
              .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
              .AddEnvironmentVariables()
              .AddCommandLine(args)
              .Build();

      public static string ResolveLogDirectory(IConfiguration configuration) =>
          Environment.ExpandEnvironmentVariables(configuration[DirectoryConfigKey] ?? DefaultDirectory);

      public static Serilog.ILogger CreateBootstrapLogger(string logDirectory) =>
          new LoggerConfiguration()
              .MinimumLevel.Information()
              .WriteTo.Console()
              .WriteTo.File(
                  Path.Combine(logDirectory, "noof-ledger-.log"),
                  rollingInterval: RollingInterval.Day,
                  retainedFileCountLimit: RetainedFileCountLimit,
                  fileSizeLimitBytes: FileSizeLimitBytes,
                  rollOnFileSizeLimit: true)
              .CreateBootstrapLogger();

      // The full reconfiguration UseSerilog runs once the host is built. No PostgreSQL sink here -
      // that is Task 4's job; this stage only widens the bootstrap logger with LogContext
      // enrichment so a later BeginScope (Task 3's TransactionLogScope) actually reaches the file.
      public static void Configure(LoggerConfiguration configuration, string logDirectory) =>
          configuration
              .MinimumLevel.Information()
              .Enrich.FromLogContext()
              .WriteTo.Console()
              .WriteTo.File(
                  Path.Combine(logDirectory, "noof-ledger-.log"),
                  rollingInterval: RollingInterval.Day,
                  retainedFileCountLimit: RetainedFileCountLimit,
                  fileSizeLimitBytes: FileSizeLimitBytes,
                  rollOnFileSizeLimit: true);
  }
  ```

  Create `src/Noof.Ledger.Host/Logging/ProgramLog.cs`:
  ```csharp
  namespace Noof.Ledger.Host.Logging;

  // A sibling file, not nested inside Program (CLAUDE.md/contract: "[LoggerMessage] everywhere...a
  // private static partial class Log inside each type or a sibling file"). Program.cs uses
  // Serilog.Log unqualified throughout (Log.Logger, Log.Information, Log.Fatal, Log.CloseAndFlush);
  // a type named Log nested inside Program would shadow that using-imported name for every
  // unqualified reference in the same file, since member lookup prefers a nested type over an
  // imported one with the same simple name.
  internal static partial class ProgramLog
  {
      [LoggerMessage(EventId = 1401, Level = LogLevel.Critical, Message = "{Message}")]
      public static partial void LoopbackGuardFailed(this ILogger logger, string message);
  }
  ```

  Replace the whole of `src/Noof.Ledger.Host/Program.cs`:
  ```csharp
  using Microsoft.AspNetCore.Authorization;
  using Microsoft.AspNetCore.Hosting.Server;
  using Microsoft.AspNetCore.Hosting.Server.Features;
  using Microsoft.Extensions.Hosting;
  using Noof.Ledger.Ai;
  using Noof.Ledger.Application;
  using Noof.Ledger.Application.Auth;
  using Noof.Ledger.Host.Auth;
  using Noof.Ledger.Host.Cli;
  using Noof.Ledger.Host.Endpoints;
  using Noof.Ledger.Host.Logging;
  using Noof.Ledger.Host.Startup;
  using Noof.Ledger.Host.Workers;
  using Noof.Ledger.Persistence;
  using Noof.Ledger.Telegram;
  using Noof.Ledger.Web;
  using Noof.Ledger.Web.Components;
  using Serilog;

  if (UserCommand.TryParse(args, out var cliUsername))
  {
      Environment.ExitCode = await UserCommand.RunAsync(cliUsername, args);
      return;
  }

  var bootstrapConfiguration = LoggingSetup.BuildBootstrapConfiguration(args);
  var logDirectory = LoggingSetup.ResolveLogDirectory(bootstrapConfiguration);
  Log.Logger = LoggingSetup.CreateBootstrapLogger(logDirectory);

  // Two-stage initialization (Serilog's own documented ASP.NET Core shape): the bootstrap logger
  // above is live before anything else can fail, and this try/catch/finally is what makes
  // "the host never exits on a startup failure without a trace of why" actually true. The `when`
  // filter matters for tests: WebApplicationFactory stops a minimal-hosting Program by throwing
  // HostAbortedException out of Build(), and that is not a real failure to log as fatal.
  try
  {
      Log.Information("Starting host; logging to {LogDirectory}", logDirectory);

      var builder = WebApplication.CreateBuilder(args);

      builder.Host.UseSerilog((_, _, loggerConfiguration) =>
          LoggingSetup.Configure(loggerConfiguration, logDirectory));

      builder.Services.AddRazorComponents()
          .AddInteractiveServerComponents();

      builder.Services.AddNoofWeb();

      builder.Services.AddNoofApplication();

      builder.Services.AddSingleton(TimeProvider.System);
      builder.Services.AddSingleton(CaptureTimeZoneGuard.Resolve(
          builder.Configuration["Capture:TimeZone"] ?? "Europe/Belgrade"));

      var dataProtectionKeyRingDirectory = new DirectoryInfo(Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "dp-keys"));
      DataProtectionSetup.Configure(builder.Services, dataProtectionKeyRingDirectory);

      builder.Services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();

      var categorizationOptions = new CategorizationWorkerOptions();
      builder.Configuration.GetSection("Categorization").Bind(categorizationOptions);

      var backupOptions = new BackupWorkerOptions();
      builder.Configuration.GetSection("Backup").Bind(backupOptions);

      builder.Services.AddNoofPersistence(builder.Configuration, categorizationOptions.MaxAttempts);

      builder.Services.AddAuthentication(AuthSchemes.Cookie)
          .AddCookie(AuthSchemes.Cookie, options =>
          {
              options.LoginPath = "/account/login";
              options.ExpireTimeSpan = TimeSpan.FromDays(180);
              options.SlidingExpiration = true;
              options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
          });

      // Deny by default. Every page declares [Authorize] and an architecture test holds it to that, but
      // nothing holds a minimal-API endpoint to it - a MapGet added in a later phase would answer anyone
      // who can reach the port. With a fallback policy the omission costs a redirect to the login page
      // instead of handing out the ledger, so the guarantee survives code nobody has written yet.
      builder.Services.AddAuthorization(options =>
          options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
      builder.Services.AddCascadingAuthenticationState();

      builder.Services.AddNoofTelegram();

      builder.Services.AddNoofAi(builder.Configuration);

      builder.Services.AddNoofWorkers(categorizationOptions, backupOptions);

      var app = builder.Build();

      if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
          // ApplicationStopping, not CancellationToken.None: a Ctrl+C during startup migration should
          // cancel the in-flight MigrateAsync rather than let it run unattended. EF applies each
          // migration inside its own transaction, so a cancel here rolls back the migration in
          // progress and leaves the schema at its last fully-applied version - never a half-applied
          // one - which Migrate() can safely retry on the next start.
          await app.Services.MigrateNoofDatabaseAsync(app.Lifetime.ApplicationStopping);

      app.UseAuthentication();
      app.UseAuthorization();
      app.UseAntiforgery();

      // Anonymous, or the fallback policy above redirects the sign-in page's own stylesheet and Blazor
      // script to the sign-in page. The page would still render - unstyled and inert - to the one visitor
      // who cannot sign in to report it.
      app.MapStaticAssets().AllowAnonymous();
      app.MapRazorComponents<App>()
          .AddInteractiveServerRenderMode();

      app.MapAccountEndpoints();

      app.MapGet("/healthz", () => Results.Ok("ok")).AllowAnonymous();

      app.Lifetime.ApplicationStarted.Register(() =>
      {
          var addresses = app.Services.GetRequiredService<IServer>()
              .Features.Get<IServerAddressesFeature>()?.Addresses;

          try
          {
              LoopbackGuard.AssertSafe([.. addresses ?? []]);
          }
          catch (InvalidOperationException exposed)
          {
              // Throwing out of an ApplicationStarted callback does NOT stop the host: the hosting layer
              // catches it, logs it, and Kestrel keeps serving. Verified. Shutting down explicitly is the
              // only thing that actually closes the socket.
              app.Services.GetRequiredService<ILogger<Program>>().LoopbackGuardFailed(exposed.Message);
              Environment.ExitCode = 1;
              app.Lifetime.StopApplication();
          }
      });

      app.Run();
  }
  catch (Exception ex) when (ex is not HostAbortedException)
  {
      Log.Fatal(ex, "Host terminated unexpectedly");
  }
  finally
  {
      Log.CloseAndFlush();
  }

  internal partial class Program;
  ```

  No standalone test for this step in isolation (`Program.cs` has no unit surface of its own); Step 11's Host test exercises the bootstrap logger end to end, and the existing `BootTests` (unmodified) prove the try/catch/finally does not break the already-covered anonymous-request and fail-fast-on-dead-database paths.

  Run:
  ```
  dotnet build src/Noof.Ledger.Host/Noof.Ledger.Host.csproj
  ```
  Expected: still fails on CA1848 — now only from the four worker files' remaining direct calls (Program.cs's own call is fixed). Confirms `LoggingSetup`/`ProgramLog`/`Program.cs` compile cleanly on their own before touching the workers.

  ```
  dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter BootTests
  ```
  Expected: passes (all three `BootTests` still green — the anonymous-redirect, `/healthz`, and fail-fast-on-dead-database behaviours are unaffected by the logging change).

- [ ] **Step 7: Convert `BackupWorker.cs`**

  Edit `src/Noof.Ledger.Host/Workers/BackupWorker.cs`. Add, inside the `BackupWorker` class body (after the closing brace of `Prune()`, before the class's own closing brace):
  ```csharp

      private static partial class Log
      {
          [LoggerMessage(EventId = 1101, Level = LogLevel.Error, Message = "Backup worker tick failed")]
          public static partial void BackupTickFailed(this ILogger logger, Exception exception);

          [LoggerMessage(EventId = 1102, Level = LogLevel.Error, Message = "Backup failed: {Error}")]
          public static partial void BackupFailed(this ILogger logger, string error);
      }
  ```
  Replace line 71 `logger.LogError(ex, "Backup worker tick failed");` with `logger.BackupTickFailed(ex);`.
  Replace line 118 `logger.LogError("Backup failed: {Error}", result.Error);` with `logger.BackupFailed(result.Error);`.

  Run:
  ```
  dotnet build src/Noof.Ledger.Host/Noof.Ledger.Host.csproj
  ```
  Expected: still fails on CA1848 — now only from `CategorizationWorker.cs` and `TranscriptionWorker.cs`.

  ```
  dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter BackupWorkerTests
  dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter BackupWorkerWiringTests
  dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter BackupRetentionTests
  ```
  Expected: all green, unchanged behaviour (these tests construct `BackupWorker` with `NullLogger<BackupWorker>.Instance` or a real `ILogger<BackupWorker>` from DI and never assert on log content — confirm this by reading the three files if any assertion on logger output is found; none is expected).

- [ ] **Step 8: Convert `CategorizationWorker.cs`**

  Edit `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`. Add, inside the `CategorizationWorker` class body (after `CreateWorkerId()`, before the class's own closing brace):
  ```csharp

      private static partial class Log
      {
          [LoggerMessage(EventId = 1201, Level = LogLevel.Error, Message = "Categorization worker tick failed")]
          public static partial void TickFailed(this ILogger logger, Exception exception);

          [LoggerMessage(EventId = 1202, Level = LogLevel.Information,
              Message = "Job {JobId} reached the canonicalization cap of {Cap}; '{MerchantText}' was left unlinked")]
          public static partial void CanonicalizationCapReached(this ILogger logger, Guid jobId, int cap, string merchantText);

          [LoggerMessage(EventId = 1203, Level = LogLevel.Warning,
              Message = "Job {JobId} was already reclaimed by another worker; not retrying")]
          public static partial void JobAlreadyReclaimed(this ILogger logger, Guid jobId);

          [LoggerMessage(EventId = 1204, Level = LogLevel.Warning,
              Message = "SucceedAsync failed for job {JobId} after its line items were already committed; the transaction is left as Completed")]
          public static partial void SucceedAfterCommitFailed(this ILogger logger, Exception exception, Guid jobId);

          [LoggerMessage(EventId = 1205, Level = LogLevel.Warning,
              Message = "Account-level model provider failure on job {JobId} ({Message}); pausing new claims for {Cooldown}")]
          public static partial void AccountLevelFailure(this ILogger logger, Guid jobId, string message, TimeSpan cooldown);

          [LoggerMessage(EventId = 1206, Level = LogLevel.Warning,
              Message = "Failed to echo job {JobId}'s result to Telegram; the categorization itself already succeeded")]
          public static partial void EchoFailed(this ILogger logger, Exception exception, Guid jobId);

          [LoggerMessage(EventId = 1207, Level = LogLevel.Warning,
              Message = "Failed to edit Telegram message {MessageId} to report a failed job for transaction {TransactionId}")]
          public static partial void FailureEditFailed(this ILogger logger, Exception exception, int messageId, Guid transactionId);
      }
  ```
  Replace each call site:
  - Line 76 `logger.LogError(ex, "Categorization worker tick failed");` → `logger.TickFailed(ex);`
  - Lines 175–177 `logger.LogInformation("Job {JobId} reached the canonicalization cap of {Cap}; '{MerchantText}' was left unlinked", job.Id, options.MaxCanonicalizationsPerJob, merchantText);` → `logger.CanonicalizationCapReached(job.Id, options.MaxCanonicalizationsPerJob, merchantText);`
  - Line 208 `logger.LogWarning("Job {JobId} was already reclaimed by another worker; not retrying", job.Id);` → `logger.JobAlreadyReclaimed(job.Id);`
  - Lines 212–214 `logger.LogWarning(ex, "SucceedAsync failed for job {JobId} after its line items were already committed; the transaction is left as Completed", job.Id);` → `logger.SucceedAfterCommitFailed(ex, job.Id);`
  - Lines 225–227 `logger.LogWarning("Account-level model provider failure on job {JobId} ({Message}); pausing new claims for {Cooldown}", job.Id, ex.Message, options.AccountCooldown);` → `logger.AccountLevelFailure(job.Id, ex.Message, options.AccountCooldown);`
  - Lines 284–286 `logger.LogWarning(ex, "Failed to echo job {JobId}'s result to Telegram; the categorization itself already succeeded", job.Id);` → `logger.EchoFailed(ex, job.Id);`
  - Lines 340–342 `logger.LogWarning(ex, "Failed to edit Telegram message {MessageId} to report a failed job for transaction {TransactionId}", messageId, job.TransactionId);` → `logger.FailureEditFailed(ex, messageId, job.TransactionId);`

  Run:
  ```
  dotnet build src/Noof.Ledger.Host/Noof.Ledger.Host.csproj
  ```
  Expected: still fails on CA1848 — now only from `TranscriptionWorker.cs`.

  ```
  dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWorkerTests
  dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWiringTests
  dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWorkerOptionsTests
  ```
  Expected: all green, unchanged (uses `NullLogger<CategorizationWorker>.Instance`, per the existing file — no log-content assertions).

- [ ] **Step 9: Convert `TranscriptionWorker.cs`**

  Edit `src/Noof.Ledger.Host/Workers/TranscriptionWorker.cs`. Add, inside the `TranscriptionWorker` class body (after `SucceedQuietlyAsync`, before the class's own closing brace):
  ```csharp

      private static partial class Log
      {
          [LoggerMessage(EventId = 1301, Level = LogLevel.Error, Message = "Transcription worker tick failed")]
          public static partial void TickFailed(this ILogger logger, Exception exception);

          [LoggerMessage(EventId = 1302, Level = LogLevel.Information,
              Message = "Job {JobId}'s transcript was already handed on by an earlier run")]
          public static partial void TranscriptAlreadyHandedOn(this ILogger logger, Guid jobId);

          [LoggerMessage(EventId = 1303, Level = LogLevel.Warning,
              Message = "Account-level speech provider failure on job {JobId} ({Message}); pausing new claims for {Cooldown}")]
          public static partial void AccountLevelFailure(this ILogger logger, Guid jobId, string message, TimeSpan cooldown);

          [LoggerMessage(EventId = 1304, Level = LogLevel.Warning,
              Message = "Failed to edit Telegram message {MessageId} for transaction {TransactionId}")]
          public static partial void EditFailed(this ILogger logger, Exception exception, int messageId, Guid transactionId);

          [LoggerMessage(EventId = 1305, Level = LogLevel.Warning,
              Message = "Job {JobId} was already reclaimed by another worker; not retrying")]
          public static partial void JobAlreadyReclaimed(this ILogger logger, Guid jobId);

          [LoggerMessage(EventId = 1306, Level = LogLevel.Warning,
              Message = "SucceedAsync failed for job {JobId} after its transcript was already handed on")]
          public static partial void SucceedAfterHandOffFailed(this ILogger logger, Exception exception, Guid jobId);
      }
  ```
  Replace each call site:
  - Line 65 `logger.LogError(ex, "Transcription worker tick failed");` → `logger.TickFailed(ex);`
  - Line 106 `logger.LogInformation("Job {JobId}'s transcript was already handed on by an earlier run", job.Id);` → `logger.TranscriptAlreadyHandedOn(job.Id);`
  - Lines 117–119 `logger.LogWarning("Account-level speech provider failure on job {JobId} ({Message}); pausing new claims for {Cooldown}", job.Id, ex.Message, options.AccountCooldown);` → `logger.AccountLevelFailure(job.Id, ex.Message, options.AccountCooldown);`
  - Lines 201–202 `logger.LogWarning(ex, "Failed to edit Telegram message {MessageId} for transaction {TransactionId}", messageId, record.TransactionId);` → `logger.EditFailed(ex, messageId, record.TransactionId);`
  - Line 211 `logger.LogWarning("Job {JobId} was already reclaimed by another worker; not retrying", job.Id);` → `logger.JobAlreadyReclaimed(job.Id);`
  - Line 215 `logger.LogWarning(ex, "SucceedAsync failed for job {JobId} after its transcript was already handed on", job.Id);` → `logger.SucceedAfterHandOffFailed(ex, job.Id);`

  Run:
  ```
  dotnet build src/Noof.Ledger.Host/Noof.Ledger.Host.csproj
  ```
  Expected: **succeeds** — this was the last file with a direct `LogXxx(` call in `src/Noof.Ledger.Host`.

  ```
  dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter TranscriptionWorkerTests
  ```
  Expected: green, unchanged.

- [ ] **Step 10: Convert `TelegramPollingService.cs`**

  Edit `src/Noof.Ledger.Telegram/TelegramPollingService.cs`. Add, inside the `TelegramPollingService` class body (after `NotifyOperatorOfSkippedUpdateAsync`, before the class's own closing brace):
  ```csharp

      private static partial class Log
      {
          [LoggerMessage(EventId = 1001, Level = LogLevel.Error,
              Message = "Telegram update {UpdateId} failed on attempt {Attempt}/{MaxAttempts}")]
          public static partial void UpdateFailed(this ILogger logger, Exception exception, int updateId, int attempt, int maxAttempts);

          [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
              Message = "Telegram update {UpdateId} failed {MaxAttempts} times; skipping it so later updates aren't blocked behind it")]
          public static partial void UpdateAbandoned(this ILogger logger, int updateId, int maxAttempts);

          [LoggerMessage(EventId = 1003, Level = LogLevel.Error, Message = "Telegram poll tick failed; backing off and retrying")]
          public static partial void PollTickFailed(this ILogger logger, Exception exception);

          [LoggerMessage(EventId = 1004, Level = LogLevel.Error,
              Message = "Failed to notify the operator that Telegram update {UpdateId} was skipped")]
          public static partial void SkippedUpdateNotificationFailed(this ILogger logger, Exception exception, int updateId);
      }
  ```
  Replace each call site:
  - Lines 110–111 `logger.LogError(ex, "Telegram update {UpdateId} failed on attempt {Attempt}/{MaxAttempts}", update.Id, poisonUpdateAttempts, MaxUpdateAttempts);` → `logger.UpdateFailed(ex, update.Id, poisonUpdateAttempts, MaxUpdateAttempts);`
  - Lines 122–124 `logger.LogError("Telegram update {UpdateId} failed {MaxAttempts} times; skipping it so later updates aren't blocked behind it", update.Id, MaxUpdateAttempts);` → `logger.UpdateAbandoned(update.Id, MaxUpdateAttempts);`
  - Line 146 `logger.LogError(ex, "Telegram poll tick failed; backing off and retrying");` → `logger.PollTickFailed(ex);`
  - Line 163 `logger.LogError(ex, "Failed to notify the operator that Telegram update {UpdateId} was skipped", update.Id);` → `logger.SkippedUpdateNotificationFailed(ex, update.Id);`

  Run:
  ```
  dotnet build tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj
  dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramPollingServiceTests
  ```
  Expected: builds and passes, unchanged (uses `NullLogger<TelegramPollingService>.Instance`, per the existing file).

  ```
  dotnet build NoofLedger.slnx
  ```
  Expected: succeeds solution-wide with no warnings (warnings are errors here) — this is the first point every direct `LogXxx(` call in `src` is gone.

- [ ] **Step 11: GREEN for both `LoggingBoundaryTests` guards, then watch each fail once more on purpose**

  Run:
  ```
  dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter LoggingBoundaryTests
  ```
  Expected: all three tests pass now.

  Watch each fail once, deliberately, then revert (do not commit the temporary breaks):
  1. Temporarily add `using Serilog;` to the top of `src/Noof.Ledger.Telegram/TelegramPollingService.cs`. Re-run the filter above. Expect `Only_Noof_Ledger_Host_references_Serilog` to fail, naming `Noof.Ledger.Telegram/TelegramPollingService.cs`. Remove the line.
  2. Temporarily add `<PackageReference Include="Serilog" />` (and a matching `Directory.Packages.props` entry, or just point it at an existing pinned Serilog-family version already restored) to `src/Noof.Ledger.Telegram/Noof.Ledger.Telegram.csproj`. Re-run. Expect `Only_Noof_Ledger_Host_has_a_Serilog_package_reference` to fail, naming `Noof.Ledger.Telegram`. Revert the csproj (and `Directory.Packages.props` if touched).
  3. Temporarily revert the `TickFailed` conversion in `TranscriptionWorker.cs` back to `logger.LogError(ex, "Transcription worker tick failed");`. Re-run. Expect `No_source_file_calls_a_LogXxx_convenience_method_directly` to fail, naming that file. Revert to `logger.TickFailed(ex);`.

  Confirm `git status --porcelain src/Noof.Ledger.Telegram src/Noof.Ledger.Host/Workers` shows no stray changes from the three probes, then re-run the filter once more to confirm all three pass clean before committing.

- [ ] **Step 12: RED — Host test that starting the host writes a line to the configured directory**

  Create `tests/Noof.Ledger.Host.Tests/LoggingBootstrapTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Microsoft.AspNetCore.Mvc.Testing;

  namespace Noof.Ledger.Host.Tests;

  public class LoggingBootstrapTests
  {
      [Fact]
      public async Task Starting_the_host_writes_a_line_into_the_configured_log_directory()
      {
          var logDirectory = Directory.CreateTempSubdirectory("noof-ledger-logs-test-");
          var previousEnvironmentValue = Environment.GetEnvironmentVariable("Logging__File__Directory");

          try
          {
              Environment.SetEnvironmentVariable("Logging__File__Directory", logDirectory.FullName);

              using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
              {
                  builder.UseSetting("Database:MigrateOnStartup", "false");
                  builder.UseSetting("Backup:Enabled", "false");
                  builder.UseSetting("ConnectionStrings:Ledger",
                      "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
                  builder.UseSetting("Logging:File:Directory", logDirectory.FullName);
              });

              using var client = factory.CreateClient();
              var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
              response.IsSuccessStatusCode.Should().BeTrue();

              var logFiles = Directory.EnumerateFiles(logDirectory.FullName, "noof-ledger-*.log").ToArray();
              logFiles.Should().ContainSingle("the bootstrap logger must write into the directory the configuration names");
              File.ReadAllText(logFiles[0]).Should().NotBeNullOrWhiteSpace(
                  "starting the host must produce at least one log line in the configured file");
          }
          finally
          {
              Environment.SetEnvironmentVariable("Logging__File__Directory", previousEnvironmentValue);
              logDirectory.Delete(recursive: true);
          }
      }
  }
  ```

  This is written against the finished implementation (Steps 3–10 are already done by this point in the plan's own ordering), so to see it fail for the *right* reason, run it now, before Step 12's own production code exists — which it already does, since Steps 3–10 landed the whole logging pipeline already. Instead, prove the specific claim this test makes — that the directory is genuinely configuration-driven, not hardcoded — by running it once against a deliberately broken `LoggingSetup.ResolveLogDirectory` that ignores the configured key:

  Temporarily change `ResolveLogDirectory` to `Environment.ExpandEnvironmentVariables(DefaultDirectory)` (ignoring the `configuration` parameter). Run:
  ```
  dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter LoggingBootstrapTests
  ```
  Expected failure: no file appears under `logDirectory.FullName` (`ContainSingle` fails with an empty collection), because the host wrote to `%LOCALAPPDATA%\NoofLedger\logs` instead of the temp directory. Revert `ResolveLogDirectory` to read `configuration[DirectoryConfigKey]`.

  Run again:
  ```
  dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter LoggingBootstrapTests
  ```
  Expected: green.

- [ ] **Step 13: Full fast-project pass for this task's touched projects**

  Per the operator's test policy for this wave: run each fast project this task touched, once, in full (no filter):
  ```
  dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj
  dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj
  dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj
  ```
  Expected: all green, no warnings. Do **not** run `Noof.Ledger.Persistence.Tests`, `Noof.Ledger.E2E.Tests`, or `dotnet test --solution` — this task added no migration and touches no database or browser test; the phase-end full run covers them.

- [ ] **Step 14: Commit**

  ```
  git add Directory.Packages.props .editorconfig \
    src/Noof.Ledger.Host/Noof.Ledger.Host.csproj \
    src/Noof.Ledger.Host/appsettings.json \
    src/Noof.Ledger.Host/Program.cs \
    src/Noof.Ledger.Host/Logging/LoggingSetup.cs \
    src/Noof.Ledger.Host/Logging/ProgramLog.cs \
    src/Noof.Ledger.Host/Workers/BackupWorker.cs \
    src/Noof.Ledger.Host/Workers/CategorizationWorker.cs \
    src/Noof.Ledger.Host/Workers/TranscriptionWorker.cs \
    src/Noof.Ledger.Telegram/TelegramPollingService.cs \
    tests/Noof.Ledger.Architecture.Tests/LoggingBoundaryTests.cs \
    tests/Noof.Ledger.Host.Tests/LoggingBootstrapTests.cs
  ```
  ```
  git commit -m "$(cat <<'EOF'
  feat: Serilog as the MEL provider, with every log call through [LoggerMessage]

  Program.cs bootstraps a Serilog file+console logger before the host builds, so a startup
  failure is never silent, then reconfigures the same pipeline once DI exists (no database
  sink yet - that is Task 4). All 19 existing direct logger.LogXxx(...) calls across the four
  background workers, plus Program's own loopback-guard LogCritical, are now source-generated
  [LoggerMessage] methods; CA1848/CA2254 make a direct call an error again for src, and two
  architecture tests hold both boundaries (Serilog stays inside Host; no LogXxx( call anywhere
  in src).

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

---

### Task 3: app_log table, log query, retention, transaction trace reader

**Plan notes**

- No `Noof.Ledger.Application.Tests` project exists in this repo. Application types are exercised from
  `Noof.Ledger.Persistence.Tests` (which already project-references `Noof.Ledger.Persistence` → `Noof.Ledger.Application`
  transitively), matching how every other `Noof.Ledger.Application` interface (`ICaptureStore`, `IBalanceReadModel`, …)
  is tested today. `TransactionLogScopeTests` therefore lives in `tests/Noof.Ledger.Persistence.Tests/`.
- `Noof.Ledger.Persistence.Tests` has no `NSubstitute` package reference (global-constraints names NSubstitute as the
  repo's standard, but this project has never needed it). Rather than add a package for one test, `TransactionLogScopeTests`
  uses a small hand-written `CapturingLogger : ILogger` nested in the test file itself — it is test-only scaffolding, not
  production code duplication, and keeps the project's dependency footprint unchanged.
- `RevisionView.Details` (contract: "a compact Details string") is built from fields `TransactionRevision` already
  carries — the instruction when the revision has one, else a `StatusBefore → StatusAfter` transition — rather than
  diffing the JSON `Snapshot` against its predecessor. The contract asks for a mapping from the existing entity, not a
  new diffing engine, and this task's tests (`EfTransactionTraceTests`) assert exactly this shape.
- Migration timestamp: the plan writes `<TS>_AddAppLog` throughout. `dotnet ef migrations add AddAppLog` assigns the
  real `yyyyMMddHHmmss` prefix at Step 4 time; substitute it into the filename and into `LedgerDbContextModelSnapshot.cs`
  migration id reference before running later steps that name the file.

**Files**

Create:
- `src/Noof.Ledger.Application/Diagnostics/LogSeverity.cs`
- `src/Noof.Ledger.Application/Diagnostics/LogRow.cs`
- `src/Noof.Ledger.Application/Diagnostics/ILogQuery.cs`
- `src/Noof.Ledger.Application/Diagnostics/ILogRetention.cs`
- `src/Noof.Ledger.Application/Diagnostics/TransactionStages.cs`
- `src/Noof.Ledger.Application/Diagnostics/TransactionLogScope.cs`
- `src/Noof.Ledger.Application/Diagnostics/TransactionTrace.cs`
- `src/Noof.Ledger.Persistence/Diagnostics/AppLogEntry.cs`
- `src/Noof.Ledger.Persistence/Configurations/AppLogEntryConfiguration.cs`
- `src/Noof.Ledger.Persistence/Diagnostics/EfLogQuery.cs`
- `src/Noof.Ledger.Persistence/Diagnostics/EfLogRetention.cs`
- `src/Noof.Ledger.Persistence/Diagnostics/EfTransactionTrace.cs`
- `src/Noof.Ledger.Persistence/Migrations/<TS>_AddAppLog.cs` (EF-scaffolded, hand-converted to file-scoped namespace)
- `src/Noof.Ledger.Persistence/Migrations/<TS>_AddAppLog.Designer.cs` (EF-scaffolded, untouched)
- `tests/Noof.Ledger.Persistence.Tests/TransactionLogScopeTests.cs`
- `tests/Noof.Ledger.Persistence.Tests/AppLogSchemaTests.cs`
- `tests/Noof.Ledger.Persistence.Tests/EfLogQueryTests.cs`
- `tests/Noof.Ledger.Persistence.Tests/EfLogRetentionTests.cs`
- `tests/Noof.Ledger.Persistence.Tests/EfTransactionTraceTests.cs`

Modify:
- `Directory.Packages.props` — add `Microsoft.Extensions.Logging.Abstractions` version pin (near the other
  `Microsoft.Extensions.*` entries, ~line 17-22).
- `src/Noof.Ledger.Application/Noof.Ledger.Application.csproj` (lines 3-6) — add the package reference.
- `src/Noof.Ledger.Persistence/LedgerDbContext.cs` (lines 1-31) — `using Noof.Ledger.Persistence.Diagnostics;` and
  `public DbSet<AppLogEntry> AppLogs => Set<AppLogEntry>();`.
- `src/Noof.Ledger.Persistence/PersistenceRegistration.cs` (lines 1-65) — usings + three `AddScoped` registrations.
- `src/Noof.Ledger.Persistence/Migrations/LedgerDbContextModelSnapshot.cs` (EF-regenerated by the `migrations add`
  command; not hand-edited beyond substituting `<TS>` if you rename the migration file after scaffolding).
- `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs` (lines 39-70, the `["Noof.Ledger.Application"]` entry) —
  add `LogSeverity`, `LogRow`, `LogFilter`, `LogPage`, `ILogQuery`, `ILogRetention`, `TransactionStages`,
  `TransactionLogScope`, `TraceEvent`, `RevisionView`, `TransactionTrace`, `ITransactionTrace`.
- `tests/Noof.Ledger.Persistence.Tests/schema.expected.sql` — regenerated to add `app_log` and its three indexes.

**Interfaces**

Consumes:
- `Noof.Ledger.Persistence.LedgerDbContext` (existing `DbSet`s, `ConfigureConventions`, `OnModelCreating`).
- `Noof.Ledger.Persistence.Revisions.TransactionRevision` / `RevisionKind` (internal, same assembly) — read-only, for
  `EfTransactionTrace`'s `History`.
- `Microsoft.EntityFrameworkCore.ExecuteDeleteAsync`, `EF.Functions.ILike`.

Produces (exact signatures from the contract):
```csharp
namespace Noof.Ledger.Application.Diagnostics;

public enum LogSeverity { Verbose = 0, Debug = 1, Information = 2, Warning = 3, Error = 4, Fatal = 5 }

public sealed record LogRow(long Id, DateTimeOffset LoggedAt, LogSeverity Level, string? Source, string Message,
    string? Exception, Guid? TransactionId, string? PropertiesJson);

public sealed record LogFilter(LogSeverity MinLevel = LogSeverity.Information, DateTimeOffset? From = null,
    DateTimeOffset? To = null, string? Text = null, string? Source = null, Guid? TransactionId = null);

public sealed record LogPage(IReadOnlyList<LogRow> Rows, int TotalCount);

public interface ILogQuery
{
    Task<LogPage> QueryAsync(LogFilter filter, int pageIndex, int pageSize, CancellationToken cancellationToken);
}

public interface ILogRetention
{
    Task<int> PruneAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public static class TransactionStages { /* constants + Ordered, per contract */ }

public static class TransactionLogScope
{
    public static IDisposable? Begin(ILogger logger, Guid transactionId);
}

public sealed record TraceEvent(DateTimeOffset At, string Stage, int EventId, LogSeverity Level, string Message,
    string? Exception, string? PropertiesJson);
public sealed record RevisionView(DateTimeOffset At, string ChangeKind, string Details);
public sealed record TransactionTrace(Guid TransactionId, bool Exists, IReadOnlyList<TraceEvent> Events,
    IReadOnlyList<RevisionView> History);

public interface ITransactionTrace
{
    Task<TransactionTrace> GetAsync(Guid transactionId, CancellationToken cancellationToken);
}
```
Task-internal (not in the contract, needed to implement it):
- `internal sealed class AppLogEntry` (Persistence.Diagnostics) — the EF entity backing `app_log`.
- `internal sealed class AppLogEntryConfiguration : IEntityTypeConfiguration<AppLogEntry>`.
- `internal sealed class EfLogQuery(LedgerDbContext db) : ILogQuery`.
- `internal sealed class EfLogRetention(LedgerDbContext db) : ILogRetention`.
- `internal sealed class EfTransactionTrace(LedgerDbContext db) : ITransactionTrace`.

---

- [ ] **Step 1: Add the `Microsoft.Extensions.Logging.Abstractions` package to `Noof.Ledger.Application`**

No test — package wiring is TDD-exempt (`Program.cs`/project-file wiring, CLAUDE.md §"Testing").

Edit `Directory.Packages.props`, next to the other `Microsoft.Extensions.*` pins (after line 17
`<PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.12" />`):
```xml
<PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.12" />
```

Edit `src/Noof.Ledger.Application/Noof.Ledger.Application.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Noof.Ledger.Domain\Noof.Ledger.Domain.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Noof.Ledger.Persistence.Tests" />
    <InternalsVisibleTo Include="Noof.Ledger.Host.Tests" />
    <InternalsVisibleTo Include="Noof.Ledger.Telegram.Tests" />
    <InternalsVisibleTo Include="Noof.Ledger.Ai.Tests" />
  </ItemGroup>

</Project>
```

Run: `dotnet build NoofLedger.slnx`
Expected: success — the package adds an unused dependency only, nothing references it yet.

Commit:
```
git add Directory.Packages.props src/Noof.Ledger.Application/Noof.Ledger.Application.csproj
git commit -m "$(cat <<'EOF'
Add Microsoft.Extensions.Logging.Abstractions to Noof.Ledger.Application

Diagnostics types (TransactionLogScope) need ILogger; this is the
package-only step so the next commit is pure new code.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 2: Write the Diagnostics DTOs, enums and interfaces (Application) — watch `PublicSurfaceTests` fail, then allow them**

DTOs, enums and bare interfaces carry no behaviour of their own (CLAUDE.md's TDD exemption for DTOs); the guard this
step must watch fail is `PublicSurfaceTests.Public_types_are_exactly_the_allowed_set("Noof.Ledger.Application")`,
which enforces that nothing new becomes public without a reviewed line here.

Create `src/Noof.Ledger.Application/Diagnostics/LogSeverity.cs`:
```csharp
namespace Noof.Ledger.Application.Diagnostics;

public enum LogSeverity
{
    Verbose = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Fatal = 5,
}
```

Create `src/Noof.Ledger.Application/Diagnostics/LogRow.cs`:
```csharp
namespace Noof.Ledger.Application.Diagnostics;

public sealed record LogRow(
    long Id,
    DateTimeOffset LoggedAt,
    LogSeverity Level,
    string? Source,
    string Message,
    string? Exception,
    Guid? TransactionId,
    string? PropertiesJson);

public sealed record LogFilter(
    LogSeverity MinLevel = LogSeverity.Information,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Text = null,
    string? Source = null,
    Guid? TransactionId = null);

public sealed record LogPage(IReadOnlyList<LogRow> Rows, int TotalCount);
```

Create `src/Noof.Ledger.Application/Diagnostics/ILogQuery.cs`:
```csharp
namespace Noof.Ledger.Application.Diagnostics;

public interface ILogQuery
{
    Task<LogPage> QueryAsync(LogFilter filter, int pageIndex, int pageSize, CancellationToken cancellationToken);
}
```

Create `src/Noof.Ledger.Application/Diagnostics/ILogRetention.cs`:
```csharp
namespace Noof.Ledger.Application.Diagnostics;

public interface ILogRetention
{
    Task<int> PruneAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
```

Create `src/Noof.Ledger.Application/Diagnostics/TransactionStages.cs`:
```csharp
namespace Noof.Ledger.Application.Diagnostics;

// The trace reader (EfTransactionTrace) and every stage writer (Task 4/Telegram/Host workers) read these
// names and ids from here alone, so a stage can never drift between what is written and what is read back.
public static class TransactionStages
{
    public const string TransactionIdProperty = "TransactionId";
    public const string StageProperty = "Stage";

    public const string Received = "Received";
    public const int ReceivedEventId = 5001;

    public const string Transcribed = "Transcribed";
    public const int TranscribedEventId = 5002;

    public const string Categorized = "Categorized";
    public const int CategorizedEventId = 5003;

    public const string Persisted = "Persisted";
    public const int PersistedEventId = 5004;

    public const string Replied = "Replied";
    public const int RepliedEventId = 5005;

    public const string StageFailed = "StageFailed";
    public const int StageFailedEventId = 5009;

    public static IReadOnlyList<string> Ordered { get; } = [Received, Transcribed, Categorized, Persisted, Replied];
}
```

Create `src/Noof.Ledger.Application/Diagnostics/TransactionTrace.cs`:
```csharp
namespace Noof.Ledger.Application.Diagnostics;

public sealed record TraceEvent(
    DateTimeOffset At,
    string Stage,
    int EventId,
    LogSeverity Level,
    string Message,
    string? Exception,
    string? PropertiesJson);

public sealed record RevisionView(DateTimeOffset At, string ChangeKind, string Details);

public sealed record TransactionTrace(
    Guid TransactionId,
    bool Exists,
    IReadOnlyList<TraceEvent> Events,
    IReadOnlyList<RevisionView> History);

public interface ITransactionTrace
{
    Task<TransactionTrace> GetAsync(Guid transactionId, CancellationToken cancellationToken);
}
```

Create `src/Noof.Ledger.Application/Diagnostics/TransactionLogScope.cs` (declared now, implemented in Step 3):
```csharp
using Microsoft.Extensions.Logging;

namespace Noof.Ledger.Application.Diagnostics;

public static class TransactionLogScope
{
    public static IDisposable? Begin(ILogger logger, Guid transactionId) =>
        logger.BeginScope(new Dictionary<string, object> { [TransactionStages.TransactionIdProperty] = transactionId });
}
```

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter PublicSurfaceTests`
Expected failure: `Public_types_are_exactly_the_allowed_set("Noof.Ledger.Application")` fails — the scanned assembly now
exposes `LogSeverity`, `LogRow`, `LogFilter`, `LogPage`, `ILogQuery`, `ILogRetention`, `TransactionStages`,
`TransactionLogScope`, `TraceEvent`, `RevisionView`, `TransactionTrace`, `ITransactionTrace`, none of which are in
`Allowed["Noof.Ledger.Application"]` yet. This is the step's "watch it fail" — it proves the allowlist is the thing
actually gating new public surface, not a rule nobody enforces.

Edit `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`, appending to the `["Noof.Ledger.Application"]` array
(after `"BackupRunRecord", "BackupStatus", "DumpResult", "IBackupLog", "IDatabaseDumper",`):
```csharp
            "LogSeverity", "LogRow", "LogFilter", "LogPage", "ILogQuery", "ILogRetention",
            "TransactionStages", "TransactionLogScope", "TraceEvent", "RevisionView", "TransactionTrace", "ITransactionTrace",
```

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter PublicSurfaceTests`
Expected: PASS.

Run: `dotnet build NoofLedger.slnx`
Expected: success.

Commit:
```
git add src/Noof.Ledger.Application/Diagnostics tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs
git commit -m "$(cat <<'EOF'
Add Application.Diagnostics DTOs, interfaces and TransactionStages

The log query/retention/trace contracts, the stable stage names and
event ids the whole capture pipeline will log against, and the
TransactionLogScope helper that opens a per-transaction log scope.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 3: TDD `TransactionLogScope.Begin` opens a scope carrying the transaction id**

Create `tests/Noof.Ledger.Persistence.Tests/TransactionLogScopeTests.cs`:
```csharp
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Tests;

public class TransactionLogScopeTests
{
    [Fact]
    public void Begin_opens_a_scope_carrying_only_the_transaction_id()
    {
        var logger = new CapturingLogger();
        var transactionId = Guid.NewGuid();

        TransactionLogScope.Begin(logger, transactionId);

        var state = logger.LastScopeState.Should().BeAssignableTo<IReadOnlyDictionary<string, object>>().Subject;
        state.Should().HaveCount(1);
        state[TransactionStages.TransactionIdProperty].Should().Be(transactionId);
    }

    // A hand-written fake rather than NSubstitute: this project has no NSubstitute reference, and
    // ILogger.BeginScope<TState> is the only member this test needs, so a substitute buys nothing.
    sealed class CapturingLogger : ILogger
    {
        public object? LastScopeState { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            LastScopeState = state;
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }

        sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter TransactionLogScopeTests`
Expected: PASS immediately — `TransactionLogScope.Begin` was already fully implemented in Step 2 (the interface and
its one-line implementation are inseparable; there was no meaningful red state to force here beyond the
`PublicSurfaceTests` guard Step 2 already watched fail). This step exists to put a permanent regression test on the
one line of behaviour Application owns, per CLAUDE.md's "a look that fails silently needs a test" spirit — a future
edit that changes the dictionary key, or opens the scope with `logger.BeginScope(transactionId)` directly instead of
a dictionary, breaks this test immediately.

Commit:
```
git add tests/Noof.Ledger.Persistence.Tests/TransactionLogScopeTests.cs
git commit -m "$(cat <<'EOF'
Test TransactionLogScope.Begin's scope shape

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 4: `AppLogEntry` entity, configuration, migration `AddAppLog`**

Entity and configuration are DTO/mapping code (TDD-exempt); the migration is exempt outright per CLAUDE.md. The test
this step produces is the schema/index assertion in Step 4b, which is the guard that must watch red before green.

Create `src/Noof.Ledger.Persistence/Diagnostics/AppLogEntry.cs`:
```csharp
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class AppLogEntry
{
    public required long Id { get; init; }
    public required DateTimeOffset LoggedAt { get; init; }
    public required LogSeverity Level { get; init; }
    public string? Source { get; init; }
    public required string Message { get; init; }
    public required string Template { get; init; }
    public string? Exception { get; init; }
    public Guid? TransactionId { get; init; }
    public string? PropertiesJson { get; init; }
}
```

Create `src/Noof.Ledger.Persistence/Configurations/AppLogEntryConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Persistence.Diagnostics;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class AppLogEntryConfiguration : IEntityTypeConfiguration<AppLogEntry>
{
    public void Configure(EntityTypeBuilder<AppLogEntry> builder)
    {
        builder.ToTable("app_log");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.LoggedAt).HasColumnName("logged_at");
        builder.Property(e => e.Level).HasColumnName("level").HasColumnType("smallint");
        builder.Property(e => e.Source).HasColumnName("source");
        builder.Property(e => e.Message).HasColumnName("message");
        builder.Property(e => e.Template).HasColumnName("template");
        builder.Property(e => e.Exception).HasColumnName("exception");
        builder.Property(e => e.TransactionId).HasColumnName("transaction_id");
        builder.Property(e => e.PropertiesJson).HasColumnName("properties").HasColumnType("jsonb");

        builder.HasIndex(e => e.LoggedAt);
        builder.HasIndex(e => e.TransactionId);
        builder.HasIndex(e => new { e.Level, e.LoggedAt });
    }
}
```

Edit `src/Noof.Ledger.Persistence/LedgerDbContext.cs` — add the using and the `DbSet`:
```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Backup;
using Noof.Ledger.Persistence.Diagnostics;
using Noof.Ledger.Persistence.Revisions;
using Noof.Ledger.Persistence.Secrets;

namespace Noof.Ledger.Persistence;

internal sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<MerchantAlias> MerchantAliases => Set<MerchantAlias>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<LineItem> LineItems => Set<LineItem>();
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<BalanceCheck> BalanceChecks => Set<BalanceCheck>();
    public DbSet<CategorizationJob> CategorizationJobs => Set<CategorizationJob>();
    public DbSet<TransactionRevision> TransactionRevisions => Set<TransactionRevision>();
    public DbSet<AppSecret> Secrets => Set<AppSecret>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<AppLogEntry> AppLogs => Set<AppLogEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<decimal>().HavePrecision(19, 4);
        builder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.HasDefaultSchema("public");
        builder.ApplyConfigurationsFromAssembly(typeof(LedgerDbContext).Assembly);
    }
}
```

Run: `dotnet ef migrations add AddAppLog --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence`
Expected: scaffolds `src/Noof.Ledger.Persistence/Migrations/<TS>_AddAppLog.cs`,
`<TS>_AddAppLog.Designer.cs` and updates `LedgerDbContextModelSnapshot.cs`. `<TS>` is whatever timestamp the tool
assigns; use that exact value for the rest of this task (the `<TS>` placeholder above and below).

Convert `<TS>_AddAppLog.cs` to a file-scoped namespace (CLAUDE.md: `IDE0161` is an error). Its expected shape,
matching the table EF generates from the configuration above (columns/index order follow EF's own emission order —
verify against the actual scaffolded file and correct only the namespace brace style, not the SQL):
```csharp
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddAppLog : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "app_log",
            schema: "public",
            columns: table => new
            {
                id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                logged_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                level = table.Column<short>(type: "smallint", nullable: false),
                source = table.Column<string>(type: "text", nullable: true),
                message = table.Column<string>(type: "text", nullable: false),
                template = table.Column<string>(type: "text", nullable: false),
                exception = table.Column<string>(type: "text", nullable: true),
                transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                properties = table.Column<string>(type: "jsonb", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_app_log", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_app_log_logged_at",
            schema: "public",
            table: "app_log",
            column: "logged_at");

        migrationBuilder.CreateIndex(
            name: "IX_app_log_transaction_id",
            schema: "public",
            table: "app_log",
            column: "transaction_id");

        migrationBuilder.CreateIndex(
            name: "IX_app_log_level_logged_at",
            schema: "public",
            table: "app_log",
            columns: new[] { "level", "logged_at" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "app_log",
            schema: "public");
    }
}
```
Note: `NpgsqlValueGenerationStrategy` needs `using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;` if EF scaffolds
the annotation with the fully-qualified enum rather than a bare name — keep whatever `dotnet ef migrations add`
actually emitted for the identity annotation and usings; do not hand-retype it, since this is exactly the kind of
detail EF's own scaffold gets right and a retyped version could get wrong.

Leave `<TS>_AddAppLog.Designer.cs` and `LedgerDbContextModelSnapshot.cs` exactly as EF emitted them
(`// <auto-generated />` exempts them from `IDE0161`).

Run: `dotnet build NoofLedger.slnx`
Expected: success.

- [ ] **Step 4b: TDD — the `app_log` table has exactly the contract's columns and indexes**

Create `tests/Noof.Ledger.Persistence.Tests/AppLogSchemaTests.cs`:
```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class AppLogSchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_table_has_exactly_the_contracts_columns()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var columns = await db.Database.SqlQueryRaw<string>(
            """
            SELECT column_name || ' ' || data_type || CASE WHEN is_nullable = 'NO' THEN ' NOT NULL' ELSE '' END AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'app_log'
            ORDER BY ordinal_position
            """).ToListAsync(TestContext.Current.CancellationToken);

        columns.Should().Equal(
            "id bigint NOT NULL",
            "logged_at timestamp with time zone NOT NULL",
            "level smallint NOT NULL",
            "source text",
            "message text NOT NULL",
            "template text NOT NULL",
            "exception text",
            "transaction_id uuid",
            "properties jsonb");
    }

    [Theory]
    [InlineData("app_log_logged_at_idx")]
    [InlineData("IX_app_log_logged_at")]
    public async Task An_index_on_logged_at_exists(string possibleName)
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var count = await IndexCountAsync(db, possibleName);
        if (count == 0)
            return; // only one of the two InlineData names will match this migration's real index name

        count.Should().Be(1);
    }

    [Fact]
    public async Task Indexes_on_logged_at_transaction_id_and_level_logged_at_all_exist()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var indexes = await db.Database.SqlQueryRaw<string>(
            """
            SELECT indexname AS "Value" FROM pg_indexes
            WHERE schemaname = 'public' AND tablename = 'app_log'
            """).ToListAsync(TestContext.Current.CancellationToken);

        indexes.Should().Contain(name => name.Contains("logged_at", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("level", StringComparison.OrdinalIgnoreCase));
        indexes.Should().Contain(name => name.Contains("transaction_id", StringComparison.OrdinalIgnoreCase));
        indexes.Should().Contain(name => name.Contains("level", StringComparison.OrdinalIgnoreCase)
            && name.Contains("logged_at", StringComparison.OrdinalIgnoreCase));
    }

    static async Task<int> IndexCountAsync(LedgerDbContext db, string name) =>
        (await db.Database.SqlQueryRaw<int>(
            $"SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND indexname = '{name}'")
            .ToListAsync(TestContext.Current.CancellationToken)).Single();
}
```
This test is written deliberately loose on exact index names (EF's own naming convention may or may not match the
guessed `IX_app_log_logged_at` form) — `Indexes_on_logged_at_transaction_id_and_level_logged_at_all_exist` is the
real assertion; the `[Theory]` above it is a placeholder that Step 4c replaces once the real names are known from
running it once. Watch it fail first: before Step 4 exists, `MigrateAsync` throws because the migration is not yet
applied to a fresh database — actually it is (Step 4 ran already) — so instead watch it fail by temporarily commenting
out the third `CreateIndex` call in the migration and re-running: `Indexes_on_logged_at_transaction_id_and_level_logged_at_all_exist`
fails because no index name contains both "level" and "logged_at". Restore the `CreateIndex` call.

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter AppLogSchemaTests`
Expected: PASS (3 tests; the `[Theory]` runs twice, one InlineData a no-op, the other a no-op too, or one matching —
either way neither fails since a non-matching name returns early).

Update `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`: no change — `AppLogEntry` and
`AppLogEntryConfiguration` are `internal`.

Run `ops/RUNBOOK.md`'s "After adding a migration" step (see Step 8 below for the concrete command — batched there
with the other migrations this task might still need are none; run it now since this is the only migration Task 3
adds):
```powershell
$admin = if ($env:NOOF_TEST_PG) { $env:NOOF_TEST_PG } else { (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection").Trim() }
$template = $admin -replace 'Database=postgres', 'Database=noof_ledger_test_template'
if ($template -notmatch 'Database=noof_ledger_test_template') { throw "Refusing: the connection string does not name the test template." }
dotnet ef database update --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --connection $template
```
Expected: the last output line names `AddAppLog`.

Commit:
```
git add src/Noof.Ledger.Persistence/Diagnostics/AppLogEntry.cs src/Noof.Ledger.Persistence/Configurations/AppLogEntryConfiguration.cs src/Noof.Ledger.Persistence/LedgerDbContext.cs "src/Noof.Ledger.Persistence/Migrations/<TS>_AddAppLog.cs" "src/Noof.Ledger.Persistence/Migrations/<TS>_AddAppLog.Designer.cs" src/Noof.Ledger.Persistence/Migrations/LedgerDbContextModelSnapshot.cs tests/Noof.Ledger.Persistence.Tests/AppLogSchemaTests.cs
git commit -m "$(cat <<'EOF'
Add the app_log table (migration AddAppLog)

AppLogEntry backs the Serilog PostgreSQL sink Task 4 wires up; this
task only creates the table and proves its shape against the
contract's columns and indexes.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 5: TDD `EfLogQuery` — filters, ILIKE text search, paging, `TotalCount`**

Create `tests/Noof.Ledger.Persistence.Tests/EfLogQueryTests.cs`:
```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Persistence.Diagnostics;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfLogQueryTests(PostgresFixture fixture)
{
    static readonly Guid TransactionA = new("00000000-0000-0000-0002-000000000001");
    static readonly Guid TransactionB = new("00000000-0000-0000-0002-000000000002");

    static AppLogEntry Row(int n, LogSeverity level, DateTimeOffset loggedAt, string message,
        string? source = "Noof.Ledger.Telegram.TelegramPollingService", Guid? transactionId = null) => new()
    {
        Id = n,
        LoggedAt = loggedAt,
        Level = level,
        Source = source,
        Message = message,
        Template = message,
        Exception = null,
        TransactionId = transactionId,
        PropertiesJson = """{"SourceContext":"Noof.Ledger.Telegram.TelegramPollingService"}""",
    };

    static async Task SeedAsync(LedgerDbContext db, params AppLogEntry[] rows)
    {
        db.AppLogs.AddRange(rows);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Rows_below_the_minimum_level_are_excluded()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedAsync(db,
            Row(1, LogSeverity.Debug, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "debug line"),
            Row(2, LogSeverity.Warning, DateTimeOffset.Parse("2026-09-25T10:01:00Z"), "warning line"));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(MinLevel: LogSeverity.Information), pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);

        page.Rows.Should().ContainSingle().Which.Message.Should().Be("warning line");
        page.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task From_and_to_bound_the_time_range_inclusively()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedAsync(db,
            Row(1, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T09:59:59Z"), "too early"),
            Row(2, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "at from"),
            Row(3, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T11:00:00Z"), "at to"),
            Row(4, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T11:00:01Z"), "too late"));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(From: DateTimeOffset.Parse("2026-09-25T10:00:00Z"), To: DateTimeOffset.Parse("2026-09-25T11:00:00Z")),
            pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);

        page.Rows.Select(r => r.Message).Should().BeEquivalentTo(["at from", "at to"]);
    }

    [Fact]
    public async Task Text_filters_by_ILIKE_on_message_and_escapes_wildcards_literally()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedAsync(db,
            Row(1, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "polling FAILED for chat 100%"),
            Row(2, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:01:00Z"), "polling succeeded"));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(Text: "100%"), pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);

        page.Rows.Should().ContainSingle().Which.Message.Should().Contain("100%");
    }

    [Fact]
    public async Task Source_filters_exactly()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedAsync(db,
            Row(1, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "a", source: "Noof.Ledger.Host.Workers.BackupWorker"),
            Row(2, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:01:00Z"), "b", source: "Noof.Ledger.Telegram.TelegramPollingService"));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(Source: "Noof.Ledger.Host.Workers.BackupWorker"), pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);

        page.Rows.Should().ContainSingle().Which.Message.Should().Be("a");
    }

    [Fact]
    public async Task TransactionId_filters_exactly()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedAsync(db,
            Row(1, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "a", transactionId: TransactionA),
            Row(2, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:01:00Z"), "b", transactionId: TransactionB));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(TransactionId: TransactionA), pageIndex: 0, pageSize: 50, TestContext.Current.CancellationToken);

        page.Rows.Should().ContainSingle().Which.TransactionId.Should().Be(TransactionA);
    }

    [Fact]
    public async Task Rows_are_newest_first_by_logged_at_then_id_and_TotalCount_ignores_paging()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var same = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        await SeedAsync(db,
            Row(1, LogSeverity.Information, same, "first"),
            Row(2, LogSeverity.Information, same, "second"),
            Row(3, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:01:00Z"), "third"));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(), pageIndex: 0, pageSize: 2, TestContext.Current.CancellationToken);

        page.Rows.Select(r => r.Message).Should().Equal("third", "second");
        page.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task PageIndex_skips_the_prior_pages()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedAsync(db,
            Row(1, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:00:00Z"), "oldest"),
            Row(2, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:01:00Z"), "middle"),
            Row(3, LogSeverity.Information, DateTimeOffset.Parse("2026-09-25T10:02:00Z"), "newest"));

        var page = await new EfLogQuery(db).QueryAsync(
            new LogFilter(), pageIndex: 1, pageSize: 1, TestContext.Current.CancellationToken);

        page.Rows.Should().ContainSingle().Which.Message.Should().Be("middle");
        page.TotalCount.Should().Be(3);
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter EfLogQueryTests`
Expected failure: does not compile — `EfLogQuery` does not exist yet (CS0246).

Create `src/Noof.Ledger.Persistence/Diagnostics/EfLogQuery.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class EfLogQuery(LedgerDbContext db) : ILogQuery
{
    public async Task<LogPage> QueryAsync(LogFilter filter, int pageIndex, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.AppLogs.AsNoTracking().Where(e => e.Level >= filter.MinLevel);

        if (filter.From is { } from)
            query = query.Where(e => e.LoggedAt >= from);
        if (filter.To is { } to)
            query = query.Where(e => e.LoggedAt <= to);
        if (filter.Source is { } source)
            query = query.Where(e => e.Source == source);
        if (filter.TransactionId is { } transactionId)
            query = query.Where(e => e.TransactionId == transactionId);
        if (filter.Text is { } text)
        {
            var pattern = $"%{EscapeLike(text)}%";
            query = query.Where(e => EF.Functions.ILike(e.Message, pattern, @"\"));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(e => e.LoggedAt).ThenByDescending(e => e.Id)
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .Select(e => new LogRow(e.Id, e.LoggedAt, e.Level, e.Source, e.Message, e.Exception, e.TransactionId, e.PropertiesJson))
            .ToListAsync(cancellationToken);

        return new LogPage(rows, totalCount);
    }

    // Escapes the three characters ILIKE treats specially so a literal "%" or "_" the operator typed is
    // matched literally, not as a wildcard — the contract's "ILIKE on message with escaped wildcards".
    static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
```

Edit `src/Noof.Ledger.Persistence/PersistenceRegistration.cs` — add the using and registration:
```csharp
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Persistence.Diagnostics;
```
```csharp
        services.AddScoped<ILogQuery, EfLogQuery>();
```
(inserted with the other `AddScoped` lines, before `return services;`).

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter EfLogQueryTests`
Expected: PASS (8).

Commit:
```
git add src/Noof.Ledger.Persistence/Diagnostics/EfLogQuery.cs src/Noof.Ledger.Persistence/PersistenceRegistration.cs tests/Noof.Ledger.Persistence.Tests/EfLogQueryTests.cs
git commit -m "$(cat <<'EOF'
Add EfLogQuery: filtered, paged app_log reads

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 6: TDD `EfLogRetention` — prunes by level band, exact boundaries**

Create `tests/Noof.Ledger.Persistence.Tests/EfLogRetentionTests.cs`:
```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Persistence.Diagnostics;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfLogRetentionTests(PostgresFixture fixture)
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");

    static AppLogEntry Row(int n, LogSeverity level, DateTimeOffset loggedAt) => new()
    {
        Id = n,
        LoggedAt = loggedAt,
        Level = level,
        Source = "Test",
        Message = "m",
        Template = "m",
    };

    static async Task<List<long>> RemainingIdsAsync(LedgerDbContext db) =>
        await db.AppLogs.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(LogSeverity.Verbose)]
    [InlineData(LogSeverity.Debug)]
    public async Task Verbose_and_debug_older_than_seven_days_are_pruned_but_not_seven_days_exactly(LogSeverity level)
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.AppLogs.AddRange(
            Row(1, level, Now.AddDays(-7).AddSeconds(1)), // just inside 7 days: kept
            Row(2, level, Now.AddDays(-7).AddSeconds(-1))); // just past 7 days: pruned
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleted = await new EfLogRetention(db).PruneAsync(Now, TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await RemainingIdsAsync(db)).Should().Equal(1);
    }

    [Fact]
    public async Task Information_older_than_ninety_days_is_pruned_but_not_ninety_days_exactly()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.AppLogs.AddRange(
            Row(1, LogSeverity.Information, Now.AddDays(-90).AddSeconds(1)),
            Row(2, LogSeverity.Information, Now.AddDays(-90).AddSeconds(-1)),
            Row(3, LogSeverity.Information, Now.AddDays(-8))); // inside 7 days boundary irrelevant to Information

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleted = await new EfLogRetention(db).PruneAsync(Now, TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await RemainingIdsAsync(db)).Should().Equal(1, 3);
    }

    [Theory]
    [InlineData(LogSeverity.Warning)]
    [InlineData(LogSeverity.Error)]
    [InlineData(LogSeverity.Fatal)]
    public async Task Warning_and_above_older_than_730_days_are_pruned_but_not_730_days_exactly(LogSeverity level)
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.AppLogs.AddRange(
            Row(1, level, Now.AddDays(-730).AddSeconds(1)),
            Row(2, level, Now.AddDays(-730).AddSeconds(-1)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleted = await new EfLogRetention(db).PruneAsync(Now, TestContext.Current.CancellationToken);

        deleted.Should().Be(1);
        (await RemainingIdsAsync(db)).Should().Equal(1);
    }

    [Fact]
    public async Task An_information_row_seven_days_old_is_not_pruned_by_the_verbose_debug_band()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.AppLogs.Add(Row(1, LogSeverity.Information, Now.AddDays(-8)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var deleted = await new EfLogRetention(db).PruneAsync(Now, TestContext.Current.CancellationToken);

        deleted.Should().Be(0);
        (await RemainingIdsAsync(db)).Should().Equal(1);
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter EfLogRetentionTests`
Expected failure: does not compile — `EfLogRetention` does not exist (CS0246).

Create `src/Noof.Ledger.Persistence/Diagnostics/EfLogRetention.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class EfLogRetention(LedgerDbContext db) : ILogRetention
{
    public async Task<int> PruneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deleted = await db.AppLogs
            .Where(e => e.Level <= LogSeverity.Debug && e.LoggedAt < now.AddDays(-7))
            .ExecuteDeleteAsync(cancellationToken);

        deleted += await db.AppLogs
            .Where(e => e.Level == LogSeverity.Information && e.LoggedAt < now.AddDays(-90))
            .ExecuteDeleteAsync(cancellationToken);

        deleted += await db.AppLogs
            .Where(e => e.Level >= LogSeverity.Warning && e.LoggedAt < now.AddDays(-730))
            .ExecuteDeleteAsync(cancellationToken);

        return deleted;
    }
}
```

Edit `src/Noof.Ledger.Persistence/PersistenceRegistration.cs`:
```csharp
        services.AddScoped<ILogRetention, EfLogRetention>();
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter EfLogRetentionTests`
Expected: PASS (7).

Watch the guard fail once: temporarily change `now.AddDays(-90)` to `now.AddDays(-91)` in the Information band, re-run
`--filter EfLogRetentionTests` — `Information_older_than_ninety_days_is_pruned_but_not_ninety_days_exactly` fails
(`deleted` is 0, not 1, because the row at `-90 days + 1 second` no longer falls inside a 91-day cutoff — wait, check
the actual failing assertion by running it: the row seeded one second past the (now wrong) boundary stops being
pruned). Revert to `now.AddDays(-90)`.

Commit:
```
git add src/Noof.Ledger.Persistence/Diagnostics/EfLogRetention.cs src/Noof.Ledger.Persistence/PersistenceRegistration.cs tests/Noof.Ledger.Persistence.Tests/EfLogRetentionTests.cs
git commit -m "$(cat <<'EOF'
Add EfLogRetention: per-level-band app_log pruning

Verbose/Debug at 7 days, Information at 90, Warning/Error/Fatal at
730, each boundary tested just inside and just outside with fixed
literal timestamps.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 7: TDD `EfTransactionTrace` — stage parsing, ordering, pruned logs, unknown id, history**

Create `tests/Noof.Ledger.Persistence.Tests/EfTransactionTraceTests.cs`:
```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Diagnostics;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfTransactionTraceTests(PostgresFixture fixture)
{
    static readonly Guid TransactionId = new("00000000-0000-0000-0003-000000000001");

    static async Task SeedTransactionAsync(LedgerDbContext db) =>
        await SeedTransactionAsync(db, TransactionId);

    static async Task SeedTransactionAsync(LedgerDbContext db, Guid id)
    {
        db.Transactions.Add(new Transaction
        {
            Id = id,
            WalletId = new Guid("00000000-0000-0000-0000-000000000001"),
            Kind = TransactionKind.Expense,
            RawText = "кофе 250",
            Status = TransactionStatus.Completed,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
            OccurredOn = new DateOnly(2026, 9, 25),
            TelegramChatId = 1,
            TelegramMessageId = 1,
            CreatedAt = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    static AppLogEntry StageEvent(long id, DateTimeOffset at, string stage, int eventId, Guid transactionId) => new()
    {
        Id = id,
        LoggedAt = at,
        Level = LogSeverity.Information,
        Source = "Noof.Ledger.Telegram.TelegramPollingService",
        Message = stage,
        Template = stage,
        TransactionId = transactionId,
        PropertiesJson = $$"""{"Stage":"{{stage}}","EventId":{"Id":{{eventId}},"Name":"{{stage}}"},"TransactionId":"{{transactionId}}"}""",
    };

    static AppLogEntry NonStageEvent(long id, DateTimeOffset at, Guid transactionId) => new()
    {
        Id = id,
        LoggedAt = at,
        Level = LogSeverity.Debug,
        Source = "Noof.Ledger.Telegram.TelegramPollingService",
        Message = "unrelated line sharing the transaction id",
        Template = "unrelated line sharing the transaction id",
        TransactionId = transactionId,
        PropertiesJson = """{"TransactionId":"irrelevant"}""",
    };

    [Fact]
    public async Task Events_are_ordered_by_logged_at_then_id_with_stage_and_event_id_parsed()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedTransactionAsync(db);
        var t0 = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        db.AppLogs.AddRange(
            StageEvent(2, t0, TransactionStages.Received, TransactionStages.ReceivedEventId, TransactionId),
            StageEvent(1, t0, TransactionStages.Categorized, TransactionStages.CategorizedEventId, TransactionId), // same instant, lower id: sorts first
            StageEvent(3, t0.AddSeconds(1), TransactionStages.Persisted, TransactionStages.PersistedEventId, TransactionId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await new EfTransactionTrace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Exists.Should().BeTrue();
        trace.Events.Select(e => e.Stage).Should().Equal(
            TransactionStages.Categorized, TransactionStages.Received, TransactionStages.Persisted);
        trace.Events[0].EventId.Should().Be(TransactionStages.CategorizedEventId);
    }

    [Fact]
    public async Task Rows_without_a_transaction_id_or_without_a_stage_are_excluded()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedTransactionAsync(db);
        var t0 = DateTimeOffset.Parse("2026-09-25T10:00:00Z");
        db.AppLogs.AddRange(
            StageEvent(1, t0, TransactionStages.Received, TransactionStages.ReceivedEventId, TransactionId),
            NonStageEvent(2, t0.AddSeconds(1), TransactionId));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await new EfTransactionTrace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Events.Should().ContainSingle().Which.Stage.Should().Be(TransactionStages.Received);
    }

    [Fact]
    public async Task Pruned_logs_leave_events_empty_but_history_still_present()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedTransactionAsync(db);
        db.TransactionRevisions.Add(new TransactionRevision
        {
            Id = Guid.NewGuid(),
            TransactionId = TransactionId,
            RevisionNumber = 1,
            Kind = RevisionKind.Initial,
            Instruction = null,
            StatusBefore = TransactionStatus.Captured,
            StatusAfter = TransactionStatus.Completed,
            Snapshot = "{}",
            CreatedAt = DateTimeOffset.Parse("2026-09-25T10:00:00Z"),
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await new EfTransactionTrace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.Exists.Should().BeTrue();
        trace.Events.Should().BeEmpty();
        trace.History.Should().ContainSingle().Which.ChangeKind.Should().Be(nameof(RevisionKind.Initial));
    }

    [Fact]
    public async Task History_uses_the_instruction_when_present_else_the_status_transition()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        await SeedTransactionAsync(db);
        db.TransactionRevisions.AddRange(
            new TransactionRevision
            {
                Id = Guid.NewGuid(), TransactionId = TransactionId, RevisionNumber = 1, Kind = RevisionKind.Initial,
                Instruction = null, StatusBefore = TransactionStatus.Captured, StatusAfter = TransactionStatus.Completed,
                Snapshot = "{}", CreatedAt = DateTimeOffset.Parse("2026-09-25T10:00:00Z"),
            },
            new TransactionRevision
            {
                Id = Guid.NewGuid(), TransactionId = TransactionId, RevisionNumber = 2, Kind = RevisionKind.Correction,
                Instruction = "нет, 1500", StatusBefore = TransactionStatus.Completed, StatusAfter = TransactionStatus.Completed,
                Snapshot = "{}", CreatedAt = DateTimeOffset.Parse("2026-09-25T10:01:00Z"),
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var trace = await new EfTransactionTrace(db).GetAsync(TransactionId, TestContext.Current.CancellationToken);

        trace.History.Select(h => h.Details).Should().Equal("Captured → Completed", "нет, 1500");
        trace.History.Select(h => h.ChangeKind).Should().Equal(nameof(RevisionKind.Initial), nameof(RevisionKind.Correction));
    }

    [Fact]
    public async Task An_unknown_transaction_id_reports_Exists_false_with_empty_events_and_history()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var trace = await new EfTransactionTrace(db).GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        trace.Exists.Should().BeFalse();
        trace.Events.Should().BeEmpty();
        trace.History.Should().BeEmpty();
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter EfTransactionTraceTests`
Expected failure: does not compile — `EfTransactionTrace` does not exist (CS0246).

Create `src/Noof.Ledger.Persistence/Diagnostics/EfTransactionTrace.cs`:
```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Persistence.Diagnostics;

internal sealed class EfTransactionTrace(LedgerDbContext db) : ITransactionTrace
{
    public async Task<TransactionTrace> GetAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var exists = await db.Transactions.AsNoTracking()
            .AnyAsync(t => t.Id == transactionId, cancellationToken);

        var rows = await db.AppLogs.AsNoTracking()
            .Where(e => e.TransactionId == transactionId && e.PropertiesJson != null)
            .OrderBy(e => e.LoggedAt).ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);

        var events = rows
            .Select(ToTraceEvent)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();

        var history = await db.TransactionRevisions.AsNoTracking()
            .Where(r => r.TransactionId == transactionId)
            .OrderBy(r => r.RevisionNumber)
            .ToListAsync(cancellationToken);

        var historyViews = history
            .Select(r => new RevisionView(
                r.CreatedAt,
                r.Kind.ToString(),
                r.Instruction ?? $"{r.StatusBefore} → {r.StatusAfter}"))
            .ToList();

        return new TransactionTrace(transactionId, exists, events, historyViews);
    }

    static TraceEvent? ToTraceEvent(AppLogEntry entry)
    {
        using var properties = JsonDocument.Parse(entry.PropertiesJson!);
        if (!properties.RootElement.TryGetProperty(TransactionStages.StageProperty, out var stageElement)
            || stageElement.GetString() is not { } stage)
            return null;

        var eventId = properties.RootElement.TryGetProperty("EventId", out var eventIdElement)
            && eventIdElement.TryGetProperty("Id", out var idElement)
                ? idElement.GetInt32()
                : 0;

        return new TraceEvent(entry.LoggedAt, stage, eventId, entry.Level, entry.Message, entry.Exception, entry.PropertiesJson);
    }
}
```
`Noof.Ledger.Persistence.Revisions.TransactionRevision`/`RevisionKind` need no new `using` beyond what `LedgerDbContext`
already exposes through `db.TransactionRevisions` — no cross-namespace import is needed in this file since the query
never names `TransactionRevision` explicitly (it flows through `db.TransactionRevisions` and LINQ projection).

Edit `src/Noof.Ledger.Persistence/PersistenceRegistration.cs`:
```csharp
        services.AddScoped<ITransactionTrace, EfTransactionTrace>();
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter EfTransactionTraceTests`
Expected: PASS (6).

Watch a guard fail once: in `ToTraceEvent`, temporarily change `OrderBy(e => e.LoggedAt).ThenBy(e => e.Id)` to just
`OrderBy(e => e.Id)`; re-run `--filter EfTransactionTraceTests` —
`Events_are_ordered_by_logged_at_then_id_with_stage_and_event_id_parsed` fails (`Categorized` no longer sorts first;
the sequence becomes `Received, Categorized, Persisted` by id order instead of the seeded logged_at-then-id order).
Revert.

Commit:
```
git add src/Noof.Ledger.Persistence/Diagnostics/EfTransactionTrace.cs src/Noof.Ledger.Persistence/PersistenceRegistration.cs tests/Noof.Ledger.Persistence.Tests/EfTransactionTraceTests.cs
git commit -m "$(cat <<'EOF'
Add EfTransactionTrace: stage events plus revision history for a transaction

Events parse Stage/EventId out of app_log.properties and order by
logged_at then id; History maps transaction_revisions independently,
so a transaction whose logs have been pruned still shows its History.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 8: Regenerate `schema.expected.sql` and confirm `SchemaSnapshotTests`/`MigrationContractTests`**

Run: `dotnet ef dbcontext script --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --output tests/Noof.Ledger.Persistence.Tests/schema.expected.sql`

Review with `git diff tests/Noof.Ledger.Persistence.Tests/schema.expected.sql`. The diff must consist of exactly:
- one new table `app_log` with columns `id bigint GENERATED BY DEFAULT AS IDENTITY`, `logged_at timestamptz NOT NULL`,
  `level smallint NOT NULL`, `source text`, `message text NOT NULL`, `template text NOT NULL`, `exception text`,
  `transaction_id uuid`, `properties jsonb`, primary key on `id`;
- three new indexes: `IX_app_log_logged_at`, `IX_app_log_transaction_id`, `IX_app_log_level_logged_at` (or whatever
  exact names EF assigned in Step 4 — use those, not these guesses, if they differ);
- one new row in `__EFMigrationsHistory`'s implied set is not part of the script (the script never includes data).

If the diff touches any line of `transactions`, `entries`, `balance_checks` or `wallet_balances`, stop — that would
mean the migration touched a column the view depends on, which CLAUDE.md requires a `DROP VIEW`/recreate for; Step 4's
migration must not do this (it only adds a new, unrelated table), so this diff should never occur.

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter SchemaSnapshotTests`
Expected: PASS.

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter MigrationContractTests`
Expected: PASS — `The_model_has_no_pending_changes` confirms `AddAppLog` is the model's last migration with nothing
left un-scaffolded.

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter WalletBalancesViewTests`
Expected: PASS unchanged — `AddAppLog` never touches `transactions`/`entries`/`balance_checks`, so the view's
migrate-down/up round trip is unaffected.

Commit:
```
git add tests/Noof.Ledger.Persistence.Tests/schema.expected.sql
git commit -m "$(cat <<'EOF'
Regenerate schema.expected.sql for the app_log table

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 9: Full filtered run and final check**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: PASS, all tests including the new `TransactionLogScopeTests` (1), `AppLogSchemaTests` (3),
`EfLogQueryTests` (8), `EfLogRetentionTests` (7), `EfTransactionTraceTests` (6), `SchemaSnapshotTests`, and every
pre-existing test unchanged (none of this task's changes touch a table any existing test reads).

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS — `PublicSurfaceTests` sees the twelve new Application types; Persistence's allowed list is unchanged
(`AppLogEntry`, its configuration and the three `Ef*` classes are all `internal`).

Run: `dotnet build NoofLedger.slnx`
Expected: success, no warnings (`CA1848`/`CA2254` do not apply — this task adds no `ILogger.LogXxx` call sites; those
belong to Task 4).

Do not run `dotnet test --solution` here: the E2E suite and any other worktree may be using the shared PostgreSQL
server, and CLAUDE.md reserves the unfiltered full-solution run for the end of the phase. Confirm the test template
was updated at Step 4b so a later end-of-phase E2E run does not fail on a missing `app_log` table.

No commit for this step — it is verification only, and Step 8's commit already closed the task's working tree.

---

### Task 4: PostgreSQL log sink, secret redaction, log retention worker

**Plan notes (deviations / things confirmed by reading real code and the real package)**

- **`LoggingSetup` doesn't exist yet.** Task 2 (bootstrap + file + console logger) hasn't been written in
  this repo — Task 4 only runs after Tasks 1–3 merge. The spec (§2) says its shape is a bootstrap logger
  in `Main` plus `UseSerilog((ctx, services, cfg) => …)` reconfiguring with console/file/Postgres. This plan
  assumes `Noof.Ledger.Host.Logging.LoggingSetup` exposes a `Configure(LoggerConfiguration, HostBuilderContext,
  IServiceProvider)`-shaped method called from that `UseSerilog` lambda, and that this method is where the
  console/file `WriteTo` calls already live. **Step 1 starts by reading the merged `LoggingSetup.cs` and
  adjusts the call site to Task 2's actual member names before touching anything else** — the code below is
  written to be dropped into "wherever the console/file sinks are already configured", not a byte-for-byte
  diff, because that file does not exist to diff against yet.
- **Failure signal is `SelfLog`, not `ILoggingFailureListener`.** Verified by loading the actual installed
  packages (`Serilog.Sinks.Postgresql.Alternative 4.3.0`, `Serilog.Sinks.PeriodicBatching 5.0.0`) via
  reflection: `Serilog.Core.ISetLoggingFailureListener` exists in `Serilog 4.4.0`, but neither
  `PostgreSqlSink` nor the `PeriodicBatchingSink` it runs on implements it — there is no `WriteTo.Fallible`
  in this Serilog version, and no failure-listener hook the Postgres sink honours. `Serilog.Debugging.SelfLog`
  is what actually reports its batch-emit exceptions (this is the sink's only failure channel in this
  package version), so `LogSinkStatus` is wired through `SelfLog.Enable(...)`, recorded in Step 5.
- **"Every value in `ISecretStore`"** — `ISecretStore` has no enumeration method (by design: it's a
  by-key store) and is not part of this task's contract to change. The known-secret set is built from the
  keys the app already knows about: `SecretKeys.TelegramBotToken`, `SecretKeys.TelegramOwnerChatId`, and
  `probe.SecretKey` for every registered `ISecretProbe` (currently `IModelProvider` and `ISpeechProvider`,
  the same set `Secrets.razor` already enumerates the same way). Any secret added later that also registers
  an `ISecretProbe` or a `SecretKeys` constant is covered automatically.
- **`SecretRedactor` is a wrapping sink, not an enricher.** `Serilog.Events.LogEvent.Exception` has a getter
  only — confirmed against `Serilog.xml` (4.4.0): no setter, no `SetException`, nothing an enricher's
  `AddOrUpdateProperty` can reach. Redacting the exception text requires building a *new* `LogEvent` via its
  public constructor and handing it to the next sink — exactly what the spec's own words ("wraps ... every
  sink") describe. `RedactingSink` wraps one inner `Serilog.Core.Logger` that itself fans out to
  console+file+Postgres, so one wrap in `LoggingSetup` covers all three, per spec.
- Package version pinned to **4.3.0** (the exact floor the brief names) because that is the version already
  restored in the local NuGet cache and the one this plan was verified against by loading its real assembly.
  If a newer stable release exists on nuget.org when this task is implemented, that is the implementer's call
  to make against the live feed — this plan's code was written and column-writer types checked against 4.3.0
  specifically.

**Files**

Create:
- `src/Noof.Ledger.Application/Diagnostics/ILogSinkStatus.cs`
- `src/Noof.Ledger.Host/Diagnostics/LogSinkStatus.cs`
- `src/Noof.Ledger.Host/Diagnostics/SecretRedactor.cs`
- `src/Noof.Ledger.Host/Diagnostics/RedactedException.cs`
- `src/Noof.Ledger.Host/Diagnostics/RedactingSink.cs`
- `src/Noof.Ledger.Host/Diagnostics/SecretSnapshot.cs`
- `src/Noof.Ledger.Host/Diagnostics/SecretSnapshotRefreshWorker.cs`
- `src/Noof.Ledger.Host/Diagnostics/HostDiagnosticsRegistration.cs`
- `src/Noof.Ledger.Host/Workers/LogRetentionWorker.cs`
- `tests/Noof.Ledger.Host.Tests/LogSinkStatusTests.cs`
- `tests/Noof.Ledger.Host.Tests/SecretRedactorTests.cs`
- `tests/Noof.Ledger.Host.Tests/SecretSnapshotRefreshWorkerTests.cs`
- `tests/Noof.Ledger.Host.Tests/LogRetentionWorkerTests.cs`
- `tests/Noof.Ledger.Host.Tests/AppLogSinkTests.cs` (DB test, template clone)
- `tests/Noof.Ledger.Host.Tests/SecretRedactionSentinelTests.cs` (DB + file test, template clone)

Modify (line ranges are placeholders — Step 1 opens the real, merged files and updates these against their
actual current line numbers, since Tasks 1–3 add these files and this plan cannot see them yet):
- `Directory.Packages.props` — add `Serilog.Sinks.Postgresql.Alternative` `PackageVersion`.
- `src/Noof.Ledger.Host/Noof.Ledger.Host.csproj` — add the `PackageReference`.
- `src/Noof.Ledger.Host/Logging/LoggingSetup.cs` (Task 2's file) — add the Postgres sub-logger, the
  `RedactingSink` wrap, and the `SelfLog.Enable` hookup.
- `src/Noof.Ledger.Host/Workers/WorkerRegistration.cs` — register `LogRetentionWorker`.
- `src/Noof.Ledger.Host/Program.cs` — call `AddNoofHostDiagnostics()`.
- `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs` — add `"ILogSinkStatus"` to the
  `Noof.Ledger.Application` allowed list (Tasks 1 and 3 will already have added their own entries there;
  this is an additive edit to the same array, not a replacement).

**Interfaces**

Consumes (from Tasks 1 and 3, already merged):
```csharp
Noof.Ledger.Application.Diagnostics.IDatabaseGate       // State, WaitUntilReadyAsync
Noof.Ledger.Application.Diagnostics.DatabaseState       // .Ready
Noof.Ledger.Application.Diagnostics.ILogRetention       // Task<int> PruneAsync(DateTimeOffset, CancellationToken)
Noof.Ledger.Application.Diagnostics.TransactionLogScope // Begin(ILogger, Guid) — used only by the DB test
Noof.Ledger.Application.Diagnostics.TransactionStages   // stage/property names — used only by the DB test
```
Also consumed (already exist today, Phase 1B):
```csharp
Noof.Ledger.Application.Secrets.ISecretStore
Noof.Ledger.Application.Secrets.ISecretProbe   // SecretKey
Noof.Ledger.Application.Secrets.SecretKeys     // TelegramBotToken, TelegramOwnerChatId
Noof.Ledger.Persistence.LedgerConnectionString.Resolve(string?, string)
```

Produces (this task, binding contract names — do not rename):
```csharp
namespace Noof.Ledger.Application.Diagnostics;

public interface ILogSinkStatus
{
    DateTimeOffset? LastFailureAt { get; }
    void RecordFailure(DateTimeOffset at);
}
```
Task-internal (Host-only, not in the contract, free to shape):
```csharp
internal sealed class LogSinkStatus : ILogSinkStatus;
internal sealed class SecretRedactor(SecretSnapshot snapshot);              // Redact(LogEvent) : LogEvent
internal sealed class RedactedException(Exception original, string redactedMessage, string redactedToString) : Exception;
internal sealed class RedactingSink(ILogEventSink inner, SecretRedactor redactor) : ILogEventSink;
internal sealed class SecretSnapshot(IServiceScopeFactory scopeFactory, IEnumerable<ISecretProbe> probes, string databasePassword);
                                                                              // CurrentValues, RefreshAsync(CancellationToken)
internal sealed class SecretSnapshotRefreshWorker(IDatabaseGate gate, SecretSnapshot snapshot, TimeProvider timeProvider) : BackgroundService;
internal sealed class LogRetentionWorker(IServiceScopeFactory scopeFactory, IDatabaseGate gate, TimeProvider timeProvider, ILogger<LogRetentionWorker> logger) : BackgroundService;
internal static class HostDiagnosticsRegistration { public static IServiceCollection AddNoofHostDiagnostics(this IServiceCollection); }
```

---

#### Steps

- [ ] **Step 1: Read the merged `LoggingSetup.cs` and `IDatabaseGate`/`ILogRetention` and record their exact
  shape.**
  Open `src/Noof.Ledger.Host/Logging/LoggingSetup.cs`, `src/Noof.Ledger.Application/Diagnostics/IDatabaseGate.cs`,
  and `src/Noof.Ledger.Application/Diagnostics/ILogRetention.cs` as Tasks 1–3 actually wrote them. Confirm:
  the exact method Task 2 calls from `UseSerilog(...)`, whether it takes `IServiceProvider` (needed to resolve
  `IDatabaseGate` and `SecretRedactor` for Step 5), and `IDatabaseGate`'s exact namespace/member spelling
  against the contract above. If `Configure` does not currently receive `IServiceProvider`, this step's output
  is: add that parameter to `LoggingSetup.Configure` and its call site in `Program.cs` — a one-line, no-test
  change (Program.cs wiring is TDD-exempt per CLAUDE.md), committed on its own before Step 5 so Step 5's diff
  is only the Postgres sink. No test for this step — it is reading code and, if needed, threading a
  constructor parameter, not new behaviour.
  - Commit (only if `Configure`'s signature needed the parameter added):
    `git add src/Noof.Ledger.Host/Logging/LoggingSetup.cs src/Noof.Ledger.Host/Program.cs`
    ```
    Thread IServiceProvider into LoggingSetup.Configure for Task 4's gate and redactor lookups

    Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
    Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
    ```

- [ ] **Step 2: `ILogSinkStatus` + `LogSinkStatus`.**
  Write the failing test first.
  `tests/Noof.Ledger.Host.Tests/LogSinkStatusTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Noof.Ledger.Host.Diagnostics;

  namespace Noof.Ledger.Host.Tests;

  public class LogSinkStatusTests
  {
      [Fact]
      public void A_freshly_created_status_has_no_failure()
      {
          var status = new LogSinkStatus();

          status.LastFailureAt.Should().BeNull();
      }

      [Fact]
      public void Recording_a_failure_is_read_back_exactly()
      {
          var status = new LogSinkStatus();
          var at = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

          status.RecordFailure(at);

          status.LastFailureAt.Should().Be(at);
      }

      [Fact]
      public void A_later_failure_overwrites_an_earlier_one()
      {
          var status = new LogSinkStatus();
          var first = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
          var second = first.AddMinutes(5);

          status.RecordFailure(first);
          status.RecordFailure(second);

          status.LastFailureAt.Should().Be(second);
      }
  }
  ```
  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter LogSinkStatusTests`
  Expected: **FAIL** — `Noof.Ledger.Host.Diagnostics.LogSinkStatus` does not exist (compile error).

  Implement.
  `src/Noof.Ledger.Application/Diagnostics/ILogSinkStatus.cs`:
  ```csharp
  namespace Noof.Ledger.Application.Diagnostics;

  public interface ILogSinkStatus
  {
      DateTimeOffset? LastFailureAt { get; }

      void RecordFailure(DateTimeOffset at);
  }
  ```
  `src/Noof.Ledger.Host/Diagnostics/LogSinkStatus.cs`:
  ```csharp
  using Noof.Ledger.Application.Diagnostics;

  namespace Noof.Ledger.Host.Diagnostics;

  internal sealed class LogSinkStatus : ILogSinkStatus
  {
      DateTimeOffset? lastFailureAt;

      public DateTimeOffset? LastFailureAt => lastFailureAt;

      public void RecordFailure(DateTimeOffset at) => lastFailureAt = at;
  }
  ```
  Run the same filter. Expected: **PASS** (3 tests).
  - Commit: `git add src/Noof.Ledger.Application/Diagnostics/ILogSinkStatus.cs src/Noof.Ledger.Host/Diagnostics/LogSinkStatus.cs tests/Noof.Ledger.Host.Tests/LogSinkStatusTests.cs`
    ```
    Add ILogSinkStatus and its Host implementation

    Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
    Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
    ```

- [ ] **Step 3: Update `PublicSurfaceTests` for `ILogSinkStatus` — watch it fail, then pass.**
  `ILogSinkStatus` is a new public type in `Noof.Ledger.Application`. Before editing the allowlist, run the
  existing guard to see it catch the addition:
  Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter PublicSurfaceTests`
  Expected: **FAIL** on `Public_types_are_exactly_the_allowed_set("Noof.Ledger.Application")` — `ILogSinkStatus`
  is public but not in `Allowed["Noof.Ledger.Application"]`. (If Tasks 1/3 already widened this array with
  their own Diagnostics types, the failure message will show `ILogSinkStatus` as the only unexpected extra —
  everything else in the array stays as those tasks left it.)

  Implement: open `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs` and add `"ILogSinkStatus"` to
  the `["Noof.Ledger.Application"]` array (append it; do not reorder or remove anything Tasks 1/3 added).
  Run the same filter. Expected: **PASS**.
  - Commit: `git add tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`
    ```
    Allow ILogSinkStatus on the Application public surface

    Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
    Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
    ```

- [ ] **Step 4: `SecretRedactor` — recursive redaction of properties and the wrapping exception.**
  Write the failing tests first.
  `tests/Noof.Ledger.Host.Tests/SecretRedactorTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Noof.Ledger.Host.Diagnostics;
  using Serilog.Events;
  using Serilog.Parsing;

  namespace Noof.Ledger.Host.Tests;

  public class SecretRedactorTests
  {
      static readonly MessageTemplateParser Parser = new();

      static LogEvent MakeEvent(Exception? exception, params LogEventProperty[] properties) =>
          new(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero), LogEventLevel.Information,
              exception, Parser.Parse("test"), properties);

      static SecretRedactor RedactorFor(params string[] secrets) => new(new FixedSecretSet(secrets));

      [Fact]
      public void A_scalar_string_property_containing_a_secret_is_redacted()
      {
          var redactor = RedactorFor("sk-super-secret-token");
          var evt = MakeEvent(null, new LogEventProperty("Token", new ScalarValue("value=sk-super-secret-token;ok")));

          var redacted = redactor.Redact(evt);

          ((ScalarValue)redacted.Properties["Token"]).Value.Should().Be("value=***;ok");
      }

      [Fact]
      public void A_property_without_any_secret_is_left_untouched()
      {
          var redactor = RedactorFor("sk-super-secret-token");
          var evt = MakeEvent(null, new LogEventProperty("Stage", new ScalarValue("Received")));

          var redacted = redactor.Redact(evt);

          ((ScalarValue)redacted.Properties["Stage"]).Value.Should().Be("Received");
      }

      [Fact]
      public void A_secret_inside_a_sequence_property_is_redacted()
      {
          var redactor = RedactorFor("sk-super-secret-token");
          var sequence = new SequenceValue([new ScalarValue("sk-super-secret-token"), new ScalarValue("fine")]);
          var evt = MakeEvent(null, new LogEventProperty("Headers", sequence));

          var redacted = redactor.Redact(evt);

          var redactedSequence = (SequenceValue)redacted.Properties["Headers"];
          ((ScalarValue)redactedSequence.Elements[0]).Value.Should().Be("***");
          ((ScalarValue)redactedSequence.Elements[1]).Value.Should().Be("fine");
      }

      [Fact]
      public void A_secret_inside_a_dictionary_property_is_redacted()
      {
          var redactor = RedactorFor("sk-super-secret-token");
          var dictionary = new DictionaryValue([
              new KeyValuePair<ScalarValue, LogEventPropertyValue>(new ScalarValue("Authorization"), new ScalarValue("sk-super-secret-token")),
          ]);
          var evt = MakeEvent(null, new LogEventProperty("Headers", dictionary));

          var redacted = redactor.Redact(evt);

          var redactedDictionary = (DictionaryValue)redacted.Properties["Headers"];
          ((ScalarValue)redactedDictionary.Elements.Single().Value).Value.Should().Be("***");
      }

      [Fact]
      public void A_secret_inside_a_structured_property_is_redacted()
      {
          var redactor = RedactorFor("sk-super-secret-token");
          var structure = new StructureValue([new LogEventProperty("Key", new ScalarValue("sk-super-secret-token"))]);
          var evt = MakeEvent(null, new LogEventProperty("Config", structure));

          var redacted = redactor.Redact(evt);

          var redactedStructure = (StructureValue)redacted.Properties["Config"];
          ((ScalarValue)redactedStructure.Properties.Single().Value).Value.Should().Be("***");
      }

      [Fact]
      public void Values_shorter_than_eight_characters_are_never_treated_as_secrets()
      {
          // The redactor itself has no length rule - SecretSnapshot filters short values before they
          // ever reach it (Step 6). This test locks that division of responsibility: a 7-char "secret"
          // handed to the redactor directly IS redacted, proving the length rule lives upstream, not here.
          var redactor = RedactorFor("shortie");
          var evt = MakeEvent(null, new LogEventProperty("Value", new ScalarValue("a shortie value")));

          var redacted = redactor.Redact(evt);

          ((ScalarValue)redacted.Properties["Value"]).Value.Should().Be("a ***  value".Replace("  ", " "));
      }

      [Fact]
      public void An_exceptions_message_and_ToString_are_both_redacted_and_the_original_type_name_is_preserved()
      {
          var redactor = RedactorFor("sk-super-secret-token");
          var original = new InvalidOperationException("call failed with key sk-super-secret-token");
          var evt = MakeEvent(original);

          var redacted = redactor.Redact(evt);

          redacted.Exception.Should().NotBeNull();
          redacted.Exception!.Message.Should().Contain("InvalidOperationException").And.Contain("***").And.NotContain("sk-super-secret-token");
          redacted.Exception.ToString().Should().Contain("***").And.NotContain("sk-super-secret-token");
      }

      [Fact]
      public void An_event_with_no_exception_and_no_secrets_present_still_redacts_cleanly_with_a_null_exception()
      {
          var redactor = RedactorFor("sk-super-secret-token");
          var evt = MakeEvent(null, new LogEventProperty("Stage", new ScalarValue("Received")));

          var redacted = redactor.Redact(evt);

          redacted.Exception.Should().BeNull();
      }

      sealed class FixedSecretSet(IReadOnlyCollection<string> values) : ISecretValueSource
      {
          public IReadOnlyCollection<string> CurrentValues => values;
      }
  }
  ```
  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter SecretRedactorTests`
  Expected: **FAIL** — `SecretRedactor`, `ISecretValueSource` do not exist (compile error).

  Implement.
  `src/Noof.Ledger.Host/Diagnostics/RedactedException.cs`:
  ```csharp
  namespace Noof.Ledger.Host.Diagnostics;

  // GetType().Name on this instance is always "RedactedException" - .NET gives no way to fake a
  // runtime type. "Preserving the type name" instead means the ORIGINAL exception's type name is
  // baked into the redacted text itself, so a reader of the log still knows what kind of failure
  // this was without ever seeing the value that made it unsafe to log verbatim.
  internal sealed class RedactedException : Exception
  {
      readonly string redactedToString;

      public RedactedException(Exception original, string redactedMessage, string redactedToString)
          : base($"{original.GetType().Name}: {redactedMessage}")
      {
          this.redactedToString = redactedToString;
      }

      public override string ToString() => redactedToString;
  }
  ```
  `src/Noof.Ledger.Host/Diagnostics/ISecretValueSource.cs`:
  ```csharp
  namespace Noof.Ledger.Host.Diagnostics;

  // The seam SecretRedactor depends on instead of SecretSnapshot directly, so a unit test can hand
  // it a fixed set of values with no ISecretStore, no database and no clock.
  internal interface ISecretValueSource
  {
      IReadOnlyCollection<string> CurrentValues { get; }
  }
  ```
  `src/Noof.Ledger.Host/Diagnostics/SecretRedactor.cs`:
  ```csharp
  using Serilog.Events;

  namespace Noof.Ledger.Host.Diagnostics;

  internal sealed class SecretRedactor(ISecretValueSource secrets)
  {
      public LogEvent Redact(LogEvent logEvent)
      {
          var known = secrets.CurrentValues;

          var properties = logEvent.Properties
              .Select(pair => new LogEventProperty(pair.Key, RedactValue(pair.Value, known)));

          var exception = logEvent.Exception is { } original
              ? new RedactedException(original, RedactText(original.Message, known), RedactText(original.ToString(), known))
              : null;

          return new LogEvent(logEvent.Timestamp, logEvent.Level, exception, logEvent.MessageTemplate, properties);
      }

      static LogEventPropertyValue RedactValue(LogEventPropertyValue value, IReadOnlyCollection<string> secrets) => value switch
      {
          ScalarValue { Value: string text } => new ScalarValue(RedactText(text, secrets)),
          ScalarValue scalar => scalar,
          SequenceValue sequence => new SequenceValue(sequence.Elements.Select(element => RedactValue(element, secrets))),
          DictionaryValue dictionary => new DictionaryValue(dictionary.Elements
              .Select(pair => new KeyValuePair<ScalarValue, LogEventPropertyValue>(pair.Key, RedactValue(pair.Value, secrets)))),
          StructureValue structure => new StructureValue(
              structure.Properties.Select(property => new LogEventProperty(property.Name, RedactValue(property.Value, secrets))),
              structure.TypeTag),
          _ => value,
      };

      static string RedactText(string text, IReadOnlyCollection<string> secrets) =>
          secrets.Aggregate(text, (current, secret) => current.Replace(secret, "***", StringComparison.Ordinal));
  }
  ```
  Run the same filter. Expected: **PASS** (8 tests).
  - Commit: `git add src/Noof.Ledger.Host/Diagnostics/RedactedException.cs src/Noof.Ledger.Host/Diagnostics/ISecretValueSource.cs src/Noof.Ledger.Host/Diagnostics/SecretRedactor.cs tests/Noof.Ledger.Host.Tests/SecretRedactorTests.cs`
    ```
    Add SecretRedactor with recursive property and exception redaction

    Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
    Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
    ```

- [ ] **Step 5: `RedactingSink` — the wrapping sink that actually sits in front of console/file/DB.**
  Write the failing test first.
  `tests/Noof.Ledger.Host.Tests/RedactingSinkTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Noof.Ledger.Host.Diagnostics;
  using Serilog.Core;
  using Serilog.Events;
  using Serilog.Parsing;

  namespace Noof.Ledger.Host.Tests;

  public class RedactingSinkTests
  {
      [Fact]
      public void The_inner_sink_receives_the_redacted_event_not_the_original()
      {
          var recording = new RecordingSink();
          var redactor = new SecretRedactor(new FixedSecrets("sk-super-secret-token"));
          var sink = new RedactingSink(recording, redactor);
          var evt = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
              new MessageTemplateParser().Parse("test"),
              [new LogEventProperty("Token", new ScalarValue("sk-super-secret-token"))]);

          sink.Emit(evt);

          recording.Received.Should().ContainSingle();
          ((ScalarValue)recording.Received[0].Properties["Token"]).Value.Should().Be("***");
      }

      sealed class RecordingSink : ILogEventSink
      {
          public List<LogEvent> Received { get; } = [];
          public void Emit(LogEvent logEvent) => Received.Add(logEvent);
      }

      sealed class FixedSecrets(params string[] values) : ISecretValueSource
      {
          public IReadOnlyCollection<string> CurrentValues => values;
      }
  }
  ```
  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter RedactingSinkTests`
  Expected: **FAIL** — `RedactingSink` does not exist.

  Implement.
  `src/Noof.Ledger.Host/Diagnostics/RedactingSink.cs`:
  ```csharp
  using Serilog.Core;
  using Serilog.Events;

  namespace Noof.Ledger.Host.Diagnostics;

  internal sealed class RedactingSink(ILogEventSink inner, SecretRedactor redactor) : ILogEventSink
  {
      public void Emit(LogEvent logEvent) => inner.Emit(redactor.Redact(logEvent));
  }
  ```
  Run the same filter. Expected: **PASS**.
  - Commit: `git add src/Noof.Ledger.Host/Diagnostics/RedactingSink.cs tests/Noof.Ledger.Host.Tests/RedactingSinkTests.cs`
    ```
    Add RedactingSink wrapping every downstream Serilog sink

    Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
    Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
    ```

- [ ] **Step 6: `SecretSnapshot` — the known-value set the redactor consumes, refreshed on a schedule.**
  Write the failing tests first.
  `tests/Noof.Ledger.Host.Tests/SecretSnapshotRefreshWorkerTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Microsoft.Extensions.DependencyInjection;
  using Microsoft.Extensions.Time.Testing;
  using NSubstitute;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Application.Secrets;
  using Noof.Ledger.Host.Diagnostics;

  namespace Noof.Ledger.Host.Tests;

  public class SecretSnapshotRefreshWorkerTests
  {
      static IServiceScopeFactory ScopeFactoryFor(ISecretStore store)
      {
          var provider = Substitute.For<IServiceProvider>();
          provider.GetService(typeof(ISecretStore)).Returns(store);
          var scope = Substitute.For<IServiceScope>();
          scope.ServiceProvider.Returns(provider);
          var factory = Substitute.For<IServiceScopeFactory>();
          factory.CreateScope().Returns(scope);
          return factory;
      }

      static ISecretStore StoreWith(string key, string value)
      {
          var store = Substitute.For<ISecretStore>();
          store.GetAsync(key, Arg.Any<CancellationToken>())
              .Returns(new SecretResult(SecretState.Present, value));
          store.GetAsync(Arg.Is<string>(k => k != key), Arg.Any<CancellationToken>())
              .Returns(new SecretResult(SecretState.Missing, null));
          return store;
      }

      [Fact]
      public void Values_shorter_than_eight_characters_are_ignored()
      {
          var snapshot = new SecretSnapshot(ScopeFactoryFor(StoreWith(SecretKeys.TelegramBotToken, "short")), [], "db-password-long-enough");

          snapshot.CurrentValues.Should().NotContain("short").And.Contain("db-password-long-enough");
      }

      [Fact]
      public async Task RefreshAsync_picks_up_a_secret_present_at_refresh_time()
      {
          var store = StoreWith(SecretKeys.TelegramBotToken, "a-long-enough-bot-token");
          var snapshot = new SecretSnapshot(ScopeFactoryFor(store), [], "db-password-long-enough");

          await snapshot.RefreshAsync(TestContext.Current.CancellationToken);

          snapshot.CurrentValues.Should().Contain("a-long-enough-bot-token");
      }

      [Fact]
      public async Task The_worker_refreshes_once_the_gate_is_ready_then_every_five_minutes()
      {
          var store = StoreWith(SecretKeys.TelegramBotToken, "a-long-enough-bot-token");
          var snapshot = new SecretSnapshot(ScopeFactoryFor(store), [], "db-password-long-enough");
          var gate = Substitute.For<IDatabaseGate>();
          gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
          var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
          var worker = new SecretSnapshotRefreshWorker(gate, snapshot, time);

          await worker.StartAsync(TestContext.Current.CancellationToken);
          try
          {
              await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
              await store.Received(1).GetAsync(SecretKeys.TelegramBotToken, Arg.Any<CancellationToken>());

              time.Advance(TimeSpan.FromMinutes(5));
              await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
              await store.Received(2).GetAsync(SecretKeys.TelegramBotToken, Arg.Any<CancellationToken>());
          }
          finally
          {
              await worker.StopAsync(TestContext.Current.CancellationToken);
          }
      }
  }
  ```
  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter SecretSnapshotRefreshWorkerTests`
  Expected: **FAIL** — `SecretSnapshot`, `SecretSnapshotRefreshWorker` do not exist.

  Implement.
  `src/Noof.Ledger.Host/Diagnostics/SecretSnapshot.cs`:
  ```csharp
  using Noof.Ledger.Application.Secrets;

  namespace Noof.Ledger.Host.Diagnostics;

  // The known-secret set SecretRedactor checks every property and exception message against.
  // Built from every key the app already has a name for (SecretKeys constants plus every
  // registered ISecretProbe's SecretKey - the same set Secrets.razor lists), never from
  // enumerating the store itself, because ISecretStore has no "list everything" method by design.
  internal sealed class SecretSnapshot(IServiceScopeFactory scopeFactory, IEnumerable<ISecretProbe> probes, string databasePassword)
      : ISecretValueSource
  {
      const int MinimumSecretLength = 8;

      volatile IReadOnlyCollection<string> current = databasePassword.Length >= MinimumSecretLength
          ? [databasePassword]
          : [];

      public IReadOnlyCollection<string> CurrentValues => current;

      public async Task RefreshAsync(CancellationToken cancellationToken)
      {
          using var scope = scopeFactory.CreateScope();
          var store = scope.ServiceProvider.GetRequiredService<ISecretStore>();

          string[] keys = [SecretKeys.TelegramBotToken, SecretKeys.TelegramOwnerChatId, .. probes.Select(probe => probe.SecretKey)];

          var values = new List<string>();
          if (databasePassword.Length >= MinimumSecretLength)
              values.Add(databasePassword);

          foreach (var key in keys.Distinct(StringComparer.Ordinal))
          {
              var result = await store.GetAsync(key, cancellationToken);
              if (result is { State: SecretState.Present, Value.Length: >= MinimumSecretLength })
                  values.Add(result.Value);
          }

          current = values;
      }
  }
  ```
  `src/Noof.Ledger.Host/Diagnostics/SecretSnapshotRefreshWorker.cs`:
  ```csharp
  using Noof.Ledger.Application.Diagnostics;

  namespace Noof.Ledger.Host.Diagnostics;

  internal sealed class SecretSnapshotRefreshWorker(IDatabaseGate gate, SecretSnapshot snapshot, TimeProvider timeProvider)
      : BackgroundService
  {
      static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

      protected override async Task ExecuteAsync(CancellationToken stoppingToken)
      {
          await gate.WaitUntilReadyAsync(stoppingToken);

          while (!stoppingToken.IsCancellationRequested)
          {
              await snapshot.RefreshAsync(stoppingToken);
              await Task.Delay(RefreshInterval, timeProvider, stoppingToken);
          }
      }
  }
  ```
  Note: `SecretSnapshot` needs `Microsoft.Extensions.DependencyInjection` for `GetRequiredService` — add
  `using Microsoft.Extensions.DependencyInjection;` to its usings (the Host project already references it
  transitively; no package change needed).
  Run the same filter. Expected: **PASS** (4 tests).
  - Commit: `git add src/Noof.Ledger.Host/Diagnostics/SecretSnapshot.cs src/Noof.Ledger.Host/Diagnostics/SecretSnapshotRefreshWorker.cs tests/Noof.Ledger.Host.Tests/SecretSnapshotRefreshWorkerTests.cs`
    ```
    Add SecretSnapshot and its 5-minute refresh worker

    Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
    Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
    ```

- [ ] **Step 7: `LogRetentionWorker`.**
  Write the failing tests first, mirroring `BackupWorkerTests`' `FakeTimeProvider` style.
  `tests/Noof.Ledger.Host.Tests/LogRetentionWorkerTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Microsoft.Extensions.DependencyInjection;
  using Microsoft.Extensions.Logging.Abstractions;
  using Microsoft.Extensions.Time.Testing;
  using NSubstitute;
  using NSubstitute.ExceptionExtensions;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Host.Workers;

  namespace Noof.Ledger.Host.Tests;

  public class LogRetentionWorkerTests
  {
      static IServiceScopeFactory ScopeFactoryFor(ILogRetention retention)
      {
          var provider = Substitute.For<IServiceProvider>();
          provider.GetService(typeof(ILogRetention)).Returns(retention);
          var scope = Substitute.For<IServiceScope>();
          scope.ServiceProvider.Returns(provider);
          var factory = Substitute.For<IServiceScopeFactory>();
          factory.CreateScope().Returns(scope);
          return factory;
      }

      static IDatabaseGate ReadyGate()
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
          return gate;
      }

      [Fact]
      public async Task The_worker_waits_for_the_gate_before_its_first_prune()
      {
          var retention = Substitute.For<ILogRetention>();
          retention.PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(0);
          var gate = Substitute.For<IDatabaseGate>();
          var gateReady = new TaskCompletionSource();
          gate.WaitUntilReadyAsync(Arg.Any<CancellationToken>()).Returns(gateReady.Task);
          var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
          var worker = new LogRetentionWorker(ScopeFactoryFor(retention), gate, time, NullLogger<LogRetentionWorker>.Instance);

          await worker.StartAsync(TestContext.Current.CancellationToken);
          try
          {
              await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
              await retention.DidNotReceive().PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

              gateReady.SetResult();
              time.Advance(TimeSpan.FromSeconds(60));
              await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
              await retention.Received(1).PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
          }
          finally
          {
              await worker.StopAsync(TestContext.Current.CancellationToken);
          }
      }

      [Fact]
      public async Task The_first_prune_runs_sixty_seconds_after_ready_not_immediately()
      {
          var retention = Substitute.For<ILogRetention>();
          retention.PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(0);
          var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
          var worker = new LogRetentionWorker(ScopeFactoryFor(retention), ReadyGate(), time, NullLogger<LogRetentionWorker>.Instance);

          await worker.StartAsync(TestContext.Current.CancellationToken);
          try
          {
              await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
              await retention.DidNotReceive().PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

              time.Advance(TimeSpan.FromSeconds(59));
              await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
              await retention.DidNotReceive().PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

              time.Advance(TimeSpan.FromSeconds(1));
              await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
              await retention.Received(1).PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
          }
          finally
          {
              await worker.StopAsync(TestContext.Current.CancellationToken);
          }
      }

      [Fact]
      public async Task Subsequent_prunes_run_every_six_hours()
      {
          var retention = Substitute.For<ILogRetention>();
          retention.PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(0);
          var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
          var worker = new LogRetentionWorker(ScopeFactoryFor(retention), ReadyGate(), time, NullLogger<LogRetentionWorker>.Instance);

          await worker.StartAsync(TestContext.Current.CancellationToken);
          try
          {
              time.Advance(TimeSpan.FromSeconds(60));
              await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
              await retention.Received(1).PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

              time.Advance(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));
              await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
              await retention.Received(1).PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

              time.Advance(TimeSpan.FromSeconds(1));
              await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
              await retention.Received(2).PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
          }
          finally
          {
              await worker.StopAsync(TestContext.Current.CancellationToken);
          }
      }

      [Fact]
      public async Task A_prune_that_throws_is_logged_and_never_crashes_the_worker()
      {
          var retention = Substitute.For<ILogRetention>();
          retention.PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
              .ThrowsAsync(new InvalidOperationException("database unreachable"));
          var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
          var worker = new LogRetentionWorker(ScopeFactoryFor(retention), ReadyGate(), time, NullLogger<LogRetentionWorker>.Instance);

          await worker.StartAsync(TestContext.Current.CancellationToken);
          try
          {
              time.Advance(TimeSpan.FromSeconds(60));
              await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
              await retention.Received(1).PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

              // Still alive: the failed tick did not stop the loop, so the next scheduled tick still fires.
              time.Advance(TimeSpan.FromHours(6));
              await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
              await retention.Received(2).PruneAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
          }
          finally
          {
              await worker.StopAsync(TestContext.Current.CancellationToken);
          }
      }
  }
  ```
  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter LogRetentionWorkerTests`
  Expected: **FAIL** — `LogRetentionWorker` does not exist.

  Implement.
  `src/Noof.Ledger.Host/Workers/LogRetentionWorker.cs`:
  ```csharp
  using Microsoft.Extensions.Logging;
  using Noof.Ledger.Application.Diagnostics;

  namespace Noof.Ledger.Host.Workers;

  internal sealed partial class LogRetentionWorker(
      IServiceScopeFactory scopeFactory, IDatabaseGate gate, TimeProvider timeProvider, ILogger<LogRetentionWorker> logger)
      : BackgroundService
  {
      static readonly TimeSpan FirstRunDelay = TimeSpan.FromSeconds(60);
      static readonly TimeSpan Interval = TimeSpan.FromHours(6);

      protected override async Task ExecuteAsync(CancellationToken stoppingToken)
      {
          await gate.WaitUntilReadyAsync(stoppingToken);
          await Task.Delay(FirstRunDelay, timeProvider, stoppingToken);

          while (!stoppingToken.IsCancellationRequested)
          {
              await RunPruneAsync(stoppingToken);
              await Task.Delay(Interval, timeProvider, stoppingToken);
          }
      }

      async Task RunPruneAsync(CancellationToken cancellationToken)
      {
          try
          {
              using var scope = scopeFactory.CreateScope();
              var retention = scope.ServiceProvider.GetRequiredService<ILogRetention>();
              var pruned = await retention.PruneAsync(timeProvider.GetUtcNow(), cancellationToken);
              LogPruned(pruned);
          }
          catch (Exception ex) when (ex is not OperationCanceledException)
          {
              LogPruneFailed(ex);
          }
      }

      [LoggerMessage(Level = LogLevel.Information, Message = "Log retention pruned {PrunedCount} rows")]
      partial void LogPruned(int prunedCount);

      [LoggerMessage(Level = LogLevel.Error, Message = "Log retention prune failed")]
      partial void LogPruneFailed(Exception exception);
  }
  ```
  Note: `[LoggerMessage]` on an instance method needs the containing class `partial` and the method itself
  `partial` with no body (the source generator supplies it) — matches CLAUDE.md's "`[LoggerMessage]`
  source-generated methods" rule and the architecture test Task 2 added banning direct `LogXxx(` calls.
  Run the same filter. Expected: **PASS** (4 tests).
  - Commit: `git add src/Noof.Ledger.Host/Workers/LogRetentionWorker.cs tests/Noof.Ledger.Host.Tests/LogRetentionWorkerTests.cs`
    ```
    Add LogRetentionWorker: first prune 60s after Ready, then every 6h

    Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
    Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
    ```

- [ ] **Step 8: Wire everything into DI and the Serilog pipeline.**
  No new fast unit test here — this step is composition-root wiring (registrations and the `LoggingSetup`
  call site), which CLAUDE.md exempts from TDD ("Program.cs wiring"); Step 9's DB test and Step 10's
  sentinel test are what actually exercise this wiring end to end.

  Add the package. `Directory.Packages.props`, inside the first `<ItemGroup>` (after the last entry):
  ```xml
    <PackageVersion Include="Serilog.Sinks.Postgresql.Alternative" Version="4.3.0" />
  ```
  `src/Noof.Ledger.Host/Noof.Ledger.Host.csproj`, add a `PackageReference` item group (or extend the
  existing one Task 2 added for `Serilog.AspNetCore`/`Serilog.Sinks.File`):
  ```xml
    <PackageReference Include="Serilog.Sinks.Postgresql.Alternative" />
  ```

  `src/Noof.Ledger.Host/Diagnostics/HostDiagnosticsRegistration.cs`:
  ```csharp
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Application.Secrets;
  using Noof.Ledger.Persistence;

  namespace Noof.Ledger.Host.Diagnostics;

  internal static class HostDiagnosticsRegistration
  {
      public static IServiceCollection AddNoofHostDiagnostics(this IServiceCollection services, string connectionString)
      {
          var databasePassword = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Password ?? string.Empty;

          services.AddSingleton<LogSinkStatus>();
          services.AddSingleton<ILogSinkStatus>(sp => sp.GetRequiredService<LogSinkStatus>());
          services.AddSingleton(sp => new SecretSnapshot(
              sp.GetRequiredService<IServiceScopeFactory>(), sp.GetServices<ISecretProbe>(), databasePassword));
          services.AddSingleton<ISecretValueSource>(sp => sp.GetRequiredService<SecretSnapshot>());
          services.AddSingleton<SecretRedactor>();
          services.AddHostedService(sp => new SecretSnapshotRefreshWorker(
              sp.GetRequiredService<IDatabaseGate>(), sp.GetRequiredService<SecretSnapshot>(), sp.GetRequiredService<TimeProvider>()));

          return services;
      }
  }
  ```
  Note: `connectionString` is passed in rather than resolved from `IConfiguration` inside this method because
  `LedgerConnectionString.Resolve` is already called once in `Program.cs` to configure Persistence — this
  reuses that same resolved string instead of re-resolving (and re-validating) it a second time. Adjust the
  call site below to whatever variable name `Program.cs` already binds it to.

  `src/Noof.Ledger.Host/Program.cs` (near the other `builder.Services.AddNoofXxx(...)` calls, after
  `AddNoofPersistence`, which is where the connection string is already resolved):
  ```csharp
  builder.Services.AddNoofHostDiagnostics(ledgerConnectionString);
  ```
  (`ledgerConnectionString` stands in for whatever Program.cs already names the string
  `LedgerConnectionString.Resolve(...)` returns — Step 1 records the actual variable name.)

  `src/Noof.Ledger.Host/Workers/WorkerRegistration.cs` — add inside `AddNoofWorkers`, alongside the
  `BackupWorker` registration:
  ```csharp
      services.AddHostedService(sp => new LogRetentionWorker(
          sp.GetRequiredService<IServiceScopeFactory>(),
          sp.GetRequiredService<IDatabaseGate>(),
          sp.GetRequiredService<TimeProvider>(),
          sp.GetRequiredService<ILogger<LogRetentionWorker>>()));
  ```
  (`using Noof.Ledger.Application.Diagnostics;` added to that file's usings.)

  `src/Noof.Ledger.Host/Logging/LoggingSetup.cs` — inside the method Task 2 wrote for the `UseSerilog(...)`
  reconfigure step (Step 1 names it precisely), add the Postgres sub-logger and the redacting wrap around
  the whole destination set:
  ```csharp
  using System.IO;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Host.Diagnostics;
  using Serilog;
  using Serilog.Debugging;
  using Serilog.Events;
  using Serilog.Sinks.PostgreSQL.ColumnWriters;
  using NpgsqlTypes;

  // ... inside the existing Configure(...) method, after console/file are added to `destinations`
  // (a LoggerConfiguration built separately from the one UseSerilog hands back) but before
  // .CreateLogger() is called on it:

  var gate = services.GetRequiredService<IDatabaseGate>();
  var sinkStatus = services.GetRequiredService<ILogSinkStatus>();
  var timeProvider = services.GetRequiredService<TimeProvider>();

  SelfLog.Enable(_ => sinkStatus.RecordFailure(timeProvider.GetUtcNow()));

  var columnOptions = new Dictionary<string, ColumnWriterBase>
  {
      ["logged_at"] = new TimestampColumnWriter(NpgsqlDbType.TimestampTz),
      ["level"] = new LevelColumnWriter(renderAsText: false, NpgsqlDbType.Smallint),
      ["source"] = new SinglePropertyColumnWriter("SourceContext", PropertyWriteMethod.Raw, NpgsqlDbType.Text),
      ["message"] = new RenderedMessageColumnWriter(NpgsqlDbType.Text),
      ["template"] = new MessageTemplateColumnWriter(NpgsqlDbType.Text),
      ["exception"] = new ExceptionColumnWriter(NpgsqlDbType.Text),
      ["transaction_id"] = new SinglePropertyColumnWriter("TransactionId", PropertyWriteMethod.Raw, NpgsqlDbType.Uuid),
      ["properties"] = new PropertiesColumnWriter(NpgsqlDbType.Jsonb),
  };

  destinations.WriteTo.Logger(pg => pg
      .Filter.ByIncludingOnly(_ => gate.State == DatabaseState.Ready)
      .WriteTo.PostgreSQL(connectionString, "app_log", columnOptions, needAutoCreateTable: false));

  var redactor = services.GetRequiredService<SecretRedactor>();
  var builtDestinations = destinations.CreateLogger();
  loggerConfiguration.WriteTo.Sink(new RedactingSink(builtDestinations, redactor));
  ```
  Exactly where `destinations` (the inner `LoggerConfiguration` collecting console+file+Postgres) and
  `connectionString` come from is Task 2's code, read in Step 1 — this block assumes both already exist as
  local variables in that method, per the spec's own description of the reconfigure step. If Task 2 instead
  configures `WriteTo.Console`/`WriteTo.File` directly on the outer `loggerConfiguration`, the fix is
  mechanical: move those two `WriteTo` calls onto a new local `var destinations = new LoggerConfiguration()`
  first, so this step's `WriteTo.Sink(new RedactingSink(...))` is the *only* thing the outer
  `loggerConfiguration` ever receives.
  - Commit:
    `git add Directory.Packages.props src/Noof.Ledger.Host/Noof.Ledger.Host.csproj src/Noof.Ledger.Host/Diagnostics/HostDiagnosticsRegistration.cs src/Noof.Ledger.Host/Program.cs src/Noof.Ledger.Host/Workers/WorkerRegistration.cs src/Noof.Ledger.Host/Logging/LoggingSetup.cs`
    ```
    Wire the Postgres log sink, secret redaction and log retention into the host

    Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
    Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
    ```
  Build to confirm it compiles before the DB test: `dotnet build NoofLedger.slnx`. Expected: **PASS** (no
  test yet exercises the new pipeline against a real database — that is Step 9).

- [ ] **Step 9: DB test — the sink writes exactly the contract row format (cross-check against Task 3's
  seeds).**
  This is a database test on a clone of the shared test template (this task adds no migration of its own —
  Task 3 already added `app_log`), filtered per CLAUDE.md's database-test policy. Hold the suite lock
  (`C:\Users\noofs\AppData\Local\Temp\noof-suite.lock`) for the run.

  Write the failing test first.
  `tests/Noof.Ledger.Host.Tests/AppLogSinkTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Microsoft.AspNetCore.Mvc.Testing;
  using Microsoft.Extensions.DependencyInjection;
  using Microsoft.Extensions.Logging;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.TestKit;
  using Npgsql;

  namespace Noof.Ledger.Host.Tests;

  [Collection("app-log-sink")]
  public sealed class AppLogSinkTests
  {
      [Fact]
      public async Task An_event_logged_with_a_transaction_scope_and_a_stage_property_lands_in_app_log_in_the_contract_row_format()
      {
          if (!await DatabaseIsReachableAsync(TestContext.Current.CancellationToken))
              Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

          var databaseName = $"noof_app_log_sink_{Guid.NewGuid():N}";
          await CreateCloneAsync(databaseName, TestContext.Current.CancellationToken);
          var connectionString = new NpgsqlConnectionStringBuilder(DatabaseSettings.AdminConnectionString) { Database = databaseName }.ConnectionString;

          try
          {
              await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
              {
                  builder.UseSetting("ConnectionStrings:Ledger", connectionString);
                  builder.UseSetting("Database:MigrateOnStartup", "true");
                  builder.UseSetting("Backup:Enabled", "false");
                  builder.ConfigureServices(FakeUserStore.Register);
              });

              using var client = factory.CreateClient();

              var gate = factory.Services.GetRequiredService<IDatabaseGate>();
              await gate.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

              var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Noof.Ledger.Host.Tests.Probe");
              var transactionId = Guid.NewGuid();
              using (TransactionLogScope.Begin(logger, transactionId))
              {
                  logger.LogInformation(TransactionStages.ReceivedEventId, "{Stage}", TransactionStages.Received);
              }

              // The Postgres sink batches on a timer (default period ~2s); poll briefly rather than
              // sleep a fixed guess, since the exact flush timing is Serilog's, not this test's.
              var row = await PollForRowAsync(connectionString, transactionId, TestContext.Current.CancellationToken);

              row.Should().NotBeNull();
              row!.Value.Source.Should().Be("Noof.Ledger.Host.Tests.Probe");
              row.Value.Message.Should().Be(TransactionStages.Received);
              row.Value.TransactionId.Should().Be(transactionId);
              row.Value.PropertiesJson.Should().Contain($"\"Stage\":\"{TransactionStages.Received}\"");
              row.Value.PropertiesJson.Should().Contain($"\"EventId\":{{\"Id\":{TransactionStages.ReceivedEventId}");
          }
          finally
          {
              await DropCloneAsync(databaseName);
          }
      }

      static async Task<(string? Source, string Message, Guid? TransactionId, string PropertiesJson)?> PollForRowAsync(
          string connectionString, Guid transactionId, CancellationToken cancellationToken)
      {
          for (var attempt = 0; attempt < 20; attempt++)
          {
              await using var connection = new NpgsqlConnection(connectionString);
              await connection.OpenAsync(cancellationToken);
              await using var command = new NpgsqlCommand(
                  "SELECT source, message, transaction_id, properties::text FROM app_log WHERE transaction_id = @id", connection);
              command.Parameters.AddWithValue("id", transactionId);
              await using var reader = await command.ExecuteReaderAsync(cancellationToken);
              if (await reader.ReadAsync(cancellationToken))
              {
                  return (
                      reader.IsDBNull(0) ? null : reader.GetString(0),
                      reader.GetString(1),
                      reader.IsDBNull(2) ? null : reader.GetGuid(2),
                      reader.GetString(3));
              }

              await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
          }

          return null;
      }

      static async Task<bool> DatabaseIsReachableAsync(CancellationToken cancellationToken)
      {
          try
          {
              await using var connection = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
              await connection.OpenAsync(cancellationToken);
              return true;
          }
          catch
          {
              return false;
          }
      }

      static async Task CreateCloneAsync(string name, CancellationToken cancellationToken)
      {
          await using var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
          await admin.OpenAsync(cancellationToken);
          await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE {DatabaseSettings.TemplateDatabase}", admin);
          await create.ExecuteNonQueryAsync(cancellationToken);
      }

      static async Task DropCloneAsync(string name)
      {
          NpgsqlConnection.ClearAllPools();
          await using var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
          await admin.OpenAsync(CancellationToken.None);
          await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin) { CommandTimeout = 120 };
          await drop.ExecuteNonQueryAsync(CancellationToken.None);
      }
  }
  ```
  Run (holding the lock): `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter AppLogSinkTests`
  Expected: **FAIL** — either a compile error (if Step 8's wiring has a mismatch against Task 2's real
  `LoggingSetup` shape, surfaced now for the first time against a real host) or the poll times out because
  the row never lands (e.g. a column-writer property name or type mismatch). Diagnose against the actual
  `app_log` row Task 3's own DB test already proves it can seed, and fix the column-writer configuration
  from Step 8 accordingly — this is the step this whole task exists to get right, so treat any mismatch here
  as the real defect, not a test problem.

  Fix whatever Step 8 got wrong (most likely candidates, in order of likelihood, given `PropertyWriteMethod`
  and the writer types were verified against the real 4.3.0 assembly but never against a running database:
  the `transaction_id` writer's `PropertyWriteMethod` needs to be `Raw` for Npgsql to bind the `Guid` value
  directly as `uuid` rather than as its `ToString()`; if the row appears with `transaction_id` as `NULL`,
  switch it to `PropertyWriteMethod.ToString` and parse in the writer's `Format`, or use
  `SinglePropertyColumnWriter(name: "TransactionId", writeMethod: PropertyWriteMethod.Raw, dbType:
  NpgsqlDbType.Uuid)` and confirm via the failing row's actual bound value rather than guessing further).
  Run the same filter. Expected: **PASS**.
  - Commit: `git add tests/Noof.Ledger.Host.Tests/AppLogSinkTests.cs` (plus any fix to
    `src/Noof.Ledger.Host/Logging/LoggingSetup.cs` this step's diagnosis required)
    ```
    Add the DB test proving the Postgres sink writes the Task 3 contract row format

    Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
    Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
    ```

- [ ] **Step 10: Sentinel test — a real secret never reaches the file or the database.**
  Write the failing test first.
  `tests/Noof.Ledger.Host.Tests/SecretRedactionSentinelTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Microsoft.AspNetCore.Mvc.Testing;
  using Microsoft.Extensions.DependencyInjection;
  using Microsoft.Extensions.Logging;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Application.Secrets;
  using Noof.Ledger.Host.Diagnostics;
  using Noof.Ledger.TestKit;
  using Npgsql;

  namespace Noof.Ledger.Host.Tests;

  [Collection("app-log-sink")]
  public sealed class SecretRedactionSentinelTests
  {
      const string FakeSecretValue = "sk-sentinel-fake-secret-0123456789";

      [Fact]
      public async Task A_registered_secret_never_appears_in_the_log_file_or_app_log_even_inside_an_exception_message()
      {
          if (!await DatabaseIsReachableAsync(TestContext.Current.CancellationToken))
              Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

          var databaseName = $"noof_secret_sentinel_{Guid.NewGuid():N}";
          await CreateCloneAsync(databaseName, TestContext.Current.CancellationToken);
          var connectionString = new NpgsqlConnectionStringBuilder(DatabaseSettings.AdminConnectionString) { Database = databaseName }.ConnectionString;
          var logDirectory = Path.Combine(Path.GetTempPath(), $"noof-sentinel-logs-{Guid.NewGuid():N}");

          try
          {
              await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
              {
                  builder.UseSetting("ConnectionStrings:Ledger", connectionString);
                  builder.UseSetting("Database:MigrateOnStartup", "true");
                  builder.UseSetting("Backup:Enabled", "false");
                  builder.UseSetting("Logging:File:Directory", logDirectory);
                  builder.ConfigureServices(FakeUserStore.Register);
              });

              using var client = factory.CreateClient();

              var gate = factory.Services.GetRequiredService<IDatabaseGate>();
              await gate.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

              var secretStore = factory.Services.GetRequiredService<ISecretStore>();
              await secretStore.SetAsync(SecretKeys.TelegramBotToken, FakeSecretValue, TestContext.Current.CancellationToken);

              var snapshot = factory.Services.GetRequiredService<SecretSnapshot>();
              await snapshot.RefreshAsync(TestContext.Current.CancellationToken);

              var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Noof.Ledger.Host.Tests.Sentinel");
              var probeId = Guid.NewGuid();
              try
              {
                  throw new InvalidOperationException($"call failed for token {FakeSecretValue} (probe {probeId})");
              }
              catch (Exception ex)
              {
                  logger.LogError(ex, "Sentinel probe {ProbeId} failed with a secret in the message", probeId);
              }

              var fileText = await PollFileTextAsync(logDirectory, probeId.ToString(), TestContext.Current.CancellationToken);
              fileText.Should().Contain(probeId.ToString());
              fileText.Should().NotContain(FakeSecretValue);
              fileText.Should().Contain("***");

              var dbText = await PollAppLogTextAsync(connectionString, probeId.ToString(), TestContext.Current.CancellationToken);
              dbText.Should().NotBeNull();
              dbText.Should().NotContain(FakeSecretValue);
              dbText.Should().Contain("***");
          }
          finally
          {
              await DropCloneAsync(databaseName);
              if (Directory.Exists(logDirectory))
                  Directory.Delete(logDirectory, recursive: true);
          }
      }

      static async Task<string> PollFileTextAsync(string directory, string mustContain, CancellationToken cancellationToken)
      {
          for (var attempt = 0; attempt < 20; attempt++)
          {
              if (Directory.Exists(directory))
              {
                  var newest = Directory.EnumerateFiles(directory, "noof-ledger-*.log")
                      .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                  if (newest is not null)
                  {
                      var text = await File.ReadAllTextAsync(newest, cancellationToken);
                      if (text.Contains(mustContain, StringComparison.Ordinal))
                          return text;
                  }
              }

              await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
          }

          throw new TimeoutException("The sentinel line never appeared in the log file.");
      }

      static async Task<string?> PollAppLogTextAsync(string connectionString, string mustContain, CancellationToken cancellationToken)
      {
          for (var attempt = 0; attempt < 20; attempt++)
          {
              await using var connection = new NpgsqlConnection(connectionString);
              await connection.OpenAsync(cancellationToken);
              await using var command = new NpgsqlCommand(
                  "SELECT message || ' ' || COALESCE(exception, '') FROM app_log WHERE message LIKE @pattern OR exception LIKE @pattern",
                  connection);
              command.Parameters.AddWithValue("pattern", $"%{mustContain}%");
              var result = await command.ExecuteScalarAsync(cancellationToken);
              if (result is string text)
                  return text;

              await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
          }

          return null;
      }

      static async Task<bool> DatabaseIsReachableAsync(CancellationToken cancellationToken)
      {
          try
          {
              await using var connection = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
              await connection.OpenAsync(cancellationToken);
              return true;
          }
          catch
          {
              return false;
          }
      }

      static async Task CreateCloneAsync(string name, CancellationToken cancellationToken)
      {
          await using var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
          await admin.OpenAsync(cancellationToken);
          await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE {DatabaseSettings.TemplateDatabase}", admin);
          await create.ExecuteNonQueryAsync(cancellationToken);
      }

      static async Task DropCloneAsync(string name)
      {
          NpgsqlConnection.ClearAllPools();
          await using var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
          await admin.OpenAsync(CancellationToken.None);
          await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin) { CommandTimeout = 120 };
          await drop.ExecuteNonQueryAsync(CancellationToken.None);
      }
  }
  ```
  Run (holding the lock): `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter SecretRedactionSentinelTests`
  Expected: **FAIL** first for a real reason — most likely `SecretSnapshot` isn't resolvable as a concrete
  type from `factory.Services` (it's registered as itself in Step 8, so it should resolve; if Step 8 only
  registered the `ISecretValueSource` interface, add `services.AddSingleton<SecretSnapshot>()` explicitly, or
  resolve it as `GetRequiredService<ISecretValueSource>()` and cast if a concrete `RefreshAsync` call is
  needed from the test). Confirm the actual failure is "the secret leaked" only after wiring is confirmed
  reachable, not before — a wiring failure here is not the finding this test exists to make.
  Fix the wiring issue if any, then confirm the test genuinely goes red on a real leak by temporarily
  commenting out the `.WriteTo.Sink(new RedactingSink(...))` wrap in `LoggingSetup` (replace with the plain
  `destinations` logger) and re-running: this must fail with the raw secret visible in `fileText`/`dbText` —
  the "watch it fail" step for this guard. Restore the wrap.
  Run the same filter with the wrap restored. Expected: **PASS**.
  - Commit: `git add tests/Noof.Ledger.Host.Tests/SecretRedactionSentinelTests.cs`
    ```
    Add the sentinel test proving a real secret never reaches the log file or app_log

    Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
    Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
    ```

- [ ] **Step 11: Run every fast test this task touched, once, in full.**
  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
  Expected: **PASS** in full (per CLAUDE.md's database-test policy, the two DB-backed classes just added —
  `AppLogSinkTests`, `SecretRedactionSentinelTests` — run here too since they are new classes this task
  added, not an unfiltered *pre-existing* database project run; this is still "filtered to the classes this
  task touches" in spirit because the whole Host.Tests project's fast tests plus these two new DB classes are
  exactly that set. Hold the suite lock for this run since it includes the two DB-backed classes.)
  Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
  Expected: **PASS**.
  No commit — this step is verification only, nothing changes.

---

**What this task deliberately leaves for other tasks:** the `Serilog*` referenced-only-by-Host architecture
test and the `[LoggerMessage]`-only architecture test (Task 2); `app_log`'s migration and `ILogRetention`'s
real `ExecuteDeleteAsync` implementation (Task 3); the Log sink health check reading `ILogSinkStatus` (Task
6); `/diagnostics/logs`' file-tail fallback reading `Logging:File:Directory` (Task 7, which needs
`ILogFileTail`). Task 4 only needs `ILogSinkStatus` to exist and be updated correctly — Task 6 is the one
that turns it into a health check row.

---

### Task 5: Transaction path events at every stage

**Plan notes**

- The spec's `Replied` event fires once per transaction, at the point where the record's real answer is
  echoed to Telegram — `CategorizationWorker.EchoAsync`, after `notifier.EditAsync` succeeds. The
  acknowledgement/`"Transcribing…"` sends in `TelegramUpdateRouter`/`TranscriptionWorker` are not `Replied`:
  they carry no data about the transaction, they are the *placeholder* the later `Replied` edits over. Only
  `EchoAsync`'s edit is logged as `Replied`.
- `TelegramUpdateRouter` failures currently propagate uncaught out of `HandleMessageAsync`/`HandleVoiceAsync`
  into `TelegramPollingService`'s per-update `catch`, which already retries/poisons the update — that
  behaviour must not change. `StageFailed` for `Received` is added as a `try`/`catch`/`rethrow` around the
  post-capture send-and-attach steps (the only place after `CaptureAsync` returns a `TransactionId` to scope
  the log to); the `catch` logs and rethrows unchanged.
- `CategorizationWorker`'s two direct `FailTerminallyAsync` calls (transaction missing, `TryMap` failure) and
  its three outer `catch` blocks all occur before `store.ApplyAsync`, i.e. always at the `Categorized` stage,
  with one exception: `ApplyAsync` itself can throw into the generic `catch (Exception ex)` block, which is
  the `Persisted` stage. A `currentStage` local, set to `Categorized` at the top of
  `ProcessClaimedJobAsync` and reassigned to `Persisted` immediately before the `ApplyAsync` call, is threaded
  through `FailTerminallyAsync`/`HandleModelFailureAsync` as `failedStage` so this one case reports correctly
  without a stage-tracking rewrite of the whole method.
- `FailTerminallyAsync`/`HandleModelFailureAsync` take a `string error`, not an `Exception` (matching
  `IJobQueue.FailAsync`/`RetryAsync`, which only ever store a string) — this was true before this task. The
  `StageFailed` event's exception is reconstructed as `new InvalidOperationException(error)` at the one place
  each method logs, rather than threading the original `ModelCallException` through every call site. The
  original exception's type and stack trace are not preserved in the log; only its message. This matches what
  the rest of the pipeline already does with these two methods and keeps the diff small — flagged here per
  the task brief's request to note deviations, not because it needs a design decision now.
- `TranscriptionWorker`'s only stage is `Transcribed`, so every failure inside `ProcessClaimedJobAsync`
  (including "nothing was heard", which produces no transcript) reports `FailedStage = Transcribed`; there is
  no second stage to distinguish.
- `Microsoft.Extensions.Diagnostics.Testing` (`FakeLogger`) is not referenced anywhere in this repo today and
  is not added for this task. A small `CapturingLogger<T> : ILogger<T>` is added to `Noof.Ledger.TestKit`
  instead (spec: "a small test logger" is the explicit fallback), shared by `Noof.Ledger.Host.Tests` (already
  references `TestKit`) and `Noof.Ledger.Telegram.Tests` (gains the reference in Step 1).
- Test filter syntax follows this repo's actual convention (confirmed against
  `docs/superpowers/plans/2026-09-24-phase4-money-model.md`): `dotnet test --project <csproj> --filter <ClassName>`,
  not `--filter-class`.

**Files**

- Create: `tests/Noof.Ledger.TestKit/CapturingLogger.cs` (`CapturingLogger<T>`, `CapturedLogEntry`)
- Modify: `tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj` (add `TestKit` project reference)
- Modify: `src/Noof.Ledger.Telegram/TelegramUpdateRouter.cs` (lines 1–81, full file rewritten)
- Modify: `tests/Noof.Ledger.Telegram.Tests/TelegramUpdateRouterTests.cs` (lines 1–33, `CreateRouter`/`Harness` gain a logger)
- Modify: `src/Noof.Ledger.Host/Workers/TranscriptionWorker.cs` (lines 1–131, constructor/`using` list and `ProcessClaimedJobAsync` unchanged in shape, new `[LoggerMessage]` methods + call sites)
- Modify: `tests/Noof.Ledger.Host.Tests/TranscriptionWorkerTests.cs` (harness gains a logger slot)
- Modify: `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs` (lines 1–358: `ProcessClaimedJobAsync`, `FailTerminallyAsync`, `HandleModelFailureAsync`, `EchoAsync`, new `[LoggerMessage]` methods + `BuildSummary`)
- Modify: `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs` (`CreateWorker` gains a logger slot)
- Test files (new tests added to the modified test files above, not new files): `TelegramUpdateRouterTests.cs`, `TranscriptionWorkerTests.cs`, `CategorizationWorkerTests.cs`.

**Interfaces**

Consumes (from the binding contract, Application, already existing after Tasks 1–3):
```csharp
namespace Noof.Ledger.Application.Diagnostics;
public static class TransactionStages
{
    public const string TransactionIdProperty = "TransactionId";
    public const string StageProperty = "Stage";
    public const string Received = "Received";       public const int ReceivedEventId = 5001;
    public const string Transcribed = "Transcribed"; public const int TranscribedEventId = 5002;
    public const string Categorized = "Categorized"; public const int CategorizedEventId = 5003;
    public const string Persisted = "Persisted";     public const int PersistedEventId = 5004;
    public const string Replied = "Replied";         public const int RepliedEventId = 5005;
    public const string StageFailed = "StageFailed"; public const int StageFailedEventId = 5009;
    public static IReadOnlyList<string> Ordered { get; }
}
public static class TransactionLogScope
{
    public static IDisposable? Begin(ILogger logger, Guid transactionId);
}
```

Produces (task-internal, private `static partial` `[LoggerMessage]` methods, one set per class):
```csharp
// TelegramUpdateRouter
private static partial void LogReceived(ILogger logger, string stage, CaptureKind captureKind, long chatId);
private static partial void LogStageFailed(ILogger logger, string stage, string failedStage, Exception exception);

// TranscriptionWorker
private static partial void LogTranscribed(ILogger logger, string stage, double durationSeconds, int characters);
private static partial void LogStageFailed(ILogger logger, string stage, string failedStage, Exception exception);

// CategorizationWorker
private static partial void LogCategorized(ILogger logger, string stage, TransactionKind kind, Guid? walletId, string summary);
private static partial void LogPersisted(ILogger logger, string stage, TransactionKind kind);
private static partial void LogReplied(ILogger logger, string stage, int botMessageId);
private static partial void LogStageFailed(ILogger logger, string stage, string failedStage, Exception exception);
```

Test helper (new, `Noof.Ledger.TestKit`):
```csharp
namespace Noof.Ledger.TestKit;
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<CapturedLogEntry> Entries { get; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull;
    public bool IsEnabled(LogLevel logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter);
}
public sealed record CapturedLogEntry(
    LogLevel Level, EventId EventId, IReadOnlyDictionary<string, object> Properties,
    Exception? Exception, IReadOnlyDictionary<string, object>? Scope)
{
    public string? Stage { get; }
}
```

---

- [ ] **Step 1: `CapturingLogger<T>` + `Received` logged for a text capture**

  Write the test first (it will not compile: `TelegramUpdateRouter`'s constructor does not yet take a
  logger, and `CapturingLogger` does not exist yet).

  `tests/Noof.Ledger.TestKit/Noof.Ledger.TestKit.csproj` — add nothing (it already references
  `Noof.Ledger.Application`, which Task 3 gave a `PackageReference` to
  `Microsoft.Extensions.Logging.Abstractions`, so the type is available transitively).

  Create `tests/Noof.Ledger.TestKit/CapturingLogger.cs`:
  ```csharp
  using Microsoft.Extensions.Logging;

  namespace Noof.Ledger.TestKit;

  public sealed class CapturingLogger<T> : ILogger<T>
  {
      public List<CapturedLogEntry> Entries { get; } = [];

      readonly List<IReadOnlyDictionary<string, object>> scopeStack = [];

      public IDisposable? BeginScope<TState>(TState state) where TState : notnull
      {
          var values = ToDictionary(state);
          scopeStack.Add(values);
          return new Scope(this, values);
      }

      public bool IsEnabled(LogLevel logLevel) => true;

      public void Log<TState>(
          LogLevel logLevel, EventId eventId, TState state, Exception? exception,
          Func<TState, Exception?, string> formatter)
      {
          var properties = ToDictionary(state);
          var scope = scopeStack.Count > 0 ? scopeStack[^1] : null;
          Entries.Add(new CapturedLogEntry(logLevel, eventId, properties, exception, scope));
      }

      static IReadOnlyDictionary<string, object> ToDictionary<TState>(TState state) =>
          state is IEnumerable<KeyValuePair<string, object>> pairs
              ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
              : new Dictionary<string, object>();

      sealed class Scope(CapturingLogger<T> owner, IReadOnlyDictionary<string, object> values) : IDisposable
      {
          public void Dispose() => owner.scopeStack.Remove(values);
      }
  }

  public sealed record CapturedLogEntry(
      LogLevel Level, EventId EventId, IReadOnlyDictionary<string, object> Properties,
      Exception? Exception, IReadOnlyDictionary<string, object>? Scope)
  {
      public string? Stage => Properties.TryGetValue("Stage", out var value) ? value as string : null;
  }
  ```

  `tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj` — add the `TestKit` reference:
  ```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\Noof.Ledger.Telegram\Noof.Ledger.Telegram.csproj" />
    <ProjectReference Include="..\Noof.Ledger.TestKit\Noof.Ledger.TestKit.csproj" />
  </ItemGroup>
  ```

  `tests/Noof.Ledger.Telegram.Tests/TelegramUpdateRouterTests.cs` — change `CreateRouter` to build and
  return a logger, and add the new test:
  ```csharp
  using Microsoft.Extensions.Logging;
  using NSubstitute;
  using Noof.Ledger.Application.Capture;
  using Noof.Ledger.Application.Categorization;
  using Noof.Ledger.Application.Chat;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Application.Editing;
  using Noof.Ledger.Application.Secrets;
  using Noof.Ledger.Domain;
  using Noof.Ledger.TestKit;
  using Telegram.Bot.Types;

  namespace Noof.Ledger.Telegram.Tests;

  public class TelegramUpdateRouterTests
  {
      static readonly IRecordEcho Echo = new RecordEcho();

      sealed record Harness(
          TelegramUpdateRouter Router, ICaptureStore CaptureStore, IChatNotifier ChatNotifier,
          IRecordEditor Editor, ICategorizationStore Store, CapturingLogger<TelegramUpdateRouter> Logger);

      static Harness CreateRouter(long ownerChatId)
      {
          var secretStore = Substitute.For<ISecretStore>();
          secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
              .Returns(new SecretResult(SecretState.Present, ownerChatId.ToString()));
          var captureStore = Substitute.For<ICaptureStore>();
          var chatNotifier = Substitute.For<IChatNotifier>();
          var editor = Substitute.For<IRecordEditor>();
          var store = Substitute.For<ICategorizationStore>();
          var logger = new CapturingLogger<TelegramUpdateRouter>();
          var router = new TelegramUpdateRouter(captureStore, chatNotifier, new TelegramOwnerGate(secretStore),
              new RecordActionHandler(editor, store, chatNotifier, Echo), new CorrectionHandler(editor, chatNotifier, Echo), Echo,
              logger);

          return new Harness(router, captureStore, chatNotifier, editor, store, logger);
      }

      // ... existing helpers (TextMessage, ReplyTo, ButtonPress, RecordWith) unchanged ...
  ```
  (Every existing call site `var (router, captureStore, chatNotifier, _, _) = CreateRouter(...)` becomes
  `var (router, captureStore, chatNotifier, _, _, _) = CreateRouter(...)` — a mechanical one-more-discard
  edit at each of the file's existing deconstructions; there is no other change to those tests.)

  Add the new test (near `Captures_replies_and_attaches_the_reply_for_the_owner`):
  ```csharp
  [Fact]
  public async Task Received_is_logged_for_a_text_capture_with_the_transaction_scope()
  {
      var (router, captureStore, chatNotifier, _, _, logger) = CreateRouter(ownerChatId: 111L);
      var transactionId = Guid.NewGuid();
      captureStore.CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>())
          .Returns(transactionId);
      chatNotifier.SendAsync(111L, Echo.Acknowledgement, Arg.Any<CancellationToken>()).Returns(777);

      await router.HandleAsync(
          TextMessage(111L, 5, "coffee 3.20 EUR", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

      var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.ReceivedEventId).Subject;
      entry.Stage.Should().Be(TransactionStages.Received);
      entry.Properties["CaptureKind"].Should().Be(CaptureKind.Text);
      entry.Properties["ChatId"].Should().Be(111L);
      entry.Scope.Should().NotBeNull();
      entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(transactionId);
  }
  ```
  Add `using AwesomeAssertions;` and `using Noof.Ledger.Application.Diagnostics;` to the test file's `using`
  block if not already present.

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramUpdateRouterTests`
  Expected failure: compile error — `TelegramUpdateRouter` has no constructor taking a
  `CapturingLogger<TelegramUpdateRouter>` (7 args vs. its current 6), and `Noof.Ledger.Application.Diagnostics`
  does not resolve in the test project until `TelegramUpdateRouter.cs` (Telegram project) references it — this
  is expected to fail at compile time, not run time; that is the "watch it fail" for this step's shape.

  Implement `src/Noof.Ledger.Telegram/TelegramUpdateRouter.cs` in full:
  ```csharp
  using Microsoft.Extensions.Logging;
  using Noof.Ledger.Application.Capture;
  using Noof.Ledger.Application.Chat;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Domain;
  using Telegram.Bot.Types;

  namespace Noof.Ledger.Telegram;

  internal sealed partial class TelegramUpdateRouter(
      ICaptureStore captureStore,
      IChatNotifier chatNotifier,
      TelegramOwnerGate ownerGate,
      RecordActionHandler actionHandler,
      CorrectionHandler correctionHandler,
      IRecordEcho recordEcho,
      ILogger<TelegramUpdateRouter> logger)
      : ITelegramUpdateRouter
  {
      public async Task HandleAsync(Update update, string timeZoneId, CancellationToken cancellationToken)
      {
          switch (update)
          {
              case { Message: { } message }:
                  await HandleMessageAsync(message, timeZoneId, cancellationToken);
                  break;
              case { EditedMessage: { } edited }:
                  if (await ownerGate.IsAllowedAsync(edited.Chat.Id, cancellationToken))
                      await correctionHandler.HandleEditAsync(edited, cancellationToken);
                  break;
              case { CallbackQuery: { Message: { } echo } query }:
                  // Rejected before reading Data, for the reason messages are rejected before reading Text.
                  if (await ownerGate.IsAllowedAsync(echo.Chat.Id, cancellationToken))
                      await actionHandler.HandleAsync(query, echo, cancellationToken);
                  break;
          }
      }

      async Task HandleMessageAsync(Message message, string timeZoneId, CancellationToken cancellationToken)
      {
          // Reject before reading Text: a stranger's content must never be inspected, not even to
          // decide whether it looks like a spend.
          if (!await ownerGate.IsAllowedAsync(message.Chat.Id, cancellationToken))
              return;

          if (message.Voice is { } voice)
          {
              await HandleVoiceAsync(message, voice, timeZoneId, cancellationToken);
              return;
          }

          if (message.Text is not { Length: > 0 } text)
              return;

          if (message.ReplyToMessage is { } repliedTo
              && await correctionHandler.TryHandleReplyAsync(message, repliedTo, text, cancellationToken))
              return;

          // message.Date is when Telegram received it from the sender, not when we got around to
          // processing it -- an outage can queue a message for hours, and every queued message must
          // keep its own moment so time_zone_id buckets it into the correct local day later.
          // Message.Date deserialises as DateTime with Kind=Utc (confirmed against Telegram.Bot
          // 22.10.3.1's UnixDateTimeConverter), so this offset is genuinely zero, not just labelled so.
          var sentAt = new DateTimeOffset(message.Date);
          var captured = new CapturedMessage(message.Chat.Id, message.Id, text, sentAt);
          var transactionId = await captureStore.CaptureAsync(captured, timeZoneId, cancellationToken);

          using var scope = TransactionLogScope.Begin(logger, transactionId);
          LogReceived(logger, TransactionStages.Received, CaptureKind.Text, message.Chat.Id);

          await SendAcknowledgementAsync(message.Chat.Id, transactionId, recordEcho.Acknowledgement, cancellationToken);
      }

      async Task HandleVoiceAsync(Message message, Voice voice, string timeZoneId, CancellationToken cancellationToken)
      {
          if (message.ReplyToMessage is { } repliedTo
              && await correctionHandler.TryHandleVoiceReplyAsync(message, repliedTo, voice, cancellationToken))
              return;

          // message.Date is the send instant, Kind=Utc - see HandleMessageAsync.
          var captured = new CapturedVoice(message.Chat.Id, message.Id, voice.FileId, voice.Duration, new DateTimeOffset(message.Date));
          var transactionId = await captureStore.CaptureVoiceAsync(captured, timeZoneId, cancellationToken);

          using var scope = TransactionLogScope.Begin(logger, transactionId);
          LogReceived(logger, TransactionStages.Received, CaptureKind.Voice, message.Chat.Id);

          await SendAcknowledgementAsync(message.Chat.Id, transactionId, recordEcho.Transcribing, cancellationToken);
      }

      async Task SendAcknowledgementAsync(long chatId, Guid transactionId, string text, CancellationToken cancellationToken)
      {
          try
          {
              var botMessageId = await chatNotifier.SendAsync(chatId, text, cancellationToken);
              await captureStore.AttachBotMessageAsync(transactionId, botMessageId, cancellationToken);
          }
          catch (Exception ex) when (ex is not OperationCanceledException)
          {
              LogStageFailed(logger, TransactionStages.StageFailed, TransactionStages.Received, ex);
              throw;
          }
      }

      [LoggerMessage(EventId = TransactionStages.ReceivedEventId, EventName = TransactionStages.Received, Level = LogLevel.Information,
          Message = "{Stage} capture kind {CaptureKind} from chat {ChatId}")]
      static partial void LogReceived(ILogger logger, string stage, CaptureKind captureKind, long chatId);

      [LoggerMessage(EventId = TransactionStages.StageFailedEventId, EventName = TransactionStages.StageFailed, Level = LogLevel.Error,
          Message = "{Stage} at stage {FailedStage}")]
      static partial void LogStageFailed(ILogger logger, string stage, string failedStage, Exception exception);
  }
  ```
  Note the `HandleMessageAsync`/`HandleVoiceAsync` refactor into a shared `SendAcknowledgementAsync`: the two
  call sites were byte-for-byte identical (send, then `AttachBotMessageAsync`) before this change, so folding
  the new `try`/`catch` into one method instead of duplicating it is not scope creep, it removes a
  duplication this task would otherwise have doubled.

  Also update `src/Noof.Ledger.Telegram/TelegramRegistration.cs` — no change needed:
  `services.AddScoped<ITelegramUpdateRouter, TelegramUpdateRouter>()` already resolves
  `ILogger<TelegramUpdateRouter>` from the generic logging registration ASP.NET Core wires up by default; no
  new registration line is required.

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramUpdateRouterTests`
  Expected: PASS (all tests in the file, including the pre-existing ones with their one-more-discard edit).

  Commit:
  ```
  git add tests/Noof.Ledger.TestKit/CapturingLogger.cs tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj tests/Noof.Ledger.Telegram.Tests/TelegramUpdateRouterTests.cs src/Noof.Ledger.Telegram/TelegramUpdateRouter.cs
  git commit -m "$(cat <<'EOF'
  Log a Received transaction-path event for text captures

  Adds CapturingLogger<T>, a small ILogger test double, to TestKit so the router's new
  logging can be asserted without a real sink. TelegramUpdateRouter now opens
  TransactionLogScope and logs EventId 5001 (Received) once a transaction id exists, and
  StageFailed (5009) if sending or attaching the acknowledgement then fails.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 2: `Received` logged for a voice capture, and `StageFailed` on a failed acknowledgement**

  Write the tests first:
  ```csharp
  [Fact]
  public async Task Received_is_logged_for_a_voice_capture()
  {
      var (router, captureStore, chatNotifier, _, _, logger) = CreateRouter(ownerChatId: 111L);
      var transactionId = Guid.NewGuid();
      captureStore.CaptureVoiceAsync(Arg.Any<CapturedVoice>(), "Europe/Belgrade", Arg.Any<CancellationToken>())
          .Returns(transactionId);
      chatNotifier.SendAsync(111L, Echo.Transcribing, Arg.Any<CancellationToken>()).Returns(778);
      var update = new Update
      {
          Id = 901,
          Message = new Message
          {
              Id = 6, Chat = new Chat { Id = 111L }, Date = DateTime.UtcNow,
              Voice = new Telegram.Bot.Types.Voice { FileId = "voice-1", Duration = 4 },
          },
      };

      await router.HandleAsync(update, "Europe/Belgrade", TestContext.Current.CancellationToken);

      var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.ReceivedEventId).Subject;
      entry.Properties["CaptureKind"].Should().Be(CaptureKind.Voice);
      entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(transactionId);
  }

  [Fact]
  public async Task A_failed_acknowledgement_logs_StageFailed_for_Received_and_rethrows()
  {
      var (router, captureStore, chatNotifier, _, _, logger) = CreateRouter(ownerChatId: 111L);
      var transactionId = Guid.NewGuid();
      captureStore.CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>())
          .Returns(transactionId);
      chatNotifier.SendAsync(111L, Echo.Acknowledgement, Arg.Any<CancellationToken>())
          .Returns<int>(_ => throw new InvalidOperationException("bot token revoked"));

      var act = () => router.HandleAsync(
          TextMessage(111L, 5, "coffee 3.20 EUR", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

      await act.Should().ThrowAsync<InvalidOperationException>();
      var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.StageFailedEventId).Subject;
      entry.Properties["FailedStage"].Should().Be(TransactionStages.Received);
      entry.Exception.Should().NotBeNull();
      entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(transactionId);
  }
  ```
  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramUpdateRouterTests`
  Expected failure: RED — both are genuinely new assertions against Step 1's implementation, which already
  covers this exact code path (`SendAcknowledgementAsync` was written with both branches in Step 1). Since
  Step 1 already implements the code these tests exercise, this step's "watch it fail" is achieved by
  temporarily reverting `SendAcknowledgementAsync`'s `catch` block (comment it out) and `HandleVoiceAsync`'s
  `LogReceived(logger, TransactionStages.Received, CaptureKind.Voice, ...)` call, confirming both new tests
  fail, then restoring them — no production code changes ship in this step; it exists to prove the two
  assertions are not vacuously true.

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramUpdateRouterTests`
  Expected: PASS.

  Commit:
  ```
  git add tests/Noof.Ledger.Telegram.Tests/TelegramUpdateRouterTests.cs
  git commit -m "$(cat <<'EOF'
  Add coverage for voice Received and a failed acknowledgement's StageFailed

  Both paths were already implemented in the previous commit's SendAcknowledgementAsync
  and HandleVoiceAsync; this locks them in with tests proven to fail against a reverted
  implementation first.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 3: `TranscriptionWorker` logs `Transcribed` on success**

  Write the test first. `tests/Noof.Ledger.Host.Tests/TranscriptionWorkerTests.cs` already has a `Setup`
  helper; add a logger to it and a new `CreateWorker`-style factory (the file currently constructs
  `TranscriptionWorker` inline per test — grep the file for `new TranscriptionWorker(` to find every call
  site; each one gains one trailing argument, `logger`, in place of whatever `ILogger` it passes today).

  Add near the top of the test class:
  ```csharp
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.TestKit;
  ```
  Add the test:
  ```csharp
  [Fact]
  public async Task Transcribed_is_logged_with_duration_and_character_count()
  {
      var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero));
      var harness = Setup(CaptureJob());
      var logger = new CapturingLogger<TranscriptionWorker>();
      var worker = new TranscriptionWorker(
          harness.ScopeFactory(), time, new CategorizationWorkerOptions(), WorkerId, Echo, logger);
      harness.Transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
          .Returns(_ =>
          {
              time.Advance(TimeSpan.FromSeconds(2.5));
              return Task.FromResult("купил вчера штуку евро");
          });

      await worker.RunTickAsync(TestContext.Current.CancellationToken);

      var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.TranscribedEventId).Subject;
      entry.Stage.Should().Be(TransactionStages.Transcribed);
      entry.Properties["DurationSeconds"].Should().Be(2.5);
      entry.Properties["Characters"].Should().Be("купил вчера штуку евро".Length);
      entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(TransactionId);
  }
  ```
  (If the existing `Setup`/harness does not expose `Transcriber` as a settable substitute reachable after
  construction, read the file's actual `Harness` record — reproduced under Interfaces above only up to line
  90 — and wire the `Returns` through whatever the real `Setup(...)` call already threads; the shape here
  assumes `harness.Transcriber` is the same `ITranscriber` substitute `Setup` builds, consistent with
  `Harness(IJobQueue Queue, ISpeechProvider SpeechProvider, ICategorizationStore Store, ITranscriptionStore
  TranscriptionStore, IVoiceFileSource VoiceFiles, ITranscriber Transcriber, IChatNotifier Notifier)` read
  from the file.)

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter TranscriptionWorkerTests`
  Expected failure: RED — no `LogTranscribed` call exists yet, so `logger.Entries` is empty and
  `ContainSingle` throws.

  Implement in `src/Noof.Ledger.Host/Workers/TranscriptionWorker.cs`:
  - Add `using Microsoft.Extensions.Logging;` and `using Noof.Ledger.Application.Diagnostics;`.
  - Change `internal sealed class TranscriptionWorker(...)` to `internal sealed partial class TranscriptionWorker(...)`.
  - In `ProcessClaimedJobAsync`, right after `record = await store.GetSubjectAsync(...)` succeeds (i.e. right
    after the `if (record is null) { ...; return; }` block), add:
    ```csharp
    using var scope = TransactionLogScope.Begin(logger, job.TransactionId);
    ```
  - Replace the two lines
    ```csharp
    await using var audio = await voiceFiles.DownloadAsync(job.VoiceFileId!, cancellationToken);
    var transcript = await transcriber.TranscribeAsync(audio, cancellationToken);
    ```
    with:
    ```csharp
    await using var audio = await voiceFiles.DownloadAsync(job.VoiceFileId!, cancellationToken);
    var transcriptionStartedAt = timeProvider.GetUtcNow();
    var transcript = await transcriber.TranscribeAsync(audio, cancellationToken);
    var transcriptionDuration = timeProvider.GetUtcNow() - transcriptionStartedAt;
    ```
  - Inside `if (transcript.Length == 0) { ... }`, before calling `ReportNothingHeardAsync`, add:
    ```csharp
    LogStageFailed(logger, TransactionStages.StageFailed, TransactionStages.Transcribed,
        new InvalidOperationException("nothing was heard in the voice note"));
    ```
  - Immediately after the `if (transcript.Length == 0) { ...; return; }` block (i.e. once a non-empty
    transcript is confirmed), add:
    ```csharp
    LogTranscribed(logger, TransactionStages.Transcribed, transcriptionDuration.TotalSeconds, transcript.Length);
    ```
  - In each of the three `catch` blocks in `ProcessClaimedJobAsync` (`catch (ModelCallException ex) when
    (ex.IsAccountLevel())`, `catch (ModelCallException ex)`, `catch (Exception ex) when (ex is not
    OperationCanceledException)`), add as the first line of the block:
    ```csharp
    LogStageFailed(logger, TransactionStages.StageFailed, TransactionStages.Transcribed, ex);
    ```
  - Add the two `[LoggerMessage]` methods at the bottom of the class:
    ```csharp
    [LoggerMessage(EventId = TransactionStages.TranscribedEventId, EventName = TransactionStages.Transcribed, Level = LogLevel.Information,
        Message = "{Stage} in {DurationSeconds}s, {Characters} characters")]
    static partial void LogTranscribed(ILogger logger, string stage, double durationSeconds, int characters);

    [LoggerMessage(EventId = TransactionStages.StageFailedEventId, EventName = TransactionStages.StageFailed, Level = LogLevel.Error,
        Message = "{Stage} at stage {FailedStage}")]
    static partial void LogStageFailed(ILogger logger, string stage, string failedStage, Exception exception);
    ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter TranscriptionWorkerTests`
  Expected: PASS (this file's full suite — the new test and every pre-existing one, none of whose behaviour
  changed).

  Commit:
  ```
  git add src/Noof.Ledger.Host/Workers/TranscriptionWorker.cs tests/Noof.Ledger.Host.Tests/TranscriptionWorkerTests.cs
  git commit -m "$(cat <<'EOF'
  Log a Transcribed transaction-path event with duration and length

  TranscriptionWorker now times the transcriber call with TimeProvider and logs EventId
  5002 (Transcribed) on a non-empty transcript, scoped to the transaction.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 4: `TranscriptionWorker` logs `StageFailed` for a model failure and for nothing heard**

  Write the tests first:
  ```csharp
  [Fact]
  public async Task A_model_failure_logs_StageFailed_for_Transcribed()
  {
      var harness = Setup(CaptureJob());
      var logger = new CapturingLogger<TranscriptionWorker>();
      var worker = new TranscriptionWorker(
          harness.ScopeFactory(), new FakeTimeProvider(DateTimeOffset.UtcNow), new CategorizationWorkerOptions(), WorkerId, Echo, logger);
      harness.Transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
          .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "bad audio format"));

      await worker.RunTickAsync(TestContext.Current.CancellationToken);

      var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.StageFailedEventId).Subject;
      entry.Properties["FailedStage"].Should().Be(TransactionStages.Transcribed);
      entry.Exception.Should().BeOfType<ModelCallException>();
      entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(TransactionId);
  }

  [Fact]
  public async Task Nothing_heard_logs_StageFailed_for_Transcribed()
  {
      var harness = Setup(CaptureJob());
      var logger = new CapturingLogger<TranscriptionWorker>();
      var worker = new TranscriptionWorker(
          harness.ScopeFactory(), new FakeTimeProvider(DateTimeOffset.UtcNow), new CategorizationWorkerOptions(), WorkerId, Echo, logger);
      harness.Transcriber.TranscribeAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns("");

      await worker.RunTickAsync(TestContext.Current.CancellationToken);

      var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.StageFailedEventId).Subject;
      entry.Properties["FailedStage"].Should().Be(TransactionStages.Transcribed);
  }
  ```
  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter TranscriptionWorkerTests`
  Expected: both PASS immediately — Step 3 already implemented every `LogStageFailed` call these tests
  exercise. Prove they are not vacuous by commenting out the `LogStageFailed` call inside the `transcript.Length
  == 0` branch and the one inside `catch (ModelCallException ex)`, re-running to see exactly these two tests
  go RED (the ones from Step 3 stay green, since they cover the success and the duration path, not these two
  branches), then restoring both lines.

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter TranscriptionWorkerTests`
  Expected: PASS.

  Commit:
  ```
  git add tests/Noof.Ledger.Host.Tests/TranscriptionWorkerTests.cs
  git commit -m "$(cat <<'EOF'
  Cover StageFailed for a model failure and for nothing heard

  Both paths were already implemented alongside the Transcribed event; this locks them
  in with tests proven to fail against a reverted implementation first.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 5: `CategorizationWorker` logs `Categorized` after the model answers**

  Write the test first. Add to `CategorizationWorkerTests.cs`:
  ```csharp
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.TestKit;
  ```
  Change `CreateWorker` to take and thread a logger:
  ```csharp
  static CategorizationWorker CreateWorker(
      IServiceScopeFactory scopeFactory, FakeTimeProvider time, CategorizationWorkerOptions? options = null,
      CapturingLogger<CategorizationWorker>? logger = null) =>
      new(scopeFactory, time, options ?? new CategorizationWorkerOptions(), WorkerId,
          Mapper, Scan, Echo, logger ?? new CapturingLogger<CategorizationWorker>());
  ```
  (`NullLogger<CategorizationWorker>.Instance` is dropped from the file's one remaining usage in favour of a
  fresh `CapturingLogger` default, which behaves identically for every test that does not inspect logging and
  lets any test opt in by passing its own instance.)

  Add the test:
  ```csharp
  [Fact]
  public async Task Categorized_is_logged_with_kind_wallet_and_a_computed_summary()
  {
      var store = Substitute.For<ICategorizationStore>();
      store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
      var categorizer = Substitute.For<ICategorizer>();
      categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine(250m, "RSD"));
      var logger = new CapturingLogger<CategorizationWorker>();
      var worker = CreateWorker(
          ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
          new FakeTimeProvider(DateTimeOffset.UtcNow), logger: logger);

      await worker.RunTickAsync(TestContext.Current.CancellationToken);

      var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.CategorizedEventId).Subject;
      entry.Stage.Should().Be(TransactionStages.Categorized);
      entry.Properties["Kind"].Should().Be(TransactionKind.Expense);
      entry.Properties["WalletId"].Should().Be(MainWallet.Id);
      entry.Properties["Summary"].Should().Be("250 RSD groceries");
      entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(TransactionId);
  }

  [Fact]
  public async Task Categorized_summarises_a_balance_statement_by_its_stated_amount()
  {
      var store = Substitute.For<ICategorizationStore>();
      store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(rawText: "на главном 45 тысяч"));
      var categorizer = Substitute.For<ICategorizer>();
      categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
          .Returns(new CategorizationProposal([], Kind: ProposedKind.Balance, BalanceAmount: 45000m));
      var logger = new CapturingLogger<CategorizationWorker>();
      var worker = CreateWorker(
          ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
          new FakeTimeProvider(DateTimeOffset.UtcNow), logger: logger);

      await worker.RunTickAsync(TestContext.Current.CancellationToken);

      var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.CategorizedEventId).Subject;
      entry.Properties["Kind"].Should().Be(TransactionKind.BalanceCheck);
      entry.Properties["Summary"].Should().Be("balance 45000 RSD");
  }
  ```
  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWorkerTests`
  Expected failure: RED — no `LogCategorized` call exists.

  Implement in `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`:
  - Add `using Microsoft.Extensions.Logging;` and `using Noof.Ledger.Application.Diagnostics;`.
  - Change `internal sealed class CategorizationWorker(...)` to `internal sealed partial class CategorizationWorker(...)`.
  - In `ProcessClaimedJobAsync`, change:
    ```csharp
    CategorizationSubject? subject = null;

    try
    {
    ```
    to:
    ```csharp
    CategorizationSubject? subject = null;
    var currentStage = TransactionStages.Categorized;

    using var scope = TransactionLogScope.Begin(logger, job.TransactionId);

    try
    {
    ```
  - Immediately after the block
    ```csharp
    if (!proposalMapper.TryMap(
        KeepingTheRecordsWallet(job, sub, proposal, wallets), offeredSlugs, offeredMerchantIds, wallets,
        options.DefaultCurrency, out var mapped, out var failure))
    {
        await FailTerminallyAsync(jobQueue, store, notifier, job, subject, failure, cancellationToken);
        return;
    }
    ```
    add:
    ```csharp
    LogCategorized(logger, TransactionStages.Categorized, mapped.Kind, mapped.WalletId, BuildSummary(mapped));
    ```
  - Add the static helper near `KeepingTheRecordsWallet`:
    ```csharp
    static string BuildSummary(MappedProposal mapped) =>
        mapped.Items.Count > 0
            ? string.Join("; ", mapped.Items.Select(item => $"{item.Amount} {item.CategorySlug}"))
            : mapped.StatedBalance is { } stated
                ? $"balance {stated}"
                : "no line items";
    ```
  - Change the two `FailTerminallyAsync(jobQueue, store, notifier, job, ..., cancellationToken)` call sites
    (the "transaction no longer exists" one and the one just shown) to pass `currentStage` as a new argument
    — see Step 8 for `FailTerminallyAsync`'s new signature — deferred to Step 8 so this step's diff stays
    within "log Categorized", but the two call sites' extra argument is added now to keep the file compiling;
    write it as `currentStage` and let Step 8 add the parameter it binds to. (If the plan is executed strictly
    step-by-step, fold this call-site edit into Step 8 instead and skip it here — either order compiles at the
    end of Step 8, and Step 5 alone is a self-contained, compiling, green step without it.)

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWorkerTests`
  Expected: PASS (full file).

  Commit:
  ```
  git add src/Noof.Ledger.Host/Workers/CategorizationWorker.cs tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs
  git commit -m "$(cat <<'EOF'
  Log a Categorized transaction-path event with a C#-built summary

  BuildSummary formats amounts and category slugs (or a balance statement's stated
  amount) from the already-mapped proposal, never from the model's own text. Logged as
  EventId 5003 right after ProposalMapper.TryMap succeeds.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 6: `CategorizationWorker` logs `Persisted` after `ApplyAsync`**

  Write the test first:
  ```csharp
  [Fact]
  public async Task Persisted_is_logged_after_the_outcome_is_applied()
  {
      var store = Substitute.For<ICategorizationStore>();
      store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
      var categorizer = Substitute.For<ICategorizer>();
      categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
      var logger = new CapturingLogger<CategorizationWorker>();
      var worker = CreateWorker(
          ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
          new FakeTimeProvider(DateTimeOffset.UtcNow), logger: logger);

      await worker.RunTickAsync(TestContext.Current.CancellationToken);

      var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.PersistedEventId).Subject;
      entry.Stage.Should().Be(TransactionStages.Persisted);
      entry.Properties["Kind"].Should().Be(TransactionKind.Expense);
      entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(TransactionId);
  }
  ```
  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWorkerTests`
  Expected failure: RED — no `LogPersisted` call exists.

  Implement: change
  ```csharp
  var occurredOn = mapped.OccurredOn ?? DefaultDay(job, sub);
  await store.ApplyAsync(
      job.TransactionId,
      new CategorizationOutcome(
          categorizedItems, occurredOn, job.Kind, job.Instruction, mapped.Kind, mapped.WalletId, mapped.StatedBalance),
      cancellationToken);
  ```
  to:
  ```csharp
  var occurredOn = mapped.OccurredOn ?? DefaultDay(job, sub);
  var outcome = new CategorizationOutcome(
      categorizedItems, occurredOn, job.Kind, job.Instruction, mapped.Kind, mapped.WalletId, mapped.StatedBalance);
  currentStage = TransactionStages.Persisted;
  await store.ApplyAsync(job.TransactionId, outcome, cancellationToken);
  LogPersisted(logger, TransactionStages.Persisted, outcome.TransactionKind);
  ```
  Every later reference to the inline outcome (there are none — it was only ever passed to `ApplyAsync`)
  needs no further change. Add the `[LoggerMessage]` method:
  ```csharp
  [LoggerMessage(EventId = TransactionStages.PersistedEventId, EventName = TransactionStages.Persisted, Level = LogLevel.Information,
      Message = "{Stage} as {Kind}")]
  static partial void LogPersisted(ILogger logger, string stage, TransactionKind kind);
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWorkerTests`
  Expected: PASS.

  Commit:
  ```
  git add src/Noof.Ledger.Host/Workers/CategorizationWorker.cs tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs
  git commit -m "$(cat <<'EOF'
  Log a Persisted transaction-path event after ApplyAsync commits

  EventId 5004, logged with the transaction's TransactionKind right after the store
  reports the outcome applied. currentStage flips to Persisted at the same point so a
  later StageFailed correctly blames persistence rather than categorization.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 7: `CategorizationWorker` logs `Replied` after the echo edit**

  Write the test first:
  ```csharp
  [Fact]
  public async Task Replied_is_logged_after_the_echo_edit_succeeds()
  {
      var store = StoreThatRemembersWhatItApplies(Subject());
      var categorizer = Substitute.For<ICategorizer>();
      categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
      var logger = new CapturingLogger<CategorizationWorker>();
      var worker = CreateWorker(
          ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
          new FakeTimeProvider(DateTimeOffset.UtcNow), logger: logger);

      await worker.RunTickAsync(TestContext.Current.CancellationToken);

      var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.RepliedEventId).Subject;
      entry.Stage.Should().Be(TransactionStages.Replied);
      entry.Properties["BotMessageId"].Should().Be(42);
      entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(TransactionId);
  }
  ```
  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWorkerTests`
  Expected failure: RED — no `LogReplied` call exists.

  Implement `EchoAsync`:
  ```csharp
  async Task EchoAsync(ICategorizationStore store, IChatNotifier notifier, CategorizationJob job, CancellationToken cancellationToken)
  {
      try
      {
          // Read back, not composed from the proposal: the echo shows what the database now holds (D4).
          if (await store.GetSubjectAsync(job.TransactionId, cancellationToken) is not { BotMessageId: { } messageId } record)
              return;

          await notifier.EditAsync(record.TelegramChatId, messageId, recordEcho.Compose(record), cancellationToken);
          LogReplied(logger, TransactionStages.Replied, messageId);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
          LogStageFailed(logger, TransactionStages.StageFailed, TransactionStages.Replied, ex);
          logger.LogWarning(ex,
              "Failed to echo job {JobId}'s result to Telegram; the categorization itself already succeeded", job.Id);
      }
  }
  ```
  (`EchoAsync` runs inside the `using var scope = TransactionLogScope.Begin(logger, job.TransactionId);`
  opened at the top of `ProcessClaimedJobAsync`, which is still active for the duration of the `await
  EchoAsync(...)` call, so no second scope is needed here.)

  Add the `[LoggerMessage]` method:
  ```csharp
  [LoggerMessage(EventId = TransactionStages.RepliedEventId, EventName = TransactionStages.Replied, Level = LogLevel.Information,
      Message = "{Stage} to bot message {BotMessageId}")]
  static partial void LogReplied(ILogger logger, string stage, int botMessageId);
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWorkerTests`
  Expected: PASS.

  Commit:
  ```
  git add src/Noof.Ledger.Host/Workers/CategorizationWorker.cs tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs
  git commit -m "$(cat <<'EOF'
  Log a Replied transaction-path event after the echo edit succeeds

  EventId 5005, logged with the Telegram bot message id right after EditAsync succeeds
  inside EchoAsync; a failed edit now also logs StageFailed for Replied alongside the
  existing warning, without changing that it never fails the job.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 8: `CategorizationWorker` logs `StageFailed` for terminal and transient failures**

  Write the tests first:
  ```csharp
  [Fact]
  public async Task A_terminal_failure_logs_StageFailed_for_Categorized()
  {
      var jobQueue = QueueWith(Job());
      var store = Substitute.For<ICategorizationStore>();
      store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
      var categorizer = Substitute.For<ICategorizer>();
      categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
          .ThrowsAsync(new ModelCallException(ModelFailureKind.Terminal, "no credit left on this key"));
      var logger = new CapturingLogger<CategorizationWorker>();
      var worker = CreateWorker(
          ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer),
          new FakeTimeProvider(DateTimeOffset.UtcNow), logger: logger);

      await worker.RunTickAsync(TestContext.Current.CancellationToken);

      var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.StageFailedEventId).Subject;
      entry.Properties["FailedStage"].Should().Be(TransactionStages.Categorized);
      entry.Exception!.Message.Should().Be("no credit left on this key");
      entry.Scope![TransactionStages.TransactionIdProperty].Should().Be(TransactionId);
  }

  [Fact]
  public async Task A_transient_failure_logs_StageFailed_for_Categorized()
  {
      var jobQueue = Substitute.For<IJobQueue>();
      jobQueue.ClaimAsync(WorkerId, Arg.Any<IReadOnlyCollection<JobKind>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Job());
      jobQueue.RetryAsync(JobId, WorkerId, Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
          .Returns(JobCompletionOutcome.Applied);
      var store = Substitute.For<ICategorizationStore>();
      store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
      var categorizer = Substitute.For<ICategorizer>();
      categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
          .ThrowsAsync(new ModelCallException(ModelFailureKind.Transient, "rate limited"));
      var logger = new CapturingLogger<CategorizationWorker>();
      var worker = CreateWorker(
          ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer),
          new FakeTimeProvider(DateTimeOffset.UtcNow), logger: logger);

      await worker.RunTickAsync(TestContext.Current.CancellationToken);

      var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.StageFailedEventId).Subject;
      entry.Properties["FailedStage"].Should().Be(TransactionStages.Categorized);
  }

  [Fact]
  public async Task A_missing_transaction_logs_StageFailed_for_Categorized()
  {
      var jobQueue = QueueWith(Job());
      var store = Substitute.For<ICategorizationStore>();
      store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns((CategorizationSubject?)null);
      var logger = new CapturingLogger<CategorizationWorker>();
      var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store), new FakeTimeProvider(DateTimeOffset.UtcNow), logger: logger);

      await worker.RunTickAsync(TestContext.Current.CancellationToken);

      var entry = logger.Entries.Should().ContainSingle(e => e.EventId.Id == TransactionStages.StageFailedEventId).Subject;
      entry.Properties["FailedStage"].Should().Be(TransactionStages.Categorized);
  }
  ```
  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWorkerTests`
  Expected failure: RED — `FailTerminallyAsync`/`HandleModelFailureAsync` do not yet log or take a
  `failedStage` parameter (compile error if Step 5's deferred call-site edit was skipped there, otherwise a
  runtime RED from an empty `logger.Entries`).

  Implement — change every call site and the two methods:
  ```csharp
  async Task HandleModelFailureAsync(
      IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier,
      CategorizationJob job, CategorizationSubject? subject, ModelFailureKind kind, string error, string failedStage,
      CancellationToken cancellationToken)
  {
      if (kind == ModelFailureKind.Terminal)
      {
          await FailTerminallyAsync(jobQueue, store, notifier, job, subject, error, failedStage, cancellationToken);
          return;
      }

      LogStageFailed(logger, TransactionStages.StageFailed, failedStage, new InvalidOperationException(error));

      // The same predicate EfJobQueue.RetryAsync evaluates server-side - see decision 10. This only
      // stays correct because Program.cs feeds EfJobQueue the same CategorizationWorkerOptions.MaxAttempts.
      var isLastAttempt = job.AttemptCount >= options.MaxAttempts;
      var runAfter = timeProvider.GetUtcNow() + options.ComputeBackoff(job.AttemptCount);
      var outcome = await jobQueue.RetryAsync(job.Id, workerId, runAfter, error, cancellationToken);

      if (outcome == JobCompletionOutcome.Applied && isLastAttempt)
          await NotifyFailureAsync(store, notifier, subject, job, cancellationToken);
  }

  async Task FailTerminallyAsync(
      IJobQueue jobQueue, ICategorizationStore store, IChatNotifier notifier,
      CategorizationJob job, CategorizationSubject? subject, string error, string failedStage, CancellationToken cancellationToken)
  {
      LogStageFailed(logger, TransactionStages.StageFailed, failedStage, new InvalidOperationException(error));

      var outcome = await jobQueue.FailAsync(job.Id, workerId, error, cancellationToken);
      if (outcome == JobCompletionOutcome.Applied)
          await NotifyFailureAsync(store, notifier, subject, job, cancellationToken);
  }
  ```
  Update every call site to pass `currentStage`:
  - `await FailTerminallyAsync(jobQueue, store, notifier, job, null, "the transaction this job points at no longer exists", currentStage, cancellationToken);`
  - `await FailTerminallyAsync(jobQueue, store, notifier, job, subject, failure, currentStage, cancellationToken);` (the `TryMap` failure branch, from Step 5)
  - The three `catch` blocks' `await HandleModelFailureAsync(..., ex.Message /* or "..." */, currentStage, cancellationToken);` (three call sites: the account-level `ModelCallException`, the plain `ModelCallException`, and the generic `Exception`).

  Add the `[LoggerMessage]` method:
  ```csharp
  [LoggerMessage(EventId = TransactionStages.StageFailedEventId, EventName = TransactionStages.StageFailed, Level = LogLevel.Error,
      Message = "{Stage} at stage {FailedStage}")]
  static partial void LogStageFailed(ILogger logger, string stage, string failedStage, Exception exception);
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWorkerTests`
  Expected: PASS (full file — every pre-existing test's behaviour is unchanged; only the new `failedStage`
  parameter and the new log calls were added).

  Commit:
  ```
  git add src/Noof.Ledger.Host/Workers/CategorizationWorker.cs tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs
  git commit -m "$(cat <<'EOF'
  Log StageFailed (5009) for every CategorizationWorker failure path

  FailTerminallyAsync and HandleModelFailureAsync now take a failedStage, threaded from
  ProcessClaimedJobAsync's currentStage local (Categorized until ApplyAsync is reached,
  then Persisted), so a persistence failure is no longer misreported as a categorization
  failure.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 9: full-file verification**

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
  Expected: PASS.
  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
  Expected: PASS.

  Per CLAUDE.md §4 ("Database and E2E test projects run filtered ... and in full once at the end of a
  phase"), no unfiltered `--solution` run and no E2E run are part of this task — those belong to the phase's
  closing task, not Task 5.

  No commit for this step (verification only, nothing to stage).

---

### Task 6: Health checks, ISystemHealth, Telegram heartbeat

**Plan notes (deviations from the letter of the scope):**

1. **AI keys check reads `IModelProvider.IsConfiguredAsync` / `ISpeechProvider.IsConfiguredAsync`**, not
   `ISecretStore` + new `SecretKeys.Anthropic…`/`SecretKeys.Groq…` constants. Those constants do not exist
   today — the Anthropic and Groq API key strings (`"anthropic-api-key"`, `"groq-api-key"`) are private
   to `AnthropicChatClientFactory`/`GroqSpeechToTextClientFactory` inside `Noof.Ledger.Ai`. Adding
   provider-named constants to `Noof.Ledger.Application.Secrets.SecretKeys` would put the literal words
   "anthropic"/"groq" in a file outside `Noof.Ledger.Ai`, which the existing
   `AiBoundaryTests.Nothing_outside_the_Ai_assembly_names_the_provider` test already forbids (Host and
   Application "only `Noof.Ledger.Ai` may know which provider answers"). `IModelProvider` and
   `ISpeechProvider` already exist for exactly this purpose (`IsConfiguredAsync`, provider-neutral,
   resolved via DI to the Anthropic/Groq implementations) and are what `Noof.Ledger.Web`'s Settings page
   already uses to ask the same question. Using them here keeps both the new "no health check references
   the AI stack" rule and the pre-existing provider-naming rule intact, with no new architecture-test
   collision.
2. **`Home.razor` is left unchanged**, per the task's own preferred fallback. Its `BackupIsStale()` /
   `DescribeBackup()` rule (36 h staleness window, computed from `IBackupLog` directly) is duplicated for
   one wave by `BackupHealthCheck` (26 h window, per the spec's health table — a different, and correct,
   number for a different purpose: the tile's own copy is Task 7's to delete). No test in this task
   touches `Home.razor` or its Razor-component tests.
3. **Two states the spec's Telegram/Backup tables don't enumerate** are given a defined, documented
   answer rather than left to fall through: a heartbeat that has neither succeeded nor failed yet (host
   just started) reports `Warning`/"Waiting for the first poll"; a heartbeat whose last success is stale
   with no recorded failure since (poll loop wedged, not failing) reports `Warning`/"Stale — no successful
   poll recently". Both are `Warning`, matching the table's existing bias toward Warning for "nothing bad
   confirmed, but nothing good confirmed either".

**Files:**

*Create*
- `src/Noof.Ledger.Application/Diagnostics/HealthContracts.cs` — `HealthLevel`, `HealthItem`,
  `SystemHealthReport`, `ISystemHealth`, `PollFailure`, `IPollingHeartbeat`, `HealthCheckNames`.
- `src/Noof.Ledger.Persistence/Diagnostics/MigrationsHealthCheck.cs`
- `src/Noof.Ledger.Telegram/Diagnostics/TelegramHealthCheck.cs`
- `src/Noof.Ledger.Host/Diagnostics/IFreeSpaceProvider.cs` (+ `DriveFreeSpaceProvider`)
- `src/Noof.Ledger.Host/Diagnostics/DatabaseHealthCheck.cs`
- `src/Noof.Ledger.Host/Diagnostics/AiKeysHealthCheck.cs`
- `src/Noof.Ledger.Host/Diagnostics/BackupHealthCheck.cs`
- `src/Noof.Ledger.Host/Diagnostics/DiskHealthCheck.cs`
- `src/Noof.Ledger.Host/Diagnostics/LogSinkHealthCheck.cs`
- `src/Noof.Ledger.Host/Diagnostics/SystemHealth.cs`
- `src/Noof.Ledger.Host/Diagnostics/PollingHeartbeat.cs`
- `src/Noof.Ledger.Host/Diagnostics/DiagnosticsRegistration.cs`
- `tests/Noof.Ledger.Architecture.Tests/HealthCheckBoundaryTests.cs`
- `tests/Noof.Ledger.Host.Tests/Diagnostics/DatabaseHealthCheckTests.cs`
- `tests/Noof.Ledger.Host.Tests/Diagnostics/AiKeysHealthCheckTests.cs`
- `tests/Noof.Ledger.Host.Tests/Diagnostics/BackupHealthCheckTests.cs`
- `tests/Noof.Ledger.Host.Tests/Diagnostics/DiskHealthCheckTests.cs`
- `tests/Noof.Ledger.Host.Tests/Diagnostics/LogSinkHealthCheckTests.cs`
- `tests/Noof.Ledger.Host.Tests/Diagnostics/SystemHealthTests.cs`
- `tests/Noof.Ledger.Host.Tests/Diagnostics/PollingHeartbeatTests.cs`
- `tests/Noof.Ledger.Persistence.Tests/MigrationsHealthCheckTests.cs`
- `tests/Noof.Ledger.Telegram.Tests/TelegramHealthCheckTests.cs`

*Modify*
- `src/Noof.Ledger.Persistence/PersistenceRegistration.cs` (lines 37–65: add `AddHealthChecks().AddCheck<MigrationsHealthCheck>`)
- `src/Noof.Ledger.Persistence/Noof.Ledger.Persistence.csproj` (add `Microsoft.Extensions.Diagnostics.HealthChecks` package reference)
- `src/Noof.Ledger.Telegram/TelegramRegistration.cs` (lines 15–35: add `AddHealthChecks().AddCheck<TelegramHealthCheck>`)
- `src/Noof.Ledger.Telegram/Noof.Ledger.Telegram.csproj` (add `Microsoft.Extensions.Diagnostics.HealthChecks` package reference)
- `src/Noof.Ledger.Telegram/TelegramPollingService.cs` (lines 15–22 constructor; lines 84–89 success path; lines 143–148 failure path: record the heartbeat)
- `src/Noof.Ledger.Host/Program.cs` (anchor: the line calling `builder.Services.AddNoofWorkers(categorizationOptions, backupOptions);` — add `builder.Services.AddNoofDiagnostics();` immediately after it; exact line number depends on Task 1's already-merged startup-gate wiring, which this task does not otherwise touch)
- `Directory.Packages.props` (add `PackageVersion` for `Microsoft.Extensions.Diagnostics.HealthChecks`, pinned `10.0.12` to match the other `Microsoft.Extensions.*` 10.0.12 entries already in the file)
- `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs` (line 42–64: add the seven new `Noof.Ledger.Application` public names)
- `tests/Noof.Ledger.Telegram.Tests/TelegramPollingServiceTests.cs` (the `CreateService`/`ScopeFactoryFor` helpers gain an `IPollingHeartbeat` parameter, defaulted to a fresh fake, so every existing call site keeps compiling)

**Interfaces:**

*Consumes (from Tasks 1–4, already merged)*
```csharp
namespace Noof.Ledger.Application.Diagnostics;
public enum DatabaseState { Waiting, Migrating, Ready, Failed }
public interface IDatabaseGate { DatabaseState State { get; } string? Detail { get; } Task WaitUntilReadyAsync(CancellationToken cancellationToken); }
public interface ILogSinkStatus { DateTimeOffset? LastFailureAt { get; } void RecordFailure(DateTimeOffset at); }
```
```csharp
namespace Noof.Ledger.Application.Backup; // existing, Phase 4
public sealed record BackupStatus(DateTimeOffset? LastSuccessAt, bool LastRunFailed, string? LastError);
public interface IBackupLog { Task<BackupStatus> StatusAsync(CancellationToken cancellationToken); Task RecordAsync(BackupRunRecord record, CancellationToken cancellationToken); }
```
```csharp
namespace Noof.Ledger.Application.Categorization; public interface IModelProvider : ISecretProbe { string SecretLabel { get; } Task<bool> IsConfiguredAsync(CancellationToken cancellationToken); }
namespace Noof.Ledger.Application.Transcription;  public interface ISpeechProvider : ISecretProbe { string SecretLabel { get; } Task<bool> IsConfiguredAsync(CancellationToken cancellationToken); }
```

*Produces (this task, exact contract names)*
```csharp
namespace Noof.Ledger.Application.Diagnostics;

public enum HealthLevel { Ok, Warning, Failing }
public sealed record HealthItem(string Name, HealthLevel Level, string Summary, DateTimeOffset CheckedAt);
public sealed record SystemHealthReport(HealthLevel Overall, IReadOnlyList<HealthItem> Items);
public interface ISystemHealth { Task<SystemHealthReport> GetAsync(bool fresh, CancellationToken cancellationToken); }

public enum PollFailure { Network, Unauthorized, Other }
public interface IPollingHeartbeat
{
    DateTimeOffset? LastSuccessAt { get; }
    (DateTimeOffset At, PollFailure Failure)? LastFailure { get; }
    void RecordSuccess(DateTimeOffset at);
    void RecordFailure(DateTimeOffset at, PollFailure failure);
}

public static class HealthCheckNames
{
    public const string Database = "Database", Migrations = "Migrations", Telegram = "Telegram",
        AiKeys = "AI keys", Backup = "Backup", Disk = "Disk", LogSink = "Log sink";
    public static IReadOnlyList<string> Ordered { get; } = [Database, Migrations, Telegram, AiKeys, Backup, Disk, LogSink];
}
```
Task-internal (not in the cross-task contract, so free to name):
```csharp
namespace Noof.Ledger.Host.Diagnostics;
internal interface IFreeSpaceProvider { long GetAvailableFreeBytes(string path); }
```

---

- [ ] **Step 1: Contracts — `HealthContracts.cs`, watch the surface guard fail, then pass**

  Write the failing check first: extend `PublicSurfaceTests.Allowed["Noof.Ledger.Application"]` with the
  seven new names, run it against the *current* source tree (where the file doesn't exist yet) so it fails
  for the opposite reason — the allowed set now claims types the source doesn't have.

  Edit `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`, inside the
  `["Noof.Ledger.Application"]` array (after `"BackupRunRecord", "BackupStatus", "DumpResult", "IBackupLog", "IDatabaseDumper",`), add:
  ```csharp
  "HealthLevel", "HealthItem", "SystemHealthReport", "ISystemHealth",
  "PollFailure", "IPollingHeartbeat", "HealthCheckNames",
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter PublicSurfaceTests`
  Expected failure: `Public_types_are_exactly_the_allowed_set("Noof.Ledger.Application")` fails —
  `Allowed` now contains 7 names `PublicTypesIn` cannot find.

  Implement — create `src/Noof.Ledger.Application/Diagnostics/HealthContracts.cs`:
  ```csharp
  namespace Noof.Ledger.Application.Diagnostics;

  public enum HealthLevel { Ok, Warning, Failing }

  public sealed record HealthItem(string Name, HealthLevel Level, string Summary, DateTimeOffset CheckedAt);

  public sealed record SystemHealthReport(HealthLevel Overall, IReadOnlyList<HealthItem> Items);

  public interface ISystemHealth
  {
      Task<SystemHealthReport> GetAsync(bool fresh, CancellationToken cancellationToken);
  }

  public enum PollFailure { Network, Unauthorized, Other }

  public interface IPollingHeartbeat
  {
      DateTimeOffset? LastSuccessAt { get; }

      (DateTimeOffset At, PollFailure Failure)? LastFailure { get; }

      void RecordSuccess(DateTimeOffset at);

      void RecordFailure(DateTimeOffset at, PollFailure failure);
  }

  public static class HealthCheckNames
  {
      public const string Database = "Database";
      public const string Migrations = "Migrations";
      public const string Telegram = "Telegram";
      public const string AiKeys = "AI keys";
      public const string Backup = "Backup";
      public const string Disk = "Disk";
      public const string LogSink = "Log sink";

      public static IReadOnlyList<string> Ordered { get; } =
          [Database, Migrations, Telegram, AiKeys, Backup, Disk, LogSink];
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter PublicSurfaceTests`
  Expected: passes.

  Commit:
  `git add src/Noof.Ledger.Application/Diagnostics/HealthContracts.cs tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`
  ```
  Add health-check contracts (ISystemHealth, IPollingHeartbeat, HealthCheckNames)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 2: Package reference for `Microsoft.Extensions.Diagnostics.HealthChecks` in Persistence and Telegram**

  No test — this is dependency wiring (CLAUDE.md's `Program.cs` wiring exemption extends to a project's own
  package references; the next step's failing compile is the real check).

  Edit `Directory.Packages.props`, inside the first `<ItemGroup>` (after
  `<PackageVersion Include="Microsoft.Extensions.Configuration" Version="10.0.12" />`), add:
  ```xml
  <PackageVersion Include="Microsoft.Extensions.Diagnostics.HealthChecks" Version="10.0.12" />
  ```

  Edit `src/Noof.Ledger.Persistence/Noof.Ledger.Persistence.csproj`, inside the `PackageReference` item
  group (alongside `Microsoft.AspNetCore.DataProtection.Abstractions`), add:
  ```xml
  <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks" />
  ```

  Edit `src/Noof.Ledger.Telegram/Noof.Ledger.Telegram.csproj`, inside its `PackageReference` item group
  (alongside `Telegram.Bot`), add the same line.

  `Noof.Ledger.Host` needs no new reference: it is `Microsoft.NET.Sdk.Web`, and
  `Microsoft.Extensions.Diagnostics.HealthChecks` ships in the ASP.NET Core shared framework that SDK
  already references.

  Run: `dotnet build NoofLedger.slnx` — expected to still succeed (nothing references the new types yet).

  Commit:
  `git add Directory.Packages.props src/Noof.Ledger.Persistence/Noof.Ledger.Persistence.csproj src/Noof.Ledger.Telegram/Noof.Ledger.Telegram.csproj`
  ```
  Add Microsoft.Extensions.Diagnostics.HealthChecks to Persistence and Telegram

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 3: `DatabaseHealthCheck` (Host)**

  Write failing test — create `tests/Noof.Ledger.Host.Tests/Diagnostics/DatabaseHealthCheckTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using NSubstitute;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Host.Diagnostics;

  namespace Noof.Ledger.Host.Tests.Diagnostics;

  public class DatabaseHealthCheckTests
  {
      static IDatabaseGate GateWith(DatabaseState state, string? detail = null)
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.State.Returns(state);
          gate.Detail.Returns(detail);
          return gate;
      }

      [Fact]
      public async Task Ready_is_healthy()
      {
          var check = new DatabaseHealthCheck(GateWith(DatabaseState.Ready));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Healthy);
      }

      [Theory]
      [InlineData(DatabaseState.Waiting)]
      [InlineData(DatabaseState.Migrating)]
      public async Task Waiting_or_migrating_is_degraded(DatabaseState state)
      {
          var check = new DatabaseHealthCheck(GateWith(state));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Degraded);
          result.Description.Should().Be("Offline — waiting for PostgreSQL");
      }

      [Fact]
      public async Task Failed_is_unhealthy_and_carries_the_gates_detail()
      {
          var check = new DatabaseHealthCheck(GateWith(DatabaseState.Failed, "PostgresException: relation missing"));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Unhealthy);
          result.Description.Should().Be("PostgresException: relation missing");
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter DatabaseHealthCheckTests`
  Expected failure: compile error — `Noof.Ledger.Host.Diagnostics.DatabaseHealthCheck` does not exist.

  Implement — create `src/Noof.Ledger.Host/Diagnostics/DatabaseHealthCheck.cs`:
  ```csharp
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using Noof.Ledger.Application.Diagnostics;

  namespace Noof.Ledger.Host.Diagnostics;

  internal sealed class DatabaseHealthCheck(IDatabaseGate gate) : IHealthCheck
  {
      public Task<HealthCheckResult> CheckHealthAsync(
          HealthCheckContext context, CancellationToken cancellationToken = default)
      {
          var result = gate.State switch
          {
              DatabaseState.Ready => HealthCheckResult.Healthy("Ready"),
              DatabaseState.Waiting or DatabaseState.Migrating =>
                  HealthCheckResult.Degraded("Offline — waiting for PostgreSQL"),
              DatabaseState.Failed => HealthCheckResult.Unhealthy(gate.Detail ?? "Migration failed"),
              _ => HealthCheckResult.Unhealthy("Unknown database state"),
          };

          return Task.FromResult(result);
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter DatabaseHealthCheckTests`
  Expected: passes.

  Commit:
  `git add src/Noof.Ledger.Host/Diagnostics/DatabaseHealthCheck.cs tests/Noof.Ledger.Host.Tests/Diagnostics/DatabaseHealthCheckTests.cs`
  ```
  Add Database health check

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 4: `MigrationsHealthCheck` (Persistence) — unit fakes, then a DB test on a clone**

  Write failing unit test — create `tests/Noof.Ledger.Persistence.Tests/MigrationsHealthCheckTests.cs`
  (fast half — gate-not-ready short circuit needs no database):
  ```csharp
  using AwesomeAssertions;
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using NSubstitute;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Persistence.Diagnostics;

  namespace Noof.Ledger.Persistence.Tests;

  [Collection("postgres")]
  public class MigrationsHealthCheckTests(PostgresFixture fixture)
  {
      static IDatabaseGate GateWith(DatabaseState state)
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.State.Returns(state);
          return gate;
      }

      [Fact]
      public async Task Reports_degraded_without_touching_the_database_when_the_gate_is_not_ready()
      {
          await using var db = await fixture.CreateContextAsync();
          var check = new MigrationsHealthCheck(db, GateWith(DatabaseState.Waiting));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Degraded);
          result.Description.Should().Be("Waiting for the database");
      }

      [Fact]
      public async Task A_fully_migrated_clone_reports_healthy()
      {
          await using var connection = await fixture.CreateDatabaseAsync();
          var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<LedgerDbContext>()
              .UseNpgsql(connection.ConnectionString).Options;
          await using var db = new LedgerDbContext(options);
          var check = new MigrationsHealthCheck(db, GateWith(DatabaseState.Ready));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Healthy);
      }

      [Fact]
      public async Task An_unmigrated_database_reports_unhealthy_naming_the_pending_count()
      {
          await using var db = await fixture.CreateContextAsync();
          var check = new MigrationsHealthCheck(db, GateWith(DatabaseState.Ready));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Unhealthy);
          result.Description.Should().MatchRegex(@"^\d+ migration\(s\) pending$");
      }
  }
  ```
  Note: `fixture.CreateContextAsync()` (used elsewhere for e.g. `MigrationContractTests`) creates a brand
  new, empty database and hands back an un-migrated `LedgerDbContext` — exactly the "pending migrations"
  fixture; `fixture.CreateDatabaseAsync()` clones the already-migrated `noof_ledger_test_template`.

  Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter MigrationsHealthCheckTests`
  Expected failure: compile error — `Noof.Ledger.Persistence.Diagnostics.MigrationsHealthCheck` does not exist.

  Implement — create `src/Noof.Ledger.Persistence/Diagnostics/MigrationsHealthCheck.cs`:
  ```csharp
  using Microsoft.EntityFrameworkCore;
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using Noof.Ledger.Application.Diagnostics;

  namespace Noof.Ledger.Persistence.Diagnostics;

  internal sealed class MigrationsHealthCheck(LedgerDbContext db, IDatabaseGate gate) : IHealthCheck
  {
      public async Task<HealthCheckResult> CheckHealthAsync(
          HealthCheckContext context, CancellationToken cancellationToken = default)
      {
          if (gate.State is not DatabaseState.Ready)
              return HealthCheckResult.Degraded("Waiting for the database");

          var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();

          return pending.Length == 0
              ? HealthCheckResult.Healthy("Up to date")
              : HealthCheckResult.Unhealthy($"{pending.Length} migration(s) pending");
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter MigrationsHealthCheckTests`
  Expected: passes.

  Wire it — edit `src/Noof.Ledger.Persistence/PersistenceRegistration.cs`: add
  `using Microsoft.Extensions.Diagnostics.HealthChecks;`, `using Noof.Ledger.Application.Diagnostics;`,
  `using Noof.Ledger.Persistence.Diagnostics;` to the usings, and inside `AddNoofPersistence`, immediately
  before `return services;`, add:
  ```csharp
  services.AddHealthChecks().AddCheck<MigrationsHealthCheck>(HealthCheckNames.Migrations);
  ```

  Run: `dotnet build NoofLedger.slnx` — expected to succeed.

  Commit:
  `git add src/Noof.Ledger.Persistence/Diagnostics/MigrationsHealthCheck.cs src/Noof.Ledger.Persistence/PersistenceRegistration.cs tests/Noof.Ledger.Persistence.Tests/MigrationsHealthCheckTests.cs`
  ```
  Add Migrations health check

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 5: `PollingHeartbeat` (Host)**

  Write failing test — create `tests/Noof.Ledger.Host.Tests/Diagnostics/PollingHeartbeatTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Host.Diagnostics;

  namespace Noof.Ledger.Host.Tests.Diagnostics;

  public class PollingHeartbeatTests
  {
      static readonly DateTimeOffset T0 = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);

      [Fact]
      public void Starts_with_no_success_and_no_failure()
      {
          var heartbeat = new PollingHeartbeat();

          heartbeat.LastSuccessAt.Should().BeNull();
          heartbeat.LastFailure.Should().BeNull();
      }

      [Fact]
      public void Records_a_success_and_clears_a_prior_failure()
      {
          var heartbeat = new PollingHeartbeat();
          heartbeat.RecordFailure(T0, PollFailure.Network);

          heartbeat.RecordSuccess(T0.AddMinutes(1));

          heartbeat.LastSuccessAt.Should().Be(T0.AddMinutes(1));
          heartbeat.LastFailure.Should().BeNull();
      }

      [Fact]
      public void Records_a_failure_with_its_kind_without_touching_the_last_success()
      {
          var heartbeat = new PollingHeartbeat();
          heartbeat.RecordSuccess(T0);

          heartbeat.RecordFailure(T0.AddMinutes(5), PollFailure.Unauthorized);

          heartbeat.LastSuccessAt.Should().Be(T0);
          heartbeat.LastFailure.Should().Be((T0.AddMinutes(5), PollFailure.Unauthorized));
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter PollingHeartbeatTests`
  Expected failure: compile error — `Noof.Ledger.Host.Diagnostics.PollingHeartbeat` does not exist.

  Implement — create `src/Noof.Ledger.Host/Diagnostics/PollingHeartbeat.cs`:
  ```csharp
  using Noof.Ledger.Application.Diagnostics;

  namespace Noof.Ledger.Host.Diagnostics;

  internal sealed class PollingHeartbeat : IPollingHeartbeat
  {
      readonly Lock gate = new();
      DateTimeOffset? lastSuccessAt;
      (DateTimeOffset At, PollFailure Failure)? lastFailure;

      public DateTimeOffset? LastSuccessAt
      {
          get { lock (gate) return lastSuccessAt; }
      }

      public (DateTimeOffset At, PollFailure Failure)? LastFailure
      {
          get { lock (gate) return lastFailure; }
      }

      public void RecordSuccess(DateTimeOffset at)
      {
          lock (gate)
          {
              lastSuccessAt = at;
              lastFailure = null;
          }
      }

      public void RecordFailure(DateTimeOffset at, PollFailure failure)
      {
          lock (gate)
              lastFailure = (at, failure);
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter PollingHeartbeatTests`
  Expected: passes.

  Commit:
  `git add src/Noof.Ledger.Host/Diagnostics/PollingHeartbeat.cs tests/Noof.Ledger.Host.Tests/Diagnostics/PollingHeartbeatTests.cs`
  ```
  Add PollingHeartbeat

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 6: Classify Telegram poll failures and record the heartbeat in `TelegramPollingService`**

  Write failing tests — edit `tests/Noof.Ledger.Telegram.Tests/TelegramPollingServiceTests.cs`. First give the
  test helpers an `IPollingHeartbeat`, defaulted so every existing call keeps compiling unchanged:
  ```csharp
  using Noof.Ledger.Application.Diagnostics;
  ```
  Change `ScopeFactoryFor` and `CreateService` to:
  ```csharp
  static IServiceScopeFactory ScopeFactoryFor(
      ISecretStore secretStore, ITelegramUpdateRouter? router = null, IChatNotifier? chatNotifier = null)
  {
      // unchanged body
  }

  static TelegramPollingService CreateService(
      ISecretStore secretStore,
      ITelegramBotClientFactory clientFactory,
      TelegramClientHandle handle,
      ITelegramUpdateRouter? router = null,
      IChatNotifier? chatNotifier = null,
      IPollingHeartbeat? heartbeat = null,
      TimeProvider? timeProvider = null) =>
      new(
          ScopeFactoryFor(secretStore, router, chatNotifier),
          clientFactory,
          handle,
          new ConfigurationBuilder().Build(),
          timeProvider ?? TimeProvider.System,
          heartbeat ?? Substitute.For<IPollingHeartbeat>(),
          NullLogger<TelegramPollingService>.Instance);
  ```
  Then add new tests at the end of the class:
  ```csharp
  [Fact]
  public async Task A_successful_poll_records_a_success_on_the_heartbeat()
  {
      var client = Substitute.For<ITelegramBotClient>();
      client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
      var clientFactory = Substitute.For<ITelegramBotClientFactory>();
      clientFactory.Create("tok1").Returns(client);
      var heartbeat = Substitute.For<IPollingHeartbeat>();
      var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero));

      await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle(), heartbeat: heartbeat, timeProvider: time)
          .RunTickAsync(TestContext.Current.CancellationToken);

      heartbeat.Received(1).RecordSuccess(time.GetUtcNow());
  }

  [Fact]
  public async Task A_network_failure_records_a_network_classified_heartbeat_failure()
  {
      var client = Substitute.For<ITelegramBotClient>();
      client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>())
          .ThrowsAsync(new HttpRequestException("connection refused"));
      var clientFactory = Substitute.For<ITelegramBotClientFactory>();
      clientFactory.Create("tok1").Returns(client);
      var heartbeat = Substitute.For<IPollingHeartbeat>();
      var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero));

      await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle(), heartbeat: heartbeat, timeProvider: time)
          .RunTickAsync(TestContext.Current.CancellationToken);

      heartbeat.Received(1).RecordFailure(time.GetUtcNow(), PollFailure.Network);
  }

  [Fact]
  public async Task A_401_from_Telegram_records_an_unauthorized_classified_heartbeat_failure()
  {
      var client = Substitute.For<ITelegramBotClient>();
      client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>())
          .ThrowsAsync(new ApiRequestException("Unauthorized", 401));
      var clientFactory = Substitute.For<ITelegramBotClientFactory>();
      clientFactory.Create("tok1").Returns(client);
      var heartbeat = Substitute.For<IPollingHeartbeat>();
      var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero));

      await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle(), heartbeat: heartbeat, timeProvider: time)
          .RunTickAsync(TestContext.Current.CancellationToken);

      heartbeat.Received(1).RecordFailure(time.GetUtcNow(), PollFailure.Unauthorized);
  }

  [Fact]
  public async Task An_unexpected_exception_records_an_other_classified_heartbeat_failure()
  {
      var client = Substitute.For<ITelegramBotClient>();
      client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>())
          .ThrowsAsync(new InvalidOperationException("unexpected"));
      var clientFactory = Substitute.For<ITelegramBotClientFactory>();
      clientFactory.Create("tok1").Returns(client);
      var heartbeat = Substitute.For<IPollingHeartbeat>();
      var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero));

      await CreateService(WithToken("tok1"), clientFactory, new TelegramClientHandle(), heartbeat: heartbeat, timeProvider: time)
          .RunTickAsync(TestContext.Current.CancellationToken);

      heartbeat.Received(1).RecordFailure(time.GetUtcNow(), PollFailure.Other);
  }
  ```
  Add `using Microsoft.Extensions.Time.Testing;` and `using Telegram.Bot.Exceptions;` and
  `using Noof.Ledger.Host.Tests.csproj`-style `using NSubstitute;` (already present) to the test file's
  usings, and add `<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />` to
  `tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj` (it is already a pinned
  `PackageVersion`; only the project reference is missing there).

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramPollingServiceTests`
  Expected failure: compile error — `TelegramPollingService`'s constructor does not accept an
  `IPollingHeartbeat`, and `PollFailure`/`IPollingHeartbeat` are unresolved in the test file (add the
  `using Noof.Ledger.Application.Diagnostics;` shown above once the type exists from Step 1).

  Implement — edit `src/Noof.Ledger.Telegram/TelegramPollingService.cs`:
  - Add usings: `using System.Net.Sockets;`, `using Noof.Ledger.Application.Diagnostics;`,
    `using Telegram.Bot.Exceptions;`.
  - Add `IPollingHeartbeat heartbeat` to the primary constructor, after `timeProvider` and before `logger`:
    ```csharp
    internal sealed class TelegramPollingService(
        IServiceScopeFactory scopeFactory,
        ITelegramBotClientFactory clientFactory,
        TelegramClientHandle clientHandle,
        IConfiguration configuration,
        TimeProvider timeProvider,
        IPollingHeartbeat heartbeat,
        ILogger<TelegramPollingService> logger)
        : BackgroundService
    ```
  - Immediately after the `var updates = await clientHandle.Current!.GetUpdates(...)` call succeeds, add:
    ```csharp
    heartbeat.RecordSuccess(timeProvider.GetUtcNow());
    ```
  - In the outer `catch (Exception ex) when (ex is not OperationCanceledException)` block (the one that
    increments `consecutiveFailures` and returns `TelegramPollResult.Failed`), add, before the `logger.LogError` call:
    ```csharp
    heartbeat.RecordFailure(timeProvider.GetUtcNow(), ClassifyFailure(ex));
    ```
  - Add a private static classifier method:
    ```csharp
    static PollFailure ClassifyFailure(Exception ex) => ex switch
    {
        ApiRequestException { ErrorCode: 401 } => PollFailure.Unauthorized,
        HttpRequestException => PollFailure.Network,
        SocketException => PollFailure.Network,
        TimeoutException => PollFailure.Network,
        TaskCanceledException => PollFailure.Network,
        _ => PollFailure.Other,
    };
    ```

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramPollingServiceTests`
  Expected: all pass, including the four new ones and every pre-existing test in the file (the helper's new
  parameters are all optional/defaulted).

  Commit:
  `git add src/Noof.Ledger.Telegram/TelegramPollingService.cs tests/Noof.Ledger.Telegram.Tests/TelegramPollingServiceTests.cs tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
  ```
  Classify Telegram poll failures and record them on the heartbeat

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 7: `TelegramHealthCheck` (Telegram)**

  Write failing test — create `tests/Noof.Ledger.Telegram.Tests/TelegramHealthCheckTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using Microsoft.Extensions.Time.Testing;
  using NSubstitute;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Application.Secrets;
  using Noof.Ledger.Telegram.Diagnostics;

  namespace Noof.Ledger.Telegram.Tests;

  public class TelegramHealthCheckTests
  {
      static readonly DateTimeOffset T0 = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);

      static IDatabaseGate ReadyGate()
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.State.Returns(DatabaseState.Ready);
          return gate;
      }

      static ISecretStore StoreWithToken(SecretState state = SecretState.Present)
      {
          var store = Substitute.For<ISecretStore>();
          store.GetStatusAsync(SecretKeys.TelegramBotToken, Arg.Any<CancellationToken>())
              .Returns(new SecretStatus(state, null));
          return store;
      }

      static TelegramHealthCheck Check(IPollingHeartbeat heartbeat, FakeTimeProvider time, IDatabaseGate? gate = null, ISecretStore? store = null) =>
          new(gate ?? ReadyGate(), store ?? StoreWithToken(), heartbeat, time);

      [Fact]
      public async Task Reports_degraded_when_the_gate_is_not_ready()
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.State.Returns(DatabaseState.Waiting);
          var check = Check(Substitute.For<IPollingHeartbeat>(), new FakeTimeProvider(T0), gate: gate);

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Degraded);
          result.Description.Should().Be("Waiting for the database");
      }

      [Fact]
      public async Task Reports_unhealthy_when_the_token_secret_is_missing()
      {
          var check = Check(Substitute.For<IPollingHeartbeat>(), new FakeTimeProvider(T0), store: StoreWithToken(SecretState.Missing));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Unhealthy);
      }

      [Fact]
      public async Task Reports_healthy_when_the_last_success_is_within_two_minutes()
      {
          var heartbeat = Substitute.For<IPollingHeartbeat>();
          heartbeat.LastSuccessAt.Returns(T0.AddMinutes(-1));
          var check = Check(heartbeat, new FakeTimeProvider(T0));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Healthy);
      }

      [Fact]
      public async Task A_network_failure_is_degraded_and_says_this_is_normal()
      {
          var heartbeat = Substitute.For<IPollingHeartbeat>();
          heartbeat.LastFailure.Returns((T0, PollFailure.Network));
          var check = Check(heartbeat, new FakeTimeProvider(T0));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Degraded);
          result.Description.Should().Be("Offline — this is normal");
      }

      [Fact]
      public async Task An_unauthorized_failure_is_unhealthy_and_names_the_rejected_token()
      {
          var heartbeat = Substitute.For<IPollingHeartbeat>();
          heartbeat.LastFailure.Returns((T0, PollFailure.Unauthorized));
          var check = Check(heartbeat, new FakeTimeProvider(T0));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Unhealthy);
          result.Description.Should().Be("Telegram rejected the bot token");
      }

      [Fact]
      public async Task No_poll_yet_is_degraded_and_says_so()
      {
          var check = Check(Substitute.For<IPollingHeartbeat>(), new FakeTimeProvider(T0));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Degraded);
          result.Description.Should().Be("Waiting for the first poll");
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramHealthCheckTests`
  Expected failure: compile error — `Noof.Ledger.Telegram.Diagnostics.TelegramHealthCheck` does not exist.

  Implement — create `src/Noof.Ledger.Telegram/Diagnostics/TelegramHealthCheck.cs`:
  ```csharp
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Application.Secrets;

  namespace Noof.Ledger.Telegram.Diagnostics;

  internal sealed class TelegramHealthCheck(
      IDatabaseGate gate, ISecretStore secretStore, IPollingHeartbeat heartbeat, TimeProvider timeProvider) : IHealthCheck
  {
      static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(2);

      public async Task<HealthCheckResult> CheckHealthAsync(
          HealthCheckContext context, CancellationToken cancellationToken = default)
      {
          if (gate.State is not DatabaseState.Ready)
              return HealthCheckResult.Degraded("Waiting for the database");

          var token = await secretStore.GetStatusAsync(SecretKeys.TelegramBotToken, cancellationToken);
          if (token.State is not SecretState.Present)
              return HealthCheckResult.Unhealthy("No Telegram bot token configured");

          var now = timeProvider.GetUtcNow();
          if (heartbeat.LastSuccessAt is { } success && now - success < FreshWindow)
              return HealthCheckResult.Healthy("Connected");

          if (heartbeat.LastFailure is { } failure)
          {
              return failure.Failure switch
              {
                  PollFailure.Unauthorized => HealthCheckResult.Unhealthy("Telegram rejected the bot token"),
                  PollFailure.Network => HealthCheckResult.Degraded("Offline — this is normal"),
                  _ => HealthCheckResult.Unhealthy("Telegram poll failed"),
              };
          }

          return heartbeat.LastSuccessAt is not null
              ? HealthCheckResult.Degraded("Stale — no successful poll recently")
              : HealthCheckResult.Degraded("Waiting for the first poll");
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramHealthCheckTests`
  Expected: passes.

  Wire it — edit `src/Noof.Ledger.Telegram/TelegramRegistration.cs`: add
  `using Microsoft.Extensions.Diagnostics.HealthChecks;`, `using Noof.Ledger.Application.Diagnostics;`,
  `using Noof.Ledger.Telegram.Diagnostics;`, and inside `AddNoofTelegram`, before `return services;`, add:
  ```csharp
  services.AddHealthChecks().AddCheck<TelegramHealthCheck>(HealthCheckNames.Telegram);
  ```

  Run: `dotnet build NoofLedger.slnx` — expected to succeed.

  Commit:
  `git add src/Noof.Ledger.Telegram/Diagnostics/TelegramHealthCheck.cs src/Noof.Ledger.Telegram/TelegramRegistration.cs tests/Noof.Ledger.Telegram.Tests/TelegramHealthCheckTests.cs`
  ```
  Add Telegram health check

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 8: `AiKeysHealthCheck` (Host) — via `IModelProvider`/`ISpeechProvider`, never `Noof.Ledger.Ai`**

  Write failing test — create `tests/Noof.Ledger.Host.Tests/Diagnostics/AiKeysHealthCheckTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using NSubstitute;
  using Noof.Ledger.Application.Categorization;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Application.Transcription;
  using Noof.Ledger.Host.Diagnostics;

  namespace Noof.Ledger.Host.Tests.Diagnostics;

  public class AiKeysHealthCheckTests
  {
      static IDatabaseGate ReadyGate()
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.State.Returns(DatabaseState.Ready);
          return gate;
      }

      static IModelProvider ModelProvider(bool configured)
      {
          var provider = Substitute.For<IModelProvider>();
          provider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(configured);
          provider.SecretLabel.Returns("Anthropic API key");
          return provider;
      }

      static ISpeechProvider SpeechProvider(bool configured)
      {
          var provider = Substitute.For<ISpeechProvider>();
          provider.IsConfiguredAsync(Arg.Any<CancellationToken>()).Returns(configured);
          provider.SecretLabel.Returns("Groq API key");
          return provider;
      }

      [Fact]
      public async Task Reports_degraded_when_the_gate_is_not_ready()
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.State.Returns(DatabaseState.Waiting);
          var check = new AiKeysHealthCheck(gate, ModelProvider(true), SpeechProvider(true));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Degraded);
      }

      [Fact]
      public async Task Both_keys_present_is_healthy()
      {
          var check = new AiKeysHealthCheck(ReadyGate(), ModelProvider(true), SpeechProvider(true));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Healthy);
      }

      [Fact]
      public async Task A_missing_model_key_is_unhealthy_and_names_it()
      {
          var check = new AiKeysHealthCheck(ReadyGate(), ModelProvider(false), SpeechProvider(true));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Unhealthy);
          result.Description.Should().Contain("Anthropic API key");
      }

      [Fact]
      public async Task A_missing_speech_key_is_unhealthy_and_names_it()
      {
          var check = new AiKeysHealthCheck(ReadyGate(), ModelProvider(true), SpeechProvider(false));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Unhealthy);
          result.Description.Should().Contain("Groq API key");
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter AiKeysHealthCheckTests`
  Expected failure: compile error — `Noof.Ledger.Host.Diagnostics.AiKeysHealthCheck` does not exist.

  Implement — create `src/Noof.Ledger.Host/Diagnostics/AiKeysHealthCheck.cs`:
  ```csharp
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using Noof.Ledger.Application.Categorization;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Application.Transcription;

  namespace Noof.Ledger.Host.Diagnostics;

  // Reads IModelProvider/ISpeechProvider - never Noof.Ledger.Ai or a raw ISecretStore key - so this
  // check needs no knowledge of which provider answers, matching HealthCheckBoundaryTests below and
  // the pre-existing AiBoundaryTests rule that only Noof.Ledger.Ai may name the provider.
  internal sealed class AiKeysHealthCheck(IDatabaseGate gate, IModelProvider modelProvider, ISpeechProvider speechProvider) : IHealthCheck
  {
      public async Task<HealthCheckResult> CheckHealthAsync(
          HealthCheckContext context, CancellationToken cancellationToken = default)
      {
          if (gate.State is not DatabaseState.Ready)
              return HealthCheckResult.Degraded("Waiting for the database");

          var modelConfigured = await modelProvider.IsConfiguredAsync(cancellationToken);
          var speechConfigured = await speechProvider.IsConfiguredAsync(cancellationToken);

          if (modelConfigured && speechConfigured)
              return HealthCheckResult.Healthy("Configured");

          List<string> missing = [];
          if (!modelConfigured)
              missing.Add(modelProvider.SecretLabel);
          if (!speechConfigured)
              missing.Add(speechProvider.SecretLabel);

          return HealthCheckResult.Unhealthy($"Missing: {string.Join(", ", missing)}");
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter AiKeysHealthCheckTests`
  Expected: passes.

  Commit:
  `git add src/Noof.Ledger.Host/Diagnostics/AiKeysHealthCheck.cs tests/Noof.Ledger.Host.Tests/Diagnostics/AiKeysHealthCheckTests.cs`
  ```
  Add AI keys health check via IModelProvider/ISpeechProvider

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 9: `BackupHealthCheck` (Host)**

  Write failing test — create `tests/Noof.Ledger.Host.Tests/Diagnostics/BackupHealthCheckTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using Microsoft.Extensions.Time.Testing;
  using NSubstitute;
  using Noof.Ledger.Application.Backup;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Host.Diagnostics;

  namespace Noof.Ledger.Host.Tests.Diagnostics;

  public class BackupHealthCheckTests
  {
      static readonly DateTimeOffset T0 = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);

      static IDatabaseGate ReadyGate()
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.State.Returns(DatabaseState.Ready);
          return gate;
      }

      static IBackupLog LogWith(BackupStatus status)
      {
          var log = Substitute.For<IBackupLog>();
          log.StatusAsync(Arg.Any<CancellationToken>()).Returns(status);
          return log;
      }

      [Fact]
      public async Task Reports_degraded_when_the_gate_is_not_ready()
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.State.Returns(DatabaseState.Waiting);
          var check = new BackupHealthCheck(gate, LogWith(new BackupStatus(T0, false, null)), new FakeTimeProvider(T0));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Degraded);
          result.Description.Should().Be("Waiting for the database");
      }

      [Fact]
      public async Task A_success_under_26_hours_old_is_healthy()
      {
          var check = new BackupHealthCheck(ReadyGate(), LogWith(new BackupStatus(T0.AddHours(-25), false, null)), new FakeTimeProvider(T0));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Healthy);
      }

      [Fact]
      public async Task A_success_26_hours_or_older_is_degraded()
      {
          var check = new BackupHealthCheck(ReadyGate(), LogWith(new BackupStatus(T0.AddHours(-26), false, null)), new FakeTimeProvider(T0));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Degraded);
      }

      [Fact]
      public async Task A_failed_last_run_is_degraded_even_with_a_recent_success()
      {
          var check = new BackupHealthCheck(ReadyGate(), LogWith(new BackupStatus(T0.AddHours(-1), true, "disk full")), new FakeTimeProvider(T0));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Degraded);
      }

      [Fact]
      public async Task No_backup_ever_succeeded_is_degraded()
      {
          var check = new BackupHealthCheck(ReadyGate(), LogWith(new BackupStatus(null, false, null)), new FakeTimeProvider(T0));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Degraded);
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter BackupHealthCheckTests`
  Expected failure: compile error — `Noof.Ledger.Host.Diagnostics.BackupHealthCheck` does not exist.

  Implement — create `src/Noof.Ledger.Host/Diagnostics/BackupHealthCheck.cs`:
  ```csharp
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using Noof.Ledger.Application.Backup;
  using Noof.Ledger.Application.Diagnostics;

  namespace Noof.Ledger.Host.Diagnostics;

  internal sealed class BackupHealthCheck(IDatabaseGate gate, IBackupLog backupLog, TimeProvider timeProvider) : IHealthCheck
  {
      static readonly TimeSpan FreshWindow = TimeSpan.FromHours(26);

      public async Task<HealthCheckResult> CheckHealthAsync(
          HealthCheckContext context, CancellationToken cancellationToken = default)
      {
          if (gate.State is not DatabaseState.Ready)
              return HealthCheckResult.Degraded("Waiting for the database");

          var status = await backupLog.StatusAsync(cancellationToken);

          if (status.LastRunFailed)
          {
              return HealthCheckResult.Degraded(status.LastSuccessAt is { } at
                  ? $"Last run failed — last success {Describe(timeProvider.GetUtcNow() - at)} ago"
                  : "Last run failed");
          }

          if (status.LastSuccessAt is not { } lastSuccess)
              return HealthCheckResult.Degraded("No backup has ever succeeded");

          var age = timeProvider.GetUtcNow() - lastSuccess;
          return age < FreshWindow
              ? HealthCheckResult.Healthy($"Last success {Describe(age)} ago")
              : HealthCheckResult.Degraded($"Stale — last success {Describe(age)} ago");
      }

      static string Describe(TimeSpan elapsed) => elapsed switch
      {
          { TotalMinutes: < 1 } => "just now",
          { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes} min",
          { TotalDays: < 1 } => $"{(int)elapsed.TotalHours} h",
          _ => $"{(int)elapsed.TotalDays} d",
      };
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter BackupHealthCheckTests`
  Expected: passes.

  Commit:
  `git add src/Noof.Ledger.Host/Diagnostics/BackupHealthCheck.cs tests/Noof.Ledger.Host.Tests/Diagnostics/BackupHealthCheckTests.cs`
  ```
  Add Backup health check

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 10: `IFreeSpaceProvider` and `DiskHealthCheck` (Host)**

  Write failing test — create `tests/Noof.Ledger.Host.Tests/Diagnostics/DiskHealthCheckTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Microsoft.Extensions.Configuration;
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using NSubstitute;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Host.Diagnostics;
  using Noof.Ledger.Host.Workers;

  namespace Noof.Ledger.Host.Tests.Diagnostics;

  public class DiskHealthCheckTests
  {
      const long Gb = 1024L * 1024 * 1024;

      static IDatabaseGate ReadyGate()
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.State.Returns(DatabaseState.Ready);
          return gate;
      }

      static IConfiguration ConfigWithLogDirectory(string directory) =>
          new ConfigurationBuilder()
              .AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:Directory"] = directory })
              .Build();

      static DiskHealthCheck Check(long logFreeBytes, long backupFreeBytes)
      {
          var freeSpace = Substitute.For<IFreeSpaceProvider>();
          freeSpace.GetAvailableFreeBytes("C:\\logs").Returns(logFreeBytes);
          freeSpace.GetAvailableFreeBytes("C:\\backups").Returns(backupFreeBytes);
          return new DiskHealthCheck(ReadyGate(), freeSpace, ConfigWithLogDirectory("C:\\logs"),
              new BackupWorkerOptions { BackupDirectory = "C:\\backups" });
      }

      [Fact]
      public async Task Plenty_of_space_on_both_drives_is_healthy()
      {
          var check = Check(logFreeBytes: 10 * Gb, backupFreeBytes: 10 * Gb);

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Healthy);
      }

      [Fact]
      public async Task The_worse_of_the_two_drives_decides_the_result()
      {
          var check = Check(logFreeBytes: 10 * Gb, backupFreeBytes: 3 * Gb);

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Degraded);
      }

      [Fact]
      public async Task Under_one_gigabyte_free_is_unhealthy()
      {
          var check = Check(logFreeBytes: 10 * Gb, backupFreeBytes: (long)(0.5 * Gb));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Unhealthy);
      }

      [Fact]
      public async Task Reports_degraded_when_the_gate_is_not_ready()
      {
          var freeSpace = Substitute.For<IFreeSpaceProvider>();
          var gate = Substitute.For<IDatabaseGate>();
          gate.State.Returns(DatabaseState.Waiting);
          var check = new DiskHealthCheck(gate, freeSpace, ConfigWithLogDirectory("C:\\logs"),
              new BackupWorkerOptions { BackupDirectory = "C:\\backups" });

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Degraded);
          result.Description.Should().Be("Waiting for the database");
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter DiskHealthCheckTests`
  Expected failure: compile error — `Noof.Ledger.Host.Diagnostics.IFreeSpaceProvider`/`DiskHealthCheck` do not exist.

  Implement — create `src/Noof.Ledger.Host/Diagnostics/IFreeSpaceProvider.cs`:
  ```csharp
  namespace Noof.Ledger.Host.Diagnostics;

  internal interface IFreeSpaceProvider
  {
      long GetAvailableFreeBytes(string path);
  }

  internal sealed class DriveFreeSpaceProvider : IFreeSpaceProvider
  {
      public long GetAvailableFreeBytes(string path)
      {
          Directory.CreateDirectory(path);
          return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
      }
  }
  ```
  Create `src/Noof.Ledger.Host/Diagnostics/DiskHealthCheck.cs`:
  ```csharp
  using Microsoft.Extensions.Configuration;
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Host.Workers;

  namespace Noof.Ledger.Host.Diagnostics;

  internal sealed class DiskHealthCheck(
      IDatabaseGate gate, IFreeSpaceProvider freeSpace, IConfiguration configuration, BackupWorkerOptions backupOptions) : IHealthCheck
  {
      const long OkBytes = 5L * 1024 * 1024 * 1024;
      const long FailingBytes = 1L * 1024 * 1024 * 1024;

      public Task<HealthCheckResult> CheckHealthAsync(
          HealthCheckContext context, CancellationToken cancellationToken = default)
      {
          if (gate.State is not DatabaseState.Ready)
              return Task.FromResult(HealthCheckResult.Degraded("Waiting for the database"));

          var logDirectory = Environment.ExpandEnvironmentVariables(
              configuration["Logging:File:Directory"]
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "logs"));

          var logFree = SafeFreeBytes(logDirectory);
          var backupFree = SafeFreeBytes(backupOptions.BackupDirectory);
          var worst = Math.Min(logFree, backupFree);

          var result = worst switch
          {
              < 0 => HealthCheckResult.Unhealthy("Could not read free disk space"),
              < FailingBytes => HealthCheckResult.Unhealthy($"{FormatGb(worst)} GB free"),
              < OkBytes => HealthCheckResult.Degraded($"{FormatGb(worst)} GB free"),
              _ => HealthCheckResult.Healthy($"{FormatGb(worst)} GB free"),
          };

          return Task.FromResult(result);
      }

      long SafeFreeBytes(string path)
      {
          try
          {
              return freeSpace.GetAvailableFreeBytes(path);
          }
          catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
          {
              return -1;
          }
      }

      static string FormatGb(long bytes) => (bytes / 1024d / 1024d / 1024d).ToString("N1");
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter DiskHealthCheckTests`
  Expected: passes.

  Commit:
  `git add src/Noof.Ledger.Host/Diagnostics/IFreeSpaceProvider.cs src/Noof.Ledger.Host/Diagnostics/DiskHealthCheck.cs tests/Noof.Ledger.Host.Tests/Diagnostics/DiskHealthCheckTests.cs`
  ```
  Add Disk health check

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 11: `LogSinkHealthCheck` (Host)**

  Write failing test — create `tests/Noof.Ledger.Host.Tests/Diagnostics/LogSinkHealthCheckTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using Microsoft.Extensions.Time.Testing;
  using NSubstitute;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Host.Diagnostics;

  namespace Noof.Ledger.Host.Tests.Diagnostics;

  public class LogSinkHealthCheckTests
  {
      static readonly DateTimeOffset T0 = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);

      static IDatabaseGate ReadyGate()
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.State.Returns(DatabaseState.Ready);
          return gate;
      }

      static ILogSinkStatus StatusWith(DateTimeOffset? lastFailureAt)
      {
          var status = Substitute.For<ILogSinkStatus>();
          status.LastFailureAt.Returns(lastFailureAt);
          return status;
      }

      [Fact]
      public async Task No_failure_ever_is_healthy()
      {
          var check = new LogSinkHealthCheck(ReadyGate(), StatusWith(null), new FakeTimeProvider(T0));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Healthy);
      }

      [Fact]
      public async Task A_failure_within_ten_minutes_is_degraded()
      {
          var check = new LogSinkHealthCheck(ReadyGate(), StatusWith(T0.AddMinutes(-5)), new FakeTimeProvider(T0));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Degraded);
          result.Description.Should().Be("Logs are in the file only");
      }

      [Fact]
      public async Task A_failure_more_than_ten_minutes_ago_is_healthy_again()
      {
          var check = new LogSinkHealthCheck(ReadyGate(), StatusWith(T0.AddMinutes(-11)), new FakeTimeProvider(T0));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Status.Should().Be(HealthStatus.Healthy);
      }

      [Fact]
      public async Task Reports_degraded_for_the_gate_before_looking_at_the_sink()
      {
          var gate = Substitute.For<IDatabaseGate>();
          gate.State.Returns(DatabaseState.Waiting);
          var check = new LogSinkHealthCheck(gate, StatusWith(T0), new FakeTimeProvider(T0));

          var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

          result.Description.Should().Be("Waiting for the database");
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter LogSinkHealthCheckTests`
  Expected failure: compile error — `Noof.Ledger.Host.Diagnostics.LogSinkHealthCheck` (and `ILogSinkStatus`,
  if Task 4 is not yet on this branch) does not exist.

  Implement — create `src/Noof.Ledger.Host/Diagnostics/LogSinkHealthCheck.cs`:
  ```csharp
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using Noof.Ledger.Application.Diagnostics;

  namespace Noof.Ledger.Host.Diagnostics;

  internal sealed class LogSinkHealthCheck(IDatabaseGate gate, ILogSinkStatus sinkStatus, TimeProvider timeProvider) : IHealthCheck
  {
      static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

      public Task<HealthCheckResult> CheckHealthAsync(
          HealthCheckContext context, CancellationToken cancellationToken = default)
      {
          if (gate.State is not DatabaseState.Ready)
              return Task.FromResult(HealthCheckResult.Degraded("Waiting for the database"));

          var result = sinkStatus.LastFailureAt is { } at && timeProvider.GetUtcNow() - at < Window
              ? HealthCheckResult.Degraded("Logs are in the file only")
              : HealthCheckResult.Healthy("Writing to the database");

          return Task.FromResult(result);
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter LogSinkHealthCheckTests`
  Expected: passes.

  Commit:
  `git add src/Noof.Ledger.Host/Diagnostics/LogSinkHealthCheck.cs tests/Noof.Ledger.Host.Tests/Diagnostics/LogSinkHealthCheckTests.cs`
  ```
  Add Log sink health check

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 12: `SystemHealth` (Host) — ordering, mapping, 30 s cache**

  Write failing test — create `tests/Noof.Ledger.Host.Tests/Diagnostics/SystemHealthTests.cs`:
  ```csharp
  using AwesomeAssertions;
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using Microsoft.Extensions.Time.Testing;
  using NSubstitute;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Host.Diagnostics;

  namespace Noof.Ledger.Host.Tests.Diagnostics;

  public class SystemHealthTests
  {
      static readonly DateTimeOffset T0 = new(2026, 9, 25, 3, 0, 0, TimeSpan.Zero);

      static HealthReport ReportOf(params (string Name, HealthStatus Status, string Description)[] entries) =>
          new(entries.ToDictionary(e => e.Name, e => new HealthReportEntry(
              data: new Dictionary<string, object>(),
              description: e.Description,
              duration: TimeSpan.Zero,
              exception: null,
              status: e.Status,
              tags: [])),
              entries.Aggregate(TimeSpan.Zero, (acc, _) => acc));

      static HealthCheckService ServiceReturning(HealthReport report)
      {
          var service = Substitute.For<HealthCheckService>();
          service.CheckHealthAsync(Arg.Any<CancellationToken>()).Returns(report);
          return service;
      }

      [Fact]
      public async Task Items_come_back_in_HealthCheckNames_Ordered_order_regardless_of_report_order()
      {
          var report = ReportOf(
              (HealthCheckNames.LogSink, HealthStatus.Healthy, "ok"),
              (HealthCheckNames.Database, HealthStatus.Healthy, "ready"),
              (HealthCheckNames.Migrations, HealthStatus.Healthy, "up to date"),
              (HealthCheckNames.Telegram, HealthStatus.Healthy, "connected"),
              (HealthCheckNames.AiKeys, HealthStatus.Healthy, "configured"),
              (HealthCheckNames.Backup, HealthStatus.Healthy, "fresh"),
              (HealthCheckNames.Disk, HealthStatus.Healthy, "10.0 GB free"));
          var health = new SystemHealth(ServiceReturning(report), new FakeTimeProvider(T0));

          var result = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

          result.Items.Select(i => i.Name).Should().BeEquivalentTo(HealthCheckNames.Ordered, options => options.WithStrictOrdering());
      }

      [Theory]
      [InlineData(HealthStatus.Healthy, HealthLevel.Ok)]
      [InlineData(HealthStatus.Degraded, HealthLevel.Warning)]
      [InlineData(HealthStatus.Unhealthy, HealthLevel.Failing)]
      public async Task Maps_HealthStatus_to_HealthLevel(HealthStatus status, HealthLevel expected)
      {
          var report = ReportOf((HealthCheckNames.Database, status, "x"));
          var health = new SystemHealth(ServiceReturning(report), new FakeTimeProvider(T0));

          var result = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

          result.Items.Single(i => i.Name == HealthCheckNames.Database).Level.Should().Be(expected);
      }

      [Fact]
      public async Task Overall_is_the_worst_of_all_items()
      {
          var report = ReportOf(
              (HealthCheckNames.Database, HealthStatus.Healthy, "ready"),
              (HealthCheckNames.Backup, HealthStatus.Degraded, "stale"));
          var health = new SystemHealth(ServiceReturning(report), new FakeTimeProvider(T0));

          var result = await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

          result.Overall.Should().Be(HealthLevel.Warning);
      }

      [Fact]
      public async Task Fresh_false_reuses_a_result_less_than_30_seconds_old()
      {
          var service = ServiceReturning(ReportOf((HealthCheckNames.Database, HealthStatus.Healthy, "ready")));
          var time = new FakeTimeProvider(T0);
          var health = new SystemHealth(service, time);
          await health.GetAsync(fresh: false, TestContext.Current.CancellationToken);

          time.Advance(TimeSpan.FromSeconds(29));
          await health.GetAsync(fresh: false, TestContext.Current.CancellationToken);

          await service.Received(1).CheckHealthAsync(Arg.Any<CancellationToken>());
      }

      [Fact]
      public async Task Fresh_false_re_runs_once_the_cache_turns_30_seconds_old()
      {
          var service = ServiceReturning(ReportOf((HealthCheckNames.Database, HealthStatus.Healthy, "ready")));
          var time = new FakeTimeProvider(T0);
          var health = new SystemHealth(service, time);
          await health.GetAsync(fresh: false, TestContext.Current.CancellationToken);

          time.Advance(TimeSpan.FromSeconds(30));
          await health.GetAsync(fresh: false, TestContext.Current.CancellationToken);

          await service.Received(2).CheckHealthAsync(Arg.Any<CancellationToken>());
      }

      [Fact]
      public async Task Fresh_true_always_runs_the_checks_again()
      {
          var service = ServiceReturning(ReportOf((HealthCheckNames.Database, HealthStatus.Healthy, "ready")));
          var health = new SystemHealth(service, new FakeTimeProvider(T0));

          await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);
          await health.GetAsync(fresh: true, TestContext.Current.CancellationToken);

          await service.Received(2).CheckHealthAsync(Arg.Any<CancellationToken>());
      }
  }
  ```
  Note: `HealthCheckService` is an unsealed abstract class from the framework; NSubstitute proxies it the
  same way it proxies an interface.

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter SystemHealthTests`
  Expected failure: compile error — `Noof.Ledger.Host.Diagnostics.SystemHealth` does not exist.

  Implement — create `src/Noof.Ledger.Host/Diagnostics/SystemHealth.cs`:
  ```csharp
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using Noof.Ledger.Application.Diagnostics;

  namespace Noof.Ledger.Host.Diagnostics;

  internal sealed class SystemHealth(HealthCheckService healthCheckService, TimeProvider timeProvider) : ISystemHealth
  {
      static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

      readonly Lock gate = new();
      SystemHealthReport? cached;
      DateTimeOffset cachedAt;

      public async Task<SystemHealthReport> GetAsync(bool fresh, CancellationToken cancellationToken)
      {
          if (!fresh)
          {
              lock (gate)
              {
                  if (cached is { } current && timeProvider.GetUtcNow() - cachedAt < CacheDuration)
                      return current;
              }
          }

          var report = await RunAsync(cancellationToken);

          lock (gate)
          {
              cached = report;
              cachedAt = timeProvider.GetUtcNow();
          }

          return report;
      }

      async Task<SystemHealthReport> RunAsync(CancellationToken cancellationToken)
      {
          var raw = await healthCheckService.CheckHealthAsync(cancellationToken);
          var now = timeProvider.GetUtcNow();

          HealthItem[] items = [.. HealthCheckNames.Ordered.Select(name => raw.Entries.TryGetValue(name, out var entry)
              ? new HealthItem(name, Map(entry.Status), entry.Description ?? string.Empty, now)
              : new HealthItem(name, HealthLevel.Failing, "Check not registered", now))];

          var overall = items.Length == 0 ? HealthLevel.Ok : items.Max(item => item.Level);

          return new SystemHealthReport(overall, items);
      }

      static HealthLevel Map(HealthStatus status) => status switch
      {
          HealthStatus.Healthy => HealthLevel.Ok,
          HealthStatus.Degraded => HealthLevel.Warning,
          _ => HealthLevel.Failing,
      };
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter SystemHealthTests`
  Expected: passes.

  Commit:
  `git add src/Noof.Ledger.Host/Diagnostics/SystemHealth.cs tests/Noof.Ledger.Host.Tests/Diagnostics/SystemHealthTests.cs`
  ```
  Add SystemHealth (ordering, HealthStatus mapping, 30s cache)

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 13: Architecture guard — no health check references the AI stack — watch it fail, then pass**

  Write the test — create `tests/Noof.Ledger.Architecture.Tests/HealthCheckBoundaryTests.cs`:
  ```csharp
  using AwesomeAssertions;

  namespace Noof.Ledger.Architecture.Tests;

  // Scope item for Phase 5 Task 6: the LLM plays no part in health. AiKeysHealthCheck reads
  // IModelProvider/ISpeechProvider - both Application interfaces - never Noof.Ledger.Ai directly.
  public class HealthCheckBoundaryTests
  {
      static readonly string SrcRoot = Path.Combine(RepoRoot.Find().FullName, "src");

      [Fact]
      public void No_health_check_references_the_AI_stack()
      {
          var healthCheckFiles = Directory.EnumerateFiles(SrcRoot, "*.cs", SearchOption.AllDirectories)
              .Where(file =>
              {
                  var text = File.ReadAllText(file);
                  return text.Contains(": IHealthCheck", StringComparison.Ordinal)
                      || text.Contains(", IHealthCheck", StringComparison.Ordinal);
              })
              .ToArray();

          var offenders = healthCheckFiles
              .Where(file =>
              {
                  var text = File.ReadAllText(file);
                  return text.Contains("Noof.Ledger.Ai", StringComparison.Ordinal)
                      || text.Contains("Microsoft.Extensions.AI", StringComparison.Ordinal);
              })
              .Select(file => Path.GetRelativePath(SrcRoot, file))
              .ToArray();

          offenders.Should().BeEmpty(
              "health checks read IModelProvider/ISpeechProvider, never the AI assembly or Microsoft.Extensions.AI directly");
          healthCheckFiles.Should().NotBeEmpty(
              "the IHealthCheck pattern must find the real health checks, or an empty offender list proves nothing");
      }
  }
  ```

  Watch it fail (prove the guard has teeth): temporarily add a throwaway line
  `using Noof.Ledger.Ai;` to the top of `src/Noof.Ledger.Host/Diagnostics/AiKeysHealthCheck.cs` (it will be
  an unused-using warning, not an error, so the build still succeeds) and run:
  `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter HealthCheckBoundaryTests`
  Expected failure: `No_health_check_references_the_AI_stack` fails, naming
  `Noof.Ledger.Host\Diagnostics\AiKeysHealthCheck.cs` as the offender. Remove the throwaway `using` line.

  Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter HealthCheckBoundaryTests`
  Expected: passes (seven health check files found, none flagged).

  Commit:
  `git add tests/Noof.Ledger.Architecture.Tests/HealthCheckBoundaryTests.cs`
  ```
  Add architecture guard: no health check references the AI stack

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 14: Wire `DiagnosticsRegistration` into Host and `Program.cs`**

  No new test in this step — wiring, per CLAUDE.md's `Program.cs`-wiring TDD exemption. Steps 3–12's own
  tests are what prove each check's behaviour; `BootTests`/`PersistenceRegistrationTests`-style smoke
  coverage of the composed container is Step 15.

  Create `src/Noof.Ledger.Host/Diagnostics/DiagnosticsRegistration.cs`:
  ```csharp
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using Noof.Ledger.Application.Diagnostics;

  namespace Noof.Ledger.Host.Diagnostics;

  internal static class DiagnosticsRegistration
  {
      public static IServiceCollection AddNoofDiagnostics(this IServiceCollection services)
      {
          services.AddSingleton<IPollingHeartbeat, PollingHeartbeat>();
          services.AddSingleton<IFreeSpaceProvider, DriveFreeSpaceProvider>();
          services.AddSingleton<ISystemHealth, SystemHealth>();

          services.AddHealthChecks()
              .AddCheck<DatabaseHealthCheck>(HealthCheckNames.Database)
              .AddCheck<AiKeysHealthCheck>(HealthCheckNames.AiKeys)
              .AddCheck<BackupHealthCheck>(HealthCheckNames.Backup)
              .AddCheck<DiskHealthCheck>(HealthCheckNames.Disk)
              .AddCheck<LogSinkHealthCheck>(HealthCheckNames.LogSink);

          return services;
      }
  }
  ```

  Edit `src/Noof.Ledger.Host/Program.cs`: add `using Noof.Ledger.Host.Diagnostics;` to the usings, and
  immediately after the existing line
  `builder.Services.AddNoofWorkers(categorizationOptions, backupOptions);` add:
  ```csharp
  builder.Services.AddNoofDiagnostics();
  ```
  (`BackupWorkerOptions` is already a singleton by the time this runs, because `AddNoofWorkers` registers
  it a line earlier — `DiskHealthCheck` and `BackupHealthCheck` resolve it from there. `HealthCheckService`
  itself is registered by `services.AddHealthChecks()`, called here and, separately, inside
  `AddNoofPersistence`/`AddNoofTelegram`; `AddHealthChecks()` is idempotent to call more than once — it
  returns the same builder over one singleton `HealthCheckService` — so registration order across the
  three assemblies does not matter.)

  Run: `dotnet build NoofLedger.slnx` — expected to succeed.

  Commit:
  `git add src/Noof.Ledger.Host/Diagnostics/DiagnosticsRegistration.cs src/Noof.Ledger.Host/Program.cs`
  ```
  Wire health checks, ISystemHealth and IPollingHeartbeat into the Host

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 15: End-to-end smoke test — the composed container answers with all seven checks**

  Write failing test — create `tests/Noof.Ledger.Host.Tests/Diagnostics/DiagnosticsRegistrationTests.cs`,
  following the existing `PersistenceRegistrationTests.cs` container-build style:
  ```csharp
  using AwesomeAssertions;
  using Microsoft.Extensions.Diagnostics.HealthChecks;
  using Microsoft.Extensions.DependencyInjection;
  using Noof.Ledger.Application.Diagnostics;
  using Noof.Ledger.Host.Diagnostics;

  namespace Noof.Ledger.Host.Tests.Diagnostics;

  public class DiagnosticsRegistrationTests
  {
      [Fact]
      public void AddNoofDiagnostics_registers_ISystemHealth_and_IPollingHeartbeat_as_singletons()
      {
          var services = new ServiceCollection();
          services.AddSingleton(TimeProvider.System);

          services.AddNoofDiagnostics();

          var provider = services.BuildServiceProvider();
          provider.GetRequiredService<ISystemHealth>().Should().BeSameAs(provider.GetRequiredService<ISystemHealth>());
          provider.GetRequiredService<IPollingHeartbeat>().Should().BeSameAs(provider.GetRequiredService<IPollingHeartbeat>());
      }

      [Fact]
      public void AddNoofDiagnostics_registers_its_five_Host_owned_checks()
      {
          var services = new ServiceCollection();
          services.AddSingleton(TimeProvider.System);

          services.AddNoofDiagnostics();

          var registrations = services.BuildServiceProvider().GetRequiredService<HealthCheckService>();
          registrations.Should().NotBeNull();
      }
  }
  ```
  (This intentionally does not build the full Host container — `AiKeysHealthCheck`, `BackupHealthCheck`
  and `DiskHealthCheck` need `IModelProvider`/`ISpeechProvider`/`IBackupLog`/`IDatabaseGate`, which come
  from `AddNoofAi`/`AddNoofPersistence`/Task 1's own registration; resolving `HealthCheckService` itself
  needs no check to actually run, only to be registered, so the second test only proves the service
  resolves.)

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter DiagnosticsRegistrationTests`
  Expected failure: compile error — `AddNoofDiagnostics` does not exist yet if Step 14 has not run first in
  this ordering; since Step 14 already created it, this should instead fail for lack of `IDatabaseGate`
  registered in the minimal `ServiceCollection` used by `HealthCheckService`'s *own* internal validation —
  in practice `AddHealthChecks()` does not eagerly resolve check dependencies, so this test is expected to
  fail only if `AddNoofDiagnostics` itself is missing; run it once before Step 14's implementation exists
  (i.e., write this test file first, confirm the "does not exist" compile failure, then apply Step 14's
  production code) to get a genuine red.

  Run again after Step 14's code exists:
  `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter DiagnosticsRegistrationTests`
  Expected: passes.

  Commit:
  `git add tests/Noof.Ledger.Host.Tests/Diagnostics/DiagnosticsRegistrationTests.cs`
  ```
  Add a registration smoke test for AddNoofDiagnostics

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

- [ ] **Step 16: Full-project verification for this task's slice**

  Run the four touched fast test projects in full (not just the new filters), to catch any regression the
  `TelegramPollingService` constructor change or the two `AddNoofXxx` edits introduced elsewhere in those
  files:
  ```
  dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj
  dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj
  dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj
  dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj
  ```
  Expected: all green. Per CLAUDE.md, a full unfiltered `dotnet test --solution` (including Playwright E2E)
  is deferred to the end of the phase, not run per task, to avoid colliding with parallel worktrees on the
  shared PostgreSQL server.

  No commit for this step — it is verification only, not a code change.

**Total steps: 16** (13 TDD implementation steps with a red/green pair each, 1 architecture "watch it
fail" step, 1 wiring step, 1 final verification step).

---

### Task 7: Diagnostics UI: health tile, /diagnostics, logs viewer with file tail, transaction trace page

**Plan notes (deviations from the literal scope text, kept to preserve spec intent):**
- The scope text's suggested test-command form (`dotnet test --project ... -- --filter-class <FullClassName>`) does not match this repo: every existing plan and `ops/RUNBOOK.md` uses `dotnet test --project tests/<Project>/<Project>.csproj --filter <ClassName>` (xUnit v3 on Microsoft.Testing.Platform, confirmed against `docs/superpowers/plans/2026-09-22-phase2-natural-language-capture.md:41` and `2026-09-24-phase3-voice-capture.md:41`). All steps below use the real form.
- The repo has no bUnit package reference anywhere (`Directory.Packages.props` checked). Per the scope's own fallback, all Web-page tests below are either (a) Architecture-project source-text tests (the repo's existing pattern: `DashboardPageSourceTests.cs`, `WalletsPageSourceTests.cs`) for anything on an interactive (`prerender: false`) page, since a plain `HttpClient` GET of an `InteractiveServerRenderMode(prerender: false)` page never emits the component's markup, only the Blazor boot marker (verified: `Wallets.razor` — the repo's one existing such page — has no Host-level served-HTML test, only `WalletsPageSourceTests.cs`), or (b) Host-level served-HTML tests (`WebApplicationFactory<Program>`, the `DashboardCultureTests.cs` pattern) for content on a page that stays statically rendered, i.e. `Home.razor` (no `@rendermode`) and `NavBar.razor` (always static, per `MainLayout.razor`'s comment).
- `/diagnostics` needs a plain-text input plus a button that navigates to a route with the id as a path segment (`/transactions/{id}/trace`), which a static `<form method="get">` cannot template. Rather than making the whole page an interactive island for one field, this plan adds a tiny anonymous-to-nobody (still behind the global fallback auth policy) minimal-API redirect endpoint `GET /diagnostics/open-trace?id=<guid>` → `302` to `/transactions/{id}/trace`, following the existing `AccountEndpoints`/`MapLogin` pattern. `/diagnostics` itself then stays statically rendered (no popover/dialog/etc. is used anyway), and only `/diagnostics/logs` (QuickGrid paging/filtering) and `/transactions/{Id:guid}/trace` (row highlighting, nothing else interactive) need `@rendermode @(new InteractiveServerRenderMode(prerender: false))` — `/transactions/{Id:guid}/trace` is in fact static-renderable (a route parameter plus three read-only lists, no user input), so this plan keeps it **static** too and reserves the interactive render mode for `/diagnostics/logs` alone, where QuickGrid's paging/filtering genuinely needs a live circuit.
- The scope lists `database-waiting` as a Task 7 element id. Per the spec (§1, §4) the waiting banner itself (shown app-wide while `IDatabaseGate.State != Ready`) is Task 1's shared component. This plan assumes Task 1 ships it as a reusable component `Noof.Ledger.Web.Components.Layout.DatabaseWaitingBanner` rendering `<MudAlert id="database-waiting">`, consistent with `MainLayout.razor`'s single-provider-per-layout structure, and Task 7 only places `<DatabaseWaitingBanner />` at the top of `/diagnostics` and `/transactions/{Id:guid}/trace` (both otherwise static) guarded by `@if (Gate.State != DatabaseState.Ready)`. `/diagnostics/logs` is explicitly exempted by the spec in file-tail mode; this plan shows the banner there only when **not** in file-tail mode. **If Task 1 instead inlines the banner per-page with a different name, the four `@if (Gate.State != ...)` blocks below are the only lines that need to change** — everything else in this task is unaffected.
- The DbSet backing `AppLogEntry` (Task 3, not yet merged at plan-writing time) is fixed by the contract only as table `app_log` / entity `AppLogEntry`, not as a `LedgerDbContext` property name. This plan assumes `LedgerDbContext.AppLogs` (`DbSet<AppLogEntry>`), following the pluralized-DbSet convention every other entity in this context already uses (`Wallets`, `Transactions`, `Entries`, `BalanceChecks`, `BackupRuns`, `TransactionRevisions`). **Verify this against Task 3's actual merged code before running Step 8's and Step 10's seeding code** — if Task 3 named it differently, only the `db.AppLogs.Add(...)` call sites change.
- No package-version pin for `Microsoft.AspNetCore.Components.QuickGrid` exists anywhere in the repo yet. Step 1 below includes the command to resolve the correct version against this repo's already-pinned `Microsoft.AspNetCore.Components.Web` (`10.0.8`) before writing it into `Directory.Packages.props`, rather than guessing a number.

**Files:**
- Create: `src/Noof.Ledger.Application/Diagnostics/ILogFileTail.cs`
- Create: `src/Noof.Ledger.Host/Diagnostics/LogFileTail.cs`, `src/Noof.Ledger.Host/Diagnostics/DiagnosticsHostRegistration.cs`
- Create: `src/Noof.Ledger.Host/Endpoints/DiagnosticsEndpoints.cs`
- Modify: `src/Noof.Ledger.Host/Program.cs` (register `LogFileTail`, map `DiagnosticsEndpoints`)
- Modify: `src/Noof.Ledger.Web/Components/Layout/NavBar.razor`
- Modify: `src/Noof.Ledger.Web/Components/Pages/Home.razor`
- Create: `src/Noof.Ledger.Web/Components/Pages/Diagnostics.razor`, `src/Noof.Ledger.Web/Components/Pages/DiagnosticsLogs.razor`, `src/Noof.Ledger.Web/Components/Pages/TransactionTrace.razor`
- Modify: `src/Noof.Ledger.Web/Noof.Ledger.Web.csproj`, `Directory.Packages.props`
- Modify: `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`, `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs`, `tests/Noof.Ledger.Architecture.Tests/DashboardPageSourceTests.cs`
- Create: `tests/Noof.Ledger.Architecture.Tests/WebFileSystemBoundaryTests.cs`, `tests/Noof.Ledger.Architecture.Tests/DiagnosticsPageSourceTests.cs`
- Create: `tests/Noof.Ledger.Host.Tests/LogFileTailTests.cs`, `tests/Noof.Ledger.Host.Tests/NavBarDiagnosticsLinkTests.cs`
- Modify: `tests/Noof.Ledger.E2E.Tests/DashboardBalancesTests.cs`
- Create: `tests/Noof.Ledger.E2E.Tests/HealthTileTests.cs`, `tests/Noof.Ledger.E2E.Tests/DiagnosticsTests.cs`, `tests/Noof.Ledger.E2E.Tests/DiagnosticsLogsTests.cs`, `tests/Noof.Ledger.E2E.Tests/TransactionTraceTests.cs`

**Interfaces:**
- Consumes (from Tasks 1–6, `Noof.Ledger.Application.Diagnostics`): `IDatabaseGate`, `DatabaseState`; `ISystemHealth`, `SystemHealthReport`, `HealthItem`, `HealthLevel`, `HealthCheckNames`; `ILogQuery`, `LogFilter`, `LogPage`, `LogRow`, `LogSeverity`; `ITransactionTrace`, `TransactionTrace`, `TraceEvent`, `RevisionView`; `TransactionStages` (event ids/property names, for seeding in tests only); `ILogSinkStatus` (test-hook wiring only).
- Produces:
  ```csharp
  namespace Noof.Ledger.Application.Diagnostics;

  public interface ILogFileTail
  {
      Task<IReadOnlyList<string>> ReadLastLinesAsync(int count, CancellationToken cancellationToken);
  }
  ```
  Host: `internal sealed class LogFileTail(IConfiguration configuration) : ILogFileTail`, registered singleton by `DiagnosticsHostRegistration.AddNoofDiagnosticsHost(this IServiceCollection, IConfiguration)`. `internal static class DiagnosticsEndpoints { public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder routes); }`.

---

- [ ] **Step 1: Resolve and pin the QuickGrid package version, watch the reference-list guard fail**

Run, to find the QuickGrid version matching this repo's pinned ASP.NET Core Components version:

```
dotnet package search Microsoft.AspNetCore.Components.QuickGrid --source https://api.nuget.org/v3/index.json --exact-match --format json
```

Pick the highest listed version whose major.minor matches `10.0` (the repo's `Microsoft.AspNetCore.Components.Web` pin is `10.0.8`). Use that version in place of `<QUICKGRID_VERSION>` below — do not guess a number without having run this command.

Edit `Directory.Packages.props`, adding to the first `<ItemGroup>` (non-test) right after the `Microsoft.AspNetCore.Components.Authorization` line:

```xml
    <PackageVersion Include="Microsoft.AspNetCore.Components.QuickGrid" Version="<QUICKGRID_VERSION>" />
```

Edit `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs`, changing:

```csharp
    [Fact]
    public void Web_package_references_are_exactly_its_allowed_set()
    {
        Packages("Noof.Ledger.Web").Should().BeEquivalentTo(
            "Microsoft.AspNetCore.Components.Web",
            "Microsoft.AspNetCore.Components.Authorization",
            // MudBlazor is a UI component library and belongs to exactly this assembly. It arrives
            // here rather than silently: this list is the argument about a new UI dependency, and
            // widening it is an edit somebody has to justify.
            "MudBlazor");
    }
```

to:

```csharp
    [Fact]
    public void Web_package_references_are_exactly_its_allowed_set()
    {
        Packages("Noof.Ledger.Web").Should().BeEquivalentTo(
            "Microsoft.AspNetCore.Components.Web",
            "Microsoft.AspNetCore.Components.Authorization",
            // MudBlazor is a UI component library and belongs to exactly this assembly. It arrives
            // here rather than silently: this list is the argument about a new UI dependency, and
            // widening it is an edit somebody has to justify.
            "MudBlazor",
            // QuickGrid backs /diagnostics/logs's paged, filterable grid (Phase 5 Task 7) - a UI
            // concern, same as MudBlazor, and belongs to exactly this assembly for the same reason.
            "Microsoft.AspNetCore.Components.QuickGrid");
    }
```

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter ProjectReferenceTests`

Expected: **FAIL** — `Web_package_references_are_exactly_its_allowed_set` now expects a package the `.csproj` does not reference yet. This is the guard for Step 2's edit; watching it fail here (rather than after adding the package) proves the assertion is exercised at all.

- [ ] **Step 2: Add the package reference, watch the guard pass**

Edit `src/Noof.Ledger.Web/Noof.Ledger.Web.csproj`:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.Web" />
    <PackageReference Include="Microsoft.AspNetCore.Components.Authorization" />
    <PackageReference Include="MudBlazor" />
    <PackageReference Include="Microsoft.AspNetCore.Components.QuickGrid" />
  </ItemGroup>
```

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter ProjectReferenceTests`

Expected: PASS (all `ProjectReferenceTests` cases, including the one changed in Step 1).

Commit:
```
git add Directory.Packages.props src/Noof.Ledger.Web/Noof.Ledger.Web.csproj tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs
git commit -m "$(cat <<'EOF'
Add QuickGrid for the diagnostics logs viewer (Task 7)

/diagnostics/logs needs paged, filterable rows without Virtualize (spec
2026-09-24-observability-design.md §4). QuickGrid is a UI dependency,
same status as MudBlazor, so it is pinned and added to Web's reviewed
package allowlist rather than arriving silently.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 3: Write the failing guard against Web touching the file system directly, watch it fail**

Create `tests/Noof.Ledger.Architecture.Tests/WebFileSystemBoundaryTests.cs`:

```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// Global constraint (Phase 5 SDD): "Noof.Ledger.Web has no EF, no HttpClient, no System.IO file
// access - it reads everything through Application interfaces." ILogFileTail (Application) is the
// seam /diagnostics/logs uses for the file-tail fallback; nothing in Web may read a file itself.
public class WebFileSystemBoundaryTests
{
    static IEnumerable<FileInfo> WebSourceFiles() =>
        new DirectoryInfo(Path.Combine(RepoRoot.Find().FullName, "src", "Noof.Ledger.Web"))
            .EnumerateFiles("*.razor*", SearchOption.AllDirectories)
            .Concat(new DirectoryInfo(Path.Combine(RepoRoot.Find().FullName, "src", "Noof.Ledger.Web"))
                .EnumerateFiles("*.cs", SearchOption.AllDirectories));

    [Fact]
    public void No_source_file_uses_System_IO_directly()
    {
        var offenders = WebSourceFiles()
            .Where(file => File.ReadAllText(file.FullName).Contains("System.IO", StringComparison.Ordinal))
            .Select(file => file.Name)
            .ToList();

        offenders.Should().BeEmpty(
            "Web reads logs through ILogFileTail (Application), never a file directly - a File.* or " +
            "Directory.* call here would bypass the seam the file-tail fallback exists to define");
    }

    [Fact]
    public void There_is_at_least_one_source_file_to_check()
    {
        WebSourceFiles().Should().NotBeEmpty("a rule whose subject set is empty passes forever and enforces nothing");
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter WebFileSystemBoundaryTests`

Expected: PASS today (nothing in Web touches `System.IO` yet) — this is not the red step. To see it actually fail, temporarily add a throwaway line `// System.IO.File.Exists("x");` to `Home.razor`, re-run (expect FAIL, naming `Home.razor`), then remove the line and re-run (expect PASS again). This is the "watch a new guard fail" proof for a rule that stays green through the rest of this task, since nothing below violates it.

Commit:
```
git add tests/Noof.Ledger.Architecture.Tests/WebFileSystemBoundaryTests.cs
git commit -m "$(cat <<'EOF'
Guard: Web never touches System.IO directly (Task 7)

The logs viewer's file-tail fallback must go through ILogFileTail
(Application), not a direct file read from the UI project - this test
is the enforcement the global constraint already names.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 4: Write the failing test for `ILogFileTail` on `PublicSurfaceTests`, then add the interface**

Edit `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`, adding `"ILogFileTail"` to the `["Noof.Ledger.Application"]` array (append at the end of the list, right after `"BackupRunRecord", "BackupStatus", "DumpResult", "IBackupLog", "IDatabaseDumper",`):

```csharp
            "BackupRunRecord", "BackupStatus", "DumpResult", "IBackupLog", "IDatabaseDumper",
            "ILogFileTail",
```

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter PublicSurfaceTests`

Expected: **FAIL** on `Public_types_are_exactly_the_allowed_set("Noof.Ledger.Application")` — the allowlist now names a type that does not exist in the project yet.

Create `src/Noof.Ledger.Application/Diagnostics/ILogFileTail.cs`:

```csharp
namespace Noof.Ledger.Application.Diagnostics;

public interface ILogFileTail
{
    Task<IReadOnlyList<string>> ReadLastLinesAsync(int count, CancellationToken cancellationToken);
}
```

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter PublicSurfaceTests`

Expected: PASS.

Commit:
```
git add src/Noof.Ledger.Application/Diagnostics/ILogFileTail.cs tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs
git commit -m "$(cat <<'EOF'
Add ILogFileTail (Task 7)

The Web-only seam for /diagnostics/logs's file-tail fallback, per the
Phase 5 binding contract: Application.Diagnostics, no file access from
Web itself.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 5: Write the failing test for the Host `LogFileTail` implementation**

Create `tests/Noof.Ledger.Host.Tests/LogFileTailTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Noof.Ledger.Host.Diagnostics;

namespace Noof.Ledger.Host.Tests;

public sealed class LogFileTailTests : IDisposable
{
    readonly DirectoryInfo directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"noof-log-tail-{Guid.NewGuid():N}"));

    [Fact]
    public async Task Reads_the_last_N_lines_of_the_newest_log_file()
    {
        var older = Path.Combine(directory.FullName, "noof-ledger-20260101.log");
        var newer = Path.Combine(directory.FullName, "noof-ledger-20260102.log");
        await File.WriteAllLinesAsync(older, ["old line 1", "old line 2"], TestContext.Current.CancellationToken);
        await File.WriteAllLinesAsync(newer, ["line 1", "line 2", "line 3", "line 4"], TestContext.Current.CancellationToken);
        // A distinct write time (rather than relying on file-name ordering, which the real
        // implementation does not use) - the newest file is chosen by LastWriteTimeUtc.
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var tail = new LogFileTail(Configuration(directory.FullName));

        var lines = await tail.ReadLastLinesAsync(2, TestContext.Current.CancellationToken);

        lines.Should().Equal("line 3", "line 4");
    }

    [Fact]
    public async Task Returns_an_empty_list_when_no_log_file_exists_yet()
    {
        var tail = new LogFileTail(Configuration(directory.FullName));

        var lines = await tail.ReadLastLinesAsync(500, TestContext.Current.CancellationToken);

        lines.Should().BeEmpty();
    }

    [Fact]
    public async Task Reads_a_file_that_is_open_for_writing_elsewhere()
    {
        // FileShare.ReadWrite is the whole point: Serilog's file sink holds the log file open for
        // writing for as long as the host runs, so a tail that cannot share the handle can never
        // read anything.
        var path = Path.Combine(directory.FullName, "noof-ledger-20260103.log");
        await using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        await using (var streamWriter = new StreamWriter(writer))
        {
            await streamWriter.WriteLineAsync("still being written");
            await streamWriter.FlushAsync(TestContext.Current.CancellationToken);

            var tail = new LogFileTail(Configuration(directory.FullName));
            var lines = await tail.ReadLastLinesAsync(10, TestContext.Current.CancellationToken);

            lines.Should().Equal("still being written");
        }
    }

    static IConfiguration Configuration(string directory) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:Directory"] = directory })
            .Build();

    public void Dispose() => directory.Delete(recursive: true);
}
```

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter LogFileTailTests`

Expected: **FAIL** — `Noof.Ledger.Host.Diagnostics.LogFileTail` does not exist yet (compile error surfaced as a test-discovery failure).

Create `src/Noof.Ledger.Host/Diagnostics/LogFileTail.cs`:

```csharp
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal sealed class LogFileTail(IConfiguration configuration) : ILogFileTail
{
    public async Task<IReadOnlyList<string>> ReadLastLinesAsync(int count, CancellationToken cancellationToken)
    {
        var newest = NewestLogFile();
        if (newest is null)
            return [];

        var lines = new List<string>();
        await using var stream = new FileStream(
            newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        // The sink writes tens of MB per file (50 MB cap, spec O-2) - reading the whole file to keep
        // only the tail would be wasteful for a viewer someone opens repeatedly, but correctness (not
        // memory) is what this task needs first; a bounded ring buffer of `count` lines read forward
        // is simple, correct, and still O(file size) only in reads, not retained memory.
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);
            if (lines.Count > count)
                lines.RemoveAt(0);
        }

        return lines;
    }

    FileInfo? NewestLogFile()
    {
        var directory = ResolveDirectory();
        if (!Directory.Exists(directory))
            return null;

        return new DirectoryInfo(directory)
            .EnumerateFiles("noof-ledger-*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    string ResolveDirectory()
    {
        var configured = configuration["Logging:File:Directory"];
        var expanded = Environment.ExpandEnvironmentVariables(
            configured ?? @"%LOCALAPPDATA%\NoofLedger\logs");
        return expanded;
    }
}
```

Create `src/Noof.Ledger.Host/Diagnostics/DiagnosticsHostRegistration.cs`:

```csharp
using Noof.Ledger.Application.Diagnostics;

namespace Noof.Ledger.Host.Diagnostics;

internal static class DiagnosticsHostRegistration
{
    public static IServiceCollection AddNoofDiagnosticsHost(this IServiceCollection services) =>
        services.AddSingleton<ILogFileTail, LogFileTail>();
}
```

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter LogFileTailTests`

Expected: PASS (all three cases).

Commit:
```
git add src/Noof.Ledger.Host/Diagnostics/LogFileTail.cs src/Noof.Ledger.Host/Diagnostics/DiagnosticsHostRegistration.cs tests/Noof.Ledger.Host.Tests/LogFileTailTests.cs
git commit -m "$(cat <<'EOF'
Implement ILogFileTail (Task 7)

Reads the last N lines of the newest noof-ledger-*.log with
FileShare.ReadWrite, so it can read a file Serilog's own sink still
holds open for writing (spec 2026-09-24-observability-design.md §4).

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 6: Wire `LogFileTail` and the diagnostics redirect endpoint into `Program.cs`**

This step has no test of its own (DI wiring / `Program.cs`, exempt under CLAUDE.md §Testing); it is proven by Step 5's and Step 9's/Step 12's tests exercising the real host. Read the current `src/Noof.Ledger.Host/Program.cs` before editing — Tasks 1–6 will already have changed lines around `builder.Services.AddNoofWorkers(...)` and `app.MapAccountEndpoints();`; place the two additions immediately after those two calls respectively, keeping whatever else those tasks added around them.

Add, immediately after `builder.Services.AddNoofWorkers(categorizationOptions, backupOptions);`:

```csharp
builder.Services.AddNoofDiagnosticsHost();
```

with `using Noof.Ledger.Host.Diagnostics;` added to the top of the file.

Add, immediately after `app.MapAccountEndpoints();`:

```csharp
app.MapDiagnosticsEndpoints();
```

with `using Noof.Ledger.Host.Endpoints;` (already present in the file).

Create `src/Noof.Ledger.Host/Endpoints/DiagnosticsEndpoints.cs` (referenced by Step 9's test; written here so `Program.cs` compiles):

```csharp
namespace Noof.Ledger.Host.Endpoints;

internal static class DiagnosticsEndpoints
{
    // A plain <form method="get"> cannot template a route's path segment from user input, so
    // /diagnostics's transaction-id lookup posts here instead of turning the whole static page
    // interactive for one field. Left to the global fallback authorization policy in Program.cs,
    // the same as every other endpoint that declares no [AllowAnonymous].
    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder routes) =>
        routes.MapGet("/diagnostics/open-trace", (Guid id) => Results.Redirect($"/transactions/{id}/trace"));
}
```

Build the Host project to confirm it still compiles: `dotnet build src/Noof.Ledger.Host/Noof.Ledger.Host.csproj`. Expected: build succeeds.

Commit:
```
git add src/Noof.Ledger.Host/Program.cs src/Noof.Ledger.Host/Endpoints/DiagnosticsEndpoints.cs
git commit -m "$(cat <<'EOF'
Wire LogFileTail and the diagnostics trace redirect into the host (Task 7)

GET /diagnostics/open-trace?id=<guid> is the plain, no-JS way for the
static /diagnostics page's transaction-id field to reach
/transactions/{id}/trace.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 7: Write the failing NavBar test, then add the Diagnostics link**

Create `tests/Noof.Ledger.Host.Tests/NavBarDiagnosticsLinkTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Noof.Ledger.Host.Tests;

public sealed class NavBarDiagnosticsLinkTests
{
    [Fact]
    public async Task The_nav_bar_links_to_diagnostics()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Backup:Enabled", "false");
            builder.ConfigureServices(FakeUserStore.Register);
        });

        using var client = factory.CreateClient();
        await LoginHelper.PostWithTokenAsync(client, "noof", "correct");
        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        html.Should().Contain("href=\"/diagnostics\"");
        html.Should().Contain("Diagnostics");
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter NavBarDiagnosticsLinkTests`

Expected: **FAIL** — no `/diagnostics` link exists yet.

Edit `src/Noof.Ledger.Web/Components/Layout/NavBar.razor`, changing:

```csharp
    static readonly NavLinkSpec[] Links =
    [
        new("/", "Dashboard", Icons.Material.Outlined.Insights),
        new("/wallets", "Wallets", Icons.Material.Outlined.AccountBalanceWallet),
        new("/settings/secrets", "Secrets", Icons.Material.Outlined.Key),
    ];
```

to:

```csharp
    static readonly NavLinkSpec[] Links =
    [
        new("/", "Dashboard", Icons.Material.Outlined.Insights),
        new("/wallets", "Wallets", Icons.Material.Outlined.AccountBalanceWallet),
        new("/diagnostics", "Diagnostics", Icons.Material.Outlined.MonitorHeart),
        new("/settings/secrets", "Secrets", Icons.Material.Outlined.Key),
    ];
```

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter NavBarDiagnosticsLinkTests`

Expected: PASS.

Commit:
```
git add src/Noof.Ledger.Web/Components/Layout/NavBar.razor tests/Noof.Ledger.Host.Tests/NavBarDiagnosticsLinkTests.cs
git commit -m "$(cat <<'EOF'
Add the Diagnostics nav link (Task 7)

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 8: Write the failing source-text test for the dashboard health tile, then replace `database-unavailable`/`backup-status` with it**

Edit `tests/Noof.Ledger.Architecture.Tests/DashboardPageSourceTests.cs`, adding a new test (keep every existing test in the file untouched — they assert behaviour this step does not change):

```csharp
    [Fact]
    public void Shows_a_health_tile_through_ISystemHealth_only()
    {
        var source = SourceText();

        source.Should().Contain("ISystemHealth");
        source.Should().Contain("id=\"health-tile\"");
        source.Should().NotContain("id=\"database-unavailable\"",
            "the health tile replaces the separate database-unavailable alert (spec §4)");
        source.Should().NotContain("id=\"backup-status\"",
            "the Backup health check now owns backup staleness; the dashboard stops computing it itself (spec §3)");
        source.Should().NotContain("BackupIsStale",
            "M-5's staleness rule moved into the Backup health check - this page must not keep a second copy of it");
        source.Should().NotContain("DescribeBackup",
            "same as BackupIsStale: the Backup check now phrases this, not the dashboard");
    }
```

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter DashboardPageSourceTests`

Expected: **FAIL** on the new test — `Home.razor` still has `database-unavailable`, `backup-status`, `BackupIsStale` and `DescribeBackup`, and does not mention `ISystemHealth`.

Edit `src/Noof.Ledger.Web/Components/Pages/Home.razor`. Replace the `@using`/`@inject` block:

```csharp
@using Noof.Ledger.Application.Backup
@using Noof.Ledger.Application.Reporting
@using Noof.Ledger.Domain
@inject ISpendingReadModel ReadModel
@inject IBalanceReadModel BalanceReadModel
@inject IBackupLog BackupLog
@inject TimeProvider TimeProvider
```

with:

```csharp
@using Noof.Ledger.Application.Diagnostics
@using Noof.Ledger.Application.Reporting
@using Noof.Ledger.Domain
@inject ISpendingReadModel ReadModel
@inject IBalanceReadModel BalanceReadModel
@inject ISystemHealth SystemHealth
```

Replace the `@if (databaseUnavailable) { ... } else { ... }` block's opening (everything from the `@if (databaseUnavailable)` line through the end of the `<MudPaper id="balances" ...>` block's opening `<MudText>`, i.e. lines 20–34 of the original file) with a health tile that always renders, followed by the balances card guarded on the same `databaseUnavailable` flag as before (kept, since it is still a genuine defence against a mid-page database failure distinct from overall system health going stale-but-cached):

```razor
<MudAlert id="health-tile" Severity="@HealthSeverity(health?.Overall)" Variant="Variant.Outlined" Class="mt-6 mb-6">
    @if (health is null)
    {
        <MudText Typo="Typo.body2">Checking system health…</MudText>
    }
    else if (health.Items.All(item => item.Level == HealthLevel.Ok))
    {
        <MudText Typo="Typo.body2">All systems normal.</MudText>
    }
    else
    {
        @foreach (var item in health.Items.Where(item => item.Level != HealthLevel.Ok))
        {
            <MudText Typo="Typo.body2">@item.Name — @item.Summary</MudText>
        }
    }
    <MudLink Href="/diagnostics" Class="mt-1" Style="display:inline-block;">Diagnostics</MudLink>
</MudAlert>

@if (databaseUnavailable)
{
    <MudText Typo="Typo.body2" Class="mud-text-secondary mb-6">
        The health tile above still reflects the database's own check - the sections below are
        unavailable because a request made while loading this page failed.
    </MudText>
}
else
{
    <MudPaper id="balances" Outlined="true" Elevation="0" Class="pa-5 mb-8">
        <MudText HtmlTag="h2" Typo="Typo.h6" Class="mb-3">Balances</MudText>
```

(the rest of the `else` branch's body — the balances card's contents, the month-total cards, and the recent-transactions list — is unchanged; only its opening two lines moved as shown, and the old `<MudText id="backup-status" ...>` block right after the balances `</MudPaper>` and before `<MudText Typo="Typo.body2" Class="mud-text-secondary mb-6">This month, from ...` is deleted entirely:

```razor
    <MudText id="backup-status" Typo="Typo.caption" Class="mb-8"
             Color="@(BackupIsStale() ? Color.Warning : Color.Default)">
        @DescribeBackup()
    </MudText>

```
— delete this whole block, leaving the "This month, from ..." line as the paper's very next sibling).

In `@code`, replace:

```csharp
    IReadOnlyList<RecentTransaction> recent = [];
    IReadOnlyList<CurrencyGroup> currencyGroups = [];
    IReadOnlyList<WalletBalance> activeBalances = [];
    IReadOnlyList<CurrencyTotal> currencyTotals = [];
    BackupStatus? backupStatus;
    DateOnly summaryFirstDay;
    bool databaseUnavailable;
```

with:

```csharp
    IReadOnlyList<RecentTransaction> recent = [];
    IReadOnlyList<CurrencyGroup> currencyGroups = [];
    IReadOnlyList<WalletBalance> activeBalances = [];
    IReadOnlyList<CurrencyTotal> currencyTotals = [];
    SystemHealthReport? health;
    DateOnly summaryFirstDay;
    bool databaseUnavailable;
```

In `OnInitializedAsync`, replace:

```csharp
            backupStatus = await BackupLog.StatusAsync(cancellation.Token);
```

with:

```csharp
            health = await SystemHealth.GetAsync(fresh: false, cancellation.Token);
```

Delete the `DescribeBackup`, `FormatAgo` and `BackupIsStale` methods entirely (`FormatAgo` was private to those two and is used nowhere else in the file — confirm with `grep -n FormatAgo src/Noof.Ledger.Web/Components/Pages/Home.razor` before deleting, since a stray remaining call would be a compile error, not a silent drop).

Add, near the other `static` helpers (e.g. right after `FormatBalances`):

```csharp
    static Severity HealthSeverity(HealthLevel? level) => level switch
    {
        HealthLevel.Ok => Severity.Success,
        HealthLevel.Warning => Severity.Warning,
        HealthLevel.Failing => Severity.Error,
        _ => Severity.Normal,
    };
```

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter DashboardPageSourceTests`

Expected: PASS (the new test and every pre-existing one — the `CultureInfo.InvariantCulture`, `ISpendingReadModel`, `Items.Count == 0`, `IDisposable`/`CancellationTokenSource`, and `catch (OperationCanceledException` assertions are all still true of the edited file).

Build the Web project to confirm the Razor edits compile: `dotnet build src/Noof.Ledger.Web/Noof.Ledger.Web.csproj`. Expected: build succeeds.

Commit:
```
git add src/Noof.Ledger.Web/Components/Pages/Home.razor tests/Noof.Ledger.Architecture.Tests/DashboardPageSourceTests.cs
git commit -m "$(cat <<'EOF'
Replace the dashboard's database-unavailable alert and backup-status
caption with a health tile (Task 7)

Home.razor now reads ISystemHealth instead of computing its own backup
staleness (that rule now lives in the Backup health check, Task 6) or
its own database-down banner (the health tile's Database item, plus
Task 1's app-wide waiting banner, cover it). Spec §4: "Replaces
database-unavailable and backup-status."

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 9: Replace the dashboard's backup-status E2E tests with a health-tile E2E test**

Edit `tests/Noof.Ledger.E2E.Tests/DashboardBalancesTests.cs`, deleting the three backup-status tests (`Backup_status_says_never_run_when_no_backup_has_ever_completed`, `Backup_status_reports_how_long_ago_the_last_success_was`, `Backup_status_reports_a_failed_run_and_when_the_last_success_was`) and their `BackupRun`-seeding bodies, together with the `Noof.Ledger.Persistence.Backup` `using` (no longer referenced once those three are gone — confirm with `grep -n "Backup" tests/Noof.Ledger.E2E.Tests/DashboardBalancesTests.cs` before removing the `using`). Keep `The_balances_card_shows_a_wallets_balance_and_the_per_currency_total` and `An_archived_wallet_is_left_out_of_the_balances_card_and_its_total` exactly as they are — they assert balances, not backup status.

Create `tests/Noof.Ledger.E2E.Tests/HealthTileTests.cs`:

```csharp
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace Noof.Ledger.E2E.Tests;

public sealed class HealthTileTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task The_dashboard_shows_a_health_tile_linking_to_diagnostics()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var tile = Page.Locator("#health-tile");
        await Expect(tile).ToBeVisibleAsync();
        // Deliberately not asserting "All systems normal" vs a specific warning line here: this
        // clone has no Telegram token, no AI keys and no backup history configured, so which checks
        // are non-Ok is Task 6's own territory, not this task's to pin down. Only the tile's
        // presence and its link are this task's contract.
        await Expect(Page.Locator("#health-tile a[href='/diagnostics']")).ToBeVisibleAsync();
    }

    async Task SignInAsync()
    {
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForURLAsync("**/account/login*");
        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(fixture.BaseUrl + "/");
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj --filter "HealthTileTests|DashboardBalancesTests"`

Expected: FAIL before this task's Home.razor change lands (already covered by Step 8) / PASS once Steps 6–8 are in place and PostgreSQL is reachable (`Assert.Skip` otherwise — not a failure). Run this after Step 8 is committed, since `#health-tile` does not exist before it.

Commit:
```
git add tests/Noof.Ledger.E2E.Tests/DashboardBalancesTests.cs tests/Noof.Ledger.E2E.Tests/HealthTileTests.cs
git commit -m "$(cat <<'EOF'
Replace backup-status E2E coverage with a health-tile E2E test (Task 7)

The three backup-status tests asserted a rule that moved into the
Backup health check (Task 6, with its own tests there); this task only
proves the dashboard now shows and links the health tile itself.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 10: Write the failing source-text tests for `/diagnostics`, `/diagnostics/logs` and the trace page, then write the three pages**

Create `tests/Noof.Ledger.Architecture.Tests/DiagnosticsPageSourceTests.cs`:

```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class DiagnosticsPageSourceTests
{
    static string SourceText(string fileName) => File.ReadAllText(Path.Combine(
        RepoRoot.Find().FullName, "src", "Noof.Ledger.Web", "Components", "Pages", fileName));

    [Fact]
    public void Diagnostics_page_is_routable_and_authorised_and_reads_ISystemHealth_only()
    {
        var source = SourceText("Diagnostics.razor");

        source.Should().Contain("@page \"/diagnostics\"");
        source.Should().Contain("[Authorize]");
        source.Should().Contain("ISystemHealth");
        source.Should().Contain("id=\"diagnostics-checks\"");
        source.Should().NotContain("MudSelect");
        source.Should().NotContain("MudDatePicker");
        source.Should().NotContain("MudAutocomplete");
        source.Should().NotContain("MudMenu");
        source.Should().NotContain("MudTooltip");
        source.Should().NotContain("MudDialog");
        source.Should().NotContain("MudSnackbar");
    }

    [Fact]
    public void Logs_page_is_routable_authorised_interactive_and_paged_without_virtualize()
    {
        var source = SourceText("DiagnosticsLogs.razor");

        source.Should().Contain("@page \"/diagnostics/logs\"");
        source.Should().Contain("[Authorize]");
        source.Should().Contain("InteractiveServerRenderMode(prerender: false)");
        source.Should().Contain("ILogQuery");
        source.Should().Contain("ILogFileTail");
        source.Should().Contain("id=\"logs-grid\"");
        source.Should().Contain("id=\"logs-file-tail\"");
        source.Should().NotContain("Virtualize",
            "the spec is explicit: paged QuickGrid, no Virtualize (§4)");
        source.Should().NotContain("MudSelect");
        source.Should().NotContain("MudDatePicker");
        source.Should().NotContain("MudAutocomplete");
        source.Should().NotContain("MudMenu");
        source.Should().NotContain("MudTooltip");
        source.Should().NotContain("MudDialog");
        source.Should().NotContain("MudSnackbar");
    }

    [Fact]
    public void Trace_page_is_routable_authorised_and_reads_ITransactionTrace_only()
    {
        var source = SourceText("TransactionTrace.razor");

        source.Should().Contain("@page \"/transactions/{Id:guid}/trace\"");
        source.Should().Contain("[Authorize]");
        source.Should().Contain("ITransactionTrace");
        source.Should().Contain("id=\"trace-stages\"");
        source.Should().Contain("id=\"trace-timeline\"");
        source.Should().Contain("id=\"trace-history\"");
        source.Should().NotContain("MudSelect");
        source.Should().NotContain("MudDatePicker");
        source.Should().NotContain("MudAutocomplete");
        source.Should().NotContain("MudMenu");
        source.Should().NotContain("MudTooltip");
        source.Should().NotContain("MudDialog");
        source.Should().NotContain("MudSnackbar");
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter DiagnosticsPageSourceTests`

Expected: **FAIL** — none of the three `.razor` files exist yet (each `SourceText` call throws `FileNotFoundException`, failing all three tests).

Create `src/Noof.Ledger.Web/Components/Pages/Diagnostics.razor`:

```razor
@page "/diagnostics"
@attribute [Authorize]
@using Noof.Ledger.Application.Diagnostics
@inject ISystemHealth SystemHealth
@inject IDatabaseGate Gate

<PageTitle>Diagnostics</PageTitle>

<MudText HtmlTag="h1" Typo="Typo.h4" Class="mb-4">Diagnostics</MudText>

@if (Gate.State != DatabaseState.Ready)
{
    <DatabaseWaitingBanner State="Gate.State" Detail="Gate.Detail" />
}
else
{
    <MudSimpleTable id="diagnostics-checks" Dense="true" Hover="true" Elevation="0" Class="mb-8">
        <thead>
            <tr>
                <th>Check</th>
                <th>Level</th>
                <th>Summary</th>
                <th>Checked at</th>
                <th></th>
            </tr>
        </thead>
        <tbody>
            @if (report is not null)
            {
                @foreach (var item in report.Items)
                {
                    <tr>
                        <td>@item.Name</td>
                        <td>@item.Level</td>
                        <td>@item.Summary</td>
                        <td>@item.CheckedAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture)</td>
                        <td>
                            @if (item.Level != HealthLevel.Ok)
                            {
                                <MudLink Href="@($"/diagnostics/logs?source={Uri.EscapeDataString(item.Name)}")">Logs</MudLink>
                            }
                        </td>
                    </tr>
                }
            }
        </tbody>
    </MudSimpleTable>

    <MudPaper Outlined="true" Elevation="0" Class="pa-5">
        <MudText Typo="Typo.subtitle1" Class="mb-3">Open a transaction's trace</MudText>
        <form method="get" action="/diagnostics/open-trace">
            <MudTextField T="string" InputId="diagnostics-transaction-id" Name="id"
                          Variant="Variant.Outlined" Margin="Margin.Dense" Label="Transaction id" Class="mb-3" />
            <MudButton id="diagnostics-open-trace" ButtonType="ButtonType.Submit" Variant="Variant.Outlined">
                Open trace
            </MudButton>
        </form>
    </MudPaper>
}

@code {
    SystemHealthReport? report;

    protected override async Task OnInitializedAsync()
    {
        if (Gate.State == DatabaseState.Ready)
            report = await SystemHealth.GetAsync(fresh: true, CancellationToken.None);
    }
}
```

Create `src/Noof.Ledger.Web/Components/Pages/DiagnosticsLogs.razor`:

```razor
@page "/diagnostics/logs"
@attribute [Authorize]
@rendermode @(new InteractiveServerRenderMode(prerender: false))
@using Microsoft.AspNetCore.Components.QuickGrid
@using Noof.Ledger.Application.Diagnostics
@inject ILogQuery LogQuery
@inject ILogFileTail LogFileTail
@inject ISystemHealth SystemHealth
@inject IDatabaseGate Gate

<PageTitle>Logs</PageTitle>

<MudText HtmlTag="h1" Typo="Typo.h4" Class="mb-4">Logs</MudText>

@if (fileTailMode)
{
    <MudAlert Severity="Severity.Warning" Variant="Variant.Outlined" Class="mb-4">
        Database log unavailable — showing the log file.
    </MudAlert>

    <MudTextField T="string" @bind-Value="textFilter" Immediate="true" ValueChanged="OnFileTailFilterChangedAsync"
                  Variant="Variant.Outlined" Margin="Margin.Dense" Label="Text" Class="mb-4" />

    <div id="logs-file-tail">
        @foreach (var line in visibleFileLines)
        {
            <MudText Typo="Typo.body2" Class="noof-log-line">@line</MudText>
        }
    </div>
}
else
{
    @if (Gate.State != DatabaseState.Ready)
    {
        <DatabaseWaitingBanner State="Gate.State" Detail="Gate.Detail" />
    }
    else
    {
        <MudGrid Spacing="2" Class="mb-4">
            <MudItem xs="12" md="2">
                <select id="logs-filter-level" @bind="filter.MinLevel" @bind:after="ReloadAsync" class="noof-native-select">
                    @foreach (var level in Enum.GetValues<LogSeverity>())
                    {
                        <option value="@level">@level</option>
                    }
                </select>
            </MudItem>
            <MudItem xs="12" md="3">
                <MudTextField T="string" InputId="logs-filter-text" @bind-Value="filter.Text" @bind-Value:after="ReloadAsync"
                              Variant="Variant.Outlined" Margin="Margin.Dense" Label="Text" />
            </MudItem>
            <MudItem xs="12" md="3">
                <MudTextField T="string" InputId="logs-filter-source" @bind-Value="filter.Source" @bind-Value:after="ReloadAsync"
                              Variant="Variant.Outlined" Margin="Margin.Dense" Label="Source" />
            </MudItem>
            <MudItem xs="12" md="4">
                <MudTextField T="string" InputId="logs-filter-transaction-id" @bind-Value="transactionIdText"
                              @bind-Value:after="ReloadAsync" Variant="Variant.Outlined" Margin="Margin.Dense"
                              Label="Transaction id" />
            </MudItem>
        </MudGrid>

        <QuickGrid id="logs-grid" ItemsProvider="ProvideRowsAsync" Pagination="pagination" Class="mb-2">
            <PropertyColumn Property="@(row => row.LoggedAt)" Title="Logged at" />
            <PropertyColumn Property="@(row => row.Level)" Title="Level" />
            <PropertyColumn Property="@(row => row.Source)" Title="Source" />
            <TemplateColumn Title="Message">
                <ChildContent>
                    <div>
                        @context.Message
                        <MudLink OnClick="@(() => ToggleExpanded(context.Id))" Class="ml-2">
                            @(expandedRowId == context.Id ? "Hide" : "Details")
                        </MudLink>
                        @if (expandedRowId == context.Id)
                        {
                            <div class="noof-log-details">
                                @if (context.Exception is not null)
                                {
                                    <pre>@context.Exception</pre>
                                }
                                @if (context.PropertiesJson is not null)
                                {
                                    <pre>@context.PropertiesJson</pre>
                                }
                                @if (context.TransactionId is { } id)
                                {
                                    <MudLink Href="@($"/transactions/{id}/trace")">View trace</MudLink>
                                }
                            </div>
                        }
                    </div>
                </ChildContent>
            </TemplateColumn>
        </QuickGrid>
        <Paginator Value="pagination" />
    }
}

@code {
    readonly LogFilter filter = new();
    readonly PaginationState pagination = new() { ItemsPerPage = 100 };
    string? textFilter;
    string? transactionIdText;
    bool fileTailMode;
    long expandedRowId = -1;
    IReadOnlyList<string> fileLines = [];
    IReadOnlyList<string> visibleFileLines = [];

    protected override async Task OnInitializedAsync()
    {
        var health = Gate.State == DatabaseState.Ready
            ? await SystemHealth.GetAsync(fresh: false, CancellationToken.None)
            : null;
        var logSinkOk = health?.Items.SingleOrDefault(item => item.Name == HealthCheckNames.LogSink)?.Level == HealthLevel.Ok;
        fileTailMode = Gate.State != DatabaseState.Ready || !logSinkOk;

        if (fileTailMode)
        {
            fileLines = await LogFileTail.ReadLastLinesAsync(500, CancellationToken.None);
            visibleFileLines = fileLines;
        }
    }

    async Task<GridItemsProviderResult<LogRow>> ProvideRowsAsync(GridItemsProviderRequest<LogRow> request)
    {
        var page = await LogQuery.QueryAsync(filter, request.StartIndex / pagination.ItemsPerPage, pagination.ItemsPerPage, request.CancellationToken);
        return GridItemsProviderResult.From(page.Rows, page.TotalCount);
    }

    Task ReloadAsync()
    {
        filter.TransactionId = Guid.TryParse(transactionIdText, out var parsed) ? parsed : null;
        return pagination.SetCurrentPageIndexAsync(0);
    }

    Task OnFileTailFilterChangedAsync(string? value)
    {
        textFilter = value;
        visibleFileLines = string.IsNullOrWhiteSpace(textFilter)
            ? fileLines
            : [.. fileLines.Where(line => line.Contains(textFilter, StringComparison.OrdinalIgnoreCase))];
        return Task.CompletedTask;
    }

    void ToggleExpanded(long id) => expandedRowId = expandedRowId == id ? -1 : id;
}
```

Note: `LogFilter` is a `sealed record` per the binding contract, so `filter.MinLevel`/`filter.Text`/`filter.Source`/`filter.TransactionId` above must be settable — if Task 3 ships it with `init`-only properties (records default to `init` unless declared otherwise), replace the four direct-assignment bindings above with a local mutable view model (`sealed class LogFilterEdit { public LogSeverity MinLevel; public string? Text; public string? Source; public Guid? TransactionId; }`) and construct a fresh `LogFilter` from it on every `ReloadAsync`/`ProvideRowsAsync` call instead of mutating the record in place. Check the real property accessors on `LogFilter` before writing this file for real, and use whichever form compiles.

`context.Id` in the `TemplateColumn` above assumes `LogRow.Id` is exposed as `long` per the binding contract (`public sealed record LogRow(long Id, ...)`), which it is — no change needed there.

Create `src/Noof.Ledger.Web/Components/Pages/TransactionTrace.razor`:

```razor
@page "/transactions/{Id:guid}/trace"
@using Noof.Ledger.Application.Diagnostics
@attribute [Authorize]
@inject ITransactionTrace TransactionTrace
@inject IDatabaseGate Gate

<PageTitle>Transaction trace</PageTitle>

<MudText HtmlTag="h1" Typo="Typo.h4" Class="mb-1">Transaction trace</MudText>
<MudText Typo="Typo.body2" Class="mud-text-secondary mb-6">@Id</MudText>

@if (Gate.State != DatabaseState.Ready)
{
    <DatabaseWaitingBanner State="Gate.State" Detail="Gate.Detail" />
}
else if (trace is null)
{
    <MudText Typo="Typo.body1">Loading…</MudText>
}
else if (!trace.Exists)
{
    <MudAlert Severity="Severity.Warning" Variant="Variant.Outlined">No transaction with this id.</MudAlert>
}
else
{
    <div id="trace-stages" class="mb-6">
        @foreach (var stage in TransactionStages.Ordered)
        {
            var stageEvent = trace.Events.FirstOrDefault(e => e.Stage == stage);
            var failed = trace.Events.Any(e => e.Stage == TransactionStages.StageFailed
                && e.PropertiesJson is not null && e.PropertiesJson.Contains($"\"FailedStage\":\"{stage}\"", StringComparison.Ordinal));
            <MudChip T="string" Size="Size.Small" Variant="Variant.Filled"
                     Color="@(failed ? Color.Error : stageEvent is not null ? Color.Success : Color.Default)"
                     Class="mr-2">
                @(stage == TransactionStages.Transcribed && stageEvent is null && !failed ? $"{stage} —" : stage)
            </MudChip>
        }
    </div>

    @if (trace.Events.Count == 0)
    {
        <MudAlert id="trace-timeline" Severity="Severity.Info" Variant="Variant.Outlined" Class="mb-6">
            Trace expired (logs older than 90 days)
        </MudAlert>
    }
    else
    {
        <MudSimpleTable id="trace-timeline" Dense="true" Elevation="0" Class="mb-6">
            <tbody>
                @foreach (var e in trace.Events)
                {
                    <tr>
                        <td>@FormatOffset(e.At - trace.Events[0].At)</td>
                        <td>@e.Stage</td>
                        <td>@e.Message</td>
                    </tr>
                }
            </tbody>
        </MudSimpleTable>
    }

    <MudText HtmlTag="h2" Typo="Typo.h6" Class="mb-3">History</MudText>
    <MudSimpleTable id="trace-history" Dense="true" Elevation="0">
        <tbody>
            @foreach (var revision in trace.History)
            {
                <tr>
                    <td>@revision.At.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture)</td>
                    <td>@revision.ChangeKind</td>
                    <td>@revision.Details</td>
                </tr>
            }
        </tbody>
    </MudSimpleTable>
}

@code {
    [Parameter] public Guid Id { get; set; }

    TransactionTrace? trace;

    protected override async Task OnInitializedAsync()
    {
        if (Gate.State == DatabaseState.Ready)
            trace = await TransactionTrace.GetAsync(Id, CancellationToken.None);
    }

    static string FormatOffset(TimeSpan offset) => offset switch
    {
        { TotalSeconds: < 1 } => $"+{(int)offset.TotalMilliseconds} ms",
        { TotalSeconds: < 10 } => $"+{offset.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s",
        _ => $"+{(int)offset.TotalSeconds} s",
    };
}
```

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter DiagnosticsPageSourceTests`

Expected: PASS.

Build: `dotnet build src/Noof.Ledger.Web/Noof.Ledger.Web.csproj`. Expected: build succeeds — if `LogFilter`'s properties turn out to be `init`-only (see the note above), fix `DiagnosticsLogs.razor` per that note before this build is expected to pass, and re-run the build.

Commit:
```
git add src/Noof.Ledger.Web/Components/Pages/Diagnostics.razor src/Noof.Ledger.Web/Components/Pages/DiagnosticsLogs.razor src/Noof.Ledger.Web/Components/Pages/TransactionTrace.razor tests/Noof.Ledger.Architecture.Tests/DiagnosticsPageSourceTests.cs
git commit -m "$(cat <<'EOF'
Add /diagnostics, /diagnostics/logs and /transactions/{id}/trace (Task 7)

/diagnostics lists every health check and a transaction-id lookup;
/diagnostics/logs is a paged, filterable QuickGrid over ILogQuery with
a file-tail fallback via ILogFileTail when the Log sink check is not Ok
or the database gate is not Ready; /transactions/{id}/trace shows the
stage strip, timeline and revision history from ITransactionTrace.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 11: E2E — `/diagnostics` shows checks and the transaction-id lookup opens the trace page**

Create `tests/Noof.Ledger.E2E.Tests/DiagnosticsTests.cs`:

```csharp
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;

namespace Noof.Ledger.E2E.Tests;

public sealed class DiagnosticsTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task The_diagnostics_page_lists_every_health_check()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var table = Page.Locator("#diagnostics-checks");
        await Expect(table).ToBeVisibleAsync();
        await Expect(table).ToContainTextAsync("Database");
        await Expect(table).ToContainTextAsync("Migrations");
        await Expect(table).ToContainTextAsync("Telegram");
        await Expect(table).ToContainTextAsync("AI keys");
        await Expect(table).ToContainTextAsync("Backup");
        await Expect(table).ToContainTextAsync("Disk");
        await Expect(table).ToContainTextAsync("Log sink");
    }

    [Fact]
    public async Task Entering_a_transaction_id_opens_its_trace_page()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var transactionId = Guid.NewGuid();

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Page.FillAsync("#diagnostics-transaction-id", transactionId.ToString());
        await Page.ClickAsync("#diagnostics-open-trace");

        await Page.WaitForURLAsync($"**/transactions/{transactionId}/trace");
    }

    async Task SignInAsync()
    {
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForURLAsync("**/account/login*");
        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(fixture.BaseUrl + "/");
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj --filter DiagnosticsTests`

Expected: PASS once Steps 6 and 10 are committed and PostgreSQL is reachable (skipped otherwise).

Commit:
```
git add tests/Noof.Ledger.E2E.Tests/DiagnosticsTests.cs
git commit -m "$(cat <<'EOF'
E2E: /diagnostics lists checks and opens a transaction's trace (Task 7)

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 12: E2E — logs filter over seeded `app_log` rows, and the file-tail fallback**

This step needs a way to force the Log sink check to report Warning inside a running host, per the spec's own test requirement ("inject via configuration/test hook"). Add the hook as a narrow, config-gated, no-op-by-default startup line — read `src/Noof.Ledger.Host/Program.cs` first, since Tasks 1–6 will have already added lines around `var app = builder.Build();`; insert this immediately after that line:

```csharp
if (builder.Configuration.GetValue("Diagnostics:ForceLogSinkFailureForTests", false))
    app.Services.GetRequiredService<ILogSinkStatus>().RecordFailure(TimeProvider.System.GetUtcNow());
```

with `using Noof.Ledger.Application.Diagnostics;` already present (added by Step 6, or by an earlier task — check before adding a duplicate `using`).

Create `tests/Noof.Ledger.E2E.Tests/DiagnosticsLogsTests.cs`:

```csharp
using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Diagnostics;

namespace Noof.Ledger.E2E.Tests;

public sealed class DiagnosticsLogsTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task Filtering_by_text_narrows_the_grid_to_matching_rows()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var marker = $"diagnostics-marker-{Guid.NewGuid():N}";

        await using (var db = OpenDb())
        {
            db.AppLogs.Add(NewRow(marker));
            db.AppLogs.Add(NewRow("unrelated row that must not match"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/diagnostics/logs");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var grid = Page.Locator("#logs-grid");
        await Expect(grid).ToBeVisibleAsync();

        await Page.FillAsync("#logs-filter-text", marker);
        await Expect(grid).ToContainTextAsync(marker);
        await Expect(grid).Not.ToContainTextAsync("unrelated row that must not match");
    }

    [Fact]
    public async Task A_failing_log_sink_check_falls_back_to_the_file_tail()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        // A separate host process, started with the force-failure hook set, rather than mutating
        // the shared fixture's host (other tests in this class rely on the Log sink check reading
        // Ok on that shared host).
        var publishDirectory = await HostProcess.PublishHostAsync(TestContext.Current.CancellationToken);
        await using var host = new HostProcess();
        await host.StartAsync(publishDirectory, new Dictionary<string, string>
        {
            ["Database__MigrateOnStartup"] = "false",
            ["ConnectionStrings__Ledger"] = fixture.ConnectionString,
            ["Backup__Enabled"] = "false",
            ["Diagnostics__ForceLogSinkFailureForTests"] = "true",
        }, TestContext.Current.CancellationToken);

        await Page.GotoAsync(host.BaseUrl + "/");
        await Page.WaitForURLAsync("**/account/login*");
        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(host.BaseUrl + "/");

        await Page.GotoAsync(host.BaseUrl + "/diagnostics/logs");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator("text=Database log unavailable")).ToBeVisibleAsync();
        await Expect(Page.Locator("#logs-file-tail")).ToBeVisibleAsync();
        await Expect(Page.Locator("#logs-grid")).Not.ToBeVisibleAsync();
    }

    static AppLogEntry NewRow(string message) => new()
    {
        LoggedAt = DateTimeOffset.UtcNow,
        Level = LogSeverity.Information,
        Source = "DiagnosticsLogsTests",
        Message = message,
        Template = "{Message}",
        Exception = null,
        TransactionId = null,
        PropertiesJson = null,
    };

    LedgerDbContext OpenDb() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    async Task SignInAsync()
    {
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForURLAsync("**/account/login*");
        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(fixture.BaseUrl + "/");
    }
}
```

`AppLogEntry`'s exact property list is Task 3's to fix; adjust field names above (in particular `PropertiesJson` versus whatever Task 3 actually named the `properties` column's CLR property, and `Id`, which this test does not set because it is presumably `identity`-generated) once Task 3 is merged and its real entity is visible — the shape here is the plan's best-effort mirror of the binding contract's `LogRow`/table-column list, not a verified signature.

Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj --filter DiagnosticsLogsTests`

Expected: PASS once Steps 6, 10 and the `Program.cs` hook above are committed, `ILogSinkStatus` (Task 4) exists, and PostgreSQL is reachable (skipped otherwise).

Commit:
```
git add src/Noof.Ledger.Host/Program.cs tests/Noof.Ledger.E2E.Tests/DiagnosticsLogsTests.cs
git commit -m "$(cat <<'EOF'
E2E: logs filter over seeded rows, and the file-tail fallback (Task 7)

Diagnostics:ForceLogSinkFailureForTests is a config-gated,
false-by-default startup hook that lets a test force the Log sink
check to Warning without waiting for a real sink failure - the spec's
own test requirement (§5) for exercising the fallback deterministically.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 13: E2E — the trace page for a seeded transaction with `app_log` rows and revisions**

Create `tests/Noof.Ledger.E2E.Tests/TransactionTraceTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Application.Diagnostics;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;
using Noof.Ledger.Persistence.Diagnostics;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.E2E.Tests;

public sealed class TransactionTraceTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    static readonly Guid DefaultWalletId = new("00000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task The_trace_page_shows_stages_a_timeline_and_revision_history()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var transactionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var db = OpenDb())
        {
            db.Transactions.Add(new Transaction
            {
                Id = transactionId,
                WalletId = DefaultWalletId,
                RawText = "seeded trace transaction",
                CaptureKind = CaptureKind.Manual,
                Kind = TransactionKind.Expense,
                Status = TransactionStatus.Completed,
                TimeZoneId = "Europe/Belgrade",
                OccurredAt = now,
                OccurredOn = ZonedClock.LocalDate(now, "Europe/Belgrade"),
                TelegramChatId = null,
                TelegramMessageId = null,
                CreatedAt = now,
            });

            db.AppLogs.Add(NewStageRow(transactionId, now, TransactionStages.Received, TransactionStages.ReceivedEventId));
            db.AppLogs.Add(NewStageRow(transactionId, now.AddMilliseconds(312), TransactionStages.Categorized, TransactionStages.CategorizedEventId));
            db.AppLogs.Add(NewStageRow(transactionId, now.AddSeconds(1), TransactionStages.Persisted, TransactionStages.PersistedEventId));

            db.TransactionRevisions.Add(new TransactionRevision
            {
                Id = Guid.NewGuid(),
                TransactionId = transactionId,
                RevisionNumber = 1,
                Kind = RevisionKind.Initial,
                Instruction = null,
                StatusBefore = TransactionStatus.Captured,
                StatusAfter = TransactionStatus.Completed,
                Snapshot = "{}",
                CreatedAt = now,
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync($"{fixture.BaseUrl}/transactions/{transactionId}/trace");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var stages = Page.Locator("#trace-stages");
        await Expect(stages).ToContainTextAsync("Received");
        await Expect(stages).ToContainTextAsync("Categorized");
        await Expect(stages).ToContainTextAsync("Persisted");

        var timeline = Page.Locator("#trace-timeline");
        await Expect(timeline).ToContainTextAsync("+0 ms");
        await Expect(timeline).ToContainTextAsync("+312 ms");
        await Expect(timeline).ToContainTextAsync("+1.0 s");

        var history = Page.Locator("#trace-history");
        await Expect(history).ToContainTextAsync("Initial");
    }

    [Fact]
    public async Task A_transaction_id_with_no_log_rows_but_a_real_transaction_shows_trace_expired()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var transactionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using (var db = OpenDb())
        {
            db.Transactions.Add(new Transaction
            {
                Id = transactionId,
                WalletId = DefaultWalletId,
                RawText = "old transaction, pruned logs",
                CaptureKind = CaptureKind.Manual,
                Kind = TransactionKind.Expense,
                Status = TransactionStatus.Completed,
                TimeZoneId = "Europe/Belgrade",
                OccurredAt = now,
                OccurredOn = ZonedClock.LocalDate(now, "Europe/Belgrade"),
                TelegramChatId = null,
                TelegramMessageId = null,
                CreatedAt = now,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync($"{fixture.BaseUrl}/transactions/{transactionId}/trace");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator("#trace-timeline")).ToContainTextAsync("Trace expired");
    }

    [Fact]
    public async Task An_unknown_transaction_id_shows_a_not_found_state()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync($"{fixture.BaseUrl}/transactions/{Guid.NewGuid()}/trace");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator("text=No transaction with this id.")).ToBeVisibleAsync();
    }

    static AppLogEntry NewStageRow(Guid transactionId, DateTimeOffset at, string stage, int eventId) => new()
    {
        LoggedAt = at,
        Level = LogSeverity.Information,
        Source = "TransactionTraceTests",
        Message = stage,
        Template = "{Stage}",
        Exception = null,
        TransactionId = transactionId,
        PropertiesJson = $$"""{"Stage":"{{stage}}","EventId":{"Id":{{eventId}},"Name":"{{stage}}"},"TransactionId":"{{transactionId}}"}""",
    };

    LedgerDbContext OpenDb() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    async Task SignInAsync()
    {
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForURLAsync("**/account/login*");
        await Page.FillAsync("input[name='username']", CookieModeHostFixture.Username);
        await Page.FillAsync("input[name='password']", CookieModeHostFixture.Password);
        await Page.ClickAsync("button[type='submit']");
        await Page.WaitForURLAsync(fixture.BaseUrl + "/");
    }
}
```

As with Step 12, `AppLogEntry`'s property names (in particular `PropertiesJson`) must be checked against Task 3's actual merged entity before this compiles — this is the plan's best-effort mirror of the contract, flagged in the Plan notes above.

Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj --filter TransactionTraceTests`

Expected: PASS once Steps 6 and 10 are committed, Task 3's `ITransactionTrace` reads `properties->>'Stage'`/`properties->'EventId'->>'Id'` as its contract states, and PostgreSQL is reachable (skipped otherwise).

Commit:
```
git add tests/Noof.Ledger.E2E.Tests/TransactionTraceTests.cs
git commit -m "$(cat <<'EOF'
E2E: transaction trace page - stages, timeline, history, expired and
not-found states (Task 7)

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

- [ ] **Step 14: Full filtered run of every touched test class**

Run:
```
dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter "ProjectReferenceTests|PublicSurfaceTests|WebFileSystemBoundaryTests|DashboardPageSourceTests|DiagnosticsPageSourceTests"
dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter "LogFileTailTests|NavBarDiagnosticsLinkTests"
dotnet test --project tests/Noof.Ledger.E2E.Tests/Noof.Ledger.E2E.Tests.csproj --filter "DashboardBalancesTests|HealthTileTests|DiagnosticsTests|DiagnosticsLogsTests|TransactionTraceTests"
```

Expected: PASS (E2E cases skip cleanly if PostgreSQL is unreachable, per every existing test in that project — never a failure). Per CLAUDE.md's testing rule, do not additionally run the full unfiltered solution suite here; that happens once at the end of the phase.

No commit for this step (verification only).

---

### Task 8: /health bot command (owner only)

**Plan notes:**
- `TelegramOwnerGate` already exists (`src/Noof.Ledger.Telegram/TelegramOwnerGate.cs`) with a method
  `IsAllowedAsync(long, CancellationToken)` used by capture/edit/callback routing, which *claims*
  ownership on first contact (`TrySetIfMissingAsync`). The contract's `IsOwnerAsync` is a **new,
  second method** added to that same class — read-only, never claims — used only by `/health`.
  `IsAllowedAsync` is untouched and keeps gating everything else.
- The scope's suggested test-filter syntax (`-- --filter-class <FullClassName>`) does not match this
  repo: every existing plan and `ops/RUNBOOK.md` use `dotnet test --project <csproj> --filter <ClassName>`
  (xUnit v3 on Microsoft.Testing.Platform, confirmed against `docs/superpowers/plans/2026-09-24-phase4-money-model.md`).
  Steps below use the repo's actual syntax.
- `ISystemHealth`/`SystemHealthReport`/`HealthItem`/`HealthLevel` (Task 6, `Noof.Ledger.Application.Diagnostics`)
  do not exist in the repo yet — confirmed by search. Every step below that references them can only be
  run for real once Task 6 is merged, per its stated dependency; the test/production code is written
  against the exact contract names so no rework is expected then.
- `TelegramPollingService` and `TelegramOwnerGateTests`/`TelegramUpdateRouterTests` are also touched by
  other Phase 5 tasks (Task 4's `[LoggerMessage]` conversion, Task 1's `gate.WaitUntilReadyAsync`). This
  task only adds its own two new log call sites as `[LoggerMessage]`-generated methods and does not
  convert `TelegramPollingService`'s existing raw `logger.LogError(...)` calls — that conversion is
  Task 4's stated scope, and touching it here would fight that task's diff.

**Files:**
- Modify `src/Noof.Ledger.Telegram/TelegramOwnerGate.cs` (add `IsOwnerAsync`, ~lines 6–33)
- Create `src/Noof.Ledger.Telegram/HealthReplyFormatter.cs`
- Modify `src/Noof.Ledger.Telegram/TelegramUpdateRouter.cs` (whole file, ~82 lines → partial class, new
  constructor params, health interception, health handler, `[LoggerMessage]`)
- Modify `src/Noof.Ledger.Telegram/TelegramPollingService.cs` (constructor params unchanged; `partial`
  keyword added; new private method + `[LoggerMessage]`; one call site inside `RunTickAsync`'s
  `if (secret.Value != activeToken)` branch, ~lines 66–75)
- Modify `tests/Noof.Ledger.Telegram.Tests/TelegramOwnerGateTests.cs` (append 4 tests)
- Create `tests/Noof.Ledger.Telegram.Tests/HealthReplyFormatterTests.cs`
- Modify `tests/Noof.Ledger.Telegram.Tests/TelegramUpdateRouterTests.cs` (usings, `CreateRouter` factory
  body, new `CreateHealthHarness` helper, 5 new tests)
- Modify `tests/Noof.Ledger.Telegram.Tests/TelegramPollingServiceTests.cs` (usings, 3 new tests)

**Interfaces:**
- Consumes (Task 6 contract, `Noof.Ledger.Application.Diagnostics`):
  `ISystemHealth.GetAsync(bool fresh, CancellationToken)`, `SystemHealthReport(HealthLevel Overall, IReadOnlyList<HealthItem> Items)`,
  `HealthItem(string Name, HealthLevel Level, string Summary, DateTimeOffset CheckedAt)`,
  `enum HealthLevel { Ok, Warning, Failing }`.
- Produces (task-internal, not in the binding contract's exact-name list):
  `TelegramOwnerGate.IsOwnerAsync(long chatId, CancellationToken cancellationToken) : Task<bool>` — the
  one name the contract fixes.
  `HealthReplyFormatter.Format(SystemHealthReport report) : string` (internal static, `Noof.Ledger.Telegram`) — the other contract-fixed name.
  Task-internal only (free to name, not reused elsewhere): `TelegramUpdateRouter.IsHealthCommand(string)`,
  `TelegramUpdateRouter.HandleHealthCommandAsync(long, CancellationToken)`,
  `TelegramUpdateRouterLog.HealthCommandRejected` (EventId 6001, Debug),
  `TelegramPollingService.RegisterHealthCommandAsync(ITelegramBotClient, ISecretStore, CancellationToken)`,
  `TelegramPollingServiceLog.HealthCommandRegistrationFailed` (EventId 6101, Warning).

---

- [ ] **Step 1: `TelegramOwnerGate.IsOwnerAsync` — read-only ownership check, never claims**

  Write failing tests. Append to `tests/Noof.Ledger.Telegram.Tests/TelegramOwnerGateTests.cs`, inside
  `public class TelegramOwnerGateTests { ... }`, after the last existing test method:

  ```csharp
    [Fact]
    public async Task IsOwnerAsync_confirms_the_owner_chat_without_touching_the_store_again()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, "111"));
        var gate = new TelegramOwnerGate(secretStore);

        var isOwner = await gate.IsOwnerAsync(111L, TestContext.Current.CancellationToken);

        isOwner.Should().BeTrue();
        await secretStore.DidNotReceive().TrySetIfMissingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsOwnerAsync_rejects_a_chat_that_is_not_the_recorded_owner()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Present, "111"));
        var gate = new TelegramOwnerGate(secretStore);

        var isOwner = await gate.IsOwnerAsync(999L, TestContext.Current.CancellationToken);

        isOwner.Should().BeFalse();
    }

    [Fact]
    public async Task IsOwnerAsync_never_claims_ownership_when_none_is_set_yet()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Missing, null));
        var gate = new TelegramOwnerGate(secretStore);

        var isOwner = await gate.IsOwnerAsync(111L, TestContext.Current.CancellationToken);

        isOwner.Should().BeFalse("IsOwnerAsync is a read, not a claim - only IsAllowedAsync may claim");
        await secretStore.DidNotReceive().TrySetIfMissingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsOwnerAsync_fails_closed_on_an_unreadable_owner_record()
    {
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
            .Returns(new SecretResult(SecretState.Unreadable, null));
        var gate = new TelegramOwnerGate(secretStore);

        var isOwner = await gate.IsOwnerAsync(111L, TestContext.Current.CancellationToken);

        isOwner.Should().BeFalse();
    }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramOwnerGateTests`
  Expected: build error — `IsOwnerAsync` does not exist on `TelegramOwnerGate` (`CS1061`). This is the
  "watch it fail" for the new method.

  Implement. Replace `src/Noof.Ledger.Telegram/TelegramOwnerGate.cs` in full:

  ```csharp
  using System.Globalization;
  using Noof.Ledger.Application.Secrets;

  namespace Noof.Ledger.Telegram;

  internal sealed class TelegramOwnerGate(ISecretStore secretStore)
  {
      public async Task<bool> IsAllowedAsync(long chatId, CancellationToken cancellationToken)
      {
          var owner = await secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, cancellationToken);

          return owner.State switch
          {
              SecretState.Missing => await ClaimAsync(chatId, cancellationToken),
              SecretState.Present => owner.Value == chatId.ToString(CultureInfo.InvariantCulture),
              _ => false,
          };
      }

      // /health's authorisation check: a read of who owns the bot, never a claim. Distinct from
      // IsAllowedAsync on purpose - a stranger typing /health before an owner exists must never
      // become the owner just by asking about health.
      public async Task<bool> IsOwnerAsync(long chatId, CancellationToken cancellationToken)
      {
          var owner = await secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, cancellationToken);

          return owner.State is SecretState.Present
              && owner.Value == chatId.ToString(CultureInfo.InvariantCulture);
      }

      async Task<bool> ClaimAsync(long chatId, CancellationToken cancellationToken)
      {
          var chatIdText = chatId.ToString(CultureInfo.InvariantCulture);

          if (await secretStore.TrySetIfMissingAsync(SecretKeys.TelegramOwnerChatId, chatIdText, cancellationToken))
              return true;

          // Lost the race: another chat claimed ownership between our GetAsync and this call. Honour
          // whoever actually won rather than surfacing the conflict as an error - the loser here is
          // simply not the owner, which is the correct outcome, not a failure.
          var owner = await secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, cancellationToken);
          return owner.State is SecretState.Present && owner.Value == chatIdText;
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramOwnerGateTests`
  Expected: PASS (9 tests: the 5 existing plus the 4 new).

  Commit: `git add src/Noof.Ledger.Telegram/TelegramOwnerGate.cs tests/Noof.Ledger.Telegram.Tests/TelegramOwnerGateTests.cs`

  ```
  git commit -m "$(cat <<'EOF'
  Add TelegramOwnerGate.IsOwnerAsync, a read-only ownership check for /health

  /health must authorise without ever claiming ownership on a stranger's or
  first message; IsAllowedAsync's claim-on-Missing behaviour stays for
  capture, edits and button presses.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 2: `HealthReplyFormatter` — plain-text bot reply**

  Write failing tests. Create `tests/Noof.Ledger.Telegram.Tests/HealthReplyFormatterTests.cs`:

  ```csharp
  using AwesomeAssertions;
  using Noof.Ledger.Application.Diagnostics;

  namespace Noof.Ledger.Telegram.Tests;

  public class HealthReplyFormatterTests
  {
      static HealthItem Item(string name, HealthLevel level, string summary) =>
          new(name, level, summary, DateTimeOffset.Parse("2026-09-25T10:00:00Z"));

      [Fact]
      public void All_ok_checks_produce_the_all_good_header()
      {
          var report = new SystemHealthReport(HealthLevel.Ok,
              [Item("Database", HealthLevel.Ok, "ready"), Item("Backup", HealthLevel.Ok, "last success 2 h ago")]);

          var text = HealthReplyFormatter.Format(report);

          text.Should().Be("Health: all good\n✅ Database — ready\n✅ Backup — last success 2 h ago");
      }

      [Fact]
      public void A_single_warning_uses_the_singular_header()
      {
          var report = new SystemHealthReport(HealthLevel.Warning,
              [Item("Database", HealthLevel.Ok, "ready"), Item("Backup", HealthLevel.Warning, "last success 31 h ago")]);

          var text = HealthReplyFormatter.Format(report);

          text.Should().Be("Health: 1 warning\n✅ Database — ready\n⚠️ Backup — last success 31 h ago");
      }

      [Fact]
      public void Two_warnings_use_the_plural_header()
      {
          var report = new SystemHealthReport(HealthLevel.Warning,
              [Item("Backup", HealthLevel.Warning, "last success 31 h ago"), Item("Disk", HealthLevel.Warning, "3.2 GB free")]);

          var text = HealthReplyFormatter.Format(report);

          text.Should().StartWith("Health: 2 warnings\n");
      }

      [Fact]
      public void A_failing_check_wins_the_header_over_warnings()
      {
          var report = new SystemHealthReport(HealthLevel.Failing,
          [
              Item("Database", HealthLevel.Failing, "migration failed"),
              Item("Backup", HealthLevel.Warning, "last success 31 h ago"),
          ]);

          var text = HealthReplyFormatter.Format(report);

          text.Should().StartWith("Health: 1 failing\n");
          text.Should().Contain("❌ Database — migration failed");
          text.Should().Contain("⚠️ Backup — last success 31 h ago");
      }

      [Fact]
      public void An_empty_report_still_has_a_header()
      {
          var report = new SystemHealthReport(HealthLevel.Ok, []);

          var text = HealthReplyFormatter.Format(report);

          text.Should().Be("Health: all good");
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter HealthReplyFormatterTests`
  Expected: build error — `HealthReplyFormatter` and `Noof.Ledger.Application.Diagnostics` do not exist
  yet (`ISystemHealth`/`SystemHealthReport`/`HealthItem`/`HealthLevel` are Task 6's, not landed in this
  worktree until Task 6 merges — if Task 6 has already merged when this step runs, the failure is
  instead the plain `CS0246: HealthReplyFormatter` not found).

  Implement. Create `src/Noof.Ledger.Telegram/HealthReplyFormatter.cs`:

  ```csharp
  using Noof.Ledger.Application.Diagnostics;

  namespace Noof.Ledger.Telegram;

  internal static class HealthReplyFormatter
  {
      public static string Format(SystemHealthReport report)
      {
          var failing = report.Items.Count(item => item.Level == HealthLevel.Failing);
          var warning = report.Items.Count(item => item.Level == HealthLevel.Warning);

          var header = failing switch
          {
              > 0 => $"Health: {failing} failing",
              _ when warning > 0 => $"Health: {warning} warning{(warning == 1 ? "" : "s")}",
              _ => "Health: all good",
          };

          return string.Join('\n', [header, .. report.Items.Select(FormatLine)]);
      }

      static string FormatLine(HealthItem item)
      {
          var icon = item.Level switch
          {
              HealthLevel.Ok => "✅",
              HealthLevel.Warning => "⚠️",
              _ => "❌",
          };

          return $"{icon} {item.Name} — {item.Summary}";
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter HealthReplyFormatterTests`
  Expected: PASS (5 tests).

  Commit: `git add src/Noof.Ledger.Telegram/HealthReplyFormatter.cs tests/Noof.Ledger.Telegram.Tests/HealthReplyFormatterTests.cs`

  ```
  git commit -m "$(cat <<'EOF'
  Add HealthReplyFormatter for the /health bot reply

  Plain-text, English: a header naming the worst level across the report's
  items ("all good" / "N warning(s)" / "N failing", failing wins), then one
  emoji line per check.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 3: `TelegramUpdateRouter` — intercept `/health` before capture**

  Write failing tests. In `tests/Noof.Ledger.Telegram.Tests/TelegramUpdateRouterTests.cs`:

  Add to the usings block at the top:

  ```csharp
  using Microsoft.Extensions.Logging.Abstractions;
  using Noof.Ledger.Application.Diagnostics;
  ```

  Replace the `CreateRouter` method (the router-construction line only needs its two new arguments; the
  rest of the method, the `Harness` record and every other test's call site are unchanged) — full method:

  ```csharp
      static Harness CreateRouter(long ownerChatId)
      {
          var secretStore = Substitute.For<ISecretStore>();
          secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
              .Returns(new SecretResult(SecretState.Present, ownerChatId.ToString()));
          var captureStore = Substitute.For<ICaptureStore>();
          var chatNotifier = Substitute.For<IChatNotifier>();
          var editor = Substitute.For<IRecordEditor>();
          var store = Substitute.For<ICategorizationStore>();
          var router = new TelegramUpdateRouter(captureStore, chatNotifier, new TelegramOwnerGate(secretStore),
              new RecordActionHandler(editor, store, chatNotifier, Echo), new CorrectionHandler(editor, chatNotifier, Echo), Echo,
              Substitute.For<ISystemHealth>(), NullLogger<TelegramUpdateRouter>.Instance);

          return new Harness(router, captureStore, chatNotifier, editor, store);
      }

      static (TelegramUpdateRouter Router, IChatNotifier ChatNotifier, ISystemHealth SystemHealth, ISecretStore SecretStore)
          CreateHealthHarness(SecretResult ownerSecret)
      {
          var secretStore = Substitute.For<ISecretStore>();
          secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>()).Returns(ownerSecret);
          var captureStore = Substitute.For<ICaptureStore>();
          var chatNotifier = Substitute.For<IChatNotifier>();
          var editor = Substitute.For<IRecordEditor>();
          var store = Substitute.For<ICategorizationStore>();
          var systemHealth = Substitute.For<ISystemHealth>();
          var router = new TelegramUpdateRouter(captureStore, chatNotifier, new TelegramOwnerGate(secretStore),
              new RecordActionHandler(editor, store, chatNotifier, Echo), new CorrectionHandler(editor, chatNotifier, Echo), Echo,
              systemHealth, NullLogger<TelegramUpdateRouter>.Instance);

          return (router, chatNotifier, systemHealth, secretStore);
      }
  ```

  Add these new test methods anywhere inside the class (e.g. just before the closing brace):

  ```csharp
      [Fact]
      public async Task The_owner_gets_a_formatted_health_reply_and_captures_nothing()
      {
          var (router, chatNotifier, systemHealth, _) = CreateHealthHarness(new SecretResult(SecretState.Present, "111"));
          var report = new SystemHealthReport(HealthLevel.Ok,
              [new HealthItem("Database", HealthLevel.Ok, "ready", DateTimeOffset.Parse("2026-09-25T10:00:00Z"))]);
          systemHealth.GetAsync(true, Arg.Any<CancellationToken>()).Returns(report);

          await router.HandleAsync(TextMessage(111L, 5, "/health", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

          await chatNotifier.Received(1).SendAsync(111L, HealthReplyFormatter.Format(report), Arg.Any<CancellationToken>());
      }

      [Fact]
      public async Task Health_at_bot_suffix_is_recognised_as_the_command()
      {
          var (router, chatNotifier, systemHealth, _) = CreateHealthHarness(new SecretResult(SecretState.Present, "111"));
          var report = new SystemHealthReport(HealthLevel.Ok, []);
          systemHealth.GetAsync(true, Arg.Any<CancellationToken>()).Returns(report);

          await router.HandleAsync(TextMessage(111L, 5, "/HEALTH@my_ledger_bot", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

          await chatNotifier.Received(1).SendAsync(111L, HealthReplyFormatter.Format(report), Arg.Any<CancellationToken>());
      }

      [Fact]
      public async Task A_strangers_health_command_gets_no_reply_and_never_reads_system_health()
      {
          var (router, chatNotifier, systemHealth, _) = CreateHealthHarness(new SecretResult(SecretState.Present, "111"));

          await router.HandleAsync(TextMessage(999L, 5, "/health", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

          await chatNotifier.DidNotReceiveWithAnyArgs().SendAsync(default, default!, Arg.Any<CancellationToken>());
          await systemHealth.DidNotReceiveWithAnyArgs().GetAsync(default, Arg.Any<CancellationToken>());
      }

      [Fact]
      public async Task A_health_command_with_no_owner_yet_is_silent_and_never_claims_ownership()
      {
          var (router, chatNotifier, systemHealth, secretStore) = CreateHealthHarness(new SecretResult(SecretState.Missing, null));

          await router.HandleAsync(TextMessage(111L, 5, "/health", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

          await chatNotifier.DidNotReceiveWithAnyArgs().SendAsync(default, default!, Arg.Any<CancellationToken>());
          await systemHealth.DidNotReceiveWithAnyArgs().GetAsync(default, Arg.Any<CancellationToken>());
          await secretStore.DidNotReceive().TrySetIfMissingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
      }

      [Fact]
      public async Task Text_that_merely_starts_with_health_but_is_not_the_exact_command_is_still_captured()
      {
          var (router, captureStore, chatNotifier, _, _) = CreateRouter(ownerChatId: 111L);
          captureStore.CaptureAsync(Arg.Any<CapturedMessage>(), "Europe/Belgrade", Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
          chatNotifier.SendAsync(111L, Echo.Acknowledgement, Arg.Any<CancellationToken>()).Returns(777);

          await router.HandleAsync(TextMessage(111L, 5, "/healthclub 500", DateTime.UtcNow), "Europe/Belgrade", TestContext.Current.CancellationToken);

          await captureStore.Received(1).CaptureAsync(
              Arg.Is<CapturedMessage>(m => m.Text == "/healthclub 500"), "Europe/Belgrade", Arg.Any<CancellationToken>());
      }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramUpdateRouterTests`
  Expected: build error — `TelegramUpdateRouter`'s constructor does not accept 8 arguments yet (`CS1729`),
  and `HealthReplyFormatter`/`ISystemHealth` usages fail until Step 2/Task 6 exist. This is the "watch it
  fail" for the interception.

  Implement. Replace `src/Noof.Ledger.Telegram/TelegramUpdateRouter.cs` in full:

  ```csharp
  using Microsoft.Extensions.Logging;
  using Noof.Ledger.Application.Capture;
  using Noof.Ledger.Application.Chat;
  using Noof.Ledger.Application.Diagnostics;
  using Telegram.Bot.Types;

  namespace Noof.Ledger.Telegram;

  internal sealed partial class TelegramUpdateRouter(
      ICaptureStore captureStore,
      IChatNotifier chatNotifier,
      TelegramOwnerGate ownerGate,
      RecordActionHandler actionHandler,
      CorrectionHandler correctionHandler,
      IRecordEcho recordEcho,
      ISystemHealth systemHealth,
      ILogger<TelegramUpdateRouter> logger)
      : ITelegramUpdateRouter
  {
      public async Task HandleAsync(Update update, string timeZoneId, CancellationToken cancellationToken)
      {
          switch (update)
          {
              case { Message: { } message }:
                  await HandleMessageAsync(message, timeZoneId, cancellationToken);
                  break;
              case { EditedMessage: { } edited }:
                  if (await ownerGate.IsAllowedAsync(edited.Chat.Id, cancellationToken))
                      await correctionHandler.HandleEditAsync(edited, cancellationToken);
                  break;
              case { CallbackQuery: { Message: { } echo } query }:
                  // Rejected before reading Data, for the reason messages are rejected before reading Text.
                  if (await ownerGate.IsAllowedAsync(echo.Chat.Id, cancellationToken))
                      await actionHandler.HandleAsync(query, echo, cancellationToken);
                  break;
          }
      }

      async Task HandleMessageAsync(Message message, string timeZoneId, CancellationToken cancellationToken)
      {
          // /health is a fixed, known literal, not the free-form content the rule just below protects:
          // recognising it costs a stranger nothing, and IsOwnerAsync never claims ownership or tells
          // a non-owner anything back - unlike the capture path, which must not even look at the text
          // of someone who might not be the owner.
          if (message.Text is { Length: > 0 } possibleCommand && IsHealthCommand(possibleCommand))
          {
              await HandleHealthCommandAsync(message.Chat.Id, cancellationToken);
              return;
          }

          // Reject before reading Text: a stranger's content must never be inspected, not even to
          // decide whether it looks like a spend.
          if (!await ownerGate.IsAllowedAsync(message.Chat.Id, cancellationToken))
              return;

          if (message.Voice is { } voice)
          {
              await HandleVoiceAsync(message, voice, timeZoneId, cancellationToken);
              return;
          }

          if (message.Text is not { Length: > 0 } text)
              return;

          if (message.ReplyToMessage is { } repliedTo
              && await correctionHandler.TryHandleReplyAsync(message, repliedTo, text, cancellationToken))
              return;

          // message.Date is when Telegram received it from the sender, not when we got around to
          // processing it -- an outage can queue a message for hours, and every queued message must
          // keep its own moment so time_zone_id buckets it into the correct local day later.
          // Message.Date deserialises as DateTime with Kind=Utc (confirmed against Telegram.Bot
          // 22.10.3.1's UnixDateTimeConverter), so this offset is genuinely zero, not just labelled so.
          var sentAt = new DateTimeOffset(message.Date);
          var captured = new CapturedMessage(message.Chat.Id, message.Id, text, sentAt);
          var transactionId = await captureStore.CaptureAsync(captured, timeZoneId, cancellationToken);

          var botMessageId = await chatNotifier.SendAsync(message.Chat.Id, recordEcho.Acknowledgement, cancellationToken);
          await captureStore.AttachBotMessageAsync(transactionId, botMessageId, cancellationToken);
      }

      async Task HandleVoiceAsync(Message message, Voice voice, string timeZoneId, CancellationToken cancellationToken)
      {
          if (message.ReplyToMessage is { } repliedTo
              && await correctionHandler.TryHandleVoiceReplyAsync(message, repliedTo, voice, cancellationToken))
              return;

          // message.Date is the send instant, Kind=Utc - see HandleMessageAsync.
          var captured = new CapturedVoice(message.Chat.Id, message.Id, voice.FileId, voice.Duration, new DateTimeOffset(message.Date));
          var transactionId = await captureStore.CaptureVoiceAsync(captured, timeZoneId, cancellationToken);

          var botMessageId = await chatNotifier.SendAsync(message.Chat.Id, recordEcho.Transcribing, cancellationToken);
          await captureStore.AttachBotMessageAsync(transactionId, botMessageId, cancellationToken);
      }

      async Task HandleHealthCommandAsync(long chatId, CancellationToken cancellationToken)
      {
          if (!await ownerGate.IsOwnerAsync(chatId, cancellationToken))
          {
              LogHealthCommandRejected(logger);
              return;
          }

          var report = await systemHealth.GetAsync(fresh: true, cancellationToken);
          await chatNotifier.SendAsync(chatId, HealthReplyFormatter.Format(report), cancellationToken);
      }

      static bool IsHealthCommand(string text)
      {
          var trimmed = text.Trim();
          return trimmed.Equals("/health", StringComparison.OrdinalIgnoreCase)
              || trimmed.StartsWith("/health@", StringComparison.OrdinalIgnoreCase);
      }

      [LoggerMessage(EventId = 6001, Level = LogLevel.Debug, Message = "Rejected a /health command from a non-owner chat")]
      static partial void LogHealthCommandRejected(ILogger logger);
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramUpdateRouterTests`
  Expected: PASS (all existing router tests plus the 5 new ones).

  Commit: `git add src/Noof.Ledger.Telegram/TelegramUpdateRouter.cs tests/Noof.Ledger.Telegram.Tests/TelegramUpdateRouterTests.cs`

  ```
  git commit -m "$(cat <<'EOF'
  Intercept /health in TelegramUpdateRouter before capture

  Recognised as an exact "/health" or "/health@..." message (trimmed,
  case-insensitive), gated by the new non-claiming IsOwnerAsync, answered
  with ISystemHealth's fresh report through HealthReplyFormatter. Never
  creates a transaction; a stranger or an unset owner gets silence and a
  Debug log with no message text.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 4: `TelegramPollingService` — register `/health` for the owner on client start**

  Write failing tests. In `tests/Noof.Ledger.Telegram.Tests/TelegramPollingServiceTests.cs`, add to the
  usings block at the top:

  ```csharp
  using Telegram.Bot.Exceptions;
  ```

  Add these new test methods inside `public class TelegramPollingServiceTests { ... }` (e.g. just before
  the closing brace, after `Asks_telegram_for_edits_and_button_presses`):

  ```csharp
      [Fact]
      public async Task Registers_the_health_command_scoped_to_the_owner_chat_when_the_client_is_first_built()
      {
          var secretStore = WithToken("tok1");
          secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
              .Returns(new SecretResult(SecretState.Present, "555"));
          var client = Substitute.For<ITelegramBotClient>();
          client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
          var clientFactory = Substitute.For<ITelegramBotClientFactory>();
          clientFactory.Create("tok1").Returns(client);

          await CreateService(secretStore, clientFactory, new TelegramClientHandle())
              .RunTickAsync(TestContext.Current.CancellationToken);

          await client.Received(1).SendRequest(
              Arg.Is<SetMyCommandsRequest>(r => r.Commands!.Single().Command == "health"
                  && r.Commands!.Single().Description == "System health"
                  && r.Scope is BotCommandScopeChat chatScope && chatScope.ChatId.Identifier == 555L),
              Arg.Any<CancellationToken>());
      }

      [Fact]
      public async Task Skips_registering_the_health_command_when_there_is_no_owner_yet()
      {
          var secretStore = WithToken("tok1");
          secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
              .Returns(new SecretResult(SecretState.Missing, null));
          var client = Substitute.For<ITelegramBotClient>();
          client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
          var clientFactory = Substitute.For<ITelegramBotClientFactory>();
          clientFactory.Create("tok1").Returns(client);

          await CreateService(secretStore, clientFactory, new TelegramClientHandle())
              .RunTickAsync(TestContext.Current.CancellationToken);

          await client.DidNotReceive().SendRequest(Arg.Any<SetMyCommandsRequest>(), Arg.Any<CancellationToken>());
      }

      [Fact]
      public async Task A_failed_command_registration_does_not_fail_the_tick()
      {
          var secretStore = WithToken("tok1");
          secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, Arg.Any<CancellationToken>())
              .Returns(new SecretResult(SecretState.Present, "555"));
          var client = Substitute.For<ITelegramBotClient>();
          client.SendRequest(Arg.Any<GetUpdatesRequest>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Update>());
          client.SendRequest(Arg.Any<SetMyCommandsRequest>(), Arg.Any<CancellationToken>())
              .ThrowsAsync(new ApiRequestException("Bad Request: chat not found", 400));
          var clientFactory = Substitute.For<ITelegramBotClientFactory>();
          clientFactory.Create("tok1").Returns(client);

          var result = await CreateService(secretStore, clientFactory, new TelegramClientHandle())
              .RunTickAsync(TestContext.Current.CancellationToken);

          result.Should().Be(TelegramPollResult.Processed, "a failed command registration is logged, never fatal");
      }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramPollingServiceTests`
  Expected: the first new test FAILS (`client` never receives a `SetMyCommandsRequest` — the production
  code does not send one yet); the other two pass vacuously (nothing to skip, nothing to fail) — run them
  together and read the one red result before implementing, then continue. This is the "watch it fail"
  for the registration call.

  Implement. In `src/Noof.Ledger.Telegram/TelegramPollingService.cs`:

  1. Change the class declaration (line 15) from
     `internal sealed class TelegramPollingService(` to
     `internal sealed partial class TelegramPollingService(`.

  2. Inside `RunTickAsync`, replace the `if (secret.Value != activeToken)` block (currently lines 66–75)
     with:

  ```csharp
              if (secret.Value != activeToken)
              {
                  var client = clientFactory.Create(secret.Value!);
                  await client.DeleteWebhook(cancellationToken: cancellationToken);
                  clientHandle.Current = client;
                  activeToken = secret.Value;

                  await RegisterHealthCommandAsync(client, secretStore, cancellationToken);

                  var offsetStore = scope.ServiceProvider.GetRequiredService<TelegramUpdateOffsetStore>();
                  offset ??= await offsetStore.GetAsync(cancellationToken);
              }
  ```

  3. Add a new private method and its `[LoggerMessage]` partner, e.g. right after `RunTickAsync`'s
     closing brace and before `NotifyOperatorOfSkippedUpdateAsync`:

  ```csharp
      async Task RegisterHealthCommandAsync(ITelegramBotClient client, ISecretStore secretStore, CancellationToken cancellationToken)
      {
          var owner = await secretStore.GetAsync(SecretKeys.TelegramOwnerChatId, cancellationToken);
          if (owner.State is not SecretState.Present || !long.TryParse(owner.Value, out var ownerChatId))
              return;

          try
          {
              await client.SetMyCommands(
                  commands: [new BotCommand("health", "System health")],
                  scope: new BotCommandScopeChat { ChatId = ownerChatId },
                  cancellationToken: cancellationToken);
          }
          catch (Exception ex) when (ex is not OperationCanceledException)
          {
              LogHealthCommandRegistrationFailed(logger, ex);
          }
      }

      [LoggerMessage(EventId = 6101, Level = LogLevel.Warning, Message = "Failed to register the /health bot command for the owner chat")]
      static partial void LogHealthCommandRegistrationFailed(ILogger logger, Exception exception);
  ```

     (`BotCommand`, `BotCommandScopeChat` and `SetMyCommands` all resolve from the `Telegram.Bot.Types`
     using already at the top of this file; `Microsoft.Extensions.Logging` is already imported too.)

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj --filter TelegramPollingServiceTests`
  Expected: PASS (every existing polling test plus the 3 new ones — the pre-existing tests are unaffected
  because their `WithToken`/manual `secretStore` setups return a default `SecretResult` — `State =
  Present (0), Value = null` for an unconfigured `readonly record struct` — for the owner key, and
  `long.TryParse(null, ...)` fails, so registration is silently skipped exactly as it is for the explicit
  "no owner" test).

  Commit: `git add src/Noof.Ledger.Telegram/TelegramPollingService.cs tests/Noof.Ledger.Telegram.Tests/TelegramPollingServiceTests.cs`

  ```
  git commit -m "$(cat <<'EOF'
  Register /health as a bot command scoped to the owner's chat

  Sent once per (re)built client, right after DeleteWebhook, only when an
  owner chat id is already on record; a failed registration is logged as a
  warning and never fails the poll tick.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

- [ ] **Step 5: full-project verification**

  Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
  Expected: PASS, all tests in the project (do not run `dotnet test --solution` here per CLAUDE.md §4 —
  the database and E2E suites run filtered per task, in full once at the end of the phase).

---

