# Phase 4: Money Model and a Restored Backup, Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every wallet shows a balance that matches the bank — opening balance, minus spending, plus income, re-anchored by the operator's own balance statements — exact in all five currencies under `ru-RU` and `sr-Latn-RS`; and the app backs its database up daily, with a restore that has been checked.

**Architecture:** Transactions gain a kind (`Expense`, `Income`, `BalanceCheck`) and own signed **entries** (double-entry-lite). A balance statement is a **checkpoint** (`balance_checks`); a wallet's balance per currency is the latest checkpoint plus the entries after it, ordered by `(occurred_on, occurred_at)`, computed by a SQL view (`wallet_balances`) over `numeric(19,4)`. The model's answer tool becomes `record_transaction` (kind, wallet, stated balance); C# maps it, resolves the wallet (named → default for the currency → default for RSD), writes lines, entries and checkpoints in one database transaction, and the echo and dashboard read balances back from the view. Separately, a `BackupWorker` runs `pg_dump` daily into `%LOCALAPPDATA%\NoofLedger\backups`, keeps 14, logs every run to `backup_runs`, and `ops/restore-check.ps1` proves a dump restores to the same balances.

**Tech Stack:** .NET 10 · C# · EF Core 10 + Npgsql · PostgreSQL 18 (`pg_dump`/`pg_restore` 18) · Microsoft.Extensions.AI (forced strict tool call) · Telegram.Bot 22.10.3.1 · Blazor Server + MudBlazor · xUnit v3 on Microsoft Testing Platform · AwesomeAssertions · NSubstitute · Playwright

**Spec:** `docs/superpowers/specs/2026-09-24-money-model.md` (decisions M1–M13, B1–B5). Amends `docs/superpowers/specs/2026-09-19-noof-finance-design.md` §6. Predecessor plan: `docs/superpowers/plans/2026-09-24-phase3-voice-capture.md`.

> ### ⚠ Decisions in this plan that are expensive to reverse. Read these before Task 1.
>
> Every one migrates `noof_ledger` the next time the operator starts the host. The spec fixes the model (M4–M7, M13, B3); this plan fixes the names and two shapes the spec leaves open:
>
> 1. **`transactions.wallet_id` becomes nullable.** The wallet is chosen when the record is *read* (the model names it, or the currency's default is used — M3), not when it is captured; a capture no longer needs a wallet, and "no default wallet" stops being a capture-time failure. Every existing row keeps its wallet.
> 2. **`transactions.kind int NOT NULL DEFAULT 0`** (`Expense = 0`, `Income = 1`, `BalanceCheck = 2`; `3` reserved for Phase 7 `Transfer`, not declared). `capture_kind` gains `Manual = 2`. `telegram_chat_id`/`telegram_message_id` become nullable, with check constraint `ck_transactions_telegram_ids_match_capture_kind`.
> 3. **`wallets.is_default` is renamed `is_default_for_currency`**, the single-default index is replaced by `ix_wallets_one_default_per_currency ON wallets (currency) WHERE is_default_for_currency`; `aliases text[] NOT NULL DEFAULT '{}'`, `archived boolean NOT NULL DEFAULT false`, `created_at timestamptz NOT NULL DEFAULT now()` are added. The seeded *Main Wallet* (`00000000-0000-0000-0000-000000000001`, RSD) stays the RSD default.
> 4. **`entries`** (`id`, `transaction_id` → transactions CASCADE, `wallet_id` → wallets RESTRICT, `amount numeric(19,4)`, `currency varchar(3)`, `role int`), **`balance_checks`** (`transaction_id` PK → transactions CASCADE, `wallet_id` → wallets RESTRICT, `stated_amount numeric(19,4)`, `currency varchar(3)`, `computed_before numeric(19,4)`), **`backup_runs`** (`id`, `started_at`, `finished_at`, `succeeded`, `file_name`, `size_bytes`, `error`), and the **`wallet_balances` view**. The migration backfills one entry per currency for every existing expense that has line items, whatever its status (`-SUM(line_items.amount)`) — a cancelled record keeps its lines and Restore only flips its status, so its entry must already exist; the view counts `status = 1` only.
> 5. **The stated balance travels flat on the wire**: `record_transaction` has root properties `balance_amount` (`number | null`) and `balance_currency` (enum `| null`) rather than a nested nullable object — the spec's `balance: {amount, currency}` expressed within the strict-schema subset the answer tool already relies on.

## Global Constraints

- **Money is `decimal` + `CurrencyCode`**, never `double`/`float`. Amounts from the model are JSON numbers read straight into `decimal` from the argument's `JsonElement` (CLAUDE.md §4 *The model*). Sums happen in SQL `numeric(19,4)` or C# `decimal`. **Balances are derived, never stored** — no column anywhere holds a running balance (M5/M6); `balance_checks.computed_before` is history for the echo, not a balance anything reads back as current.
- **Capture has no validation layer (P2-1).** The mapper maps (an offered slug, an offered merchant id, an offered wallet id, a supported currency, an ISO date, a known kind) and fails the job only when something does not map. No plausibility checks, no bounds, no "does this income look like income".
- **Bot text is English (P2-5).** Every echo string in Task 8 is exact; amounts in the echo are formatted `amount.ToString("0.00", CultureInfo.InvariantCulture)` like every existing echo amount (so the spec's illustrative `45 230.00` renders as `45230.00`). Dashboard amounts keep `N2` + `InvariantCulture`.
- **`JobKind` and `TransactionKind` are different things.** `CategorizationOutcome.Kind` stays the `JobKind` it is today; the transaction's kind travels as `CategorizationOutcome.TransactionKind`. `TransactionRevision.Kind` stays `RevisionKind`; the snapshot JSON gains `"kind"` and `"wallet_id"` (Task 1) and `"stated_balance"` (Task 6). Never rename one to the other.
- **Only `src/Noof.Ledger.Ai/<Provider>/` knows a provider** (D-A, `AiBoundaryTests`). Nothing in this phase names Anthropic or Groq outside `Noof.Ledger.Ai`.
- **Public services go through an interface** registered by the assembly's own `AddNoofXxx` (CLAUDE.md §3). Every new public type is added to `PublicSurfaceTests.Allowed` in the task that creates it; parallel tasks conflict on that array — resolve by keeping every name from both sides.
- **`Noof.Ledger.Web` is UI only**: it may reference Application and Domain types, never Persistence/EF (`ProjectReferenceTests`). No popover, dialog, snackbar, tooltip or menu anywhere — feedback is an inline `MudAlert` (CLAUDE.md §4 *Render modes*). Every routable page carries `[Authorize]`.
- **External processes** (`pg_dump`, `pg_restore`, `psql`) get the password through `ProcessStartInfo.Environment["PGPASSWORD"]` only — never `ArgumentList`, a log line, an exception message or a `backup_runs.error` (B2). `UseShellExecute = false`, `ArgumentList` (never a concatenated command line), stdout/stderr redirected.
- **`noof_ledger` holds real credentials.** Never run a test, a script, a manual check, `dotnet ef database update` or the published host against it. `dotnet ef database update` without `--connection` resolves `noof_ledger` — always pass `--connection` naming `noof_ledger_test_template`. `ops/restore-check.ps1` is run in this phase only against a test-template clone; its one real run is the operator's (B5).
- **Never call a live model or a live speech service.** `NOOF_LEDGER_LIVE_*` stay unset.
- **Migrations:** after `dotnet ef migrations add`, convert only the new migration `.cs` to a file-scoped namespace (`IDE0161` fails the build otherwise); leave `.Designer.cs` and `LedgerDbContextModelSnapshot.cs` as generated. Never edit an applied migration. Never call `EnsureCreated()`. Seed rows the operator owns go in raw SQL with fixed ids and `ON CONFLICT (id) DO NOTHING` (the `AddCaptureModel` idiom), never `HasData`. Regenerate `tests/Noof.Ledger.Persistence.Tests/schema.expected.sql` with `dotnet ef dbcontext script` (connection-free). Update `noof_ledger_test_template` after each migration (command below), holding the suite lock. **Only one task per wave adds a migration** (Task 1 in wave 1, Task 3 in wave 2) — parallel migrations collide in the model snapshot.
- **Tests:** TDD — a failing test first, and watch every new guard fail once (break it deliberately, see the failure name it, revert). Real PostgreSQL clones, never the InMemory provider. Seed timestamps from fixed literals; never compare `DateTimeOffset.UtcNow` exactly against a value read back from PostgreSQL. Culture-sensitive assertions run under the culture explicitly set by the test.
- **Test commands.** `dotnet test --project <csproj>` or `dotnet test --solution NoofLedger.slnx`, `--filter <ClassName>` to narrow. The full run includes Playwright E2E (needs Chromium and PostgreSQL). **Serialise full-suite runs, E2E runs and template updates across worktrees** with the lock directory `C:\Users\noofs\AppData\Local\Temp\noof-suite.lock` (`mkdir` fails if it exists — wait and retry; `rmdir` when done, including on failure).
- **Modern C#** (file-scoped namespaces, primary constructors, records, collection expressions, pattern matching, `is null`); comments only explain *why*; no XML doc blocks except the ones EF's migration scaffold emits.
- **Commit trailer.** Every commit message ends with exactly these two lines (a commit written by another model may name that model instead — accepted in Phase 3):
  ```
  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  ```

### Updating the test template (Task 1 and Task 3 only)

```powershell
$admin = if ($env:NOOF_TEST_PG) { $env:NOOF_TEST_PG } else { (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection").Trim() }
$template = $admin -replace 'Database=postgres', 'Database=noof_ledger_test_template'
if ($template -notmatch 'Database=noof_ledger_test_template') { throw "Refusing: the connection string does not name the test template." }
dotnet ef database update --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --connection $template
```

The output's last line must name the new migration. Never run it without `--connection`.


## Interface contract (binding for every task)

Every name, signature, table, column and string below is fixed. A task that produces one of these writes it exactly so; a task that consumes one uses it exactly so. If a task finds the contract wrong against the code, it stops and reports, it does not improvise.

### Domain (`src/Noof.Ledger.Domain`, Task 1)

```csharp
public enum TransactionKind { Expense = 0, Income = 1, BalanceCheck = 2 }   // 3 is reserved for Phase 7's Transfer; not declared
public enum CaptureKind { Text = 0, Voice = 1, Manual = 2 }                  // Manual = made on the dashboard, no Telegram message
public enum EntryRole { Principal = 0 }                                       // Fee is reserved for Phase 7; not declared

public sealed class Wallet
{
    public required Guid Id { get; init; }
    public required string Name { get; set; }
    public required CurrencyCode Currency { get; init; }
    public string[] Aliases { get; set; } = [];          // text[]
    public bool IsDefaultForCurrency { get; set; }        // replaces IsDefault; at most one per currency (partial unique index)
    public bool Archived { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}

// Transaction: changed members only
public Guid? WalletId { get; set; }                       // was `required Guid ... init`
public TransactionKind Kind { get; set; }                 // default Expense
public long? TelegramChatId { get; init; }                // was `required long`
public int? TelegramMessageId { get; init; }              // was `required int`

public sealed class Entry
{
    public required Guid Id { get; init; }
    public required Guid TransactionId { get; init; }
    public required Guid WalletId { get; init; }
    public required Money Amount { get; init; }           // signed: an expense is negative, an income positive
    public required EntryRole Role { get; init; }
}

public sealed class BalanceCheck
{
    public required Guid TransactionId { get; init; }     // PK and FK: one checkpoint per BalanceCheck transaction
    public required Guid WalletId { get; set; }
    public required Money Stated { get; set; }            // stated_amount + currency
    public required decimal ComputedBefore { get; set; }  // what the app computed just before, same currency; history only
}
```

### Persistence internals (Task 1, Task 3)

- `LedgerDbContext` gains `DbSet<Entry> Entries`, `DbSet<BalanceCheck> BalanceChecks`, `DbSet<BackupRun> BackupRuns` (`BackupRun` is `internal sealed class` in `Noof.Ledger.Persistence.Backup`: `Guid Id`, `DateTimeOffset StartedAt`, `DateTimeOffset FinishedAt`, `bool Succeeded`, `string? FileName`, `long? SizeBytes`, `string? Error`; table `backup_runs`, columns snake_case).
- Tables/columns exactly as the "expensive to reverse" block above. Index `ix_entries_wallet_id` on `entries(wallet_id)`. Migration names: **`AddMoneyModel`** (Task 1), **`AddWalletBalancesView`** (Task 3).
- **Backfill (Task 1, `AddMoneyModel`)** — one entry per (transaction, currency) for every existing expense that has line items, **whatever its status**, `amount = -SUM(line_items.amount)`, `wallet_id` = the transaction's wallet, `role = 0`. A cancelled record keeps its lines and Restore only flips `status`, so its entry must already exist; the view counts `status = 1` only.
- **`ck_transactions_capture_has_content`** (Task 1) becomes `(capture_kind IN (0, 2) AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL)`; a Manual record needs `raw_text`. **`ck_transactions_telegram_ids_match_capture_kind`**: `capture_kind = 2` has both Telegram ids NULL, every other capture kind has both NOT NULL.
- **Revision snapshot JSON** — every existing key is unchanged; it gains `"kind"` (the `TransactionKind` enum name) and `"wallet_id"` (GUID string or null) — Task 1 — and `"stated_balance"` (`{"amount": "<decimal string>", "currency": "CUR"}` or null) — Task 6.
- **View `wallet_balances(wallet_id uuid, currency varchar(3), balance numeric(19,4), checked_on date NULL)`** (Task 3) — one row per (wallet, currency) that has a completed checkpoint or a completed entry. Only transactions with `status = 1` (Completed) count. `balance` = the stated amount of the latest checkpoint for that (wallet, currency) — latest by `(occurred_on, occurred_at)` of its transaction — plus the sum of entries whose transaction's `(occurred_on, occurred_at)` is strictly after the checkpoint's; with no checkpoint, the sum of all entries. `checked_on` = the latest checkpoint's `occurred_on`, or NULL.
- `internal static class BalanceSql` (Task 3, `Noof.Ledger.Persistence.Balances`): `public static Task<decimal> AsOfAsync(LedgerDbContext db, Guid walletId, CurrencyCode currency, DateOnly occurredOn, DateTimeOffset occurredAt, Guid excludingTransactionId, CancellationToken ct)` — the same rule as the view, restricted to transactions whose `(occurred_on, occurred_at) <= (occurredOn, occurredAt)` and excluding `excludingTransactionId`. Used by `ApplyAsync` (Task 6) to fill `ComputedBefore`.

### Application (`src/Noof.Ledger.Application`)

**Categorization contract** (`Categorization/CategorizationContract.cs`) — additions are trailing optional parameters so every positional call site keeps compiling:

```csharp
// Task 2
public sealed record WalletOption(Guid Id, string Name, CurrencyCode Currency, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency);

public sealed record CategorizationRequest(
    string RawText, DateOnly Today, IReadOnlyList<CategoryOption> Categories,
    IReadOnlyList<MerchantOption> MerchantHints, IReadOnlyList<MerchantOption> AllMerchants,
    CorrectionRequest? Correction = null,
    IReadOnlyList<WalletOption>? Wallets = null);            // null means none offered

public static class ProposedKind { public const string Expense = "expense"; public const string Income = "income"; public const string Balance = "balance"; }

public sealed record CategorizationProposal(
    IReadOnlyList<ProposedLineItem> Items, string? OccurredOn = null,
    string Kind = ProposedKind.Expense, Guid? WalletId = null,
    decimal? BalanceAmount = null, string? BalanceCurrency = null);

// Task 4
public sealed record MappedProposal(
    IReadOnlyList<ResolvedLineItem> Items, DateOnly? OccurredOn,
    TransactionKind Kind = TransactionKind.Expense, Guid WalletId = default, Money? StatedBalance = null);

public sealed record CategorizationOutcome(
    IReadOnlyList<CategorizedLineItem> Items, DateOnly OccurredOn,
    JobKind Kind = JobKind.Categorize, string? Instruction = null,
    TransactionKind TransactionKind = TransactionKind.Expense, Guid? WalletId = null, Money? StatedBalance = null);

// Task 8
public sealed record BalanceStatement(Money Stated, decimal ComputedBefore);

public sealed record CategorizationSubject(
    Guid TransactionId, string RawText, long TelegramChatId, int? BotMessageId, string WalletName,
    TransactionStatus Status, DateOnly SentOn, DateOnly OccurredOn, IReadOnlyList<RecordedLine> Lines,
    CaptureKind CaptureKind = CaptureKind.Text,
    TransactionKind Kind = TransactionKind.Expense,         // Task 8
    CurrencyCode? WalletCurrency = null,                     // Task 8
    IReadOnlyList<Money>? WalletBalances = null,             // Task 8: current balances of the record's wallet, per currency
    BalanceStatement? Statement = null,                      // Task 8: set for a BalanceCheck
    Guid? WalletId = null);                                  // Task 4 adds it last; Task 8 inserts its four parameters BEFORE it
```

Parameter order is binding: `WalletId` is last. Task 4 (wave 2) appends `Guid? WalletId = null` after `CaptureKind` and fills it from `transactions.wallet_id` in `GetSubjectAsync` (`WalletId: header.WalletId`); Task 8 (wave 4) inserts `Kind`, `WalletCurrency`, `WalletBalances`, `Statement` between `CaptureKind` and `WalletId` and keeps `WalletId: header.WalletId` in its rewrite. A `Correct` job keeps the record's wallet (`WalletId`) unless the correction names another.

`CategorizationSubject.TelegramChatId` stays `long` (a subject is only ever built for a Telegram capture; `GetSubjectAsync` maps a null chat id to `0`) and `WalletName` stays `string` (a transaction with no wallet yet maps to `""`) — Task 1 makes those two mappings.

**Mapper** (`Categorization/IProposalMapper.cs`, Task 4):

```csharp
bool TryMap(
    CategorizationProposal proposal,
    IReadOnlyCollection<string> offeredSlugs,
    IReadOnlyCollection<Guid> offeredMerchantIds,
    IReadOnlyList<WalletOption> wallets,
    string defaultCurrency,
    out MappedProposal mapped,
    out string failure);
```

Wallet resolution, in order: a `WalletId` the proposal names must be one of `wallets` (else failure `"wallet <id> was not offered"`); with none named, the currency is the first line's stated currency, else `BalanceCurrency`, else `defaultCurrency` — except for `kind = balance`, whose lines are never consulted: the currency is `BalanceCurrency`, else `defaultCurrency` — and the wallet is the one in `wallets` with `IsDefaultForCurrency` for that currency, else the one with `IsDefaultForCurrency` for `defaultCurrency`; none at all → failure `"no wallet to record into"`. A line whose currency is null takes the resolved wallet's currency. Kind: `"expense"` → `Expense`, `"income"` → `Income`, `"balance"` → `BalanceCheck`, anything else → failure. For `BalanceCheck`: `BalanceAmount` null → failure `"a balance statement with no amount"`; `StatedBalance = (BalanceAmount, BalanceCurrency ?? wallet currency)`; its items are ignored and `Items` is empty.

**Wallets** (`Wallets/`, namespace `Noof.Ledger.Application.Wallets`):

```csharp
// Task 4 (not Task 1: it returns WalletOption, which Task 2 declares in the same wave as Task 1)
public interface IWalletDirectory { Task<IReadOnlyList<WalletOption>> ActiveAsync(CancellationToken cancellationToken); }   // not archived, ordered by name

// Task 5
public sealed record WalletDetails(Guid Id, string Name, CurrencyCode Currency, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency, bool Archived);
public sealed record NewWallet(string Name, CurrencyCode Currency, decimal OpeningBalance, DateOnly OpeningDate, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency);
public interface IWalletAdmin
{
    Task<IReadOnlyList<WalletDetails>> ListAsync(CancellationToken cancellationToken);           // every wallet, archived last, then by name
    Task<Guid> CreateAsync(NewWallet wallet, CancellationToken cancellationToken);               // + opening checkpoint (M7)
    Task RenameAsync(Guid walletId, string name, CancellationToken cancellationToken);
    Task SetAliasesAsync(Guid walletId, IReadOnlyList<string> aliases, CancellationToken cancellationToken);
    Task MakeDefaultForCurrencyAsync(Guid walletId, CancellationToken cancellationToken);        // clears the previous default of that currency, same DB transaction
    Task ArchiveAsync(Guid walletId, CancellationToken cancellationToken);                       // also clears IsDefaultForCurrency
}
```

The opening checkpoint `CreateAsync` writes: a `Transaction` with `Kind = BalanceCheck`, `CaptureKind = Manual`, `RawText = "Opening balance"`, `Status = Completed`, no Telegram ids, `WalletId` = the new wallet, `TimeZoneId` = the capture zone's id, `OccurredOn = OpeningDate`, `OccurredAt` = local midnight of `OpeningDate` in that zone (so the checkpoint precedes everything that day); a `BalanceCheck` with `Stated = (OpeningBalance, Currency)`, `ComputedBefore = 0`; a revision `RevisionKind.Initial`. All in one DB transaction.

**Balances** (`Reporting/IBalanceReadModel.cs`, Task 3):

```csharp
public sealed record WalletBalance(Guid WalletId, string WalletName, CurrencyCode WalletCurrency, bool Archived, IReadOnlyList<Money> Balances, DateOnly? LastCheckedOn);
public interface IBalanceReadModel
{
    Task<IReadOnlyList<WalletBalance>> BalancesAsync(CancellationToken cancellationToken);      // every wallet (empty Balances when nothing counted yet); wallet currency first, then ordinal
    Task<IReadOnlyList<Money>> BalanceOfAsync(Guid walletId, CancellationToken cancellationToken);  // same ordering
}
```

**Backup** (`Backup/`, namespace `Noof.Ledger.Application.Backup`, Task 12):

```csharp
public sealed record BackupRunRecord(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool Succeeded, string? FileName, long? SizeBytes, string? Error);
public sealed record BackupStatus(DateTimeOffset? LastSuccessAt, bool LastRunFailed, string? LastError);
public interface IBackupLog
{
    Task RecordAsync(BackupRunRecord run, CancellationToken cancellationToken);
    Task<BackupStatus> StatusAsync(CancellationToken cancellationToken);
}
public sealed record DumpResult(bool Succeeded, string? Error);
public interface IDatabaseDumper { Task<DumpResult> DumpAsync(string targetPath, CancellationToken cancellationToken); }
```

`BackupRetention` is **not** in Application. It is `internal static class BackupRetention { public static IReadOnlyList<string> ToDelete(IReadOnlyList<string> fileNames, int keep) }` in `Noof.Ledger.Host.Workers` (Task 13), tested in `Noof.Ledger.Host.Tests`; it is not in `PublicSurfaceTests`.

**Backup worker switch** (Task 13): `BackupWorkerOptions` has `public bool Enabled { get; init; } = true;` (config `Backup:Enabled`); `WorkerRegistration.AddNoofWorkers` registers `BackupWorker` only when it is `true`. `CookieModeHostFixture` and `UnreachableDatabaseHostFixture` pass `Backup__Enabled = false`, and every `WebApplicationFactory<Program>` in `Noof.Ledger.Host.Tests` sets `Backup:Enabled = false` — otherwise the worker would dump every test clone into the operator's real backup directory and prune the operator's real dumps.

### Echo strings (Task 8, exact)

`B` = the wallet's balances, each `"{amount:0.00} {CUR}"`, wallet currency first then ordinal, joined `", "`; when the wallet has no balance row yet, `"0.00 {WalletCurrency}"`.

- Expense, completed, lines > 0: `Recorded — {WalletName} · balance {B}` + `\n` + body (today's `Body`).
- Income, completed, lines > 0: `Income — {WalletName} · balance {B}` + `\n` + body.
- Expense, completed, no lines: unchanged (`{WalletName}: found no spending here — nothing recorded.`, `[Edit]`). Income, no lines: `{WalletName}: found no income here — nothing recorded.`, `[Edit]`.
- BalanceCheck, completed: `{WalletName}: balance was {before} {CUR}, you said {stated} {CUR} — adjusted {sign}{|diff|} {CUR}` where `diff = stated − before`, `sign` is `+` or `-`; when `diff == 0` the tail is `— matches` instead of `— adjusted …`. Actions `[Cancel, Edit]`.
- Cancelled: `Cancelled — {WalletName} · balance {B}` + `\n` + body (a BalanceCheck's body is `Statement: {stated} {CUR}`). Actions `[Restore]`.
- Any line whose currency differs from `WalletCurrency` adds the final body line `Not in the wallet's currency — no conversion yet.`
- Captured / Failed / voice lines: unchanged from Phase 3.

Dashboard (Task 9, Task 10): amounts are `amount.ToString("N2", CultureInfo.InvariantCulture)`, so 3200 renders `3,200.00`; E2E assertions use that form (`3,200.00 EUR`, `44,800.00 RSD`).

### Dashboard element ids (Task 7, Task 9)

`/wallets`: `wallet-{id}` (row), `wallet-name-{id}`, `wallet-aliases-{id}`, `rename-{id}`, `save-aliases-{id}`, `make-default-{id}`, `archive-{id}`, `new-wallet-name`, `new-wallet-currency`, `new-wallet-opening`, `new-wallet-date`, `new-wallet-aliases`, `new-wallet-default`, `create-wallet`, `wallets-error`, `wallets-saved`. Home: `balances` (card), `balance-{walletId}` (row), `balance-total-{CUR}`, `backup-status`.

### Task graph and waves

| Wave | Tasks (parallel within a wave, each in its own worktree) | Depends on |
|---|---|---|
| 1 | **Task 1** schema, domain, nullable capture wallet · **Task 2** `record_transaction` in `Ai` | — |
| 2 | **Task 3** balances view + read model · **Task 4** wallet directory, mapper, worker wiring · **Task 5** wallet admin · **Task 12** backup core | 3, 5, 12 need 1; 4 needs 1 + 2 |
| 3 | **Task 6** write path (entries, checkpoints, revisions) · **Task 7** `/wallets` page · **Task 13** `BackupWorker` | 6: 3 + 4; 7: 3 + 5; 13: 12 |
| 4 | **Task 8** echo with balances · **Task 9** dashboard balances + backup status | 8: 6; 9: 3 + 6 + 12 + 13 (its tests rely on `Backup:Enabled = false` in the host fixtures) |
| 5 | **Task 10** exactness under two cultures + `ops/restore-check.ps1` | 8, 9, 13 |
| 6 | **Task 11** documentation and status | all |

Task 12 and Task 13 are the backup track (spec B1–B5); they are numbered after the ledger track so the ledger keeps its order, not because they run last.

Merge each finished task into `phase-4-money` with `--no-ff`; resolve `PublicSurfaceTests.Allowed` conflicts by union. Resolve `using` conflicts by keeping one copy of each directive — Tasks 4 and 5 both add `using Noof.Ledger.Application.Wallets;` and `using Noof.Ledger.Persistence.Wallets;` to `PersistenceRegistration.cs` and `using Noof.Ledger.Application.Wallets;` to `PersistenceRegistrationTests.cs`, and a duplicated `using` is CS0105, an error under `TreatWarningsAsErrors`. In `AddNoofPersistence`, Task 12's shape (a `connectionString` local) wins; the `AddScoped` lines Tasks 3, 4 and 5 add after `services.AddScoped<ISpendingReadModel, …>` are re-added after it.

**Spec gaps assigned:** M11 "This month counts `Expense` only" — Task 3 adds `AND t.kind = @expense` to `EfSpendingReadModel.ThisMonthAsync` with a test. B4's automated restore round trip on a template clone — Task 10 (`RestoreRoundTripTests` in `Noof.Ledger.Persistence.Tests`).

## Review Focus

1. **An opening balance typed as `1500.50` on `/wallets` from the operator's ru-RU / sr-Latn-RS Windows** — expected: a 1500.50 checkpoint; likely: MudNumericField parses with the server culture, refuses the dot, and the wallet is created with 0. Owner Task 7: `Culture="@CultureInfo.InvariantCulture"` on the numeric and date fields, a `WalletsPageSourceTests` guard, and a `WalletsTests` case that fills `1500.50` and reads the checkpoint back.
2. **The host starts while PostgreSQL (Manual-start on this machine) is still down** — expected: the daily backup happens once the database is up; likely: the first tick fails, nothing is recorded, and `BackupWorker` sleeps 24 hours. Owner Task 13: retry a failed tick after `RetryInterval` (1 h), test `A_failed_tick_is_retried_after_the_retry_interval_not_a_day_later`.
3. **"пришла зарплата 2000 евро на Wise" and then the dashboard's "This month"** — expected: spending totals exclude the salary (M11); likely: `ThisMonthAsync` has no `kind` filter and shows 2000 EUR as this month's spending. Owner Task 3: `AND t.kind = 0` and `ThisMonthAsync_counts_expenses_only_never_income_or_statements`.
4. **Archiving the wallet that is the RSD default, then "кофе 250"** — expected: a clear hint that no default wallet exists; likely: the job fails terminally with "Could not read that message" and the record goes Failed. Owner Task 4 (worker test on the `no wallet to record into` failure and its echo) with Task 7 showing a `wallets-warning` for a currency with no default.
5. **The operator runs `ops/restore-check.ps1` for the first time against the empty test template, as the RUNBOOK tells them to** — expected: `restore-check OK`; likely: `Compare-Object $null $null` throws because `wallet_balances` has no rows. Owner Task 10: `@()`-wrap both sides, compare every public table's count, and add the automated dump → `pg_restore` → compare round trip (B4) in `RestoreRoundTripTests`.

---

### Task 1: Schema, domain, and a capture that needs no wallet

**Files:**
- Create: `src/Noof.Ledger.Domain/TransactionKind.cs`, `src/Noof.Ledger.Domain/EntryRole.cs`, `src/Noof.Ledger.Domain/Entry.cs`, `src/Noof.Ledger.Domain/BalanceCheck.cs`
- Modify: `src/Noof.Ledger.Domain/CaptureKind.cs` (adds `Manual = 2`), `src/Noof.Ledger.Domain/Wallet.cs` (whole file), `src/Noof.Ledger.Domain/Transaction.cs` (lines 5–6 and 30–33)
- Create: `src/Noof.Ledger.Persistence/Configurations/EntryConfiguration.cs`, `src/Noof.Ledger.Persistence/Configurations/BalanceCheckConfiguration.cs`, `src/Noof.Ledger.Persistence/Backup/BackupRun.cs`, `src/Noof.Ledger.Persistence/Backup/BackupRunConfiguration.cs`
- Modify: `src/Noof.Ledger.Persistence/Configurations/WalletConfiguration.cs` (whole file), `src/Noof.Ledger.Persistence/Configurations/TransactionConfiguration.cs` (whole file), `src/Noof.Ledger.Persistence/LedgerDbContext.cs` (three `DbSet`s)
- Modify: `src/Noof.Ledger.Persistence/Capture/EfCaptureStore.cs` (lines 25–29 and 37), `src/Noof.Ledger.Application/Capture/ICaptureStore.cs` (comments only)
- Modify: `src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs` (`GetSubjectAsync`, lines 13–49), `src/Noof.Ledger.Persistence/Reporting/EfSpendingReadModel.cs` (`RecentAsync`, lines 16–32), `src/Noof.Ledger.Persistence/Revisions/RevisionLog.cs` (lines 51–66)
- Create: `src/Noof.Ledger.Persistence/Migrations/<timestamp>_AddMoneyModel.cs` (+ generated `.Designer.cs`, regenerated `LedgerDbContextModelSnapshot.cs`)
- Modify: `tests/Noof.Ledger.Persistence.Tests/schema.expected.sql` (regenerated)
- Create: `tests/Noof.Ledger.Domain.Tests/MoneyModelEnumTests.cs`, `tests/Noof.Ledger.Persistence.Tests/MoneyModelSchemaTests.cs`, `tests/Noof.Ledger.Persistence.Tests/MoneyModelMigrationTests.cs`
- Modify (tests): `WalletDefaultTests.cs` (rewritten), `EfCaptureStoreTests.cs` (rewritten), `SeedDataTests.cs`, `EfCategorizationStoreTests.cs`, `EfSpendingReadModelTests.cs`, `TransactionRevisionTests.cs`, `CaptureModelTests.cs`, `EfJobQueueTests.cs`, `LineItemMoneyMappingTests.cs` (all under `tests/Noof.Ledger.Persistence.Tests/`), `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`

**Interfaces:**
- Consumes: nothing from another task.
- Produces (every later task relies on these exactly):
  - Domain: `public enum TransactionKind { Expense = 0, Income = 1, BalanceCheck = 2 }`; `public enum CaptureKind { Text = 0, Voice = 1, Manual = 2 }`; `public enum EntryRole { Principal = 0 }`.
  - `Wallet`: `required Guid Id { get; init; }`, `required string Name { get; set; }`, `required CurrencyCode Currency { get; init; }`, `string[] Aliases { get; set; } = []`, `bool IsDefaultForCurrency { get; set; }`, `bool Archived { get; set; }`, `DateTimeOffset CreatedAt { get; init; }`. `IsDefault` no longer exists.
  - `Transaction`: `Guid? WalletId { get; set; }`, `TransactionKind Kind { get; set; }` (default `Expense`), `long? TelegramChatId { get; init; }`, `int? TelegramMessageId { get; init; }`. Every other member unchanged.
  - `Entry` (`required Guid Id`, `required Guid TransactionId`, `required Guid WalletId`, `required Money Amount`, `required EntryRole Role`, all `get; init;`) and `BalanceCheck` (`required Guid TransactionId { get; init; }`, `required Guid WalletId { get; set; }`, `required Money Stated { get; set; }`, `required decimal ComputedBefore { get; set; }`).
  - `LedgerDbContext.Entries`, `.BalanceChecks`, `.BackupRuns`; `internal sealed class BackupRun` in `Noof.Ledger.Persistence.Backup` with `required Guid Id`, `required DateTimeOffset StartedAt`, `required DateTimeOffset FinishedAt`, `required bool Succeeded`, `string? FileName`, `long? SizeBytes`, `string? Error` (all `get; init;`), mapped by `BackupRunConfiguration` in the same folder.
  - `WalletConfiguration.OneDefaultPerCurrencyIndex` (`internal const string`) = `"ix_wallets_one_default_per_currency"`, the unique index on `wallets (currency) WHERE is_default_for_currency`. A caller that wants to recognise a second default by its constraint name uses this constant.
  - Tables and columns exactly as the plan header's "expensive to reverse" block: `entries` (index `ix_entries_wallet_id`), `balance_checks` (PK `transaction_id`), `backup_runs`, `transactions.kind`, nullable `transactions.wallet_id`/`telegram_chat_id`/`telegram_message_id`, check constraint `ck_transactions_telegram_ids_match_capture_kind`, and `ck_transactions_capture_has_content` widened so a `Manual` record needs `raw_text`.
  - Migration `AddMoneyModel`; `AddMoneyModel.SeedIncomeCategoriesSql` (`internal const string`); income categories with fixed ids `00000000-0000-0000-0001-000000000021` (`income`, no parent) and `…022` `salary`, `…023` `refund`, `…024` `gift`, `…025` `other-income` (parent `income`).
  - `ICaptureStore.CaptureAsync`/`CaptureVoiceAsync` write `WalletId = null` and never read a wallet.
  - `ICategorizationStore.GetSubjectAsync` returns a subject for a transaction with no wallet (`WalletName == ""`) and for one with no chat (`TelegramChatId == 0`).
  - `ISpendingReadModel.RecentAsync` lists a transaction with no wallet (`WalletName == ""`).
  - Revision snapshot JSON gains `"kind"` (the `TransactionKind` name: `"Expense"`, `"Income"` or `"BalanceCheck"`) and `"wallet_id"` (a GUID string or `null`) after the existing keys `raw_text`, `occurred_on`, `items`, which do not change.

> **Contract correction (accepted by review; the contract's "Persistence internals" now says so):** the header (and M13) say the backfill writes entries for every existing `Completed` expense. This task backfills every existing expense **that has lines, whatever its status**. `CancelAsync` only flips the status, and `RestoreAsync` puts back the status from before the cancellation. A record that was completed and then cancelled before this migration still has lines. With no entries, a later Restore would make it `Completed` again and leave it out of the balance for good. After this phase, `ApplyAsync` writes entries whatever the status (Task 6). The view counts `status = 1` only (Task 3), so the entries of a Cancelled record never reach a balance until it is restored. Captured and Failed records have no lines, so they get no entries under either rule. M5's integrity rule then holds for every row: entries always equal minus the lines, per currency.

- [ ] **Step 1: Pin the new enum values**

Create `tests/Noof.Ledger.Domain.Tests/MoneyModelEnumTests.cs`:

```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Domain.Tests;

public class MoneyModelEnumTests
{
    [Fact]
    public void Transaction_kind_values_never_move()
    {
        ((int)TransactionKind.Expense).Should().Be(0);
        ((int)TransactionKind.Income).Should().Be(1);
        ((int)TransactionKind.BalanceCheck).Should().Be(2);
        Enum.GetValues<TransactionKind>().Should().HaveCount(3, "3 is kept for Phase 7's Transfer and is not declared until then");
    }

    [Fact]
    public void Capture_kind_values_never_move()
    {
        ((int)CaptureKind.Text).Should().Be(0);
        ((int)CaptureKind.Voice).Should().Be(1);
        ((int)CaptureKind.Manual).Should().Be(2);
    }

    [Fact]
    public void Entry_role_values_never_move()
    {
        ((int)EntryRole.Principal).Should().Be(0);
        Enum.GetValues<EntryRole>().Should().HaveCount(1, "Fee is kept for Phase 7 and is not declared until then");
    }
}
```

In `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`, replace the `["Noof.Ledger.Domain"]` array with:

```csharp
        ["Noof.Ledger.Domain"] =
        [
            "AppUser", "BalanceCheck", "CaptureKind", "CategorizationAuthority", "CategorizationJob", "Category", "CurrencyCode",
            "CurrencyMismatchException", "Entry", "EntryRole", "JobKind", "JobStatus", "LineItem", "Merchant", "MerchantAlias",
            "MerchantKind", "MerchantName", "Money", "Transaction", "TransactionKind",
            "TransactionStatus", "Wallet",
        ],
```

- [ ] **Step 2: Write the schema tests**

Create `tests/Noof.Ledger.Persistence.Tests/MoneyModelSchemaTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Backup;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class MoneyModelSchemaTests(PostgresFixture fixture)
{
    static readonly Guid MainWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
    static int nextMessageId = 3000;

    static Transaction NewTransaction(
        CaptureKind captureKind, long? chatId, int? messageId,
        Guid? walletId = null, TransactionKind kind = TransactionKind.Expense, string? rawText = "кофе 250") => new()
    {
        Id = Guid.NewGuid(),
        WalletId = walletId,
        Kind = kind,
        RawText = rawText,
        CaptureKind = captureKind,
        Status = TransactionStatus.Completed,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = Now,
        OccurredOn = new DateOnly(2026, 9, 24),
        TelegramChatId = chatId,
        TelegramMessageId = messageId,
        CreatedAt = Now,
    };

    static Transaction TextCapture(Guid? walletId = null) =>
        NewTransaction(CaptureKind.Text, 111, Interlocked.Increment(ref nextMessageId), walletId);

    static Transaction OpeningBalance() =>
        NewTransaction(CaptureKind.Manual, chatId: null, messageId: null, MainWalletId, TransactionKind.BalanceCheck, "Opening balance");

    static Entry NewEntry(Guid transactionId, Guid walletId, Money amount) => new()
    {
        Id = Guid.NewGuid(),
        TransactionId = transactionId,
        WalletId = walletId,
        Amount = amount,
        Role = EntryRole.Principal,
    };

    async Task<LedgerDbContext> MigratedAsync()
    {
        var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return db;
    }

    static async Task<string?> ViolatedConstraintAsync(LedgerDbContext db)
    {
        try
        {
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException postgres)
        {
            return postgres.ConstraintName;
        }
    }

    [Fact]
    public async Task A_manual_record_with_no_telegram_message_is_accepted()
    {
        await using var db = await MigratedAsync();
        var opening = OpeningBalance();
        db.Transactions.Add(opening);

        (await ViolatedConstraintAsync(db)).Should().BeNull();

        db.ChangeTracker.Clear();
        var stored = await db.Transactions.SingleAsync(t => t.Id == opening.Id, TestContext.Current.CancellationToken);
        stored.CaptureKind.Should().Be(CaptureKind.Manual);
        stored.Kind.Should().Be(TransactionKind.BalanceCheck);
        stored.TelegramChatId.Should().BeNull();
        stored.TelegramMessageId.Should().BeNull();
    }

    [Fact]
    public async Task A_manual_record_that_names_a_telegram_message_is_refused()
    {
        await using var db = await MigratedAsync();
        db.Transactions.Add(NewTransaction(CaptureKind.Manual, 111, 5, MainWalletId, TransactionKind.BalanceCheck, "Opening balance"));

        (await ViolatedConstraintAsync(db)).Should().Be("ck_transactions_telegram_ids_match_capture_kind");
    }

    [Fact]
    public async Task A_text_capture_without_its_telegram_message_is_refused()
    {
        await using var db = await MigratedAsync();
        db.Transactions.Add(NewTransaction(CaptureKind.Text, chatId: null, messageId: null));

        (await ViolatedConstraintAsync(db)).Should().Be("ck_transactions_telegram_ids_match_capture_kind");
    }

    [Fact]
    public async Task A_manual_record_without_text_is_refused()
    {
        await using var db = await MigratedAsync();
        db.Transactions.Add(NewTransaction(CaptureKind.Manual, null, null, MainWalletId, TransactionKind.BalanceCheck, rawText: null));

        (await ViolatedConstraintAsync(db)).Should().Be("ck_transactions_capture_has_content");
    }

    [Fact]
    public async Task Any_number_of_manual_records_share_the_telegram_message_index()
    {
        await using var db = await MigratedAsync();
        db.Transactions.AddRange(OpeningBalance(), OpeningBalance());

        (await ViolatedConstraintAsync(db)).Should().BeNull("the idempotency index only covers records that came from Telegram");
    }

    [Fact]
    public async Task A_capture_may_name_no_wallet_yet()
    {
        await using var db = await MigratedAsync();
        var capture = TextCapture();
        db.Transactions.Add(capture);

        (await ViolatedConstraintAsync(db)).Should().BeNull();

        db.ChangeTracker.Clear();
        (await db.Transactions.SingleAsync(t => t.Id == capture.Id, TestContext.Current.CancellationToken))
            .WalletId.Should().BeNull();
    }

    [Fact]
    public async Task An_entry_keeps_its_sign_and_every_digit()
    {
        await using var db = await MigratedAsync();
        var capture = TextCapture(MainWalletId);
        db.Transactions.Add(capture);
        db.Entries.AddRange(
            NewEntry(capture.Id, MainWalletId, new Money(-123456789012345.6789m, CurrencyCode.Kzt)),
            NewEntry(capture.Id, MainWalletId, new Money(0.01m, CurrencyCode.Eur)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var stored = await db.Entries.AsNoTracking()
            .Where(e => e.TransactionId == capture.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Select(e => e.Amount).Should().BeEquivalentTo(
            [new Money(-123456789012345.6789m, CurrencyCode.Kzt), new Money(0.01m, CurrencyCode.Eur)]);
        stored.Should().OnlyContain(e => e.Role == EntryRole.Principal && e.WalletId == MainWalletId);
    }

    [Fact]
    public async Task Deleting_a_record_deletes_its_entries_and_its_checkpoint()
    {
        await using var db = await MigratedAsync();
        var expense = TextCapture(MainWalletId);
        var opening = OpeningBalance();
        db.Transactions.AddRange(expense, opening);
        db.Entries.Add(NewEntry(expense.Id, MainWalletId, new Money(-250m, CurrencyCode.Rsd)));
        db.BalanceChecks.Add(new BalanceCheck
        {
            TransactionId = opening.Id,
            WalletId = MainWalletId,
            Stated = new Money(45000m, CurrencyCode.Rsd),
            ComputedBefore = 0m,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM public.transactions WHERE id IN ({expense.Id}, {opening.Id})", TestContext.Current.CancellationToken);

        (await db.Entries.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await db.BalanceChecks.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task A_wallet_that_holds_entries_cannot_be_deleted()
    {
        await using var db = await MigratedAsync();
        var wallet = new Wallet { Id = Guid.NewGuid(), Name = "Wise EUR", Currency = CurrencyCode.Eur };
        var capture = TextCapture();
        db.Wallets.Add(wallet);
        db.Transactions.Add(capture);
        db.Entries.Add(NewEntry(capture.Id, wallet.Id, new Money(-3.5m, CurrencyCode.Eur)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var act = () => db.Database.ExecuteSqlAsync(
            $"DELETE FROM public.wallets WHERE id = {wallet.Id}", TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("FK_entries_wallets_wallet_id");
    }

    [Fact]
    public async Task A_checkpoint_keeps_the_stated_amount_and_what_was_computed_before_it()
    {
        await using var db = await MigratedAsync();
        var statement = NewTransaction(
            CaptureKind.Text, 111, Interlocked.Increment(ref nextMessageId), MainWalletId, TransactionKind.BalanceCheck, "на райфе 45 тысяч");
        var checkpoint = new BalanceCheck
        {
            TransactionId = statement.Id,
            WalletId = MainWalletId,
            Stated = new Money(45000m, CurrencyCode.Rsd),
            ComputedBefore = 44800.5m,
        };
        db.Transactions.Add(statement);
        db.BalanceChecks.Add(checkpoint);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var stored = await db.BalanceChecks.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        stored.Should().BeEquivalentTo(checkpoint);
    }

    [Fact]
    public async Task A_backup_run_is_kept_as_written()
    {
        await using var db = await MigratedAsync();
        var run = new BackupRun
        {
            Id = Guid.NewGuid(),
            StartedAt = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2026, 9, 24, 3, 0, 12, TimeSpan.Zero),
            Succeeded = false,
            Error = "pg_dump exited with code 1",
        };
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var stored = await db.BackupRuns.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        stored.Should().BeEquivalentTo(run);
    }
}
```

In `tests/Noof.Ledger.Persistence.Tests/CaptureModelTests.cs`, add these two statements at the end of `Enum_columns_persist_as_plain_integers_not_a_native_postgres_enum`, after the `CategorizationJob.Status` assertion:

```csharp
        db.Model.FindEntityType(typeof(Transaction))!.GetProperty(nameof(Transaction.Kind))
            .GetColumnType().Should().Be("integer");
        db.Model.FindEntityType(typeof(Entry))!.GetProperty(nameof(Entry.Role))
            .GetColumnType().Should().Be("integer");
```

- [ ] **Step 3: Write the migration test: the backfill, and existing records become expenses**

Create `tests/Noof.Ledger.Persistence.Tests/MoneyModelMigrationTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class MoneyModelMigrationTests(PostgresFixture fixture)
{
    const string LastMigrationBeforeTheMoneyModel = "20260923224236_AddVoiceCapture";

    static readonly Guid MainWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly Guid Completed = new("bbbbbbbb-0000-0000-0000-000000000001");
    static readonly Guid CancelledAfterItsReading = new("bbbbbbbb-0000-0000-0000-000000000002");
    static readonly Guid CompletedWithNoLines = new("bbbbbbbb-0000-0000-0000-000000000005");

    [Fact]
    public async Task Every_existing_expense_gets_minus_its_lines_per_currency_in_the_wallet_it_was_captured_into()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = await fixture.CreateContextAsync();
        await db.GetService<IMigrator>().MigrateAsync(LastMigrationBeforeTheMoneyModel, cancellationToken);

        // The shape noof_ledger has when the operator next starts the host: every record in the one seeded wallet.
        // A cancelled record keeps its lines, so it gets entries too - the balance leaves it out by its status,
        // and Restore brings it back with its money intact. A completed reading that found nothing has no lines,
        // so it gets no entry - not a zero one.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO public.transactions
                (id, wallet_id, raw_text, capture_kind, status, time_zone_id, occurred_at, occurred_on,
                 telegram_chat_id, telegram_message_id, created_at)
            VALUES
                ('bbbbbbbb-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000001',
                 'кофе 250, хлеб 100.5, coffee 3.50 eur', 0, 1, 'Europe/Belgrade',
                 '2026-09-20T08:00:00Z', '2026-09-20', 1, 1, '2026-09-20T08:00:00Z'),
                ('bbbbbbbb-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000001',
                 'такси 900', 0, 3, 'Europe/Belgrade',
                 '2026-09-20T09:00:00Z', '2026-09-20', 1, 2, '2026-09-20T09:00:00Z'),
                ('bbbbbbbb-0000-0000-0000-000000000003', '00000000-0000-0000-0000-000000000001',
                 'хлеб 80', 0, 0, 'Europe/Belgrade',
                 '2026-09-20T10:00:00Z', '2026-09-20', 1, 3, '2026-09-20T10:00:00Z'),
                ('bbbbbbbb-0000-0000-0000-000000000004', '00000000-0000-0000-0000-000000000001',
                 'непонятно что', 0, 2, 'Europe/Belgrade',
                 '2026-09-20T11:00:00Z', '2026-09-20', 1, 4, '2026-09-20T11:00:00Z'),
                ('bbbbbbbb-0000-0000-0000-000000000005', '00000000-0000-0000-0000-000000000001',
                 'привет', 0, 1, 'Europe/Belgrade',
                 '2026-09-20T12:00:00Z', '2026-09-20', 1, 5, '2026-09-20T12:00:00Z');

            INSERT INTO public.line_items
                (id, transaction_id, description, category_id, categorized_by, merchant_id, amount, currency)
            VALUES
                ('cccccccc-0000-0000-0000-000000000001', 'bbbbbbbb-0000-0000-0000-000000000001', 'кофе', NULL, 1, NULL, 250, 'RSD'),
                ('cccccccc-0000-0000-0000-000000000002', 'bbbbbbbb-0000-0000-0000-000000000001', 'хлеб', NULL, 4, NULL, 100.5, 'RSD'),
                ('cccccccc-0000-0000-0000-000000000003', 'bbbbbbbb-0000-0000-0000-000000000001', 'coffee', NULL, 1, NULL, 3.5, 'EUR'),
                ('cccccccc-0000-0000-0000-000000000004', 'bbbbbbbb-0000-0000-0000-000000000002', 'такси', NULL, 1, NULL, 900, 'RSD');
            """,
            cancellationToken);

        await db.Database.MigrateAsync(cancellationToken);

        var entries = await db.Entries.AsNoTracking().ToListAsync(cancellationToken);
        entries.Select(e => (e.TransactionId, e.WalletId, e.Amount, e.Role)).Should().BeEquivalentTo(
        [
            (Completed, MainWalletId, new Money(-350.5m, CurrencyCode.Rsd), EntryRole.Principal),
            (Completed, MainWalletId, new Money(-3.5m, CurrencyCode.Eur), EntryRole.Principal),
            (CancelledAfterItsReading, MainWalletId, new Money(-900m, CurrencyCode.Rsd), EntryRole.Principal),
        ]);
        entries.Should().NotContain(e => e.TransactionId == CompletedWithNoLines,
            "a completed record with no lines moved no money, so it gets no entry at all");

        (await db.Transactions.AsNoTracking().Select(t => t.Kind).Distinct().ToListAsync(cancellationToken))
            .Should().Equal(TransactionKind.Expense);
        (await db.Transactions.AsNoTracking().CountAsync(t => t.WalletId == MainWalletId, cancellationToken))
            .Should().Be(5, "every existing record keeps the wallet it was captured into");
    }
}
```

- [ ] **Step 4: Rewrite the wallet-default tests for one default per currency**

Replace the whole of `tests/Noof.Ledger.Persistence.Tests/WalletDefaultTests.cs` with:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Configurations;
using Npgsql;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class WalletDefaultTests(PostgresFixture fixture)
{
    static readonly Guid MainWalletId = new("00000000-0000-0000-0000-000000000001");

    static Wallet NewWallet(CurrencyCode currency, bool isDefaultForCurrency, params string[] aliases) => new()
    {
        Id = Guid.CreateVersion7(),
        Name = "Second",
        Currency = currency,
        IsDefaultForCurrency = isDefaultForCurrency,
        Aliases = aliases,
    };

    [Fact]
    public async Task The_seeded_main_wallet_is_the_RSD_default()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var main = await db.Wallets.AsNoTracking().SingleAsync(w => w.Id == MainWalletId, TestContext.Current.CancellationToken);

        main.Currency.Should().Be(CurrencyCode.Rsd);
        main.IsDefaultForCurrency.Should().BeTrue("the one global default of Phases 1-3 becomes the RSD default, not a wallet with none");
        main.Archived.Should().BeFalse();
        main.Aliases.Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_default_for_the_same_currency_is_rejected_by_the_database()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Wallets.Add(NewWallet(CurrencyCode.Rsd, isDefaultForCurrency: true));

        var act = async () => await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>()
            .WithInnerException<DbUpdateException, PostgresException>()
            .Where(e => e.SqlState == PostgresErrorCodes.UniqueViolation
                && e.ConstraintName == WalletConfiguration.OneDefaultPerCurrencyIndex);
    }

    [Fact]
    public async Task A_default_for_another_currency_is_accepted()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Wallets.Add(NewWallet(CurrencyCode.Eur, isDefaultForCurrency: true));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await db.Wallets.CountAsync(w => w.IsDefaultForCurrency, TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task A_second_non_default_wallet_in_the_same_currency_is_accepted()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        db.Wallets.Add(NewWallet(CurrencyCode.Rsd, isDefaultForCurrency: false));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await db.Wallets.CountAsync(w => !w.IsDefaultForCurrency, TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task A_wallet_keeps_its_aliases_in_order_and_is_stamped_when_it_is_created()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Rsd, isDefaultForCurrency: false, "райф", "raiffeisen");
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var stored = await db.Wallets.AsNoTracking().SingleAsync(w => w.Id == wallet.Id, TestContext.Current.CancellationToken);

        stored.Aliases.Should().Equal("райф", "raiffeisen");
        stored.Archived.Should().BeFalse();
        stored.CreatedAt.Should().BeAfter(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "a wallet saved without CreatedAt is stamped by the database, not left at year 1");
    }
}
```

- [ ] **Step 5: Seed tests for the income categories, and the wallet seed that can no longer run**

In `tests/Noof.Ledger.Persistence.Tests/SeedDataTests.cs`, in `Reapplying_the_seed_insert_leaves_the_row_counts_unchanged`, replace the `act` lambda with:

```csharp
        // AddCaptureModel.SeedDefaultWalletSql names is_default, which AddMoneyModel renamed; it only ever runs inside
        // its own migration now, against the schema it was written for.
        var act = async () =>
        {
            await db.Database.ExecuteSqlRawAsync(AddCaptureModel.SeedTopLevelCategoriesSql, TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync(AddCaptureModel.SeedSubCategoriesSql, TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync(AddMoneyModel.SeedIncomeCategoriesSql, TestContext.Current.CancellationToken);
        };
```

Append these two tests inside the class:

```csharp
    [Fact]
    public async Task Income_has_its_own_categories_under_an_income_parent()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var categories = await db.Categories.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        var income = categories.Single(c => c.Slug == "income");

        income.ParentId.Should().BeNull("income is a subtree of its own, beside the spending ones, not under one of them");
        categories.Where(c => c.ParentId == income.Id).Select(c => c.Slug)
            .Should().BeEquivalentTo(["salary", "refund", "gift", "other-income"]);
        categories.Where(c => c.Id == income.Id || c.ParentId == income.Id).Should().OnlyContain(c => c.IsActive);
    }

    [Fact]
    public async Task Reapplying_the_income_seed_changes_nothing_and_keeps_an_operators_rename()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var salary = await db.Categories.SingleAsync(c => c.Slug == "salary", TestContext.Current.CancellationToken);
        salary.NameEn = "Paycheck";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var countBefore = await db.Categories.CountAsync(TestContext.Current.CancellationToken);

        await db.Database.ExecuteSqlRawAsync(AddMoneyModel.SeedIncomeCategoriesSql, TestContext.Current.CancellationToken);

        (await db.Categories.CountAsync(TestContext.Current.CancellationToken)).Should().Be(countBefore);
        (await db.Categories.AsNoTracking().SingleAsync(c => c.Slug == "salary", TestContext.Current.CancellationToken))
            .NameEn.Should().Be("Paycheck", "the migration mechanism running again must not overwrite an operator's rename");
    }
```

- [ ] **Step 6: Rewrite the capture-store tests: a capture names no wallet**

Replace the whole of `tests/Noof.Ledger.Persistence.Tests/EfCaptureStoreTests.cs` with the file below. Compared with today, `Throws_when_no_wallet_is_marked_default` becomes `Captures_even_when_no_wallet_is_marked_default`, and `A_replay_succeeds_even_if_no_wallet_is_marked_default_any_more` is deleted, because a capture no longer reads a wallet, so a replay cannot hit that failure. The `DefaultWallet()`/`RemoveSeededDefaultWalletAsync` setup goes too, because nothing reads the wallet it set up. The main capture test now asserts `WalletId` is null and `Kind` is `Expense`.

```csharp
using System.Globalization;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Capture;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Capture;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfCaptureStoreTests(PostgresFixture fixture)
{
    static CapturedMessage NewMessage(long chatId = 1, int messageId = 100, DateTimeOffset? sentAt = null) =>
        new(chatId, messageId, "coffee 3.50", sentAt ?? DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Captures_even_when_no_wallet_is_marked_default()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var seeded = await db.Wallets.SingleAsync(TestContext.Current.CancellationToken);
        seeded.IsDefaultForCurrency = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCaptureStore(db, new FakeTimeProvider());

        var transactionId = await store.CaptureAsync(NewMessage(), "Europe/Belgrade", TestContext.Current.CancellationToken);

        (await db.Transactions.SingleAsync(TestContext.Current.CancellationToken)).Id.Should().Be(transactionId,
            "the wallet is chosen when the record is read, so a capture never waits for one");
    }

    [Fact]
    public async Task Captures_a_transaction_and_a_pending_job_in_one_call()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var store = new EfCaptureStore(db, new FakeTimeProvider(now));
        var message = NewMessage(sentAt: now);

        var transactionId = await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        var transaction = await db.Transactions.SingleAsync(TestContext.Current.CancellationToken);
        transaction.Id.Should().Be(transactionId);
        transaction.WalletId.Should().BeNull("the model names the wallet when it reads the message, or the currency's default is used then");
        transaction.Kind.Should().Be(TransactionKind.Expense);
        transaction.CaptureKind.Should().Be(CaptureKind.Text);
        transaction.RawText.Should().Be("coffee 3.50");
        transaction.Status.Should().Be(TransactionStatus.Captured);
        transaction.TimeZoneId.Should().Be("Europe/Belgrade");
        transaction.TelegramChatId.Should().Be(1);
        transaction.TelegramMessageId.Should().Be(100);
        transaction.BotMessageId.Should().BeNull();
        transaction.CreatedAt.Should().Be(now);

        var job = await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.TransactionId.Should().Be(transactionId);
        job.Status.Should().Be(JobStatus.Pending);
        job.AttemptCount.Should().Be(0);
        job.RunAfter.Should().Be(now);
        job.CreatedAt.Should().Be(now);
        job.UpdatedAt.Should().Be(now);
    }

    [Fact]
    public async Task Occurred_at_is_the_telegram_messages_own_timestamp_not_when_it_was_processed()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var sentAt = new DateTimeOffset(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);
        var processedAt = sentAt.AddHours(3);
        var store = new EfCaptureStore(db, new FakeTimeProvider(processedAt));
        var message = NewMessage(sentAt: sentAt);

        await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        var transaction = await db.Transactions.SingleAsync(TestContext.Current.CancellationToken);
        transaction.OccurredAt.Should().Be(sentAt, "an outage delayed processing, not the purchase itself");
        transaction.CreatedAt.Should().Be(processedAt);
    }

    [Fact]
    public async Task Replaying_the_same_chat_and_message_id_returns_the_existing_transaction_without_writing_a_second_job()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new EfCaptureStore(db, new FakeTimeProvider());
        var message = NewMessage();

        var first = await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);
        var second = await store.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        second.Should().Be(first);
        (await db.Transactions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await db.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task A_failure_before_commit_leaves_neither_row_behind()
    {
        var connectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();

        await using (var seed = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options))
        {
            await seed.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        await using var breaking = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(new ThrowsBeforeCommitInterceptor())
                .Options);
        var store = new EfCaptureStore(breaking, new FakeTimeProvider());

        var act = async () =>
            await store.CaptureAsync(NewMessage(), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var verify = new LedgerDbContext(
            new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options);
        (await verify.Transactions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0,
            "the commit never happened, so the transaction insert must not be visible either");
        (await verify.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    // ICaptureStore.CaptureAsync promises idempotency on (ChatId, MessageId). A plain sequential
    // replay test (above) cannot tell "idempotent" from "the loser throws a raw 23505 that a caller
    // happens not to hit" - only genuine concurrency does. This forces it the same way the owner-claim
    // race test does: hold the winning insert open inside an uncommitted transaction so the second
    // caller's insert must block on the database lock, not race past it, before either result is
    // observed.
    [Fact]
    public async Task Concurrent_CaptureAsync_calls_for_the_same_chat_and_message_let_exactly_one_caller_insert()
    {
        await using var dbA = await fixture.CreateContextAsync();
        await dbA.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var optionsB = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(dbA.Database.GetConnectionString()!)
            .Options;
        await using var dbB = new LedgerDbContext(optionsB);

        var storeA = new EfCaptureStore(dbA, new FakeTimeProvider());
        var storeB = new EfCaptureStore(dbB, new FakeTimeProvider());
        var message = NewMessage();

        await using var txA = await dbA.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var idA = await storeA.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);

        var idBTask = storeB.CaptureAsync(message, "Europe/Belgrade", TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(idBTask, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        finished.Should().NotBeSameAs(idBTask,
            "the second capture's insert must block on the first's uncommitted row, not race past it undetected");

        await txA.CommitAsync(TestContext.Current.CancellationToken);

        var idB = await idBTask;

        idB.Should().Be(idA, "both callers captured the identical (ChatId, MessageId); the loser must return the winner's id, not throw");
        (await dbA.Transactions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await dbA.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Theory]
    [InlineData("2026-09-21T21:50:00Z", "2026-09-21")]
    [InlineData("2026-09-21T22:30:00Z", "2026-09-22")]
    public async Task Occurred_on_is_the_local_day_the_message_was_sent_in_the_capture_time_zone(string sentAt, string expectedDay)
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        // Processed the next morning: the offline queue must not move a purchase to the day it was read (D2).
        var store = new EfCaptureStore(db, new FakeTimeProvider(new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero)));

        await store.CaptureAsync(
            NewMessage(sentAt: DateTimeOffset.Parse(sentAt, CultureInfo.InvariantCulture)),
            "Europe/Belgrade",
            TestContext.Current.CancellationToken);

        var transaction = await db.Transactions.SingleAsync(TestContext.Current.CancellationToken);
        transaction.OccurredOn.Should().Be(DateOnly.Parse(expectedDay, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task AttachBotMessageAsync_stamps_the_bot_message_id_onto_the_row()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var store = new EfCaptureStore(db, new FakeTimeProvider());
        var transactionId =
            await store.CaptureAsync(NewMessage(), "Europe/Belgrade", TestContext.Current.CancellationToken);

        await store.AttachBotMessageAsync(transactionId, 555, TestContext.Current.CancellationToken);

        var transaction = await db.Transactions.SingleAsync(TestContext.Current.CancellationToken);
        transaction.BotMessageId.Should().Be(555);
    }

    [Fact]
    public async Task Captures_a_voice_note_with_no_text_and_a_transcription_job()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 24, 21, 30, 0, TimeSpan.Zero);
        var store = new EfCaptureStore(db, new FakeTimeProvider(now));

        var transactionId = await store.CaptureVoiceAsync(
            new CapturedVoice(111, 5, "voice-file-1", 4, now), "Europe/Belgrade", TestContext.Current.CancellationToken);

        var transaction = await db.Transactions.SingleAsync(TestContext.Current.CancellationToken);
        transaction.Id.Should().Be(transactionId);
        transaction.CaptureKind.Should().Be(CaptureKind.Voice);
        transaction.WalletId.Should().BeNull();
        transaction.RawText.Should().BeNull();
        transaction.VoiceFileId.Should().Be("voice-file-1");
        transaction.VoiceDurationSeconds.Should().Be(4);
        transaction.Status.Should().Be(TransactionStatus.Captured);
        transaction.OccurredOn.Should().Be(new DateOnly(2026, 9, 24), "21:30 UTC is 23:30 in Belgrade, still the 24th");

        var job = await db.CategorizationJobs.SingleAsync(TestContext.Current.CancellationToken);
        job.TransactionId.Should().Be(transactionId);
        job.Kind.Should().Be(JobKind.Transcribe);
        job.VoiceFileId.Should().Be("voice-file-1");
        job.SourceMessageId.Should().BeNull("a capture's transcription is not a correction");
        job.Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public async Task A_redelivered_voice_note_is_captured_once()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
        var store = new EfCaptureStore(db, new FakeTimeProvider(now));
        var voice = new CapturedVoice(111, 5, "voice-file-1", 4, now);

        var first = await store.CaptureVoiceAsync(voice, "Europe/Belgrade", TestContext.Current.CancellationToken);
        var second = await store.CaptureVoiceAsync(voice, "Europe/Belgrade", TestContext.Current.CancellationToken);

        second.Should().Be(first);
        (await db.Transactions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await db.CategorizationJobs.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }
}
```

- [ ] **Step 7: Readers of a wallet-less record, the snapshot's kind, and the fixtures that named `IsDefault`**

`tests/Noof.Ledger.Persistence.Tests/EfCategorizationStoreTests.cs`:
1. In `NewWallet`, delete the line `IsDefault = false,`.
2. Change `NewTransaction`'s first parameter from `Guid walletId` to `Guid? walletId`. The body stays as it is.
3. Append inside the class (after `A_record_with_no_transcript_still_gets_a_revision`):

```csharp
    [Fact]
    public async Task GetSubjectAsync_reads_a_capture_that_has_no_wallet_yet_with_an_empty_wallet_name()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewTransaction(walletId: null, chatId: 777, messageId: 9,
            occurredAt: new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero), occurredOn: new DateOnly(2026, 9, 21));
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db, Clock);

        var subject = await store.GetSubjectAsync(transaction.Id, TestContext.Current.CancellationToken);

        subject.Should().NotBeNull("a capture waits for its reading to name a wallet; it must not vanish from the pipeline meanwhile");
        subject!.WalletName.Should().BeEmpty();
        subject.TelegramChatId.Should().Be(777);
    }

    [Fact]
    public async Task GetSubjectAsync_maps_a_manual_record_with_no_chat_to_chat_zero()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var opening = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = SeededDefaultWalletId,
            Kind = TransactionKind.BalanceCheck,
            RawText = "Opening balance",
            CaptureKind = CaptureKind.Manual,
            Status = TransactionStatus.Completed,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = new DateTimeOffset(2026, 9, 20, 22, 0, 0, TimeSpan.Zero),
            OccurredOn = new DateOnly(2026, 9, 21),
            CreatedAt = new DateTimeOffset(2026, 9, 21, 10, 0, 0, TimeSpan.Zero),
        };
        db.Transactions.Add(opening);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db, Clock);

        var subject = await store.GetSubjectAsync(opening.Id, TestContext.Current.CancellationToken);

        subject!.TelegramChatId.Should().Be(0);
        subject.WalletName.Should().Be("Main Wallet");
        subject.CaptureKind.Should().Be(CaptureKind.Manual);
    }
```

`tests/Noof.Ledger.Persistence.Tests/EfSpendingReadModelTests.cs`:
1. In `NewWallet`, delete the line `IsDefault = false,`.
2. Change `NewTransaction`'s first parameter from `Guid walletId` to `Guid? walletId`.
3. Append inside the class:

```csharp
    [Fact]
    public async Task RecentAsync_shows_a_capture_that_has_no_wallet_yet()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var captured = NewTransaction(null, now, "Europe/Belgrade", TransactionStatus.Captured);
        db.Transactions.Add(captured);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var recent = await readModel.RecentAsync(10, TestContext.Current.CancellationToken);

        recent.Should().ContainSingle(r => r.Id == captured.Id, "a message awaiting its reading has no wallet yet and must still show")
            .Which.WalletName.Should().BeEmpty();
    }
```

`tests/Noof.Ledger.Persistence.Tests/TransactionRevisionTests.cs`:
1. Change `SeedTransactionAsync`'s signature to `static async Task<Guid> SeedTransactionAsync(LedgerDbContext db, TransactionKind kind = TransactionKind.Expense)`, and add `Kind = kind,` right after `WalletId = DefaultWalletId,` in its initializer.
2. Append inside the class:

```csharp
    [Theory]
    [InlineData(TransactionKind.Expense, "Expense")]
    [InlineData(TransactionKind.Income, "Income")]
    public async Task A_snapshot_names_the_records_kind_and_its_wallet(TransactionKind kind, string expected)
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transactionId = await SeedTransactionAsync(db, kind);

        await new EfCategorizationStore(db, Clock).ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21)), TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var revision = await db.TransactionRevisions.SingleAsync(TestContext.Current.CancellationToken);
        using var snapshot = JsonDocument.Parse(revision.Snapshot);
        snapshot.RootElement.GetProperty("kind").GetString().Should().Be(expected);
        snapshot.RootElement.GetProperty("wallet_id").GetGuid().Should().Be(DefaultWalletId);
        snapshot.RootElement.GetProperty("raw_text").GetString().Should().Be("кофе 250", "the existing keys do not move");
    }
```

`tests/Noof.Ledger.Persistence.Tests/EfJobQueueTests.cs`: in `NewWallet`, delete the line `IsDefault = false,`.

`tests/Noof.Ledger.Persistence.Tests/LineItemMoneyMappingTests.cs`: replace
`{ Id = Guid.NewGuid(), Name = "Test wallet", Currency = CurrencyCode.Eur, IsDefault = false };`
with
`{ Id = Guid.NewGuid(), Name = "Test wallet", Currency = CurrencyCode.Eur };`.

No other test in the solution names `IsDefault`; the E2E `DashboardTests` set `WalletId = DefaultWalletId`, which still compiles once `WalletId` is `Guid?`. Step 13 verifies the claim with a grep rather than taking it on trust.

- [ ] **Step 8: Watch it fail to compile**

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL to compile. The errors name `TransactionKind`, `EntryRole`, `Entry`, `BalanceCheck`, `CaptureKind.Manual`, `IsDefaultForCurrency`, `Aliases`, `Archived`, `Transaction.Kind`, `LedgerDbContext.Entries`/`BalanceChecks`/`BackupRuns`, `Noof.Ledger.Persistence.Backup`, `WalletConfiguration.OneDefaultPerCurrencyIndex` and `AddMoneyModel`. The compile errors are the red here: none of these tests can exist without the model.

- [ ] **Step 9: The domain types**

`src/Noof.Ledger.Domain/TransactionKind.cs`:

```csharp
namespace Noof.Ledger.Domain;

// Stored as an integer; 3 is kept for Phase 7's Transfer and is not declared until then.
public enum TransactionKind
{
    Expense = 0,
    Income = 1,
    BalanceCheck = 2,
}
```

`src/Noof.Ledger.Domain/EntryRole.cs`:

```csharp
namespace Noof.Ledger.Domain;

// Stored as an integer; Fee is kept for Phase 7 and is not declared until then.
public enum EntryRole
{
    Principal = 0,
}
```

`src/Noof.Ledger.Domain/Entry.cs`:

```csharp
namespace Noof.Ledger.Domain;

// Signed: an expense's entries are negative, an income's positive. A wallet's balance is derived from these and
// its checkpoints; nothing stores a running balance.
public sealed class Entry
{
    public required Guid Id { get; init; }
    public required Guid TransactionId { get; init; }
    public required Guid WalletId { get; init; }
    public required Money Amount { get; init; }
    public required EntryRole Role { get; init; }
}
```

`src/Noof.Ledger.Domain/BalanceCheck.cs`:

```csharp
namespace Noof.Ledger.Domain;

// A balance statement is a checkpoint, not an adjustment entry (M6). ComputedBefore is history for the echo, in the
// stated currency; nothing reads it back as a balance.
public sealed class BalanceCheck
{
    public required Guid TransactionId { get; init; }
    public required Guid WalletId { get; set; }
    public required Money Stated { get; set; }
    public required decimal ComputedBefore { get; set; }
}
```

`src/Noof.Ledger.Domain/CaptureKind.cs`, whole file:

```csharp
namespace Noof.Ledger.Domain;

public enum CaptureKind
{
    Text = 0,
    Voice = 1,

    // Made on the dashboard: there is no Telegram message behind it.
    Manual = 2,
}
```

`src/Noof.Ledger.Domain/Wallet.cs`, whole file:

```csharp
namespace Noof.Ledger.Domain;

public sealed class Wallet
{
    public required Guid Id { get; init; }
    public required string Name { get; set; }
    public required CurrencyCode Currency { get; init; }
    public string[] Aliases { get; set; } = [];

    // At most one per currency. A partial unique index holds that, not this class.
    public bool IsDefaultForCurrency { get; set; }

    public bool Archived { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
```

`src/Noof.Ledger.Domain/Transaction.cs`: replace line 6, `public required Guid WalletId { get; init; }`, with

```csharp
    // Null until the record is read: the model names the wallet, or the default for the currency is used (M3).
    public Guid? WalletId { get; set; }

    public TransactionKind Kind { get; set; }
```

and replace lines 30–33

```csharp
    public required long TelegramChatId { get; init; }

    // The user's own message.
    public required int TelegramMessageId { get; init; }
```

with

```csharp
    // Both null for a Manual record and only for one; a check constraint holds that.
    public long? TelegramChatId { get; init; }

    // The user's own message.
    public int? TelegramMessageId { get; init; }
```

- [ ] **Step 10: Map the tables**

`src/Noof.Ledger.Persistence/Configurations/WalletConfiguration.cs`, whole file:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    internal const string OneDefaultPerCurrencyIndex = "ix_wallets_one_default_per_currency";

    public void Configure(EntityTypeBuilder<Wallet> builder)
    {
        builder.ToTable("wallets");

        builder.HasKey(w => w.Id);

        builder.Property(w => w.Id).HasColumnName("id");
        builder.Property(w => w.Name).HasColumnName("name").HasMaxLength(128).IsRequired();

        builder.Property(w => w.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsRequired()
            .HasConversion(c => c.Value, v => new CurrencyCode(v));

        builder.Property(w => w.Aliases).HasColumnName("aliases").HasDefaultValueSql("'{}'");
        builder.Property(w => w.IsDefaultForCurrency).HasColumnName("is_default_for_currency");
        builder.Property(w => w.Archived).HasColumnName("archived").HasDefaultValue(false);
        builder.Property(w => w.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

        builder.HasIndex(w => w.Currency)
            .IsUnique()
            .HasFilter("is_default_for_currency")
            .HasDatabaseName(OneDefaultPerCurrencyIndex);
    }
}
```

The old `ix_wallets_single_default` was raw SQL because an index on `((true))` cannot be modelled. The per-currency index can be modelled, so it now appears in the model, the snapshot and `schema.expected.sql`.

`src/Noof.Ledger.Persistence/Configurations/TransactionConfiguration.cs`, whole file:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> builder)
    {
        builder.ToTable("transactions", table =>
        {
            table.HasCheckConstraint(
                "ck_transactions_capture_has_content",
                "(capture_kind IN (0, 2) AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL)");
            table.HasCheckConstraint(
                "ck_transactions_telegram_ids_match_capture_kind",
                "(capture_kind = 2 AND telegram_chat_id IS NULL AND telegram_message_id IS NULL) "
                + "OR (capture_kind <> 2 AND telegram_chat_id IS NOT NULL AND telegram_message_id IS NOT NULL)");
        });

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.WalletId).HasColumnName("wallet_id");
        builder.Property(t => t.Kind).HasColumnName("kind");
        builder.Property(t => t.RawText).HasColumnName("raw_text");
        builder.Property(t => t.CaptureKind).HasColumnName("capture_kind");
        builder.Property(t => t.VoiceFileId).HasColumnName("voice_file_id");
        builder.Property(t => t.VoiceDurationSeconds).HasColumnName("voice_duration_seconds");
        builder.Property(t => t.Status).HasColumnName("status");
        builder.Property(t => t.TimeZoneId).HasColumnName("time_zone_id").HasMaxLength(64).IsRequired();
        builder.Property(t => t.OccurredAt).HasColumnName("occurred_at");
        builder.Property(t => t.OccurredOn).HasColumnName("occurred_on");
        builder.Property(t => t.TelegramChatId).HasColumnName("telegram_chat_id");
        builder.Property(t => t.TelegramMessageId).HasColumnName("telegram_message_id");
        builder.Property(t => t.BotMessageId).HasColumnName("bot_message_id");
        builder.Property(t => t.PromptMessageId).HasColumnName("prompt_message_id");
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");

        // The name stays EF's default: EfCaptureStore recognises a redelivered message by it.
        builder.HasIndex(t => new { t.TelegramChatId, t.TelegramMessageId })
            .IsUnique()
            .HasFilter("telegram_chat_id IS NOT NULL");

        builder.HasOne<Wallet>()
            .WithMany()
            .HasForeignKey(t => t.WalletId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
```

`src/Noof.Ledger.Persistence/Configurations/EntryConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class EntryConfiguration : IEntityTypeConfiguration<Entry>
{
    public void Configure(EntityTypeBuilder<Entry> builder)
    {
        builder.ToTable("entries");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.TransactionId).HasColumnName("transaction_id");
        builder.Property(e => e.WalletId).HasColumnName("wallet_id");
        builder.Property(e => e.Role).HasColumnName("role");

        builder.ComplexProperty(e => e.Amount, money =>
        {
            money.Property(m => m.Amount).HasColumnName("amount").HasPrecision(19, 4);
            money.Property(m => m.Currency)
                 .HasColumnName("currency")
                 .HasMaxLength(3)
                 .IsRequired()
                 .HasConversion(c => c.Value, v => new CurrencyCode(v));
        });

        builder.HasIndex(e => e.WalletId).HasDatabaseName("ix_entries_wallet_id");

        builder.HasOne<Transaction>()
            .WithMany()
            .HasForeignKey(e => e.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Wallet>()
            .WithMany()
            .HasForeignKey(e => e.WalletId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
```

`src/Noof.Ledger.Persistence/Configurations/BalanceCheckConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Configurations;

internal sealed class BalanceCheckConfiguration : IEntityTypeConfiguration<BalanceCheck>
{
    public void Configure(EntityTypeBuilder<BalanceCheck> builder)
    {
        builder.ToTable("balance_checks");

        builder.HasKey(b => b.TransactionId);

        builder.Property(b => b.TransactionId).HasColumnName("transaction_id");
        builder.Property(b => b.WalletId).HasColumnName("wallet_id");
        builder.Property(b => b.ComputedBefore).HasColumnName("computed_before");

        builder.ComplexProperty(b => b.Stated, money =>
        {
            money.Property(m => m.Amount).HasColumnName("stated_amount").HasPrecision(19, 4);
            money.Property(m => m.Currency)
                 .HasColumnName("currency")
                 .HasMaxLength(3)
                 .IsRequired()
                 .HasConversion(c => c.Value, v => new CurrencyCode(v));
        });

        builder.HasOne<Transaction>()
            .WithOne()
            .HasForeignKey<BalanceCheck>(b => b.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Wallet>()
            .WithMany()
            .HasForeignKey(b => b.WalletId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
```

`src/Noof.Ledger.Persistence/Backup/BackupRun.cs`:

```csharp
namespace Noof.Ledger.Persistence.Backup;

internal sealed class BackupRun
{
    public required Guid Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset FinishedAt { get; init; }
    public required bool Succeeded { get; init; }
    public string? FileName { get; init; }
    public long? SizeBytes { get; init; }
    public string? Error { get; init; }
}
```

`src/Noof.Ledger.Persistence/Backup/BackupRunConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Noof.Ledger.Persistence.Backup;

internal sealed class BackupRunConfiguration : IEntityTypeConfiguration<BackupRun>
{
    public void Configure(EntityTypeBuilder<BackupRun> builder)
    {
        builder.ToTable("backup_runs");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.StartedAt).HasColumnName("started_at");
        builder.Property(r => r.FinishedAt).HasColumnName("finished_at");
        builder.Property(r => r.Succeeded).HasColumnName("succeeded");
        builder.Property(r => r.FileName).HasColumnName("file_name");
        builder.Property(r => r.SizeBytes).HasColumnName("size_bytes");
        builder.Property(r => r.Error).HasColumnName("error");
    }
}
```

`src/Noof.Ledger.Persistence/LedgerDbContext.cs`: add `using Noof.Ledger.Persistence.Backup;` with the other usings (alphabetically before `Noof.Ledger.Persistence.Revisions`). After `public DbSet<LineItem> LineItems => Set<LineItem>();`, add

```csharp
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<BalanceCheck> BalanceChecks => Set<BalanceCheck>();
```

and after `public DbSet<AppSecret> Secrets => Set<AppSecret>();` add

```csharp
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
```

`ApplyConfigurationsFromAssembly` already picks up the four new configuration classes.

- [ ] **Step 11: Capture stops reading a wallet; the readers take a record with none**

`src/Noof.Ledger.Persistence/Capture/EfCaptureStore.cs`: delete lines 25–29, which are the comment `// Checked before the wallet lookup: …` and the `var wallet = await db.Wallets.SingleOrDefaultAsync(w => w.IsDefault, …) ?? throw new InvalidOperationException(…);` statement. Also delete line 37, `WalletId = wallet.Id,`. Nothing else in the file changes. `FindExistingAsync` still compiles: `t.TelegramChatId == chatId` lifts to `long?`.

`src/Noof.Ledger.Application/Capture/ICaptureStore.cs`, whole file:

```csharp
namespace Noof.Ledger.Application.Capture;

public interface ICaptureStore
{
    // Writes the Transaction AND its CategorizationJob inside ONE database transaction.
    // Names no wallet: the wallet is chosen when the record is read (the model names one, or the default for the
    // currency is used), so a capture never fails for want of one.
    // Idempotent on (ChatId, MessageId): a replayed Telegram update returns the existing id,
    // writes nothing.
    Task<Guid> CaptureAsync(CapturedMessage message, string timeZoneId, CancellationToken cancellationToken);

    // CaptureAsync for a voice note: no wallet either, the same single database transaction and the same
    // idempotency on (ChatId, MessageId). The record has no text yet, and its job is a Transcribe job (V3).
    Task<Guid> CaptureVoiceAsync(CapturedVoice voice, string timeZoneId, CancellationToken cancellationToken);

    Task AttachBotMessageAsync(Guid transactionId, int botMessageId, CancellationToken cancellationToken);
}
```

`src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs`, in `GetSubjectAsync`: replace the header query (lines 13–22) with

```csharp
        var header = await (
            from t in db.Transactions.AsNoTracking()
            where t.Id == transactionId
            join w in db.Wallets.AsNoTracking() on t.WalletId equals (Guid?)w.Id into walletJoin
            from w in walletJoin.DefaultIfEmpty()
            select new
            {
                t.Id, t.RawText, t.TelegramChatId, t.BotMessageId, WalletName = w == null ? string.Empty : w.Name,
                t.Status, t.OccurredAt, t.TimeZoneId, t.OccurredOn, t.CaptureKind,
            })
            .SingleOrDefaultAsync(cancellationToken);
```

and replace the closing `return new CategorizationSubject(…)` (lines 44–49) with

```csharp
        // A voice capture has no text until its transcript arrives, and none at all when nothing was heard;
        // the pipeline and the echo read that as empty, which is what it is. Only a Manual record has no chat,
        // and nothing categorises or echoes one, so 0 stands in for it.
        return new CategorizationSubject(
            header.Id, header.RawText ?? string.Empty, header.TelegramChatId ?? 0, header.BotMessageId, header.WalletName,
            header.Status, ZonedClock.LocalDate(header.OccurredAt, header.TimeZoneId), header.OccurredOn, lines,
            header.CaptureKind);
```

Keep exactly this shape — the `walletJoin` / `DefaultIfEmpty()` left join and `WalletName = w == null ? string.Empty : w.Name`. Task 4 adds `t.WalletId` to this projection and `WalletId: header.WalletId` to the constructor call, and Task 8 rewrites the method starting from that result; both edit against these lines.

`src/Noof.Ledger.Persistence/Reporting/EfSpendingReadModel.cs`, in `RecentAsync`: replace the `headers` query (lines 16–32) with

```csharp
        var headers = await (
                from t in db.Transactions
                where t.Status != TransactionStatus.Cancelled
                join w in db.Wallets on t.WalletId equals (Guid?)w.Id into walletJoin
                from w in walletJoin.DefaultIfEmpty()
                select new
                {
                    t.Id,
                    t.OccurredOn,
                    t.OccurredAt,
                    t.TimeZoneId,
                    RawText = t.RawText ?? string.Empty,
                    t.Status,
                    WalletName = w == null ? string.Empty : w.Name,
                })
            .OrderByDescending(h => h.OccurredOn)
            .ThenByDescending(h => h.OccurredAt)
            .ThenByDescending(h => h.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
```

`src/Noof.Ledger.Persistence/Revisions/RevisionLog.cs`: in `SnapshotAsync`, replace the `return JsonSerializer.Serialize(new Snapshot(…));` statement (lines 51–60) with

```csharp
        return JsonSerializer.Serialize(new Snapshot(
            transaction.RawText,
            transaction.OccurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [.. lines.Select(line => new SnapshotLine(
                line.Description,
                line.Amount.Amount.ToString(CultureInfo.InvariantCulture),
                line.Amount.Currency.Value,
                line.CategorySlug,
                line.MerchantId,
                (int)line.CategorizedBy))],
            transaction.Kind.ToString(),
            transaction.WalletId));
```

and the `Snapshot` record (lines 63–66) with

```csharp
    sealed record Snapshot(
        [property: JsonPropertyName("raw_text")] string? RawText,
        [property: JsonPropertyName("occurred_on")] string OccurredOn,
        [property: JsonPropertyName("items")] IReadOnlyList<SnapshotLine> Items,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("wallet_id")] Guid? WalletId);
```

`EfRecordEditor` needs no change: its `t.TelegramChatId == chatId` comparisons lift to `long?`. `git grep -n "Wallets\b" -- src` must show no other reader: only the two left joins above.

- [ ] **Step 12: Add the migration and write its body**

Run: `dotnet ef migrations add AddMoneyModel --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence`

This opens no database connection. Before you replace anything, read what EF generated. Every operation it emitted must be one of these:
- the wallet changes;
- the transaction changes;
- the three new tables;
- the indexes and the check constraints below.

The exception is `DropColumn` of `is_default` plus `AddColumn` of `is_default_for_currency`. EF cannot tell that a column whose property and name both changed is the same column. That pair would drop every wallet's default, so it becomes one `RenameColumn` below. If EF emitted anything else, stop and report it. Keep the generated `.Designer.cs` and `LedgerDbContextModelSnapshot.cs` exactly as generated. Replace the whole generated `…_AddMoneyModel.cs` with this file (file-scoped namespace, as `IDE0161` requires):

```csharp
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddMoneyModel : Migration
{
    // Operator-owned starting data, seeded the way AddCaptureModel seeds its tree and for the same reasons (see the
    // comment there): raw SQL, fixed ids, ON CONFLICT (id) DO NOTHING. Income is marked by its parent's slug, which
    // is how every category is offered to the model, so nothing needs a column of its own.
    internal const string SeedIncomeCategoriesSql = """
        INSERT INTO public.categories (id, is_active, name_en, name_ru, parent_id, slug) VALUES
            ('00000000-0000-0000-0001-000000000021', TRUE, 'Income', 'Доходы', NULL, 'income'),
            ('00000000-0000-0000-0001-000000000022', TRUE, 'Salary', 'Зарплата', '00000000-0000-0000-0001-000000000021', 'salary'),
            ('00000000-0000-0000-0001-000000000023', TRUE, 'Refund', 'Возврат', '00000000-0000-0000-0001-000000000021', 'refund'),
            ('00000000-0000-0000-0001-000000000024', TRUE, 'Gift', 'Подарок', '00000000-0000-0000-0001-000000000021', 'gift'),
            ('00000000-0000-0000-0001-000000000025', TRUE, 'Other income', 'Прочие доходы', '00000000-0000-0000-0001-000000000021', 'other-income')
        ON CONFLICT (id) DO NOTHING;
        """;

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX public.ix_wallets_single_default;");

        migrationBuilder.DropIndex(
            name: "IX_transactions_telegram_chat_id_telegram_message_id",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions");

        // A rename, not the drop-and-add EF scaffolds: the seeded Main Wallet stays the default, now for RSD.
        migrationBuilder.RenameColumn(
            name: "is_default",
            schema: "public",
            table: "wallets",
            newName: "is_default_for_currency");

        migrationBuilder.AddColumn<string[]>(
            name: "aliases",
            schema: "public",
            table: "wallets",
            type: "text[]",
            nullable: false,
            defaultValueSql: "'{}'");

        migrationBuilder.AddColumn<bool>(
            name: "archived",
            schema: "public",
            table: "wallets",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "created_at",
            schema: "public",
            table: "wallets",
            type: "timestamptz",
            nullable: false,
            defaultValueSql: "now()");

        migrationBuilder.AlterColumn<Guid>(
            name: "wallet_id",
            schema: "public",
            table: "transactions",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");

        migrationBuilder.AlterColumn<long>(
            name: "telegram_chat_id",
            schema: "public",
            table: "transactions",
            type: "bigint",
            nullable: true,
            oldClrType: typeof(long),
            oldType: "bigint");

        migrationBuilder.AlterColumn<int>(
            name: "telegram_message_id",
            schema: "public",
            table: "transactions",
            type: "integer",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "integer");

        migrationBuilder.AddColumn<int>(
            name: "kind",
            schema: "public",
            table: "transactions",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "backup_runs",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                finished_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                succeeded = table.Column<bool>(type: "boolean", nullable: false),
                file_name = table.Column<string>(type: "text", nullable: true),
                size_bytes = table.Column<long>(type: "bigint", nullable: true),
                error = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_backup_runs", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "balance_checks",
            schema: "public",
            columns: table => new
            {
                transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                computed_before = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                stated_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_balance_checks", x => x.transaction_id);
                table.ForeignKey(
                    name: "FK_balance_checks_transactions_transaction_id",
                    column: x => x.transaction_id,
                    principalSchema: "public",
                    principalTable: "transactions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_balance_checks_wallets_wallet_id",
                    column: x => x.wallet_id,
                    principalSchema: "public",
                    principalTable: "wallets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "entries",
            schema: "public",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                wallet_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<int>(type: "integer", nullable: false),
                amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_entries", x => x.id);
                table.ForeignKey(
                    name: "FK_entries_transactions_transaction_id",
                    column: x => x.transaction_id,
                    principalSchema: "public",
                    principalTable: "transactions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_entries_wallets_wallet_id",
                    column: x => x.wallet_id,
                    principalSchema: "public",
                    principalTable: "wallets",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_wallets_one_default_per_currency",
            schema: "public",
            table: "wallets",
            column: "currency",
            unique: true,
            filter: "is_default_for_currency");

        migrationBuilder.CreateIndex(
            name: "IX_transactions_telegram_chat_id_telegram_message_id",
            schema: "public",
            table: "transactions",
            columns: new[] { "telegram_chat_id", "telegram_message_id" },
            unique: true,
            filter: "telegram_chat_id IS NOT NULL");

        migrationBuilder.AddCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions",
            sql: "(capture_kind IN (0, 2) AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL)");

        migrationBuilder.AddCheckConstraint(
            name: "ck_transactions_telegram_ids_match_capture_kind",
            schema: "public",
            table: "transactions",
            sql: "(capture_kind = 2 AND telegram_chat_id IS NULL AND telegram_message_id IS NULL) OR (capture_kind <> 2 AND telegram_chat_id IS NOT NULL AND telegram_message_id IS NOT NULL)");

        migrationBuilder.CreateIndex(
            name: "IX_balance_checks_wallet_id",
            schema: "public",
            table: "balance_checks",
            column: "wallet_id");

        migrationBuilder.CreateIndex(
            name: "IX_entries_transaction_id",
            schema: "public",
            table: "entries",
            column: "transaction_id");

        migrationBuilder.CreateIndex(
            name: "ix_entries_wallet_id",
            schema: "public",
            table: "entries",
            column: "wallet_id");

        migrationBuilder.Sql(SeedIncomeCategoriesSql);

        // Every record so far is an expense in the wallet it was captured into; its entries are minus its lines,
        // per currency (M5). Status is not a filter here: a cancelled record keeps its lines and gets its entries,
        // so Restore brings its money back, and the balance leaves out whatever is not Completed.
        migrationBuilder.Sql(
            """
            INSERT INTO public.entries (id, transaction_id, wallet_id, amount, currency, role)
            SELECT gen_random_uuid(), t.id, t.wallet_id, -SUM(li.amount), li.currency, 0
            FROM public.transactions t
            JOIN public.line_items li ON li.transaction_id = t.id
            WHERE t.kind = 0
            GROUP BY t.id, t.wallet_id, li.currency;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Down cannot hand back a database holding what the old schema has no room for: a capture with no wallet
        // yet, a Manual record, a second currency's default, or a line in an income category each make one of the
        // statements below fail, and it stops there.
        migrationBuilder.Sql(
            """
            DELETE FROM public.categories WHERE id IN (
                '00000000-0000-0000-0001-000000000022', '00000000-0000-0000-0001-000000000023',
                '00000000-0000-0000-0001-000000000024', '00000000-0000-0000-0001-000000000025');
            DELETE FROM public.categories WHERE id = '00000000-0000-0000-0001-000000000021';
            """);

        migrationBuilder.DropTable(
            name: "backup_runs",
            schema: "public");

        migrationBuilder.DropTable(
            name: "balance_checks",
            schema: "public");

        migrationBuilder.DropTable(
            name: "entries",
            schema: "public");

        migrationBuilder.DropIndex(
            name: "ix_wallets_one_default_per_currency",
            schema: "public",
            table: "wallets");

        migrationBuilder.DropIndex(
            name: "IX_transactions_telegram_chat_id_telegram_message_id",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropCheckConstraint(
            name: "ck_transactions_telegram_ids_match_capture_kind",
            schema: "public",
            table: "transactions");

        migrationBuilder.DropColumn(
            name: "aliases",
            schema: "public",
            table: "wallets");

        migrationBuilder.DropColumn(
            name: "archived",
            schema: "public",
            table: "wallets");

        migrationBuilder.DropColumn(
            name: "created_at",
            schema: "public",
            table: "wallets");

        migrationBuilder.DropColumn(
            name: "kind",
            schema: "public",
            table: "transactions");

        migrationBuilder.RenameColumn(
            name: "is_default_for_currency",
            schema: "public",
            table: "wallets",
            newName: "is_default");

        migrationBuilder.AlterColumn<Guid>(
            name: "wallet_id",
            schema: "public",
            table: "transactions",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);

        migrationBuilder.AlterColumn<long>(
            name: "telegram_chat_id",
            schema: "public",
            table: "transactions",
            type: "bigint",
            nullable: false,
            oldClrType: typeof(long),
            oldType: "bigint",
            oldNullable: true);

        migrationBuilder.AlterColumn<int>(
            name: "telegram_message_id",
            schema: "public",
            table: "transactions",
            type: "integer",
            nullable: false,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_transactions_telegram_chat_id_telegram_message_id",
            schema: "public",
            table: "transactions",
            columns: new[] { "telegram_chat_id", "telegram_message_id" },
            unique: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_transactions_capture_has_content",
            schema: "public",
            table: "transactions",
            sql: "(capture_kind = 0 AND raw_text IS NOT NULL) OR (capture_kind = 1 AND voice_file_id IS NOT NULL)");

        migrationBuilder.Sql("CREATE UNIQUE INDEX ix_wallets_single_default ON wallets ((true)) WHERE is_default;");
    }
}
```

Build: `dotnet build NoofLedger.slnx`. Expected: success, with no warnings. If a nullable warning names `TelegramChatId`, `TelegramMessageId` or `WalletId` anywhere in `src`, map the value where it leaves Persistence, the way `GetSubjectAsync` does (`?? 0`, `?? string.Empty`), and report the site. Do not widen an Application record.

Regenerate the schema snapshot (connection-free):
`dotnet ef dbcontext script --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --output tests/Noof.Ledger.Persistence.Tests/schema.expected.sql`

Review it with `git diff tests/Noof.Ledger.Persistence.Tests/schema.expected.sql`. The diff must consist of exactly these changes:
- `wallets`: `is_default` becomes `is_default_for_currency boolean NOT NULL`, and the table gains `aliases text[] NOT NULL DEFAULT '{}'`, `archived boolean NOT NULL DEFAULT FALSE` and `created_at timestamptz NOT NULL DEFAULT (now())`.
- `transactions`: `wallet_id uuid` loses `NOT NULL`, as do `telegram_chat_id bigint` and `telegram_message_id integer`; the table gains `kind integer NOT NULL`; `ck_transactions_capture_has_content` becomes `(capture_kind IN (0, 2) AND raw_text IS NOT NULL) OR …`; and the table gains `ck_transactions_telegram_ids_match_capture_kind`.
- Three new tables: `backup_runs`, `balance_checks` (PK `transaction_id`, two FKs) and `entries` (two FKs).
- Indexes: `IX_balance_checks_wallet_id`, `IX_entries_transaction_id` and `ix_entries_wallet_id` are new. `IX_transactions_telegram_chat_id_telegram_message_id` gains `WHERE telegram_chat_id IS NOT NULL`. `ix_wallets_one_default_per_currency ON public.wallets (currency) WHERE is_default_for_currency` is new.

Column order inside a table is whatever EF prints. If the file's previous form differs from `dbcontext script` output in anything else (a header line, for example), keep the previous form and apply only the schema changes by hand.

- [ ] **Step 13: Run the tests and see them pass**

Run: `dotnet test --project tests/Noof.Ledger.Domain.Tests/Noof.Ledger.Domain.Tests.csproj --filter MoneyModelEnumTests`
Expected: PASS (3).

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: PASS. Every test here migrates its own empty database with `CreateContextAsync()` and `MigrateAsync`, so none needs the template yet. The exception is `MoneyStorageTests`, which clones the template. It touches no table this task changes, so it passes against the old template too. Do not run `dotnet test --solution` before Step 15's `database update`: the E2E suite clones the template, and every E2E class fails on `column t.kind does not exist` until the template has `AddMoneyModel`. The run must include these as passing:
- `MoneyModelSchemaTests` (11) and `MoneyModelMigrationTests` (1);
- `WalletDefaultTests` (5) and the two new `SeedDataTests`;
- `EfCaptureStoreTests` (11), the two new `EfCategorizationStoreTests`, the new `EfSpendingReadModelTests` test and `TransactionRevisionTests.A_snapshot_names_the_records_kind_and_its_wallet` (2);
- `SchemaSnapshotTests`, `MigrationContractTests.The_model_has_no_pending_changes` and `MigrationContractTests.Existing_transactions_get_the_local_day_of_their_own_time_zone`. The last one now also migrates its rows through `AddMoneyModel`.

Run: `git grep -nw "IsDefault" -- tests src ':!*Designer.cs' ':!*ModelSnapshot.cs'`
Expected: no output. `-w` matches the whole word, so `IsDefaultForCurrency` is not a hit; the generated `Designer.cs` and snapshot files of earlier migrations are excluded because EF owns them.

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS. `PublicSurfaceTests` sees the four new Domain types. Persistence's list is unchanged, because `BackupRun` and every configuration are `internal`.

- [ ] **Step 14: Watch each new guard fail, then put it back**

Each break below is made in the new migration file or in one source file, and each is reverted before the next. The migration has not been applied anywhere except throwaway test databases, so editing it here is safe. Do this step before Step 15 updates the template.

| Break it | Run (`--project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`) | Expected failure |
|---|---|---|
| In `AddMoneyModel.Up`, replace the `RenameColumn` with `DropColumn("is_default")` + `AddColumn<bool>("is_default_for_currency", …, nullable: false, defaultValue: false)`: the scaffold's own version. | `--filter WalletDefaultTests` | `The_seeded_main_wallet_is_the_RSD_default`: expected `main.IsDefaultForCurrency` to be `True`, found `False` |
| Delete the `migrationBuilder.Sql("DROP INDEX public.ix_wallets_single_default;");` line | `--filter WalletDefaultTests` | `A_default_for_another_currency_is_accepted`: `23505` unique violation on `ix_wallets_single_default` |
| Delete the backfill `migrationBuilder.Sql(…INSERT INTO public.entries…)` call | `--filter MoneyModelMigrationTests` | expected 3 entries, found an empty collection |
| Add `AND t.status = 1` to the backfill's `WHERE` | `--filter MoneyModelMigrationTests` | the `CancelledAfterItsReading` RSD −900 entry is missing |
| In the backfill, change `JOIN public.line_items li` to `LEFT JOIN public.line_items li`, `-SUM(li.amount)` to `COALESCE(-SUM(li.amount), 0)` and `li.currency` (in `SELECT` and `GROUP BY`) to `COALESCE(li.currency, 'RSD')` | `--filter MoneyModelMigrationTests` | the collection has an extra `CompletedWithNoLines` RSD 0 entry (`BeEquivalentTo` reports it) |
| Change the `sql:` of `ck_transactions_telegram_ids_match_capture_kind` in `Up` to `"TRUE"` | `--filter MoneyModelSchemaTests` | `A_manual_record_that_names_a_telegram_message_is_refused` and `A_text_capture_without_its_telegram_message_is_refused`: expected `"ck_transactions_telegram_ids_match_capture_kind"`, found `<null>` |
| In `EfCategorizationStore.GetSubjectAsync`, make the join inner: replace the two lines `join w … into walletJoin` / `from w in walletJoin.DefaultIfEmpty()` with the single line `join w in db.Wallets.AsNoTracking() on t.WalletId equals (Guid?)w.Id` | `--filter EfCategorizationStoreTests` | `GetSubjectAsync_reads_a_capture_that_has_no_wallet_yet_with_an_empty_wallet_name`: expected `subject` not to be `<null>` |
| The same inner join in `EfSpendingReadModel.RecentAsync`: replace its two join lines with `join w in db.Wallets on t.WalletId equals (Guid?)w.Id` | `--filter EfSpendingReadModelTests` | `RecentAsync_shows_a_capture_that_has_no_wallet_yet`: expected a single item matching `r.Id == captured.Id`, found none |
| In `EfCaptureStore.StoreAsync`, after the `existing` check, add `_ = await db.Wallets.SingleAsync(w => w.IsDefaultForCurrency, cancellationToken);` | `--filter EfCaptureStoreTests` | `Captures_even_when_no_wallet_is_marked_default`: `InvalidOperationException` "Sequence contains no elements" |

After the last revert, run `git diff --stat`. It must show exactly this step's starting state, with none of the breaks left in. Then run the Persistence project once more: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`. Expected: PASS.

- [ ] **Step 15: Update the test template, then run the whole solution, under the suite lock**

```powershell
$lock = 'C:\Users\noofs\AppData\Local\Temp\noof-suite.lock'
while (-not (New-Item -ItemType Directory -Path $lock -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 15 }
try {
    $admin = if ($env:NOOF_TEST_PG) { $env:NOOF_TEST_PG } else { (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection").Trim() }
    $template = $admin -replace 'Database=postgres', 'Database=noof_ledger_test_template'
    if ($template -notmatch 'Database=noof_ledger_test_template') { throw "Refusing: the connection string does not name the test template." }
    dotnet ef database update --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --connection $template
    dotnet test --solution NoofLedger.slnx
}
finally {
    Remove-Item -Path $lock -Force
}
```

Expected: the `database update` output's last line names `AddMoneyModel`. Every test passes. The solution count is Phase 3's 677 plus the 25 this task adds (3 Domain, 11 schema, 1 migration, 3 wallet-default, 2 seed, 2 subject, 1 read-model, 2 snapshot) minus the 1 it deletes (`A_replay_succeeds_even_if_no_wallet_is_marked_default_any_more`), so 701. If the baseline on this branch differs from 677, the difference must still be +24. The E2E `DashboardTests` still see "Main Wallet": they seed their transactions with the seeded wallet's id. Never point this command at `noof_ledger`.

- [ ] **Step 16: Commit**

```bash
git add src/Noof.Ledger.Domain src/Noof.Ledger.Application/Capture/ICaptureStore.cs src/Noof.Ledger.Persistence tests/Noof.Ledger.Persistence.Tests tests/Noof.Ledger.Domain.Tests/MoneyModelEnumTests.cs tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs
git commit -m "$(cat <<'EOF'
feat(persistence): money model schema - transaction kinds, entries, checkpoints, a default per currency

Transactions gain a kind and may name no wallet until they are read; a capture
no longer reads one. Manual records carry no Telegram ids, held by a check
constraint. Wallets get aliases, archiving and one default per currency.
entries, balance_checks and backup_runs are created, income categories are
seeded, and every existing expense is backfilled with minus its lines per
currency. Revision snapshots name the record's kind and wallet (M4, M5, M7,
M8, M13, B3).

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

Run `git status`. Expected: a clean working tree.

---

### Task 2: record_spending becomes record_transaction (Ai + Application contract)

**Files:**
- Modify: `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs`
- Modify: `src/Noof.Ledger.Ai/CategorizationSchema.cs`
- Modify: `src/Noof.Ledger.Ai/CategorizationPrompt.cs`
- Modify: `src/Noof.Ledger.Ai/ChatCategorizer.cs`
- Modify: `src/Noof.Ledger.Ai/SchemaTools.cs` (comment only, line 8)
- Modify: `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`
- Test: `tests/Noof.Ledger.Ai.Tests/CategorizationSchemaTests.cs`, `tests/Noof.Ledger.Ai.Tests/CategorizationPromptTests.cs`, `tests/Noof.Ledger.Ai.Tests/ChatCategorizerTests.cs`, `tests/Noof.Ledger.Ai.Tests/ChatCategorizerFunctionInvokerTests.cs`, `tests/Noof.Ledger.Ai.Tests/AnswerToolGuardTests.cs`, `tests/Noof.Ledger.Ai.Tests/AnthropicResponses.cs`, `tests/Noof.Ledger.Ai.Tests/Anthropic/AnthropicTranslatingChatClientTests.cs`, `tests/Noof.Ledger.Ai.Tests/Anthropic/ChatCategorizerOverAnthropicTests.cs`

**Interfaces:**
- Consumes: nothing from another Phase 4 task — this task only touches names that already exist in the codebase today (`ChatCategorizer`, `CategorizationSchema`, `CategorizationPrompt`, `AnswerToolGuard`, `SchemaTool`, `SchemaFunction`, `StrictTool`, `CategorizationRequest`, `CategorizationProposal`, `ProposedLineItem`, `ICategorizer`).
- Produces (later tasks rely on these exactly):
  - `public sealed record WalletOption(Guid Id, string Name, CurrencyCode Currency, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency);` in `Noof.Ledger.Application.Categorization`.
  - `CategorizationRequest` gains a trailing optional `IReadOnlyList<WalletOption>? Wallets = null` (null means none offered).
  - `public static class ProposedKind { public const string Expense = "expense"; public const string Income = "income"; public const string Balance = "balance"; }`.
  - `CategorizationProposal` gains trailing optional `string Kind = ProposedKind.Expense, Guid? WalletId = null, decimal? BalanceAmount = null, string? BalanceCurrency = null`.
  - The answer tool's wire name is `record_transaction` everywhere (`ChatCategorizer`'s `RecordTransactionName` const, the schema built by `CategorizationSchema.BuildRecordTransaction`, every canned Anthropic response, every test assertion).
  - `CategorizationSchema.BuildRecordTransaction(IReadOnlyList<CategoryOption> categories, IReadOnlyList<MerchantOption> merchantHints, IReadOnlyList<WalletOption> wallets)` — root schema gains required properties `kind` (enum `expense`/`income`/`balance`), `wallet_id` (nullable string enum of the offered wallet ids, `[null]` only when `wallets` is empty), `balance_amount` (nullable number), `balance_currency` (nullable `CurrencyCode.Supported` enum). `additionalProperties: false` still appears exactly twice (root object, line-item object) — the three new properties are scalars, not nested objects.
  - `CategorizationPrompt.RenderWallets(IReadOnlyList<WalletOption> wallets)` and `CategorizationPrompt.BuildUserTurn` including a `Wallets:` section.
  - `ChatCategorizer.ProposeAsync` reads `payload.Kind`/`payload.WalletId`/`payload.BalanceAmount`/`payload.BalanceCurrency` off the tool call's `JsonElement` (amounts straight into `decimal`, `wallet_id` straight into `Guid` via `Guid.Parse`, never through `double` or a validation step) and carries them onto the returned `CategorizationProposal`.

> Task 4's `IProposalMapper.TryMap` (wallet resolution, mapping `Kind`/`WalletId`/`BalanceAmount`/`BalanceCurrency` into `MappedProposal`) is **not** part of this task — `ProposalMapper.cs` and `ProposalMapperTests.cs` are untouched here.

---

#### Step 1: `WalletOption` and the new trailing fields on the Application contract

These are plain DTOs (`CLAUDE.md` §4 exempts DTOs from write-the-test-first TDD), so this step edits the contract directly, then proves the assembly still builds and every existing positional call site still compiles.

- [ ] **Step 1a: Edit the contract**

In `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs`, replace:

```csharp
// A merchant the database already knows. Id is what the model returns when it accepts one.
public sealed record MerchantOption(Guid Id, string DisplayName);

// Today is the local day the message was SENT, never the day the job runs: a message that waited in the
// offline queue overnight must not move a day (D2). For a Correct job specifically, "the message" is the
// correction reply itself, not the original capture (docs/OPEN-QUESTIONS.md P2-2).
public sealed record CategorizationRequest(
    string RawText,
    DateOnly Today,
    IReadOnlyList<CategoryOption> Categories,
    IReadOnlyList<MerchantOption> MerchantHints,
    IReadOnlyList<MerchantOption> AllMerchants,
    CorrectionRequest? Correction = null);
```

with:

```csharp
// A merchant the database already knows. Id is what the model returns when it accepts one.
public sealed record MerchantOption(Guid Id, string DisplayName);

// A wallet the model may pick for a transaction (M3). Aliases are the words the operator uses for
// it in speech ("с налички" for a wallet named "Cash"); IsDefaultForCurrency marks the wallet the
// mapper falls back to when the model names none.
public sealed record WalletOption(Guid Id, string Name, CurrencyCode Currency, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency);

// Today is the local day the message was SENT, never the day the job runs: a message that waited in the
// offline queue overnight must not move a day (D2). For a Correct job specifically, "the message" is the
// correction reply itself, not the original capture (docs/OPEN-QUESTIONS.md P2-2).
// Wallets is null, not an empty list, when the caller offers none at all - CategorizationSchema and
// CategorizationPrompt both treat null the same as empty (M9), but the distinction stays in the type
// so a future caller can tell "no wallets exist yet" from "I forgot to pass them".
public sealed record CategorizationRequest(
    string RawText,
    DateOnly Today,
    IReadOnlyList<CategoryOption> Categories,
    IReadOnlyList<MerchantOption> MerchantHints,
    IReadOnlyList<MerchantOption> AllMerchants,
    CorrectionRequest? Correction = null,
    IReadOnlyList<WalletOption>? Wallets = null);
```

Replace:

```csharp
public sealed record CategorizationProposal(IReadOnlyList<ProposedLineItem> Items, string? OccurredOn = null);
```

with:

```csharp
// The closed set of strings the model answers "kind" with (M9). Kept as string constants, not an
// enum, because this record crosses the model boundary as JSON before anything maps it - the same
// reason ProposedLineItem.CurrencyCode is a string, not a CurrencyCode, until ProposalMapper resolves it.
public static class ProposedKind
{
    public const string Expense = "expense";
    public const string Income = "income";
    public const string Balance = "balance";
}

// Kind defaults to Expense so every existing positional construction of this record (a plain
// spending answer) keeps meaning exactly what it always meant. WalletId/BalanceAmount/BalanceCurrency
// travel flat, mirroring record_transaction's own wire shape (the "expensive to reverse" note in
// plan-00-header.md) rather than as a nested object the strict schema cannot express as cleanly.
public sealed record CategorizationProposal(
    IReadOnlyList<ProposedLineItem> Items,
    string? OccurredOn = null,
    string Kind = ProposedKind.Expense,
    Guid? WalletId = null,
    decimal? BalanceAmount = null,
    string? BalanceCurrency = null);
```

- [ ] **Step 1b: Add the two new types to the allowed public surface**

In `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`, in the `["Noof.Ledger.Application"]` array, change:

```csharp
            "IProposalMapper", "MappedProposal", "IChatNotifier", "IJobQueue", "JobCompletionOutcome",
```

to:

```csharp
            "IProposalMapper", "MappedProposal", "IChatNotifier", "IJobQueue", "JobCompletionOutcome",
            "WalletOption", "ProposedKind",
```

- [ ] **Step 1c: Build and confirm nothing broke**

```
dotnet build src/Noof.Ledger.Application/Noof.Ledger.Application.csproj
```

Expect it to succeed (both new members are additive/trailing-optional, so every existing call site in the solution still compiles).

```
dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter PublicSurfaceTests
```

Expect all `PublicSurfaceTests` green, including `Public_types_are_exactly_the_allowed_set("Noof.Ledger.Application")`.

---

#### Step 2: `CategorizationSchema` — write the failing tests

Replace the full contents of `tests/Noof.Ledger.Ai.Tests/CategorizationSchemaTests.cs` with:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Ai.Tests;

public class CategorizationSchemaTests
{
    static readonly IReadOnlyList<CategoryOption> Categories =
    [
        new CategoryOption("groceries", "Groceries", "Продукты", null),
        new CategoryOption("food-drink", "Food & Drink", "Еда и напитки", null),
    ];

    static readonly IReadOnlyList<MerchantOption> NoHints = [];

    static readonly IReadOnlyList<MerchantOption> OneHint =
    [
        new MerchantOption(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Lidl"),
    ];

    static readonly IReadOnlyList<WalletOption> NoWallets = [];

    static readonly IReadOnlyList<WalletOption> OneWallet =
    [
        new WalletOption(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Cash", CurrencyCode.Rsd, [], true),
    ];

    [Fact]
    public void Record_transaction_with_no_hints_or_wallets_matches_the_pinned_shape()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        const string expected = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["items", "occurred_on", "kind", "wallet_id", "balance_amount", "balance_currency"],
          "properties": {
            "items": {
              "type": "array",
              "minItems": 0,
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["description", "amount", "currency", "category_slug", "merchant_name"],
                "properties": {
                  "description": { "type": "string", "description": "What was bought, as short plain text in the language of the message." },
                  "amount": { "type": "number", "description": "The amount the person meant, as a number - for example 1000 or 45.3. Interpret words, slang and speech: \"штуку\" is 1000, \"полтос\" is 50, \"двести пятьдесят\" is 250." },
                  "currency": { "type": ["string", "null"], "enum": ["EUR", "RSD", "USD", "RUB", "KZT", null], "description": "The currency the message states, or null when it states none." },
                  "category_slug": { "type": "string", "enum": ["groceries", "food-drink"] },
                  "merchant_name": { "type": ["string", "null"], "description": "The merchant's name as the person wrote it, or null when no merchant is named or it is one of the known merchants." }
                }
              }
            },
            "occurred_on": { "type": ["string", "null"], "description": "The day the purchase happened, as an ISO date (YYYY-MM-DD), worked out from today's date given with the message, or null when the message names no day." },
            "kind": { "type": "string", "enum": ["expense", "income", "balance"], "description": "What kind of record this is: \"expense\" for money spent, \"income\" for money received, or \"balance\" when the person states what a wallet's balance is right now rather than a purchase or a deposit - items must be empty for kind \"balance\"." },
            "wallet_id": { "type": ["string", "null"], "enum": [null], "description": "The id of the wallet the person means, chosen from the wallets you were offered, or null when no wallet is named or none of the offered wallets fits - the ledger then uses the default wallet for the spending's currency." },
            "balance_amount": { "type": ["number", "null"], "description": "The balance the person stated, as a number, when kind is \"balance\"; null for every other kind." },
            "balance_currency": { "type": ["string", "null"], "enum": ["EUR", "RSD", "USD", "RUB", "KZT", null], "description": "The currency of the stated balance, when kind is \"balance\" and the person named one; null for every other kind, or when they named none - the wallet's own currency is used then." }
          }
        }
        """;

        JsonNode.DeepEquals(Reserialize(schema), JsonNode.Parse(expected)).Should().BeTrue();
    }

    [Fact]
    public void Occurred_on_is_required_but_nullable_so_the_model_can_answer_no_day_named()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        schema.GetProperty("properties").TryGetProperty("occurred_on", out var occurredOn).Should().BeTrue();
        occurredOn.GetProperty("type").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["string", "null"]);
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["items", "occurred_on", "kind", "wallet_id", "balance_amount", "balance_currency"]);
    }

    [Fact]
    public void An_empty_hint_list_omits_known_merchant_id_entirely()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        LineItemProperties(schema).TryGetProperty("known_merchant_id", out _).Should().BeFalse();
    }

    [Fact]
    public void With_hints_known_merchant_id_lands_between_category_slug_and_merchant_name()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, NoWallets);

        var names = LineItemProperties(schema).EnumerateObject().Select(p => p.Name);

        names.Should().ContainInOrder("category_slug", "known_merchant_id", "merchant_name");
    }

    [Fact]
    public void Known_merchant_id_enum_is_exactly_the_hinted_guids()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, NoWallets);

        var knownMerchantId = LineItemProperties(schema).GetProperty("known_merchant_id");

        knownMerchantId.GetProperty("type").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["string", "null"]);
        knownMerchantId.GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["11111111-1111-1111-1111-111111111111", null]);
        knownMerchantId.GetProperty("description").GetString().Should().Be(
            "One of the listed known merchants' ids, or null when the merchant is not one of them.");
    }

    [Fact]
    public void Category_slug_enum_is_exactly_the_offered_slugs_and_nothing_else()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        var slugs = LineItemProperties(schema).GetProperty("category_slug").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString());

        slugs.Should().BeEquivalentTo(Categories.Select(c => c.Slug));
    }

    [Fact]
    public void Currency_enum_is_the_five_CurrencyCode_statics()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        var currencies = LineItemProperties(schema).GetProperty("currency").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString());

        currencies.Should().BeEquivalentTo(["EUR", "RSD", "USD", "RUB", "KZT", null]);
    }

    [Fact]
    public void Currency_is_required_but_nullable_so_the_model_can_answer_none_stated()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        var lineItem = schema.GetProperty("properties").GetProperty("items").GetProperty("items");
        var required = lineItem.GetProperty("required").EnumerateArray().Select(e => e.GetString());

        required.Should().Contain("currency");
        LineItemProperties(schema).GetProperty("currency").GetProperty("type").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(["string", "null"]);
    }

    [Fact]
    public void Kind_enum_is_exactly_expense_income_balance()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        schema.GetProperty("properties").GetProperty("kind").GetProperty("type").GetString().Should().Be("string");
        schema.GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["expense", "income", "balance"]);
    }

    [Fact]
    public void Wallet_id_enum_is_exactly_the_offered_wallet_ids_when_wallets_are_offered()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, OneWallet);

        var walletId = schema.GetProperty("properties").GetProperty("wallet_id");
        walletId.GetProperty("type").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["string", "null"]);
        walletId.GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(["22222222-2222-2222-2222-222222222222", null]);
    }

    [Fact]
    public void Wallet_id_enum_is_only_null_when_no_wallets_are_offered()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        schema.GetProperty("properties").GetProperty("wallet_id").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo([(string?)null]);
    }

    [Fact]
    public void Balance_currency_enum_is_the_five_CurrencyCode_statics_plus_null()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        schema.GetProperty("properties").GetProperty("balance_currency").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["EUR", "RSD", "USD", "RUB", "KZT", null]);
    }

    [Fact]
    public void Balance_amount_is_a_nullable_number_with_no_bound()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, NoHints, NoWallets);

        schema.GetProperty("properties").GetProperty("balance_amount").GetProperty("type")
            .EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo(["number", "null"]);
    }

    [Fact]
    public void List_merchants_matches_the_pinned_shape()
    {
        var schema = CategorizationSchema.BuildListMerchants();

        const string expected =
            """{ "type": "object", "additionalProperties": false, "properties": {}, "required": [] }""";

        JsonNode.DeepEquals(Reserialize(schema), JsonNode.Parse(expected)).Should().BeTrue();
    }

    [Fact]
    public void Additional_properties_false_appears_at_every_object_level_of_record_transaction()
    {
        // Still exactly 2 (root + line item): kind/wallet_id/balance_amount/balance_currency are
        // scalar root properties, not a nested object - M9's decision to keep the stated balance
        // flat (plan-00-header.md, "expensive to reverse" #5) is exactly what keeps this count from
        // growing to 3.
        var json = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet).GetRawText();

        CountOccurrences(json, "\"additionalProperties\":false").Should().Be(2);
    }

    [Fact]
    public void Additional_properties_false_appears_on_list_merchants()
    {
        var json = CategorizationSchema.BuildListMerchants().GetRawText();

        CountOccurrences(json, "\"additionalProperties\":false").Should().Be(1);
    }

    [Theory]
    [InlineData("minLength")]
    [InlineData("maxLength")]
    [InlineData("\"minimum\"")]
    [InlineData("\"maximum\"")]
    [InlineData("multipleOf")]
    [InlineData("$ref")]
    [InlineData("pattern")]
    public void No_unsupported_json_schema_keyword_appears_anywhere(string forbidden)
    {
        var recordTransaction = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet).GetRawText();
        var listMerchants = CategorizationSchema.BuildListMerchants().GetRawText();

        recordTransaction.Should().NotContain(forbidden);
        listMerchants.Should().NotContain(forbidden);
    }

    [Fact]
    public void Every_minItems_value_is_zero_or_one()
    {
        var json = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet).GetRawText();

        foreach (Match match in Regex.Matches(json, "\"minItems\":(\\d+)"))
            match.Groups[1].Value.Should().BeOneOf("0", "1");
    }

    [Fact]
    public void Same_inputs_produce_byte_identical_json_twice()
    {
        var first = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet).GetRawText();
        var second = CategorizationSchema.BuildRecordTransaction([.. Categories], [.. OneHint], [.. OneWallet]).GetRawText();

        first.Should().Be(second);
    }

    [Fact]
    public void Schema_round_trips_through_JsonElement_deserialization_unchanged()
    {
        var schema = CategorizationSchema.BuildRecordTransaction(Categories, OneHint, OneWallet);
        var roundTripped = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(schema));

        JsonNode.DeepEquals(JsonNode.Parse(roundTripped.GetRawText()), JsonNode.Parse(schema.GetRawText())).Should().BeTrue();
    }

    static JsonElement LineItemProperties(JsonElement schema) =>
        schema.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties");

    static JsonNode Reserialize(JsonElement schema) => JsonNode.Parse(schema.GetRawText())!;

    static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
```

Run:

```
dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj --filter CategorizationSchemaTests
```

Expect a build failure: `CategorizationSchema.BuildRecordTransaction` does not exist yet (`BuildRecordSpending` is still the only method) — CS0117/CS1061. This is the "watch it fail" for the renamed/extended method: the test file above will not even compile against today's `CategorizationSchema.cs`.

---

#### Step 3: `CategorizationSchema` — implement `BuildRecordTransaction`

Replace the full contents of `src/Noof.Ledger.Ai/CategorizationSchema.cs` with:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Ai;

// Raw JSON Schema, built as a JsonElement rather than through a typed builder or reflection: a
// strict raw-schema AIFunctionDeclaration needs "additionalProperties": false at every object
// level, which is easiest to guarantee by building the JsonObject tree directly and controlling
// every key by hand.
internal static class CategorizationSchema
{
    const string AmountDescription =
        "The amount the person meant, as a number - for example 1000 or 45.3. Interpret words, slang and speech: "
        + "\"штуку\" is 1000, \"полтос\" is 50, \"двести пятьдесят\" is 250.";

    const string OccurredOnDescription =
        "The day the purchase happened, as an ISO date (YYYY-MM-DD), worked out from today's date given with the "
        + "message, or null when the message names no day.";

    const string KindDescription =
        "What kind of record this is: \"expense\" for money spent, \"income\" for money received, or "
        + "\"balance\" when the person states what a wallet's balance is right now rather than a purchase "
        + "or a deposit - items must be empty for kind \"balance\".";

    const string WalletIdDescription =
        "The id of the wallet the person means, chosen from the wallets you were offered, or null when no "
        + "wallet is named or none of the offered wallets fits - the ledger then uses the default wallet "
        + "for the spending's currency.";

    const string BalanceAmountDescription =
        "The balance the person stated, as a number, when kind is \"balance\"; null for every other kind.";

    const string BalanceCurrencyDescription =
        "The currency of the stated balance, when kind is \"balance\" and the person named one; null for "
        + "every other kind, or when they named none - the wallet's own currency is used then.";

    public static JsonElement BuildRecordTransaction(
        IReadOnlyList<CategoryOption> categories, IReadOnlyList<MerchantOption> merchantHints, IReadOnlyList<WalletOption> wallets)
    {
        var properties = new List<KeyValuePair<string, JsonNode?>>
        {
            new("description", new JsonObject
            {
                ["type"] = "string",
                ["description"] = "What was bought, as short plain text in the language of the message.",
            }),
            new("amount", new JsonObject
            {
                ["type"] = "number",
                ["description"] = AmountDescription,
            }),
            new("currency", new JsonObject
            {
                // Strict mode puts every declared property in "required" (below); optionality is a
                // nullable type instead of omission, and the enum keyword still applies to a null
                // value, so null has to be listed in it explicitly too (operator, 2026-09-23).
                ["type"] = new JsonArray("string", "null"),
                ["enum"] = new JsonArray([.. CurrencyCode.Supported.Select(code => (JsonNode)code.Value), null]),
                ["description"] = "The currency the message states, or null when it states none.",
            }),
            new("category_slug", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(categories.Select(c => (JsonNode)c.Slug).ToArray()),
            }),
        };

        if (merchantHints.Count > 0)
        {
            properties.Add(new("known_merchant_id", new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["enum"] = new JsonArray([.. merchantHints.Select(m => (JsonNode)m.Id.ToString()), null]),
                ["description"] = "One of the listed known merchants' ids, or null when the merchant is not one of them.",
            }));
        }

        properties.Add(new("merchant_name", new JsonObject
        {
            ["type"] = new JsonArray("string", "null"),
            ["description"] = "The merchant's name as the person wrote it, or null when no merchant is named or it is one of the known merchants.",
        }));

        var lineItem = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            // Derived from the properties actually added, not a hand-written list: strict mode
            // requires every declared property here, and this keeps known_merchant_id out of it on
            // the no-hints path, where the property itself is never declared (operator, 2026-09-23).
            ["required"] = new JsonArray([.. properties.Select(p => (JsonNode)p.Key)]),
            ["properties"] = new JsonObject(properties),
        };

        var root = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            // kind/wallet_id/balance_amount/balance_currency are root properties, not line-item
            // ones (M3, M9): one message records one transaction, in one wallet, of one kind.
            ["required"] = new JsonArray("items", "occurred_on", "kind", "wallet_id", "balance_amount", "balance_currency"),
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "array",
                    // 0, not 1: a message can genuinely describe zero purchases (a loan received,
                    // not a purchase - see CategorizationPrompt's "заняла у Маши" example, and every
                    // "balance" answer, which never has items at all). Only 0 and 1 are valid values
                    // for minItems under this API's schema subset.
                    ["minItems"] = 0,
                    ["items"] = lineItem,
                },
                ["occurred_on"] = new JsonObject
                {
                    ["type"] = new JsonArray("string", "null"),
                    ["description"] = OccurredOnDescription,
                },
                ["kind"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("expense", "income", "balance"),
                    ["description"] = KindDescription,
                },
                ["wallet_id"] = new JsonObject
                {
                    // Kept as a nullable-string enum even with zero wallets offered (rather than
                    // omitting the property, as known_merchant_id does on the no-hints path):
                    // wallet_id is a root property that always exists on this tool, so strict mode's
                    // "every declared property is required" would otherwise force a property that
                    // sometimes isn't there - the enum instead narrows to [null], which is what the
                    // API's schema subset offers for "this value can only ever be null".
                    ["type"] = new JsonArray("string", "null"),
                    ["enum"] = new JsonArray([.. wallets.Select(w => (JsonNode)w.Id.ToString()), null]),
                    ["description"] = WalletIdDescription,
                },
                ["balance_amount"] = new JsonObject
                {
                    ["type"] = new JsonArray("number", "null"),
                    ["description"] = BalanceAmountDescription,
                },
                ["balance_currency"] = new JsonObject
                {
                    ["type"] = new JsonArray("string", "null"),
                    ["enum"] = new JsonArray([.. CurrencyCode.Supported.Select(code => (JsonNode)code.Value), null]),
                    ["description"] = BalanceCurrencyDescription,
                },
            },
        };

        return ToElement(root);
    }

    public static JsonElement BuildListMerchants()
    {
        var root = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject(),
            ["required"] = new JsonArray(),
        };

        return ToElement(root);
    }

    static JsonElement ToElement(JsonObject root)
    {
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }
}
```

Run:

```
dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj --filter CategorizationSchemaTests
```

Expect every `CategorizationSchemaTests` test green.

---

#### Step 4: `CategorizationPrompt` — write the failing tests

Replace the full contents of `tests/Noof.Ledger.Ai.Tests/CategorizationPromptTests.cs` with:

```csharp
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Ai.Tests;

public class CategorizationPromptTests
{
    [Fact]
    public void System_prompt_asks_for_the_meant_amount_as_a_plain_decimal()
    {
        CategorizationPrompt.System.Should().Contain("the number the person meant");
        CategorizationPrompt.System.Should().Contain("\"штуку\"");
        CategorizationPrompt.System.Should().NotContain("character for character",
            "quote-and-verify is gone (D1): an instruction to copy verbatim makes the model refuse exactly the amounts this phase exists to accept");
    }

    [Fact]
    public void System_prompt_instructs_answering_currency_as_null_rather_than_guessing()
    {
        CategorizationPrompt.System.Should().Contain("only when the message actually states one");
        CategorizationPrompt.System.Should().Contain("answer currency as null rather");
        CategorizationPrompt.System.Should().Contain("than choosing one");
    }

    [Fact]
    public void System_prompt_has_an_example_with_no_stated_currency()
    {
        CategorizationPrompt.System.Should().Contain("The message names no currency");
    }

    [Fact]
    public void System_prompt_wraps_examples_in_the_examples_tag()
    {
        CategorizationPrompt.System.Should().Contain("<examples>");
        CategorizationPrompt.System.Should().Contain("</examples>");
    }

    [Fact]
    public void System_prompt_has_between_three_and_seven_examples()
    {
        // Widened from [3,5] (M9): the income and balance examples added below bring the count to 7.
        var opening = Regex.Matches(CategorizationPrompt.System, "<example>").Count;
        var closing = Regex.Matches(CategorizationPrompt.System, "</example>").Count;

        opening.Should().BeInRange(3, 7);
        closing.Should().Be(opening);
    }

    [Fact]
    public void System_prompt_contains_no_secret_looking_text()
    {
        CategorizationPrompt.System.Should().NotContain("sk-ant");
        CategorizationPrompt.System.Should().NotMatchRegex("(?i)api[_-]?key");
        CategorizationPrompt.System.Should().NotMatchRegex("[A-Za-z0-9_-]{32,}");
    }

    [Fact]
    public void System_prompt_explains_kind_and_that_a_balance_has_no_items()
    {
        CategorizationPrompt.System.Should().Contain("\"balance\"");
        CategorizationPrompt.System.Should().Contain("items must be empty");
    }

    [Fact]
    public void System_prompt_explains_wallet_id_falls_back_to_the_currencys_default_wallet()
    {
        CategorizationPrompt.System.Should().Contain("wallet_id");
        CategorizationPrompt.System.Should().Contain("default wallet for the spending's currency");
    }

    [Fact]
    public void System_prompt_has_an_income_example_and_a_balance_example()
    {
        CategorizationPrompt.System.Should().Contain("kind \"income\"");
        CategorizationPrompt.System.Should().Contain("kind \"balance\"");
    }

    [Fact]
    public void Render_categories_shows_both_names_and_the_parent_when_present()
    {
        IReadOnlyList<CategoryOption> categories =
        [
            new CategoryOption("groceries", "Groceries", "Продукты", null),
            new CategoryOption("dairy", "Dairy", "Молочные продукты", "groceries"),
        ];

        var rendered = CategorizationPrompt.RenderCategories(categories);

        rendered.Should().Contain("groceries: Groceries / Продукты");
        rendered.Should().Contain("dairy (under groceries): Dairy / Молочные продукты");
    }

    [Fact]
    public void Render_merchant_hints_lists_id_and_display_name()
    {
        IReadOnlyList<MerchantOption> hints =
            [new MerchantOption(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Lidl")];

        CategorizationPrompt.RenderMerchantHints(hints)
            .Should().Contain("11111111-1111-1111-1111-111111111111: Lidl");
    }

    [Fact]
    public void Render_merchant_hints_says_so_when_there_are_none()
    {
        CategorizationPrompt.RenderMerchantHints([]).Should().Contain("No known merchants");
    }

    [Fact]
    public void Render_wallets_lists_id_name_currency_aliases_and_the_default_marker()
    {
        IReadOnlyList<WalletOption> wallets =
        [
            new WalletOption(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Cash", CurrencyCode.Rsd, ["наличка", "cash"], true),
        ];

        var rendered = CategorizationPrompt.RenderWallets(wallets);

        rendered.Should().Contain("11111111-1111-1111-1111-111111111111: Cash (RSD)");
        rendered.Should().Contain("also called наличка, cash");
        rendered.Should().Contain("the default wallet for RSD");
    }

    [Fact]
    public void Render_wallets_says_so_when_there_are_none()
    {
        CategorizationPrompt.RenderWallets([]).Should().Contain("wallet_id must be null");
    }

    [Fact]
    public void Build_user_turn_gives_today_with_its_weekday_then_the_message_categories_and_hints()
    {
        IReadOnlyList<CategoryOption> categories = [new CategoryOption("groceries", "Groceries", "Продукты", null)];
        var request = new CategorizationRequest("купил вчера штуку евро", new DateOnly(2026, 9, 22), categories, [], []);

        var turn = CategorizationPrompt.BuildUserTurn(request);

        turn.Should().StartWith("Today: 2026-09-22 (Tuesday)");
        turn.Should().Contain("купил вчера штуку евро");
        turn.Should().Contain("groceries: Groceries / Продукты");
        turn.Should().Contain("No known merchants");
    }

    [Fact]
    public void Build_user_turn_includes_the_offered_wallets()
    {
        IReadOnlyList<WalletOption> wallets =
            [new WalletOption(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Cash", CurrencyCode.Rsd, [], true)];
        var request = new CategorizationRequest("кофе 250", new DateOnly(2026, 9, 22), [], [], [], Wallets: wallets);

        CategorizationPrompt.BuildUserTurn(request).Should().Contain("Cash (RSD)");
    }

    [Fact]
    public void Build_user_turn_says_no_wallets_are_offered_when_none_are_given()
    {
        var request = new CategorizationRequest("кофе 250", new DateOnly(2026, 9, 22), [], [], []);

        CategorizationPrompt.BuildUserTurn(request).Should().Contain("wallet_id must be null");
    }

    [Fact]
    public void System_prompt_explains_relative_days_are_counted_from_today()
    {
        CategorizationPrompt.System.Should().Contain("occurred_on");
        CategorizationPrompt.System.Should().Contain("counted from today");
    }

    [Fact]
    public void A_correction_adds_the_current_record_and_the_instruction_to_the_user_turn()
    {
        IReadOnlyList<CategoryOption> categories = [new CategoryOption("groceries", "Groceries", "Продукты", null)];
        var current = new RecordedLine("продукты", new Money(1000m, CurrencyCode.Eur), "groceries", "Продукты", "Lidl");
        var request = new CategorizationRequest("купил штуку евро", new DateOnly(2026, 9, 22), categories, [], [],
            new CorrectionRequest(new DateOnly(2026, 9, 21), [current], "нет, 1500"));

        var turn = CategorizationPrompt.BuildUserTurn(request);

        turn.Should().Contain("Current record (dated 2026-09-21):");
        turn.Should().Contain("- продукты: 1000 EUR, category groceries, merchant Lidl");
        // Raw string literals carry the source file's line endings, so assert per line, not on "\n".
        turn.Should().Contain("Correction from the person:");
        turn.Should().EndWith("нет, 1500");
    }

    [Fact]
    public void A_first_reading_has_no_correction_section()
    {
        var request = new CategorizationRequest("кофе 250", new DateOnly(2026, 9, 22), [], [], []);

        CategorizationPrompt.BuildUserTurn(request).Should().NotContain("Correction from the person");
    }

    [Fact]
    public void System_prompt_asks_for_the_complete_corrected_record()
    {
        CategorizationPrompt.System.Should().Contain("complete corrected record");
    }
}
```

Run:

```
dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj --filter CategorizationPromptTests
```

Expect a build failure: `CategorizationPrompt.RenderWallets` does not exist yet, and `CategorizationRequest`'s `Wallets:` named-argument use will fail to compile until Step 1 has landed (it already has, by this point in the task) — the remaining failures are `RenderWallets` missing and the new `Contains` assertions failing once it compiles (the prompt text does not yet mention "balance", "wallet_id" or an income/balance example).

---

#### Step 5: `CategorizationPrompt` — implement the wallet rendering and the new prose

Replace the full contents of `src/Noof.Ledger.Ai/CategorizationPrompt.cs` with:

```csharp
using System.Globalization;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai;

internal static class CategorizationPrompt
{
    // A role sentence focuses the model's behavior — the categories, the hints and the message
    // itself are variable input and belong in the user turn built by BuildUserTurn, never in this
    // constant. Under Microsoft.Extensions.AI this string becomes ChatOptions.Instructions, sent
    // as the request's system turn.
    //
    // The closed set of category_slug values is enforced by CategorizationSchema's enum in the
    // record_transaction tool's strict schema, not by this prompt. This prompt exists to explain what
    // each category MEANS so a line item lands under the right one; it deliberately never repeats
    // "choose only from this list" in prose, since a live slug could not even appear here — System
    // is a compile-time const, so it cannot embed data from the current request.
    //
    // Messages arrive in Russian and English, sometimes both in one message. No specific
    // multilingual-prompting technique is invented for it beyond the bilingual examples below —
    // that is the whole strategy: show, not instruct.
    public const string System = """
        You record spending, income and balance statements from a personal finance message so they
        can be reviewed later. You read one message at a time and answer with what it describes,
        nothing more. The person sees your answer echoed back in their chat and can cancel or
        correct it, so give your best reading of what they meant rather than leaving out an amount
        that is not written in digits.

        Each category you are offered has a slug, an English name, a Russian name, and may have a
        parent category. Use the names to understand what each slug means — everyday food and
        drink, transport, household bills, and so on — so a line item lands under the slug whose
        meaning actually matches it. You do not choose the set of allowed slugs; your answer only
        accepts one of the slugs you were given, so pick by meaning and let the schema reject
        anything else.

        For every amount, answer with the number the person meant — 1000, 45.3, not a word or a
        quoted string. People write amounts in words, slang and speech-recognised text: "штуку" or
        "штука" is 1000, "пятихатка" is 500, "полтос" is 50, "двести пятьдесят" is 250, "1,5к" is
        1500, "1 500" is 1500. Never add lines up into a total the message did not ask for. If a
        line has no amount at all, do not produce that line.

        Report a currency only when the message actually states one — "евро", "eur", "€", "рсд",
        "динар", "рублей". If the message names no currency at all, answer currency as null rather
        than choosing one — a missing currency is filled in later from a configured default, so
        guessing here would only replace a correct default with a wrong guess.

        The message comes with today's date and weekday in the person's time zone. When the
        message says which day the purchase happened — "вчера", "позавчера", "в пятницу", "15-го"
        — answer with occurred_on: that day as YYYY-MM-DD, counted from today. A weekday means the
        most recent such day before today. When the message names no day, answer occurred_on as
        null: it will be recorded as today.

        Sometimes the message has already been recorded and the person wants it changed — "нет,
        1500", "это было позавчера", "это подарок". Then you are also given the current record and
        their correction. Answer with the complete corrected record: every line, not only the one
        that changed, with the correction applied and everything it does not mention kept as it
        is. Days in a correction are counted from today, as above.

        A message may name zero, one or several purchases. Produce one line item per purchase that
        has an amount. If a merchant is named and it matches one of the known merchants you were
        given, set known_merchant_id to that merchant's id. If a merchant is named but matches no
        known merchant, put its name in merchant_name as the person wrote it. If no merchant is
        named, answer both known_merchant_id and merchant_name as null. If a merchant is named and
        you are unsure whether it is already known, you may call list_merchants to check the full
        list before answering.

        Every answer also says what kind of record this is: "expense" for money spent, "income" for
        money received — a salary, a refund, a gift, a loan you were given — and "balance" only
        when the person states what a wallet's balance is right now, not describing a transaction
        at all ("на райфе осталось 45 тысяч", "у меня в кошельке 20 евро"). Match a category from
        the income branch when kind is "income", and from every other branch when kind is
        "expense"; for kind "balance", items must be empty — there is nothing to categorise, only a
        balance to state.

        You may be offered a list of wallets, each with an id, a name, a currency, and sometimes
        the words the person uses for it. When the message names a wallet — by its name or by one
        of those words, such as "с налички" for a wallet called "Cash" — answer wallet_id with that
        wallet's id. When the message names no wallet, or none of the offered wallets fits, answer
        wallet_id as null: the ledger picks the default wallet for the spending's currency on its
        own.

        For kind "balance", also answer balance_amount with the number the person states as the
        wallet's current balance, and balance_currency with the currency they name, or null when
        they name none — the wallet's own currency is used then. balance_amount and
        balance_currency stay null for every other kind.

        <examples>
        <example>
        Message: "кофе 250 рсд"
        Answer with kind "expense" and one item: description "кофе", amount 250, currency "RSD",
        category_slug the one whose meaning is everyday food and drink, no merchant.
        </example>
        <example>
        Message: "купил вчера штуку евро на продукты"
        Answer with kind "expense" and one item: description "продукты", amount 1000, currency
        "EUR", category_slug the one whose meaning is groceries, no merchant, and occurred_on the
        day before today. "Штуку" is how people say one thousand; the message has no digits and
        does not need any.
        </example>
        <example>
        Message: "такси двести пятьдесят"
        Answer with kind "expense" and one item: description "такси", amount 250, category_slug
        the one whose meaning is transport, no merchant. The message names no currency, so currency
        is null — do not guess RSD, EUR or anything else.
        </example>
        <example>
        Message: "Lidl 45,30 eur продукты, потом кофе 2.50 eur"
        Answer with kind "expense" and two items. First: description "продукты", amount 45.3,
        currency "EUR", category_slug the one whose meaning is groceries, merchant_name "Lidl" (or
        known_merchant_id instead, if Lidl is already a known merchant). Second: description
        "кофе", amount 2.5, currency "EUR", category_slug the one whose meaning is everyday food
        and drink, no merchant. The comma in "45,30" is a decimal separator: the answer is the
        number 45.3, not a string.
        </example>
        <example>
        Message: "заняла у Маши 5000 рсд"
        Answer with no items at all. The message states an amount but describes a loan received,
        not a purchase — there is nothing here to record as spending. Kind is "income" (money the
        person now has), not "expense".
        </example>
        <example>
        Message: "пришла зарплата 2000 евро на Wise"
        Answer with kind "income" and one item: description "зарплата", amount 2000, currency
        "EUR", category_slug the one whose meaning is salary income, no merchant. If a wallet named
        "Wise" (or aliased to it) is among the offered wallets, set wallet_id to its id; otherwise
        leave it null.
        </example>
        <example>
        Message: "на райфе 45 тысяч"
        Answer with kind "balance", no items at all, balance_amount 45000, balance_currency null —
        the message names no currency, so the wallet's own currency applies. If a wallet aliased
        "райф" is among the offered wallets, set wallet_id to its id.
        </example>
        </examples>
        """;

    public static string RenderCategories(IReadOnlyList<CategoryOption> categories) =>
        string.Join('\n', categories.Select(RenderCategory));

    public static string RenderMerchantHints(IReadOnlyList<MerchantOption> merchantHints) =>
        merchantHints.Count == 0
            ? "No known merchants are offered for this message."
            : string.Join('\n', merchantHints.Select(m => $"- {m.Id}: {m.DisplayName}"));

    public static string RenderWallets(IReadOnlyList<WalletOption> wallets) =>
        wallets.Count == 0
            ? "No wallets are offered for this message; wallet_id must be null."
            : string.Join('\n', wallets.Select(RenderWallet));

    public static string BuildUserTurn(CategorizationRequest request)
    {
        var turn = $"""
            Today: {request.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({request.Today.DayOfWeek})

            Message:
            {request.RawText}

            Categories:
            {RenderCategories(request.Categories)}

            Known merchants:
            {RenderMerchantHints(request.MerchantHints)}

            Wallets:
            {RenderWallets(request.Wallets ?? [])}
            """;

        return request.Correction is { } correction ? $"{turn}\n\n{RenderCorrection(correction)}" : turn;
    }

    static string RenderCorrection(CorrectionRequest correction) =>
        $"""
        Current record (dated {correction.CurrentOccurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}):
        {RenderCurrentLines(correction.CurrentLines)}

        Correction from the person:
        {correction.Instruction}
        """;

    static string RenderCurrentLines(IReadOnlyList<RecordedLine> lines) =>
        lines.Count == 0
            ? "- nothing was recorded"
            : string.Join('\n', lines.Select(RenderCurrentLine));

    static string RenderCurrentLine(RecordedLine line)
    {
        var text = $"- {line.Description}: {line.Amount.Amount.ToString("0.####", CultureInfo.InvariantCulture)} "
            + $"{line.Amount.Currency}, category {line.CategorySlug ?? "none"}";
        return line.MerchantName is { } merchant ? $"{text}, merchant {merchant}" : text;
    }

    static string RenderCategory(CategoryOption category) =>
        category.ParentSlug is null
            ? $"- {category.Slug}: {category.NameEn} / {category.NameRu}"
            : $"- {category.Slug} (under {category.ParentSlug}): {category.NameEn} / {category.NameRu}";

    static string RenderWallet(WalletOption wallet)
    {
        var aliases = wallet.Aliases.Count == 0 ? "" : $", also called {string.Join(", ", wallet.Aliases)}";
        var marker = wallet.IsDefaultForCurrency ? $", the default wallet for {wallet.Currency}" : "";
        return $"- {wallet.Id}: {wallet.Name} ({wallet.Currency}){aliases}{marker}";
    }
}
```

Run:

```
dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj --filter CategorizationPromptTests
```

Expect every `CategorizationPromptTests` test green.

---

#### Step 6: `ChatCategorizer` — write the failing tests

**6a.** Replace the full contents of `tests/Noof.Ledger.Ai.Tests/ChatCategorizerTests.cs` with:

```csharp
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai.Tests;

// The categoriser with no provider underneath it at all. What reaches the wire is pinned by
// ChatCategorizerOverAnthropicTests; this pins what the provider-neutral layer itself says.
public class ChatCategorizerTests
{
    static readonly CategorizationRequest Request = new(
        "кофе 250", new DateOnly(2026, 9, 22),
        [new CategoryOption("food-drink", "Food & Drink", "Еда и напитки", null)], [], []);

    [Fact]
    public async Task Every_tool_it_offers_is_strict_in_provider_neutral_terms_and_in_no_providers_own()
    {
        var provider = new ScriptedChatClient()
            .Answer(new FunctionCallContent("call_1", "record_transaction",
                new Dictionary<string, object?>
                {
                    ["items"] = Array.Empty<object>(), ["occurred_on"] = null, ["kind"] = "expense",
                    ["wallet_id"] = null, ["balance_amount"] = null, ["balance_currency"] = null,
                }))
            .Answer(new FunctionCallContent("call_2", "canonicalize_merchant",
                new Dictionary<string, object?> { ["display_name"] = "Lidl" }));
        var categorizer = new ChatCategorizer(new FixedChatClientFactory(provider));

        await categorizer.ProposeAsync(Request, TestContext.Current.CancellationToken);
        await categorizer.CanonicalizeMerchantAsync("lidl", [], TestContext.Current.CancellationToken);

        var offered = provider.Requests.SelectMany(request => request.Options!.Tools!).ToList();
        offered.Select(tool => tool.Name).Should().BeEquivalentTo(["list_merchants", "record_transaction", "canonicalize_merchant"]);
        offered.Should().OnlyContain(tool => tool.IsStrict());
        offered.Should().OnlyContain(tool => !tool.AdditionalProperties.ContainsKey("Strict"),
            "\"Strict\" is what one provider's adapter reads; saying it here would tie this layer to that provider");
    }

    sealed class FixedChatClientFactory(IChatClient client) : IChatClientFactory
    {
        public Task<IChatClient> CreateAsync(CancellationToken cancellationToken) => Task.FromResult(client);
    }
}
```

**6b.** In `tests/Noof.Ledger.Ai.Tests/ChatCategorizerFunctionInvokerTests.cs`, replace:

```csharp
    static FunctionCallContent RecordSpendingCall(string callId, decimal amount) =>
        new(callId, "record_spending", new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["description"] = "кофе", ["amount"] = amount, ["currency"] = null,
                    ["category_slug"] = "food-drink", ["merchant_name"] = null,
                },
            },
            ["occurred_on"] = null,
        });
```

with:

```csharp
    static FunctionCallContent RecordTransactionCall(string callId, decimal amount) =>
        new(callId, "record_transaction", new Dictionary<string, object?>
        {
            ["items"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["description"] = "кофе", ["amount"] = amount, ["currency"] = null,
                    ["category_slug"] = "food-drink", ["merchant_name"] = null,
                },
            },
            ["occurred_on"] = null,
            ["kind"] = "expense",
            ["wallet_id"] = null,
            ["balance_amount"] = null,
            ["balance_currency"] = null,
        });
```

Then, in the same file, rename every call site and every literal `"record_spending"` string:

- Every `RecordSpendingCall(` call → `RecordTransactionCall(` (in `A_direct_record_spending_answer_on_the_first_call_ends_the_loop_in_one_call`, `A_tiny_amount_also_round_trips_into_decimal_exactly`, `Calling_list_merchants_first_is_answered_locally_and_the_follow_up_offers_only_record_spending_forced`, `Calling_both_tools_at_once_on_the_first_turn_is_read_as_record_spending_with_no_follow_up`).
- Method name `A_direct_record_spending_answer_on_the_first_call_ends_the_loop_in_one_call` → `A_direct_record_transaction_answer_on_the_first_call_ends_the_loop_in_one_call`.
- Inside that method: `call1.Tools!.Select(t => t.Name).Should().BeEquivalentTo(["list_merchants", "record_spending"]);` → `["list_merchants", "record_transaction"]`.
- Method name `Calling_list_merchants_first_is_answered_locally_and_the_follow_up_offers_only_record_spending_forced` → `Calling_list_merchants_first_is_answered_locally_and_the_follow_up_offers_only_record_transaction_forced`.
- Inside that method:
  ```csharp
        call2.Options!.Tools!.Select(t => t.Name).Should().BeEquivalentTo(["record_spending"],
            "record_spending is structurally the only tool left to call, ruling out a second lookup");
        call2.Options.Tools!.Should().OnlyContain(tool => tool.IsStrict());
        call2.Options.ToolMode.Should().Be(ChatToolMode.RequireSpecific("record_spending"));
  ```
  becomes
  ```csharp
        call2.Options!.Tools!.Select(t => t.Name).Should().BeEquivalentTo(["record_transaction"],
            "record_transaction is structurally the only tool left to call, ruling out a second lookup");
        call2.Options.Tools!.Should().OnlyContain(tool => tool.IsStrict());
        call2.Options.ToolMode.Should().Be(ChatToolMode.RequireSpecific("record_transaction"));
  ```
- Method name `Calling_both_tools_at_once_on_the_first_turn_is_read_as_record_spending_with_no_follow_up` → `Calling_both_tools_at_once_on_the_first_turn_is_read_as_record_transaction_with_no_follow_up`; inside it, `provider.Requests.Should().ContainSingle("record_spending answers the request outright even when list_merchants was also called");` → `"record_transaction answers the request outright even when list_merchants was also called"`.

Run:

```
dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj --filter "ChatCategorizerTests|ChatCategorizerFunctionInvokerTests"
```

Expect every test in both files to fail: `ChatCategorizer` still offers a tool named `record_spending` (`ChatCategorizer.cs`'s `RecordSpendingName` const), so `FindCall(response, RecordSpendingName)` never matches a `FunctionCallContent` named `"record_transaction"` and every `ProposeAsync` call throws `ModelCallException` instead of returning a proposal — visible as `ThrowsException` failures on tests that call `ProposeAsync` directly, and as `BeEquivalentTo` mismatches (`"record_spending"` still offered, not `"record_transaction"`) on the ones that inspect `Options.Tools`.

---

#### Step 7: `ChatCategorizer` — rename the tool and read the new fields

Replace the full contents of `src/Noof.Ledger.Ai/ChatCategorizer.cs` with:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Ai;

// Everything about categorisation that does not depend on who answers it: the prompt, the schemas,
// the list_merchants round trip, reading the answer back. The provider is whatever IChatClient the
// factory hands out, and every failure it can have arrives here already as a ModelCallException.
internal sealed class ChatCategorizer(IChatClientFactory clientFactory) : ICategorizer
{
    const string RecordTransactionName = "record_transaction";
    const string RecordTransactionDescription =
        "Record the spending, income or balance statement described in the message.";

    const string ListMerchantsName = "list_merchants";
    const string ListMerchantsDescription =
        "List every merchant this ledger already knows, with the id to use for each. " +
        "Call this only if the message names a merchant that is not in the known merchants given to you, " +
        "and you want to check whether it is already known under a different spelling.";

    const string CanonicalizeMerchantName = "canonicalize_merchant";
    const string CanonicalizeMerchantDescription = "The display name to store for this merchant.";

    static readonly JsonElement CanonicalizeMerchantSchema = BuildCanonicalizeMerchantSchema();

    public async Task<CategorizationProposal> ProposeAsync(CategorizationRequest request, CancellationToken cancellationToken)
    {
        var recordTransaction = new SchemaTool(
            RecordTransactionName, RecordTransactionDescription,
            CategorizationSchema.BuildRecordTransaction(request.Categories, request.MerchantHints, request.Wallets ?? []));

        // AllMerchants, not MerchantHints: the hints are the handful the local scan already found and
        // the model has already seen. It only calls this tool when none of them fit, so answering with
        // the same short list would make the tool pointless (Locked design decision 9).
        var listMerchants = new SchemaFunction(
            ListMerchantsName, ListMerchantsDescription,
            CategorizationSchema.BuildListMerchants(), () => JsonSerializer.Serialize(request.AllMerchants));

        // record_transaction is declaration-only, so a call to it ends the loop and comes back here;
        // list_merchants is answered locally and sent back. AnswerToolGuard makes that follow-up offer
        // record_transaction alone and force it, so there is structurally no second lookup to reach for
        // (Locked design decision 3) - enforced by the API, not by the model behaving.
        //
        // 1, not 2: the limit counts round trips, and once it is reached FunctionInvokingChatClient
        // still sends one last request with every declaration stripped - which the guard re-arms.
        // So 1 means at most two provider calls; 2 lets a second lookup through and makes three.
        using var chat = new FunctionInvokingChatClient(
            new AnswerToolGuard(await clientFactory.CreateAsync(cancellationToken), recordTransaction))
        {
            MaximumIterationsPerRequest = 1,
        };

        // Both tools are offered and one is forced (RequireAny, not Auto): the model answers either
        // record_transaction directly or list_merchants first, never plain text (operator, 2026-09-23).
        // System is the same across turns; per-request data lives only in the user turn.
        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, CategorizationPrompt.BuildUserTurn(request))],
            new ChatOptions
            {
                Instructions = CategorizationPrompt.System,
                Tools = [listMerchants, recordTransaction],
                ToolMode = ChatToolMode.RequireAny,
            },
            cancellationToken);

        return FindCall(response, RecordTransactionName) is { } call
            ? ToProposal(call)
            : throw new ModelCallException(
                ModelFailureKind.Transient,
                $"The model answered without a {RecordTransactionName} call (finish reason: {response.FinishReason}).");
    }

    public async Task<string> CanonicalizeMerchantAsync(
        string merchantText, IReadOnlyList<MerchantOption> knownMerchants, CancellationToken cancellationToken)
    {
        using var chat = await clientFactory.CreateAsync(cancellationToken);

        var instructions =
            $"""
            Decide the display name to store for a merchant mentioned in a spending message. The
            message named: "{merchantText}". If it is clearly the same merchant as one already
            known, answer with THAT existing display name exactly, character for character.
            Otherwise answer with a short, tidy display name for the new merchant.

            Known merchants (id and display name, as JSON): {JsonSerializer.Serialize(knownMerchants)}
            """;

        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, merchantText)],
            new ChatOptions
            {
                MaxOutputTokens = 256,
                Instructions = instructions,
                Tools = [new SchemaTool(CanonicalizeMerchantName, CanonicalizeMerchantDescription, CanonicalizeMerchantSchema)],
                ToolMode = ChatToolMode.RequireSpecific(CanonicalizeMerchantName),
            },
            cancellationToken);

        if (FindCall(response, CanonicalizeMerchantName) is not { } call)
        {
            throw new ModelCallException(
                ModelFailureKind.Transient,
                $"{CanonicalizeMerchantName} produced no tool call (finish reason: {response.FinishReason}).");
        }

        var payload = ToPayload<CanonicalizeMerchantPayload>(call);
        if (payload is null || string.IsNullOrWhiteSpace(payload.DisplayName))
            throw new ModelCallException(ModelFailureKind.Transient, $"{CanonicalizeMerchantName} returned an empty display_name.");

        return payload.DisplayName;
    }

    static FunctionCallContent? FindCall(ChatResponse response, string name) =>
        response.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(call => call.Name == name);

    static CategorizationProposal ToProposal(FunctionCallContent call)
    {
        var payload = ToPayload<RecordTransactionPayload>(call);
        if (payload is null)
            throw new ModelCallException(ModelFailureKind.Transient, $"{RecordTransactionName} returned an empty payload.");

        return new CategorizationProposal(
            [.. payload.Items.Select(i => i.ToProposedLineItem())],
            payload.OccurredOn,
            payload.Kind,
            string.IsNullOrEmpty(payload.WalletId) ? null : Guid.Parse(payload.WalletId),
            payload.BalanceAmount,
            payload.BalanceCurrency);
    }

    // FunctionCallContent.Arguments holds one JsonElement per top-level parameter, produced by
    // System.Text.Json with no object converter - the response's own token text, not a re-encoded
    // value (decompiled, operator 2026-09-23; Verified facts table). Re-serialising the dictionary
    // and deserialising it into the payload record in one step is what keeps "amount" and
    // "balance_amount" decimals read straight off that token text, never a double.
    static T? ToPayload<T>(FunctionCallContent call) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToElement(call.Arguments));

    static JsonElement BuildCanonicalizeMerchantSchema()
    {
        const string raw = """
            {
              "type": "object",
              "additionalProperties": false,
              "required": ["display_name"],
              "properties": {
                "display_name": { "type": "string", "description": "The display name to store for this merchant." }
              }
            }
            """;
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    // Deserialisation-only DTOs, private to this file: the JSON field names the tool schema
    // promises ("currency", not "currency_code"; "wallet_id", not "walletId") do not all match the
    // application-layer records' own property names, so this maps explicitly, field by field,
    // rather than trusting a naming-policy convention that is wrong for more than one field.
    sealed record RecordTransactionPayload(
        [property: JsonPropertyName("items")] IReadOnlyList<ProposedLineItemDto> Items,
        [property: JsonPropertyName("occurred_on")] string? OccurredOn,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("wallet_id")] string? WalletId,
        [property: JsonPropertyName("balance_amount")] decimal? BalanceAmount,
        [property: JsonPropertyName("balance_currency")] string? BalanceCurrency);

    sealed record ProposedLineItemDto(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("category_slug")] string CategorySlug,
        [property: JsonPropertyName("known_merchant_id")] string? KnownMerchantId,
        [property: JsonPropertyName("merchant_name")] string? MerchantName)
    {
        public ProposedLineItem ToProposedLineItem() => new(
            Description, Amount, Currency, CategorySlug,
            string.IsNullOrEmpty(KnownMerchantId) ? null : Guid.Parse(KnownMerchantId), MerchantName);
    }

    sealed record CanonicalizeMerchantPayload([property: JsonPropertyName("display_name")] string DisplayName);
}
```

Run:

```
dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj --filter "ChatCategorizerTests|ChatCategorizerFunctionInvokerTests"
```

Expect every test in both files green.

---

#### Step 8: The Anthropic wire-level tests — write the failing tests, then fix the fixtures

`AnswerToolGuard.cs` and `SchemaTools.cs` need no source change (`AnswerToolGuard` is generic over whatever `AIFunctionDeclaration` it is built with — see its constructor in the existing source — so it already re-forces `record_transaction` correctly now that `ChatCategorizer` builds the tool with that name). The remaining work in this step is bringing every test that hard-codes the literal `"record_spending"` up to date.

**8a.** In `tests/Noof.Ledger.Ai.Tests/AnswerToolGuardTests.cs`, replace:

```csharp
    static readonly SchemaTool Answer = new("record_spending", "The answer.", NoArguments);
```

with:

```csharp
    static readonly SchemaTool Answer = new("record_transaction", "The answer.", NoArguments);
```

and replace both occurrences of:

```csharp
        sent.ToolMode.Should().BeOfType<RequiredChatToolMode>().Which.RequiredFunctionName.Should().Be("record_spending");
```

with:

```csharp
        sent.ToolMode.Should().BeOfType<RequiredChatToolMode>().Which.RequiredFunctionName.Should().Be("record_transaction");
```

(one instance is in `Once_a_tool_result_is_in_the_history_only_the_answer_tool_is_offered_and_it_is_forced`, the other in `The_answer_tool_comes_back_even_when_every_tool_was_stripped_for_the_last_iteration`).

**8b.** In `tests/Noof.Ledger.Ai.Tests/Anthropic/AnthropicTranslatingChatClientTests.cs`, replace both occurrences of:

```csharp
        var tool = new SchemaTool("record_spending", "The answer.", NoArguments);
```

with:

```csharp
        var tool = new SchemaTool("record_transaction", "The answer.", NoArguments);
```

(one in `A_tool_marked_strict_reaches_the_SDK_adapter_carrying_its_Strict_property`, the other in `The_callers_options_are_left_as_they_were`), and replace:

```csharp
        sent.Name.Should().Be("record_spending");
```

with:

```csharp
        sent.Name.Should().Be("record_transaction");
```

**8c.** In `tests/Noof.Ledger.Ai.Tests/AnthropicResponses.cs`, replace the full contents with:

```csharp
namespace Noof.Ledger.Ai.Tests;

// Real response shapes, kept in one place so every test reads from the same ground truth instead
// of each hand-rolling its own JSON. record_transaction, canonicalize_merchant and list_merchants
// are all genuine "tool_use" blocks, forced by tool_choice (operator, 2026-09-23; renamed for M9,
// 2026-09-24).
public static class AnthropicResponses
{
    public const string RecordTransactionJsonAnswer = """
        {"id":"msg_01","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_01","name":"record_transaction","input":{"items":[{"description":"Coffee","amount":3.50,"currency":"EUR","category_slug":"food-drink","merchant_name":null}],"occurred_on":null,"kind":"expense","wallet_id":null,"balance_amount":null,"balance_currency":null}}],
         "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
        """;

    public const string ListMerchantsToolUse = """
        {"id":"msg_02","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_02","name":"list_merchants","input":{}}],
         "stop_reason":"tool_use","stop_sequence":null,
         "usage":{"input_tokens":80,"output_tokens":10}}
        """;

    public const string CanonicalizeMerchantJsonAnswer = """
        {"id":"msg_03","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_03","name":"canonicalize_merchant","input":{"display_name":"Lidl"}}],
         "stop_reason":"tool_use","stop_sequence":null,
         "usage":{"input_tokens":40,"output_tokens":8}}
        """;

    public const string RecordTransactionFromWordsAnswer = """
        {"id":"msg_05","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_05","name":"record_transaction","input":{"items":[{"description":"продукты","amount":1000,"currency":"EUR","category_slug":"food-drink","merchant_name":"Lidl"}],"occurred_on":null,"kind":"expense","wallet_id":null,"balance_amount":null,"balance_currency":null}}],
         "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
        """;

    public const string RecordTransactionWithDateAnswer = """
        {"id":"msg_06","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[{"type":"tool_use","id":"toolu_06","name":"record_transaction","input":{"items":[{"description":"продукты","amount":1000,"currency":"EUR","category_slug":"food-drink","merchant_name":null}],"occurred_on":"2026-09-21","kind":"expense","wallet_id":null,"balance_amount":null,"balance_currency":null}}],
         "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
        """;

    public const string NoAnswerAtAll = """
        {"id":"msg_04","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
         "content":[],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":50,"output_tokens":0}}
        """;

    public const string AuthenticationError = """
        {"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}
        """;

    public static string GenericError(string type, string message) =>
        $$$"""{"type":"error","error":{"type":"{{{type}}}","message":"{{{message}}}"}}""";
}
```

**8d.** In `tests/Noof.Ledger.Ai.Tests/Anthropic/ChatCategorizerOverAnthropicTests.cs`:

Rename every `AnthropicResponses.RecordSpendingJsonAnswer` → `AnthropicResponses.RecordTransactionJsonAnswer` (5 call sites: `Returns_the_proposal_when_the_first_turn_answers_the_record_spending_tool_call`, `Sends_both_tools_strict_and_forced_with_no_output_config_on_the_first_call`, `No_request_carries_an_anthropic_beta_header`, the second `handler.Enqueue` in `Answers_list_merchants_then_sends_one_follow_up_forced_onto_record_spending`, and the first `handler.Enqueue` in `A_proposal_asks_for_the_configured_output_budget_and_a_canonicalisation_for_a_small_one`).

Rename `AnthropicResponses.RecordSpendingFromWordsAnswer` → `AnthropicResponses.RecordTransactionFromWordsAnswer` (in `Maps_an_amount_read_from_words_and_a_merchant_name`).

Rename `AnthropicResponses.RecordSpendingWithDateAnswer` → `AnthropicResponses.RecordTransactionWithDateAnswer` (in `Tells_the_model_today_and_maps_the_day_it_answers`).

Rename the two test methods that name the tool in their own name:
- `Returns_the_proposal_when_the_first_turn_answers_the_record_spending_tool_call` → `Returns_the_proposal_when_the_first_turn_answers_the_record_transaction_tool_call`
- `Answers_list_merchants_then_sends_one_follow_up_forced_onto_record_spending` → `Answers_list_merchants_then_sends_one_follow_up_forced_onto_record_transaction`

Inside `Sends_both_tools_strict_and_forced_with_no_output_config_on_the_first_call`, replace:

```csharp
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString());
        names.Should().BeEquivalentTo(["list_merchants", "record_spending"]);
        foreach (var tool in tools.EnumerateArray())
            tool.GetProperty("strict").GetBoolean().Should().BeTrue($"{tool.GetProperty("name").GetString()} must be strict");

        var recordSpending = tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "record_spending");
        var currencyEnum = recordSpending.GetProperty("input_schema")
```

with:

```csharp
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString());
        names.Should().BeEquivalentTo(["list_merchants", "record_transaction"]);
        foreach (var tool in tools.EnumerateArray())
            tool.GetProperty("strict").GetBoolean().Should().BeTrue($"{tool.GetProperty("name").GetString()} must be strict");

        var recordTransaction = tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "record_transaction");
        var currencyEnum = recordTransaction.GetProperty("input_schema")
```

Inside the renamed `Answers_list_merchants_then_sends_one_follow_up_forced_onto_record_transaction`, replace:

```csharp
        secondTools.GetArrayLength().Should().Be(1, "only record_spending is offered, which is what structurally rules out a third list_merchants call");
        secondTools[0].GetProperty("name").GetString().Should().Be("record_spending");
        secondTools[0].GetProperty("strict").GetBoolean().Should().BeTrue(
            "FunctionInvokingChatClient strips every tool from this last request and AnswerToolGuard puts record_spending back - it must come back strict");
        secondSent.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("tool");
        secondSent.GetProperty("tool_choice").GetProperty("name").GetString().Should().Be("record_spending");
```

with:

```csharp
        secondTools.GetArrayLength().Should().Be(1, "only record_transaction is offered, which is what structurally rules out a third list_merchants call");
        secondTools[0].GetProperty("name").GetString().Should().Be("record_transaction");
        secondTools[0].GetProperty("strict").GetBoolean().Should().BeTrue(
            "FunctionInvokingChatClient strips every tool from this last request and AnswerToolGuard puts record_transaction back - it must come back strict");
        secondSent.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("tool");
        secondSent.GetProperty("tool_choice").GetProperty("name").GetString().Should().Be("record_transaction");
```

In the `An_amount_in_the_response_round_trips_into_decimal_exactly` theory, replace:

```csharp
        handler.Enqueue(HttpStatusCode.OK, $$$"""
            {"id":"msg_07","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
             "content":[{"type":"tool_use","id":"toolu_07","name":"record_spending","input":{"items":[{"description":"кофе","amount":{{{raw}}},"currency":null,"category_slug":"food-drink","merchant_name":null}]}}],
             "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
            """);
```

with:

```csharp
        handler.Enqueue(HttpStatusCode.OK, $$$"""
            {"id":"msg_07","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
             "content":[{"type":"tool_use","id":"toolu_07","name":"record_transaction","input":{"items":[{"description":"кофе","amount":{{{raw}}},"currency":null,"category_slug":"food-drink","merchant_name":null}],"occurred_on":null,"kind":"expense","wallet_id":null,"balance_amount":null,"balance_currency":null}}],
             "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":5}}
            """);
```

Run:

```
dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj --filter "AnswerToolGuardTests|AnthropicTranslatingChatClientTests|ChatCategorizerOverAnthropicTests"
```

Before edits 8a–8d, expect exactly the kind of failure `superpowers:test-driven-development` calls for seeing once: `AnswerToolGuardTests` fails two assertions expecting `RequiredFunctionName` to be `"record_spending"` (still true from the tool built in the test itself, so those two in isolation would still pass — the real red comes from `AnthropicTranslatingChatClientTests` and `ChatCategorizerOverAnthropicTests`, which exercise the renamed `ChatCategorizer` and canned responses). Confirm by running the whole `Noof.Ledger.Ai.Tests` project before step 8's edits and observing `ChatCategorizerOverAnthropicTests` red (the canned Anthropic responses still say `"name":"record_spending"`, so `ChatCategorizer`'s `FindCall(response, "record_transaction")` never matches and every one of its tests either throws `ModelCallException` where a proposal was expected, or never reaches the assertions on `tools`/`tool_choice` because the request body it inspects predates the Step 7 rename). After the edits in 8a–8d, expect every test in all three files green.

---

#### Step 9: Fix the stale comment in `SchemaTools.cs`, confirm no `record_spending` survives, then run the full suite and commit

`SchemaTools.cs`'s own source needs no behavioural change — `SchemaTool` and `AnswerToolGuard` are both generic over whatever name and `AIFunctionDeclaration` they are built with — but its top comment still names the old tool. In `src/Noof.Ledger.Ai/SchemaTools.cs`, replace:

```csharp
// A tool declared from a hand-built JSON Schema rather than one generated by reflection.
// Declaration-only: FunctionInvokingChatClient cannot invoke it, so a call to it ends the loop and
// comes back to the caller, which is how record_spending's answer is read.
```

with:

```csharp
// A tool declared from a hand-built JSON Schema rather than one generated by reflection.
// Declaration-only: FunctionInvokingChatClient cannot invoke it, so a call to it ends the loop and
// comes back to the caller, which is how record_transaction's answer is read.
```

Then confirm nothing in `src` or `tests` still names the retired tool:

```
git grep -n "record_spending" -- src tests
```

Expect zero hits. If this prints anything, one of Steps 1–8 was missed on that file — go back and fix it before continuing; `docs/OPEN-QUESTIONS.md` and `docs/BACKLOG.md` are the only places in the repo allowed to still say `record_spending`, and this task does not touch either file (that is Task 11's job).

```
dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj
dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj
```

Expect both green in full (not just the filtered subsets used above) — this also re-confirms `AiBoundaryTests` (nothing in this task names Anthropic or Groq outside `Noof.Ledger.Ai`) and `PublicSurfaceTests` (the two new Application types are exactly the allowlist addition made in Step 1, nothing else moved).

```
git add src/Noof.Ledger.Application/Categorization/CategorizationContract.cs
git add src/Noof.Ledger.Ai/CategorizationSchema.cs
git add src/Noof.Ledger.Ai/CategorizationPrompt.cs
git add src/Noof.Ledger.Ai/ChatCategorizer.cs
git add src/Noof.Ledger.Ai/SchemaTools.cs
git add tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs
git add tests/Noof.Ledger.Ai.Tests/CategorizationSchemaTests.cs
git add tests/Noof.Ledger.Ai.Tests/CategorizationPromptTests.cs
git add tests/Noof.Ledger.Ai.Tests/ChatCategorizerTests.cs
git add tests/Noof.Ledger.Ai.Tests/ChatCategorizerFunctionInvokerTests.cs
git add tests/Noof.Ledger.Ai.Tests/AnswerToolGuardTests.cs
git add tests/Noof.Ledger.Ai.Tests/AnthropicResponses.cs
git add tests/Noof.Ledger.Ai.Tests/Anthropic/AnthropicTranslatingChatClientTests.cs
git add tests/Noof.Ledger.Ai.Tests/Anthropic/ChatCategorizerOverAnthropicTests.cs
git commit -m "$(cat <<'EOF'
Rename record_spending to record_transaction (M9)

The answer tool now records income and balance statements too: it gains
root properties kind (expense/income/balance), wallet_id (an offered
wallet or null) and the stated balance (balance_amount/balance_currency,
flat rather than nested, per the plan's locked wire shape). Application's
CategorizationRequest/CategorizationProposal gain the matching trailing
fields; wallet resolution and mapping stay Task 4's job.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 3: The `wallet_balances` view and the balance read model

A wallet's balance is derived at the moment someone asks, never stored (M5, M6). This task writes the rule once as a PostgreSQL view, reads it through `IBalanceReadModel`, and writes it a second time as `BalanceSql.AsOfAsync`, the same rule cut off at one point of the ledger's order. Task 6 uses that to fill a statement's "balance was". Tests pin the rule directly against rows seeded straight into the context, because the write path that produces entries and checkpoints from a model's answer is Task 6's.

**Decisions this task makes (the contract leaves them open):**

- **Tie between two checkpoints.** Two statements of one (wallet, currency) can share `(occurred_on, occurred_at)`. The one whose transaction has the later `created_at` wins, and after that the higher `id`. So the statement recorded last is the one that stands, and the answer never depends on the heap.
- **An entry exactly at the checkpoint's `(occurred_on, occurred_at)` does not count.** "Strictly after" is taken literally: an entry with an equal key is treated as already inside the stated amount. The opening checkpoint (Task 5) sits at local midnight, so in practice this only matters for a record stamped at that exact instant.
- **`WalletBalance.LastCheckedOn`** is the latest `checked_on` over all of the wallet's currency lines, or `null` when none of them has a checkpoint.
- **Wallet order in `BalancesAsync`:** active wallets first, then by name (`StringComparer.OrdinalIgnoreCase`), then by id. **Line order** in both methods: the wallet's own currency first, then the others by code, ordinal.
- **`BalanceOfAsync` for an unknown wallet id** returns an empty list.
- **How the view is read:** `Database.SqlQueryRaw<WalletBalanceRow>` over an internal row type the model does not map. The view never enters the EF model: no entity, no `DbSet`, nothing in `LedgerDbContextModelSnapshot.cs`, nothing in `schema.expected.sql`.
- **`schema.expected.sql` does not change.** `SchemaSnapshotTests` compares against `Database.GenerateCreateScript()`, and that is built from the EF model alone. A view created by `migrationBuilder.Sql` is not in the model, the same way `ix_wallets_single_default` and the append-only triggers never appeared there. The file is not regenerated in this task.
- **`BalancesAsync` loads every wallet, then every view row, and joins them in memory.** A personal ledger has a handful of wallets. Do not turn this into a single SQL join: an inner join over the view loses the wallets nothing has counted for yet, and the contract lists every wallet.
- **Spec gap M11, assigned to this task by the contract:** "This month" counts `Expense` only. `EfSpendingReadModel.ThisMonthAsync` today filters only on status. Once Task 6 writes incomes with line items, a salary would show up as this month's spending. This task adds `AND t.kind = @expense` to that query, with a test (Step 12).

**Files:**
- Create: `src/Noof.Ledger.Application/Reporting/IBalanceReadModel.cs`
- Create: `src/Noof.Ledger.Persistence/Balances/EfBalanceReadModel.cs`, `src/Noof.Ledger.Persistence/Balances/BalanceSql.cs`
- Create: `src/Noof.Ledger.Persistence/Migrations/<timestamp>_AddWalletBalancesView.cs` (+ its generated `.Designer.cs`; `LedgerDbContextModelSnapshot.cs` is regenerated by the tool, and the model has not changed)
- Modify: `src/Noof.Ledger.Persistence/PersistenceRegistration.cs` (a `using` after line 13, a registration after line 46)
- Modify: `src/Noof.Ledger.Persistence/Reporting/EfSpendingReadModel.cs` (`ThisMonthAsync`: the `WHERE` clause, lines 100–102, and its parameters, lines 105–108)
- Modify: `tests/Noof.Ledger.Persistence.Tests/EfSpendingReadModelTests.cs` (one new test appended to the class)
- Modify: `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs` (the `["Noof.Ledger.Application"]` array, lines 43–58)
- Modify: `tests/Noof.Ledger.Host.Tests/PersistenceRegistrationTests.cs` (after line 44)
- Test: `tests/Noof.Ledger.Persistence.Tests/BalanceSeed.cs` (new helper), `EfBalanceReadModelTests.cs`, `WalletBalancesViewTests.cs`, `BalanceSqlTests.cs` (all new), `EfSpendingReadModelTests.cs` (one new test)

**Interfaces:**
- Consumes (Task 1): `Wallet` with `Archived`, `IsDefaultForCurrency` and `CreatedAt`. `Transaction` with `Guid? WalletId`, `TransactionKind Kind`, `long? TelegramChatId`, `int? TelegramMessageId`. `Entry`, `EntryRole.Principal`, `BalanceCheck`, `TransactionKind`. `LedgerDbContext.Entries` and `LedgerDbContext.BalanceChecks`. The column `transactions.kind integer NOT NULL` (`TransactionKind` stored as its number). In `EfSpendingReadModelTests`, Task 1's `NewTransaction(Guid? walletId, DateTimeOffset occurredAt, string timeZoneId, TransactionStatus status, DateOnly? occurredOn = null)`. The tables `entries` (`transaction_id`, `wallet_id`, `amount numeric(19,4)`, `currency varchar(3)`) and `balance_checks` (`transaction_id`, `wallet_id`, `stated_amount numeric(19,4)`, `currency varchar(3)`). The migration named `AddMoneyModel`.
- Produces:
  - `public sealed record WalletBalance(Guid WalletId, string WalletName, CurrencyCode WalletCurrency, bool Archived, IReadOnlyList<Money> Balances, DateOnly? LastCheckedOn)` and `public interface IBalanceReadModel { Task<IReadOnlyList<WalletBalance>> BalancesAsync(CancellationToken cancellationToken); Task<IReadOnlyList<Money>> BalanceOfAsync(Guid walletId, CancellationToken cancellationToken); }`, both in namespace `Noof.Ledger.Application.Reporting`. The interface is registered scoped in `AddNoofPersistence` as `EfBalanceReadModel`.
  - View `public.wallet_balances(wallet_id uuid, currency varchar(3), balance numeric(19,4), checked_on date)`, created by the migration `AddWalletBalancesView`.
  - `internal static class BalanceSql` (namespace `Noof.Ledger.Persistence.Balances`): `public static Task<decimal> AsOfAsync(LedgerDbContext db, Guid walletId, CurrencyCode currency, DateOnly occurredOn, DateTimeOffset occurredAt, Guid excludingTransactionId, CancellationToken ct)`.
  - `internal sealed class EfBalanceReadModel(LedgerDbContext db)` (namespace `Noof.Ledger.Persistence.Balances`).
  - `internal static class BalanceSeed` in `Noof.Ledger.Persistence.Tests`, with `AddWallet`, `Spend`, `Earn` and `State`. Later tasks' persistence tests may reuse it.

- [ ] **Step 1: Write the seeding helper and the failing read-model tests**

Create `tests/Noof.Ledger.Persistence.Tests/BalanceSeed.cs`:

```csharp
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

// Writes the rows the balance rule reads straight into the context. The tests that use it pin the rule itself;
// turning a model's answer into these rows is EfCategorizationStore's job and is tested there.
internal static class BalanceSeed
{
    static int nextMessageId = 60_000;

    public static Wallet AddWallet(LedgerDbContext db, CurrencyCode currency, string name = "Raiffeisen", bool archived = false)
    {
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            Name = name,
            Currency = currency,
            Archived = archived,
            CreatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        };
        db.Wallets.Add(wallet);
        return wallet;
    }

    public static Transaction Spend(
        LedgerDbContext db, Wallet wallet, decimal amount, DateOnly on, DateTimeOffset at,
        TransactionStatus status = TransactionStatus.Completed, CurrencyCode? currency = null, DateTimeOffset? createdAt = null) =>
        AddWithEntry(db, wallet, TransactionKind.Expense, -amount, on, at, status, currency, createdAt);

    public static Transaction Earn(
        LedgerDbContext db, Wallet wallet, decimal amount, DateOnly on, DateTimeOffset at,
        TransactionStatus status = TransactionStatus.Completed, CurrencyCode? currency = null, DateTimeOffset? createdAt = null) =>
        AddWithEntry(db, wallet, TransactionKind.Income, amount, on, at, status, currency, createdAt);

    public static Transaction State(
        LedgerDbContext db, Wallet wallet, decimal stated, DateOnly on, DateTimeOffset at,
        TransactionStatus status = TransactionStatus.Completed, CurrencyCode? currency = null, DateTimeOffset? createdAt = null)
    {
        var record = AddRecord(db, wallet, TransactionKind.BalanceCheck, on, at, status, createdAt);
        db.BalanceChecks.Add(new BalanceCheck
        {
            TransactionId = record.Id,
            WalletId = wallet.Id,
            Stated = new Money(stated, currency ?? wallet.Currency),
            ComputedBefore = 0m,
        });
        return record;
    }

    static Transaction AddWithEntry(
        LedgerDbContext db, Wallet wallet, TransactionKind kind, decimal signedAmount, DateOnly on, DateTimeOffset at,
        TransactionStatus status, CurrencyCode? currency, DateTimeOffset? createdAt)
    {
        var record = AddRecord(db, wallet, kind, on, at, status, createdAt);
        db.Entries.Add(new Entry
        {
            Id = Guid.NewGuid(),
            TransactionId = record.Id,
            WalletId = wallet.Id,
            Amount = new Money(signedAmount, currency ?? wallet.Currency),
            Role = EntryRole.Principal,
        });
        return record;
    }

    static Transaction AddRecord(
        LedgerDbContext db, Wallet wallet, TransactionKind kind, DateOnly on, DateTimeOffset at,
        TransactionStatus status, DateTimeOffset? createdAt)
    {
        var record = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = wallet.Id,
            Kind = kind,
            RawText = "seeded",
            Status = status,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = at,
            OccurredOn = on,
            TelegramChatId = 1,
            TelegramMessageId = Interlocked.Increment(ref nextMessageId),
            CreatedAt = createdAt ?? at,
        };
        db.Transactions.Add(record);
        return record;
    }
}
```

Create `tests/Noof.Ledger.Persistence.Tests/EfBalanceReadModelTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Balances;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfBalanceReadModelTests(PostgresFixture fixture)
{
    static DateOnly On(int day) => new(2026, 9, day);

    static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 9, day, hour, minute, 0, TimeSpan.Zero);

    async Task<LedgerDbContext> MigratedAsync()
    {
        var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return db;
    }

    static async Task<IReadOnlyList<Money>> SaveAndReadAsync(LedgerDbContext db, Wallet wallet)
    {
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return await new EfBalanceReadModel(db).BalanceOfAsync(wallet.Id, TestContext.Current.CancellationToken);
    }

    static async Task<DateOnly?> LastCheckedOnAsync(LedgerDbContext db, Wallet wallet) =>
        (await new EfBalanceReadModel(db).BalancesAsync(TestContext.Current.CancellationToken))
            .Single(balance => balance.WalletId == wallet.Id).LastCheckedOn;

    [Fact]
    public async Task With_no_checkpoint_the_balance_is_the_sum_of_the_entries()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 100.10m, On(2), At(2, 9));
        BalanceSeed.Spend(db, wallet, 0.20m, On(3), At(3, 9));
        BalanceSeed.Earn(db, wallet, 1000.00m, On(4), At(4, 9));

        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(899.70m, CurrencyCode.Rsd));
    }

    [Fact]
    public async Task A_checkpoint_re_anchors_the_balance_and_entries_before_it_stop_counting()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 300m, On(2), At(2, 9));
        BalanceSeed.State(db, wallet, 5000m, On(5), At(5, 9));
        BalanceSeed.Spend(db, wallet, 200m, On(6), At(6, 9));
        BalanceSeed.Earn(db, wallet, 50m, On(7), At(7, 9));

        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(4850m, CurrencyCode.Rsd));
        (await LastCheckedOnAsync(db, wallet)).Should().Be(On(5));
    }

    [Fact]
    public async Task Processing_order_never_changes_a_balance_only_the_ledger_order_does()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.State(db, wallet, 1000m, On(10), At(10, 9));
        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(1000m, CurrencyCode.Rsd));

        var processedLater = At(12, 18);
        // An earlier day, recorded after the statement.
        BalanceSeed.Spend(db, wallet, 100m, On(9), At(9, 18), createdAt: processedLater);
        // "вчера купил…" sent after the statement: the purchase's own day sorts first.
        BalanceSeed.Spend(db, wallet, 40m, On(9), At(10, 13), createdAt: processedLater);
        // A voice note sent at 08:59 and transcribed after the 09:00 statement.
        BalanceSeed.Spend(db, wallet, 30m, On(10), At(10, 8, 59), createdAt: processedLater);
        // Exactly the statement's own moment: already inside the stated amount.
        BalanceSeed.Spend(db, wallet, 25m, On(10), At(10, 9), createdAt: processedLater);

        (await SaveAndReadAsync(db, wallet)).Should().ContainSingle().Which.Should().Be(
            new Money(1000m, CurrencyCode.Rsd),
            "each of these sorts at or before the statement by (occurred_on, occurred_at), however late it was processed");

        BalanceSeed.Spend(db, wallet, 20m, On(10), At(10, 9, 1), createdAt: processedLater);
        BalanceSeed.Spend(db, wallet, 10m, On(11), At(11, 9), createdAt: processedLater);

        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(970m, CurrencyCode.Rsd));
    }

    [Fact]
    public async Task Of_two_statements_at_the_same_moment_the_one_recorded_later_wins()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.State(db, wallet, 700m, On(5), At(5, 9), createdAt: At(5, 9, 5));
        BalanceSeed.State(db, wallet, 500m, On(5), At(5, 9), createdAt: At(5, 9, 1));

        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(700m, CurrencyCode.Rsd));
    }

    [Fact]
    public async Task Only_completed_records_count()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 100m, On(2), At(2, 9));
        BalanceSeed.Spend(db, wallet, 1m, On(3), At(3, 9), TransactionStatus.Captured);
        BalanceSeed.Spend(db, wallet, 2m, On(3), At(3, 10), TransactionStatus.Failed);
        BalanceSeed.Spend(db, wallet, 4m, On(3), At(3, 11), TransactionStatus.Cancelled);
        BalanceSeed.Earn(db, wallet, 8m, On(3), At(3, 12), TransactionStatus.Cancelled);
        BalanceSeed.State(db, wallet, 999m, On(4), At(4, 9), TransactionStatus.Captured);
        BalanceSeed.State(db, wallet, 555m, On(4), At(4, 10), TransactionStatus.Failed);

        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(-100m, CurrencyCode.Rsd));
        (await LastCheckedOnAsync(db, wallet)).Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_statement_stops_anchoring_and_the_one_before_it_takes_over()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 100m, On(2), At(2, 9));
        var first = BalanceSeed.State(db, wallet, 1000m, On(3), At(3, 9));
        var second = BalanceSeed.State(db, wallet, 2000m, On(5), At(5, 9));
        BalanceSeed.Spend(db, wallet, 50m, On(6), At(6, 9));

        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(1950m, CurrencyCode.Rsd));
        (await LastCheckedOnAsync(db, wallet)).Should().Be(On(5));

        second.Status = TransactionStatus.Cancelled;
        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(950m, CurrencyCode.Rsd));
        (await LastCheckedOnAsync(db, wallet)).Should().Be(On(3));

        first.Status = TransactionStatus.Cancelled;
        (await SaveAndReadAsync(db, wallet)).Should().Equal(new Money(-150m, CurrencyCode.Rsd));
        (await LastCheckedOnAsync(db, wallet)).Should().BeNull();
    }

    [Fact]
    public async Task Each_currency_in_a_wallet_is_its_own_line_the_wallet_currency_first()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 1200m, On(2), At(2, 9));
        BalanceSeed.Spend(db, wallet, 5000m, On(2), At(2, 10), currency: CurrencyCode.Kzt);
        BalanceSeed.Spend(db, wallet, 10m, On(2), At(2, 11), currency: CurrencyCode.Eur);
        // A statement in EUR re-anchors the EUR line only.
        BalanceSeed.State(db, wallet, 100m, On(3), At(3, 9), currency: CurrencyCode.Eur);
        BalanceSeed.Spend(db, wallet, 2.50m, On(4), At(4, 9), currency: CurrencyCode.Eur);

        (await SaveAndReadAsync(db, wallet)).Should().Equal(
            new Money(-1200m, CurrencyCode.Rsd),
            new Money(97.50m, CurrencyCode.Eur),
            new Money(-5000m, CurrencyCode.Kzt));
    }

    [Fact]
    public async Task Every_wallet_is_listed_archived_ones_marked_and_last()
    {
        await using var db = await MigratedAsync();
        var alpha = BalanceSeed.AddWallet(db, CurrencyCode.Rsd, "Alpha");
        var beta = BalanceSeed.AddWallet(db, CurrencyCode.Eur, "Beta", archived: true);
        var gamma = BalanceSeed.AddWallet(db, CurrencyCode.Usd, "gamma");
        BalanceSeed.Spend(db, alpha, 10m, On(2), At(2, 9));
        BalanceSeed.State(db, beta, 40m, On(3), At(3, 9));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var balances = await new EfBalanceReadModel(db).BalancesAsync(TestContext.Current.CancellationToken);

        // "Main Wallet" is the wallet AddCaptureModel seeds into every migrated database.
        balances.Select(balance => balance.WalletName).Should().Equal("Alpha", "gamma", "Main Wallet", "Beta");

        var alphaLine = balances.Single(balance => balance.WalletId == alpha.Id);
        alphaLine.Archived.Should().BeFalse();
        alphaLine.WalletCurrency.Should().Be(CurrencyCode.Rsd);
        alphaLine.Balances.Should().Equal(new Money(-10m, CurrencyCode.Rsd));
        alphaLine.LastCheckedOn.Should().BeNull();

        var betaLine = balances.Single(balance => balance.WalletId == beta.Id);
        betaLine.Archived.Should().BeTrue();
        betaLine.WalletCurrency.Should().Be(CurrencyCode.Eur);
        betaLine.Balances.Should().Equal(new Money(40m, CurrencyCode.Eur));
        betaLine.LastCheckedOn.Should().Be(On(3));

        var gammaLine = balances.Single(balance => balance.WalletId == gamma.Id);
        gammaLine.Balances.Should().BeEmpty("nothing has counted for this wallet yet");
        gammaLine.LastCheckedOn.Should().BeNull();
    }

    [Fact]
    public async Task The_balance_of_an_unknown_wallet_is_empty()
    {
        await using var db = await MigratedAsync();

        var balance = await new EfBalanceReadModel(db).BalanceOfAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        balance.Should().BeEmpty();
    }
}
```

Create `tests/Noof.Ledger.Persistence.Tests/WalletBalancesViewTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class WalletBalancesViewTests(PostgresFixture fixture)
{
    [Fact]
    public async Task The_view_has_exactly_the_columns_the_contract_names()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var columns = await db.Database.SqlQueryRaw<string>(
            """
            SELECT column_name || ' ' || data_type
                   || COALESCE('(' || character_maximum_length || ')', '')
                   || COALESCE('(' || numeric_precision || ',' || numeric_scale || ')', '') AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'wallet_balances'
            ORDER BY ordinal_position
            """).ToListAsync(TestContext.Current.CancellationToken);

        columns.Should().Equal("wallet_id uuid", "currency character varying(3)", "balance numeric(19,4)", "checked_on date");
    }

    [Fact]
    public async Task Migrating_down_past_it_drops_the_view_and_migrating_up_restores_it()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

        // The full timestamped id, as MigrationContractTests names its targets, read from the assembly rather than
        // retyped: Task 1 chose the timestamp.
        var addMoneyModel = db.Database.GetMigrations().Single(id => id.EndsWith("_AddMoneyModel", StringComparison.Ordinal));

        await db.GetService<IMigrator>().MigrateAsync(addMoneyModel, TestContext.Current.CancellationToken);
        (await ViewCountAsync(db)).Should().Be(0);

        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        (await ViewCountAsync(db)).Should().Be(1);
    }

    static async Task<int> ViewCountAsync(LedgerDbContext db) =>
        (await db.Database.SqlQueryRaw<int>(
            """
            SELECT count(*)::int AS "Value"
            FROM information_schema.views
            WHERE table_schema = 'public' AND table_name = 'wallet_balances'
            """).ToListAsync(TestContext.Current.CancellationToken)).Single();
}
```

- [ ] **Step 2: Declare the read model's contract**

Create `src/Noof.Ledger.Application/Reporting/IBalanceReadModel.cs`:

```csharp
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Reporting;

public sealed record WalletBalance(
    Guid WalletId,
    string WalletName,
    CurrencyCode WalletCurrency,
    bool Archived,
    IReadOnlyList<Money> Balances,
    DateOnly? LastCheckedOn);

// Balances are derived when asked, never stored. Wallets come active first, then by name; a wallet nothing has counted
// for yet has no lines. Lines come in the wallet's own currency first, then the others by code.
public interface IBalanceReadModel
{
    Task<IReadOnlyList<WalletBalance>> BalancesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Money>> BalanceOfAsync(Guid walletId, CancellationToken cancellationToken);
}
```

In `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`, in the `["Noof.Ledger.Application"]` array, append `"WalletBalance", "IBalanceReadModel",` to the line `"RecentLineItem", "RecentTransaction", "MonthTotal", "MonthSummary",`. Other wave-2 tasks append to the same array. When merging, keep every name from both sides.

- [ ] **Step 3: Write the read model over a view that does not exist yet**

Create `src/Noof.Ledger.Persistence/Balances/EfBalanceReadModel.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Reporting;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Balances;

internal sealed class EfBalanceReadModel(LedgerDbContext db) : IBalanceReadModel
{
    public async Task<IReadOnlyList<WalletBalance>> BalancesAsync(CancellationToken cancellationToken)
    {
        var wallets = await db.Wallets.AsNoTracking().ToListAsync(cancellationToken);
        var rowsByWallet = (await Rows().ToListAsync(cancellationToken)).ToLookup(row => row.WalletId);

        return
        [
            .. wallets
                .OrderBy(wallet => wallet.Archived)
                .ThenBy(wallet => wallet.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(wallet => wallet.Id)
                .Select(wallet => new WalletBalance(
                    wallet.Id,
                    wallet.Name,
                    wallet.Currency,
                    wallet.Archived,
                    Ordered(rowsByWallet[wallet.Id], wallet.Currency),
                    rowsByWallet[wallet.Id].Max(row => row.CheckedOn))),
        ];
    }

    public async Task<IReadOnlyList<Money>> BalanceOfAsync(Guid walletId, CancellationToken cancellationToken)
    {
        var wallet = await db.Wallets.AsNoTracking().SingleOrDefaultAsync(w => w.Id == walletId, cancellationToken);
        if (wallet is null)
            return [];

        var rows = await Rows().Where(row => row.WalletId == walletId).ToListAsync(cancellationToken);
        return Ordered(rows, wallet.Currency);
    }

    // A raw query onto a type the model does not map, so the view never enters the EF model: it cannot show up in
    // the snapshot, in schema.expected.sql or in the next migration's diff.
    IQueryable<WalletBalanceRow> Rows() => db.Database.SqlQueryRaw<WalletBalanceRow>(
        """
        SELECT wallet_id AS "WalletId", currency AS "Currency", balance AS "Balance", checked_on AS "CheckedOn"
        FROM public.wallet_balances
        """);

    static IReadOnlyList<Money> Ordered(IEnumerable<WalletBalanceRow> rows, CurrencyCode walletCurrency) =>
    [
        .. rows
            .Select(row => new Money(row.Balance, new CurrencyCode(row.Currency)))
            .OrderBy(balance => balance.Currency == walletCurrency ? 0 : 1)
            .ThenBy(balance => balance.Currency),
    ];
}

internal sealed class WalletBalanceRow
{
    public Guid WalletId { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal Balance { get; init; }
    public DateOnly? CheckedOn { get; init; }
}
```

- [ ] **Step 4: Run the tests and watch them fail for the right reason**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter EfBalanceReadModelTests`
Expected: FAIL. Every test except `The_balance_of_an_unknown_wallet_is_empty` fails with `Npgsql.PostgresException : 42P01: relation "public.wallet_balances" does not exist`. The unknown-wallet test passes, because it returns before it reads the view.

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter WalletBalancesViewTests`
Expected: FAIL. `The_view_has_exactly_the_columns_the_contract_names` reports an empty collection. `Migrating_down_past_it_drops_the_view_and_migrating_up_restores_it` expects 1 and finds 0.

- [ ] **Step 5: Add the migration that creates the view**

Run: `dotnet ef migrations add AddWalletBalancesView --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence`

This opens no database connection. The model has not changed, so the generated `Up` and `Down` are empty. Leave the new `.Designer.cs` and `LedgerDbContextModelSnapshot.cs` exactly as generated. Replace the whole of the new `…_AddWalletBalancesView.cs` with the code below. It uses a file-scoped namespace, because `IDE0161` fails the build otherwise. Keep the class name and the timestamped file name the tool chose.

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Noof.Ledger.Persistence.Migrations;

/// <inheritdoc />
public partial class AddWalletBalancesView : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE VIEW public.wallet_balances AS
            WITH completed AS (
                -- 1 is TransactionStatus.Completed: nothing captured, failed or cancelled moves a balance.
                SELECT id, occurred_on, occurred_at, created_at
                FROM public.transactions
                WHERE status = 1
            ),
            latest_checkpoints AS (
                SELECT DISTINCT ON (bc.wallet_id, bc.currency)
                       bc.wallet_id, bc.currency, bc.stated_amount, c.occurred_on, c.occurred_at
                FROM public.balance_checks bc
                JOIN completed c ON c.id = bc.transaction_id
                -- Two statements at the same (occurred_on, occurred_at): the one recorded later stands, then the
                -- higher id, so the answer never depends on the heap.
                ORDER BY bc.wallet_id, bc.currency, c.occurred_on DESC, c.occurred_at DESC, c.created_at DESC, c.id DESC
            ),
            counted_entries AS (
                SELECT e.wallet_id, e.currency, e.amount, c.occurred_on, c.occurred_at
                FROM public.entries e
                JOIN completed c ON c.id = e.transaction_id
            ),
            pairs AS (
                SELECT wallet_id, currency FROM latest_checkpoints
                UNION
                SELECT wallet_id, currency FROM counted_entries
            )
            SELECT p.wallet_id,
                   CAST(p.currency AS varchar(3)) AS currency,
                   CAST(COALESCE(k.stated_amount, 0) + COALESCE(SUM(e.amount), 0) AS numeric(19,4)) AS balance,
                   k.occurred_on AS checked_on
            FROM pairs p
            LEFT JOIN latest_checkpoints k ON k.wallet_id = p.wallet_id AND k.currency = p.currency
            -- Strictly after: an entry at exactly the checkpoint's own moment is already inside the stated amount.
            LEFT JOIN counted_entries e ON e.wallet_id = p.wallet_id AND e.currency = p.currency
                AND (k.wallet_id IS NULL OR (e.occurred_on, e.occurred_at) > (k.occurred_on, k.occurred_at))
            GROUP BY p.wallet_id, p.currency, k.stated_amount, k.occurred_on;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP VIEW IF EXISTS public.wallet_balances;");
    }
}
```

Check what the tool did to `LedgerDbContextModelSnapshot.cs`, using `git diff src/Noof.Ledger.Persistence/Migrations/LedgerDbContextModelSnapshot.cs`. The model did not change, so there may be no diff at all, or a changed `ProductVersion` annotation at most. Anything more means the branch carries an unmigrated model change from elsewhere. Stop and report it. Do not fold it into this migration.

- [ ] **Step 6: Run the tests and see them pass**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter EfBalanceReadModelTests`
Expected: PASS (9).

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter WalletBalancesViewTests`
Expected: PASS (2).

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter SchemaSnapshotTests`
Expected: PASS, with `tests/Noof.Ledger.Persistence.Tests/schema.expected.sql` untouched. The script is generated from the EF model, and the view is not in the model. `git status` must not list `schema.expected.sql`.

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter MigrationContractTests`
Expected: PASS, including `The_model_has_no_pending_changes`.

- [ ] **Step 7: Watch each of the view's guards fail once**

The migration has only been applied to throwaway `noof_test_*` databases so far. Editing it now does not touch any database that keeps the migration. Update the template (Step 14) only after this step. Rebuild after each edit, and revert each edit before making the next one.

1. In the view's last `LEFT JOIN`, change `(e.occurred_on, e.occurred_at) > (k.occurred_on, k.occurred_at)` to `>=`.
   Run `--filter EfBalanceReadModelTests`. Expected: `Processing_order_never_changes_a_balance_only_the_ledger_order_does` FAILS. It finds 975 where 1000 is expected, because the entry at the statement's own moment is now counted. Revert.
2. In `latest_checkpoints`, change `c.created_at DESC` to `c.created_at ASC`.
   Expected: `Of_two_statements_at_the_same_moment_the_one_recorded_later_wins` FAILS (500, not 700). Revert.
3. Delete the line `WHERE status = 1` from `completed`.
   Expected: `Only_completed_records_count` FAILS, and so does `A_cancelled_statement_stops_anchoring_and_the_one_before_it_takes_over`. Revert.
4. In the last `LEFT JOIN`, change `(e.occurred_on, e.occurred_at) > (k.occurred_on, k.occurred_at)` to `e.occurred_at > k.occurred_at`, which orders by send instant alone.
   Expected: `Processing_order_never_changes_a_balance_only_the_ledger_order_does` FAILS (960, not 1000). The "вчера купил…" entry, sent after the statement but dated the day before, now counts. Revert.

After the reverts, run `--filter EfBalanceReadModelTests` once more. Expected: PASS (9).

- [ ] **Step 8: Write the failing `BalanceSql` tests**

Create `tests/Noof.Ledger.Persistence.Tests/BalanceSqlTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Balances;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class BalanceSqlTests(PostgresFixture fixture)
{
    static DateOnly On(int day) => new(2026, 9, day);

    static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 9, day, hour, minute, 0, TimeSpan.Zero);

    async Task<LedgerDbContext> MigratedAsync()
    {
        var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return db;
    }

    static Task<decimal> AsOfAsync(LedgerDbContext db, Wallet wallet, Transaction cutOff, Guid? excluding = null) =>
        BalanceSql.AsOfAsync(
            db, wallet.Id, wallet.Currency, cutOff.OccurredOn, cutOff.OccurredAt, excluding ?? cutOff.Id,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task It_leaves_out_the_excluded_record_and_everything_after_the_cut_off()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 100m, On(2), At(2, 9));
        BalanceSeed.State(db, wallet, 1000m, On(3), At(3, 9));
        var applying = BalanceSeed.Spend(db, wallet, 50m, On(4), At(4, 10));
        var later = BalanceSeed.Spend(db, wallet, 30m, On(6), At(6, 9));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await AsOfAsync(db, wallet, applying)).Should().Be(1000m);
        (await AsOfAsync(db, wallet, applying, excluding: later.Id)).Should().Be(
            950m, "a record exactly at the cut-off counts unless it is the one excluded");

        var sameInstantInBelgrade = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.FromHours(2));
        (await BalanceSql.AsOfAsync(
            db, wallet.Id, wallet.Currency, On(4), sameInstantInBelgrade, applying.Id, TestContext.Current.CancellationToken))
            .Should().Be(1000m, "a cut-off written with a non-zero offset is the same instant and gives the same answer");
    }

    [Fact]
    public async Task A_statement_being_applied_is_not_its_own_anchor()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 100m, On(2), At(2, 9));
        BalanceSeed.State(db, wallet, 1000m, On(3), At(3, 9));
        BalanceSeed.Spend(db, wallet, 50m, On(4), At(4, 9));
        var statement = BalanceSeed.State(db, wallet, 2000m, On(5), At(5, 9));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await AsOfAsync(db, wallet, statement)).Should().Be(950m);
        (await AsOfAsync(db, wallet, statement, excluding: Guid.NewGuid())).Should().Be(
            2000m, "without the exclusion the statement anchors itself, which is what the exclusion is for");
    }

    [Fact]
    public async Task Before_any_statement_it_is_the_sum_of_the_entries()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 100m, On(2), At(2, 9));
        var income = BalanceSeed.Earn(db, wallet, 40m, On(3), At(3, 9));
        BalanceSeed.State(db, wallet, 1000m, On(5), At(5, 9));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await AsOfAsync(db, wallet, income, excluding: Guid.NewGuid())).Should().Be(-60m);
    }

    [Fact]
    public async Task Only_completed_records_count()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 100m, On(2), At(2, 9));
        BalanceSeed.Spend(db, wallet, 7m, On(2), At(2, 10), TransactionStatus.Cancelled);
        BalanceSeed.State(db, wallet, 5000m, On(2), At(2, 11), TransactionStatus.Cancelled);
        var cutOff = BalanceSeed.Spend(db, wallet, 3m, On(3), At(3, 9), TransactionStatus.Captured);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await AsOfAsync(db, wallet, cutOff, excluding: Guid.NewGuid())).Should().Be(-100m);
    }

    [Fact]
    public async Task A_wallet_with_nothing_counted_holds_zero()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Eur);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var balance = await BalanceSql.AsOfAsync(
            db, wallet.Id, CurrencyCode.Eur, On(10), At(10, 9), Guid.NewGuid(), TestContext.Current.CancellationToken);

        balance.Should().Be(0m);
    }

    [Fact]
    public async Task At_the_end_of_the_ledger_it_agrees_with_the_view()
    {
        await using var db = await MigratedAsync();
        var wallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd);
        BalanceSeed.Spend(db, wallet, 1234.56m, On(2), At(2, 9));
        BalanceSeed.State(db, wallet, 45_000m, On(3), At(3, 9));
        BalanceSeed.Spend(db, wallet, 0.44m, On(3), At(3, 9, 30));
        BalanceSeed.Earn(db, wallet, 2000m, On(4), At(4, 9));
        BalanceSeed.Spend(db, wallet, 999m, On(4), At(4, 10), TransactionStatus.Cancelled);
        BalanceSeed.Spend(db, wallet, 12.30m, On(5), At(5, 9), currency: CurrencyCode.Eur);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var endOfLedger = new DateOnly(2100, 1, 1);
        var endInstant = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var view = await new EfBalanceReadModel(db).BalanceOfAsync(wallet.Id, TestContext.Current.CancellationToken);
        var rsd = await BalanceSql.AsOfAsync(
            db, wallet.Id, CurrencyCode.Rsd, endOfLedger, endInstant, Guid.NewGuid(), TestContext.Current.CancellationToken);
        var eur = await BalanceSql.AsOfAsync(
            db, wallet.Id, CurrencyCode.Eur, endOfLedger, endInstant, Guid.NewGuid(), TestContext.Current.CancellationToken);

        view.Should().Equal(new Money(46_999.56m, CurrencyCode.Rsd), new Money(-12.30m, CurrencyCode.Eur));
        rsd.Should().Be(46_999.56m);
        eur.Should().Be(-12.30m);
    }
}
```

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL to compile. `BalanceSql` does not exist. That is the red here, because this test cannot exist without the class it calls.

- [ ] **Step 9: Write `BalanceSql`**

Create `src/Noof.Ledger.Persistence/Balances/BalanceSql.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Balances;

// The wallet_balances rule cut off at one point of the ledger's order: what a wallet held just before a record, for a
// statement's "balance was". A view takes no parameters, so the rule is written twice, and
// BalanceSqlTests.At_the_end_of_the_ledger_it_agrees_with_the_view holds the two to one answer.
internal static class BalanceSql
{
    public static async Task<decimal> AsOfAsync(
        LedgerDbContext db, Guid walletId, CurrencyCode currency, DateOnly occurredOn, DateTimeOffset occurredAt,
        Guid excludingTransactionId, CancellationToken ct)
    {
        // Npgsql writes only offset-zero values to a timestamptz parameter.
        var cutOff = occurredAt.ToUniversalTime();

        var balance = await db.Database.SqlQuery<decimal>(
            $"""
            WITH completed AS (
                SELECT id, occurred_on, occurred_at, created_at
                FROM public.transactions
                WHERE status = {(int)TransactionStatus.Completed}
                  AND id <> {excludingTransactionId}
                  AND (occurred_on, occurred_at) <= ({occurredOn}, {cutOff})
            ),
            latest_checkpoint AS (
                SELECT bc.stated_amount, c.occurred_on, c.occurred_at
                FROM public.balance_checks bc
                JOIN completed c ON c.id = bc.transaction_id
                WHERE bc.wallet_id = {walletId} AND bc.currency = {currency.Value}
                ORDER BY c.occurred_on DESC, c.occurred_at DESC, c.created_at DESC, c.id DESC
                LIMIT 1
            )
            SELECT CAST(
                COALESCE((SELECT stated_amount FROM latest_checkpoint), 0)
                + COALESCE((
                    SELECT SUM(e.amount)
                    FROM public.entries e
                    JOIN completed c ON c.id = e.transaction_id
                    WHERE e.wallet_id = {walletId} AND e.currency = {currency.Value}
                      AND NOT EXISTS (
                          SELECT 1 FROM latest_checkpoint k
                          WHERE (c.occurred_on, c.occurred_at) <= (k.occurred_on, k.occurred_at))
                ), 0) AS numeric(19,4)) AS "Value"
            """).ToListAsync(ct);

        return balance.Single();
    }
}
```

`ToListAsync` rather than `SingleAsync`: with no LINQ operator composed on top, EF sends this SQL exactly as written. It does not wrap it in a sub-select.

- [ ] **Step 10: Run the tests, then watch the exclusion and the UTC conversion fail once**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter BalanceSqlTests`
Expected: PASS (6).

Delete the line `AND id <> {excludingTransactionId}` and run again. Expected: FAIL. `It_leaves_out_the_excluded_record_and_everything_after_the_cut_off` finds 950 where 1000 is expected, and `A_statement_being_applied_is_not_its_own_anchor` finds 2000 where 950 is expected. Revert.

Change `var cutOff = occurredAt.ToUniversalTime();` to `var cutOff = occurredAt;` and run again. Expected: FAIL. `It_leaves_out_the_excluded_record_and_everything_after_the_cut_off` throws from Npgsql (an `ArgumentException`, possibly wrapped in an `InvalidCastException`), saying it cannot write a `DateTimeOffset` with offset 02:00:00 to `timestamp with time zone` and only offset 0 (UTC) is supported. Every other test passes, because each of them passes UTC. Revert, and run again. Expected: PASS (6).

- [ ] **Step 11: Register the read model, test first**

In `tests/Noof.Ledger.Host.Tests/PersistenceRegistrationTests.cs`, after line 44 (`...GetRequiredService<ISpendingReadModel>()...`), add:

```csharp
        scope.ServiceProvider.GetRequiredService<IBalanceReadModel>().Should().NotBeNull();
```

(`using Noof.Ledger.Application.Reporting;` is already at line 8.)

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter PersistenceRegistrationTests`
Expected: FAIL with `No service for type 'Noof.Ledger.Application.Reporting.IBalanceReadModel' has been registered.`

In `src/Noof.Ledger.Persistence/PersistenceRegistration.cs`, add `using Noof.Ledger.Persistence.Balances;` after `using Noof.Ledger.Persistence.Auth;` (line 13), and after line 46 (`services.AddScoped<ISpendingReadModel, EfSpendingReadModel>();`) add:

```csharp
        services.AddScoped<IBalanceReadModel, EfBalanceReadModel>();
```

Run it again. Expected: PASS.

Tasks 4, 5 and 12 edit the same two files in this wave. When merging: keep one copy of each `using` directive (a duplicate is CS0105, an error under `TreatWarningsAsErrors`); Task 12's shape of `AddNoofPersistence` (a `connectionString` local) wins, and `services.AddScoped<IBalanceReadModel, EfBalanceReadModel>();` is re-added after `services.AddScoped<ISpendingReadModel, EfSpendingReadModel>();` in it.

- [ ] **Step 12: "This month" counts expenses only (M11), test first**

Append inside the class in `tests/Noof.Ledger.Persistence.Tests/EfSpendingReadModelTests.cs`:

```csharp
    [Fact]
    public async Task ThisMonthAsync_counts_expenses_only_never_income_or_statements()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = NewWallet(CurrencyCode.Rsd);
        var category = NewCategory("test-expenses-only", "Groceries");
        db.Wallets.Add(wallet);
        db.Categories.Add(category);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var expense = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        var salary = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        salary.Kind = TransactionKind.Income;
        var statement = NewTransaction(wallet.Id, now, "Europe/Belgrade", TransactionStatus.Completed);
        statement.Kind = TransactionKind.BalanceCheck;
        db.Transactions.AddRange(expense, salary, statement);
        db.LineItems.AddRange(
            NewLineItem(expense.Id, "market", new Money(250m, CurrencyCode.Rsd), category.Id, null),
            NewLineItem(salary.Id, "salary", new Money(2000m, CurrencyCode.Eur), category.Id, null),
            NewLineItem(statement.Id, "statement", new Money(45_000m, CurrencyCode.Rsd), category.Id, null));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var readModel = new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc);

        var summary = await readModel.ThisMonthAsync(TestContext.Current.CancellationToken);

        summary.Totals.Should().ContainSingle().Which.Should().Be(
            new MonthTotal("Groceries", CurrencyCode.Rsd, 250m),
            "a salary is not spending, and neither is a statement of what the wallet holds");
    }
```

The statement's line item is artificial, because Task 4's mapper gives a balance statement no items. It is there so the filter is pinned on `kind`, not on "has no lines".

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter ThisMonthAsync_counts_expenses_only_never_income_or_statements`
Expected: FAIL on `ContainSingle`. The totals hold two entries, `Groceries RSD 45250` and `Groceries EUR 2000`, where the single `Groceries RSD 250` was expected. That is the failure the filter exists to prevent.

In `src/Noof.Ledger.Persistence/Reporting/EfSpendingReadModel.cs`, `ThisMonthAsync`, replace

```csharp
                  AND t.status <> @cancelled
                GROUP BY COALESCE(c.name_en, @uncategorised), li.currency
```

with

```csharp
                  AND t.status <> @cancelled
                  AND t.kind = @expense
                GROUP BY COALESCE(c.name_en, @uncategorised), li.currency
```

and after `command.Parameters.Add(new NpgsqlParameter("cancelled", (int)TransactionStatus.Cancelled));` add

```csharp
            command.Parameters.Add(new NpgsqlParameter("expense", (int)TransactionKind.Expense));
```

Run the filter again. Expected: PASS.

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter EfSpendingReadModelTests`
Expected: PASS. Every existing test seeds through `NewTransaction`, whose `Kind` defaults to `Expense`, so none of them changes.

- [ ] **Step 13: Run the affected projects**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: PASS: the whole project, with 18 new tests (`EfBalanceReadModelTests` 9, `WalletBalancesViewTests` 2, `BalanceSqlTests` 6, `EfSpendingReadModelTests` 1).

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS. `PublicSurfaceTests` sees `WalletBalance` and `IBalanceReadModel` and finds them allowed. `No_public_concrete_service_crosses_an_infrastructure_boundary` passes because `EfBalanceReadModel`, `WalletBalanceRow` and `BalanceSql` are internal.

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: PASS.

- [ ] **Step 14: Update the test template, holding the suite lock**

The E2E suite and `MoneyStorageTests` clone `noof_ledger_test_template`, which needs the view before any later task's E2E test can read balances. Run from the repository root:

```powershell
$lock = 'C:\Users\noofs\AppData\Local\Temp\noof-suite.lock'
while (-not (New-Item -ItemType Directory $lock -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 30 }
try {
    $admin = if ($env:NOOF_TEST_PG) { $env:NOOF_TEST_PG } else { (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection").Trim() }
    $template = $admin -replace 'Database=postgres', 'Database=noof_ledger_test_template'
    if ($template -notmatch 'Database=noof_ledger_test_template') { throw "Refusing: the connection string does not name the test template." }
    dotnet ef database update --project src/Noof.Ledger.Persistence --startup-project src/Noof.Ledger.Persistence --connection $template
}
finally { Remove-Item $lock }
```

Expected: the output's last line names `AddWalletBalancesView`. Never run `dotnet ef database update` without `--connection`, because it resolves `noof_ledger`. From this point on, the migration is applied to a database that keeps it, so it must never be edited again.

- [ ] **Step 15: Run the whole solution, holding the suite lock**

```powershell
$lock = 'C:\Users\noofs\AppData\Local\Temp\noof-suite.lock'
while (-not (New-Item -ItemType Directory $lock -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 30 }
try { dotnet test --solution NoofLedger.slnx } finally { Remove-Item $lock }
```

Expected: every test passes. The count is the branch's count before this task plus 18.

- [ ] **Step 16: Commit**

```bash
git add src/Noof.Ledger.Application/Reporting/IBalanceReadModel.cs src/Noof.Ledger.Persistence/Balances src/Noof.Ledger.Persistence/Migrations src/Noof.Ledger.Persistence/PersistenceRegistration.cs src/Noof.Ledger.Persistence/Reporting/EfSpendingReadModel.cs tests/Noof.Ledger.Persistence.Tests/BalanceSeed.cs tests/Noof.Ledger.Persistence.Tests/EfBalanceReadModelTests.cs tests/Noof.Ledger.Persistence.Tests/WalletBalancesViewTests.cs tests/Noof.Ledger.Persistence.Tests/BalanceSqlTests.cs tests/Noof.Ledger.Persistence.Tests/EfSpendingReadModelTests.cs tests/Noof.Ledger.Host.Tests/PersistenceRegistrationTests.cs tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs
git commit -m "feat(persistence): wallet_balances view, balance read model and BalanceSql.AsOfAsync (M5, M6)

A balance is the latest completed checkpoint plus the completed entries strictly after it,
ordered by (occurred_on, occurred_at); ties between checkpoints go to the later-recorded one.
"This month" now counts expenses only, so an income's lines never read as spending (M11).

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m"
```

`git status` must show a clean tree afterwards. In particular, `schema.expected.sql` must be unmodified.

---

### Task 4: Wallet directory, mapper, worker wiring

**Files:**
- Create: `src/Noof.Ledger.Application/Wallets/IWalletDirectory.cs`
- Create: `src/Noof.Ledger.Persistence/Wallets/EfWalletDirectory.cs`
- Modify: `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs` (`MappedProposal`, `CategorizationSubject`, `CategorizationOutcome`)
- Modify: `src/Noof.Ledger.Application/Categorization/IProposalMapper.cs` (whole file, 13 lines)
- Modify: `src/Noof.Ledger.Application/Categorization/ProposalMapper.cs` (whole file, 97 lines)
- Modify: `src/Noof.Ledger.Persistence/PersistenceRegistration.cs` (usings; `AddNoofPersistence`, lines 31–52)
- Modify: `src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs` (`GetSubjectAsync` only: the header projection and the `new CategorizationSubject(...)` call)
- Modify: `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs` (`ProcessClaimedJobAsync`, lines 80–182, plus one new static helper)
- Modify: `src/Noof.Ledger.Host/Workers/CategorizationWorkerOptions.cs` (the comment on `DefaultCurrency`, lines 44–48)
- Test: `tests/Noof.Ledger.Persistence.Tests/ProposalMapperTests.cs` (rewritten), `tests/Noof.Ledger.Persistence.Tests/EfWalletDirectoryTests.cs` (new), `tests/Noof.Ledger.Persistence.Tests/EfCategorizationStoreTests.cs`, `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`, `tests/Noof.Ledger.Host.Tests/PersistenceRegistrationTests.cs`, `tests/Noof.Ledger.Host.Tests/CategorizationWiringTests.cs`, `tests/Noof.Ledger.Ai.Tests/LiveModelTests.cs` (one call site, compile only; never run live), `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`

**Interfaces:**
- Consumes:
  - Task 1: `TransactionKind { Expense = 0, Income = 1, BalanceCheck = 2 }`; `Wallet.Aliases` (`string[]`), `Wallet.IsDefaultForCurrency`, `Wallet.Archived`, `Wallet.CreatedAt`; `Transaction.WalletId` is `Guid?` with a setter; the seeded *Main Wallet* `00000000-0000-0000-0000-000000000001` (RSD, `IsDefaultForCurrency = true`); `GetSubjectAsync` already left-joins the wallet and maps a missing one to `WalletName = ""`.
  - Task 2: `WalletOption(Guid Id, string Name, CurrencyCode Currency, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency)`; `CategorizationRequest(..., CorrectionRequest? Correction = null, IReadOnlyList<WalletOption>? Wallets = null)`; `ProposedKind.Expense/Income/Balance` (`"expense"`, `"income"`, `"balance"`); `CategorizationProposal(Items, OccurredOn = null, Kind = ProposedKind.Expense, WalletId = null, BalanceAmount = null, BalanceCurrency = null)`.
  - Existing: `CategorizationWorkerOptions.DefaultCurrency` (bound from `Categorization:DefaultCurrency` in `Program.cs` line 43, default `"RSD"`).
- Produces (later tasks rely on these exactly):
  - `public interface IWalletDirectory { Task<IReadOnlyList<WalletOption>> ActiveAsync(CancellationToken cancellationToken); }` in `Noof.Ledger.Application.Wallets`: not archived, ordered by name. Implemented by `internal sealed class EfWalletDirectory(LedgerDbContext db)` in `Noof.Ledger.Persistence.Wallets`, registered scoped in `AddNoofPersistence`.
  - `IProposalMapper.TryMap(CategorizationProposal proposal, IReadOnlyCollection<string> offeredSlugs, IReadOnlyCollection<Guid> offeredMerchantIds, IReadOnlyList<WalletOption> wallets, string defaultCurrency, out MappedProposal mapped, out string failure)`.
  - `MappedProposal(IReadOnlyList<ResolvedLineItem> Items, DateOnly? OccurredOn, TransactionKind Kind = TransactionKind.Expense, Guid WalletId = default, Money? StatedBalance = null)`.
  - `CategorizationOutcome(IReadOnlyList<CategorizedLineItem> Items, DateOnly OccurredOn, JobKind Kind = JobKind.Categorize, string? Instruction = null, TransactionKind TransactionKind = TransactionKind.Expense, Guid? WalletId = null, Money? StatedBalance = null)`. From this task on, `CategorizationWorker` always sets the last three. `EfCategorizationStore.ApplyAsync` still ignores them. Task 6 writes them.
  - `CategorizationSubject(..., CaptureKind CaptureKind = CaptureKind.Text, Guid? WalletId = null)`. `GetSubjectAsync` fills it (see the contract correction below).

**The rules this task implements** (spec M3, M5, M9, M10; contract "Mapper"):

- The default currency is `CategorizationWorkerOptions.DefaultCurrency`. It comes from configuration `Categorization:DefaultCurrency` and defaults to `"RSD"`. The worker passes it to the mapper unchanged. Its only job now is the last step of wallet resolution. A line with no currency takes **the resolved wallet's** currency, not this setting. For the seeded setup (Main Wallet is the RSD default) the two agree, so no stored behaviour changes.
- Wallet resolution, in order:
  1. A `WalletId` named by the proposal must be one of `wallets`. Otherwise the job fails with `wallet <id> was not offered`.
  2. With no wallet named, the currency is the first line's stated currency, else `BalanceCurrency`, else `defaultCurrency`. The wallet is the one in `wallets` with `IsDefaultForCurrency` for that currency, else the one with `IsDefaultForCurrency` for `defaultCurrency`.
  3. If neither exists, the job fails with `no wallet to record into`.
  
  Currency codes match case-insensitively, like everywhere else in the mapper.
- Kind: `"expense"` → `Expense`, `"income"` → `Income`, `"balance"` → `BalanceCheck`, anything else fails. For `BalanceCheck`, a null `BalanceAmount` fails with `a balance statement with no amount`. Otherwise `StatedBalance = (BalanceAmount, BalanceCurrency ?? wallet currency)`, the items are ignored, and `Items` is empty.
- No plausibility checks (P2-1). The mapper maps an offered id, an offered slug, a supported currency, an ISO day and a known kind. Nothing else.

> **Contract clarification:** the contract says "the currency is the first line's stated currency, else `BalanceCurrency`". It also says a balance statement's "items are ignored". This task reads the two together: **for `kind = balance` the lines are not consulted at all**, so the currency that picks the wallet is `BalanceCurrency`, else `defaultCurrency`. Otherwise a stray RSD line next to *"на Wise 3200 евро"* would put a EUR statement into the RSD wallet. For expense and income the rule is exactly the contract's.

> **Contract correction:** `CategorizationSubject` gains a trailing `Guid? WalletId = null`. `EfCategorizationStore.GetSubjectAsync` fills it from `transactions.wallet_id`, and the worker uses it for one rule: **a `Correct` job whose proposal names no wallet keeps the record's current wallet** if that wallet is still offered.
>
> The contract has no way for the worker to know the record's wallet, and `CorrectionRequest` shows the model the lines, not the wallet. So under the literal rule, a correction such as *"нет, 300"* to a purchase recorded in *Raiffeisen RSD* comes back with `wallet = null`. It resolves to the RSD default and quietly moves the money to another wallet. That is exactly the drift M1 forbids. A correction that names a wallet (*"это было с налички"*) still moves the record (M3). A `Reinterpret` job (the operator edited the message text) reads from scratch and resolves the wallet afresh, as it does the day.
>
> Task 8 inserts its four `CategorizationSubject` parameters (`Kind`, `WalletCurrency`, `WalletBalances`, `Statement`) between `CaptureKind` and `WalletId`, so `WalletId` stays last. Its positional call in `GetSubjectAsync` then compiles unchanged, as long as it keeps the named `WalletId: header.WalletId` this task adds.

Corrections take the same path as first readings. `ProcessClaimedJobAsync` handles `Categorize`, `Correct` and `Reinterpret` alike. A spoken correction reaches it too: `TranscriptionWorker` hands the transcript to `ITranscriptionStore.CompleteCorrectionAsync`, which queues a `Correct` job. Persisting kind, wallet and stated balance is Task 6. This task only carries them to `ApplyAsync`.

- [ ] **Step 1: Declare the port and widen the contract records**

Create `src/Noof.Ledger.Application/Wallets/IWalletDirectory.cs`:

```csharp
using Noof.Ledger.Application.Categorization;

namespace Noof.Ledger.Application.Wallets;

public interface IWalletDirectory
{
    // Every wallet that is not archived, ordered by name: what capture may record into (M3, M4).
    Task<IReadOnlyList<WalletOption>> ActiveAsync(CancellationToken cancellationToken);
}
```

In `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs` replace

```csharp
public sealed record MappedProposal(IReadOnlyList<ResolvedLineItem> Items, DateOnly? OccurredOn);
```

with

```csharp
// WalletId is always a wallet the request offered: the one the model named, or the default wallet of the
// spending's currency, or the default wallet of the configured default currency (M3).
public sealed record MappedProposal(
    IReadOnlyList<ResolvedLineItem> Items,
    DateOnly? OccurredOn,
    TransactionKind Kind = TransactionKind.Expense,
    Guid WalletId = default,
    Money? StatedBalance = null);
```

replace the last parameter line of `CategorizationSubject`

```csharp
    IReadOnlyList<RecordedLine> Lines,
    CaptureKind CaptureKind = CaptureKind.Text);
```

with

```csharp
    IReadOnlyList<RecordedLine> Lines,
    CaptureKind CaptureKind = CaptureKind.Text,
    Guid? WalletId = null);
```

and replace

```csharp
public sealed record CategorizationOutcome(
    IReadOnlyList<CategorizedLineItem> Items,
    DateOnly OccurredOn,
    JobKind Kind = JobKind.Categorize,
    string? Instruction = null);
```

with

```csharp
// Kind is the job that produced this outcome; TransactionKind is what the record is. Never confuse the two.
public sealed record CategorizationOutcome(
    IReadOnlyList<CategorizedLineItem> Items,
    DateOnly OccurredOn,
    JobKind Kind = JobKind.Categorize,
    string? Instruction = null,
    TransactionKind TransactionKind = TransactionKind.Expense,
    Guid? WalletId = null,
    Money? StatedBalance = null);
```

These are DTO changes and exempt from test-first. Every parameter is trailing and optional, so every existing positional call site still compiles.

- [ ] **Step 2: Watch the public-surface guard name the new interface, then allow it**

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj --filter PublicSurfaceTests`
Expected: FAIL in `Public_types_are_exactly_the_allowed_set("Noof.Ledger.Application")`, and the message lists `IWalletDirectory` as unexpected.

In `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`, in the `["Noof.Ledger.Application"]` array, add a line after `"ITranscriber", "ISpeechProvider", "CapturedVoice", "ITranscriptionStore", "IVoiceFileSource",`:

```csharp
            "IWalletDirectory",
```

(`WalletOption` and `ProposedKind` are Task 2's entries, `WalletDetails`, `NewWallet` and `IWalletAdmin` are Task 5's. On merge, keep every name from every side.)

Run the same command again. Expected: PASS.

- [ ] **Step 3: Write the failing directory test**

Create `tests/Noof.Ledger.Persistence.Tests/EfWalletDirectoryTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Wallets;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfWalletDirectoryTests(PostgresFixture fixture)
{
    static readonly Guid SeededMainWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly DateTimeOffset Created = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    static Wallet NewWallet(
        string name, CurrencyCode currency, bool isDefaultForCurrency = false, bool archived = false, string[]? aliases = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Currency = currency,
        Aliases = aliases ?? [],
        IsDefaultForCurrency = isDefaultForCurrency,
        Archived = archived,
        CreatedAt = Created,
    };

    [Fact]
    public async Task Active_offers_every_wallet_that_is_not_archived_ordered_by_name()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wise = NewWallet("Wise EUR", CurrencyCode.Eur, isDefaultForCurrency: true, aliases: ["wise", "вайз"]);
        var cash = NewWallet("Cash", CurrencyCode.Rsd, aliases: ["налик"]);
        // Named to sort first, so a directory that forgot the archived filter fails on the order as well.
        var closed = NewWallet("Alpha Bank (closed)", CurrencyCode.Rsd, archived: true);
        db.Wallets.AddRange(wise, cash, closed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var active = await new EfWalletDirectory(db).ActiveAsync(TestContext.Current.CancellationToken);

        active.Select(wallet => wallet.Name).Should().Equal(["Cash", "Main Wallet", "Wise EUR"],
            "an archived wallet is hidden from capture (M4), and the rest come by name");
        var offered = active.Single(wallet => wallet.Id == wise.Id);
        offered.Currency.Should().Be(CurrencyCode.Eur);
        offered.Aliases.Should().Equal(["wise", "вайз"]);
        offered.IsDefaultForCurrency.Should().BeTrue();
        active.Single(wallet => wallet.Id == cash.Id).IsDefaultForCurrency.Should().BeFalse();
        active.Single(wallet => wallet.Id == SeededMainWalletId).IsDefaultForCurrency
            .Should().BeTrue("the seeded Main Wallet stays the RSD default");
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter EfWalletDirectoryTests`
Expected: FAIL — the build fails with `CS0234`/`CS0246`: `Noof.Ledger.Persistence.Wallets` / `EfWalletDirectory` does not exist.

- [ ] **Step 4: Implement the directory**

Create `src/Noof.Ledger.Persistence/Wallets/EfWalletDirectory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Wallets;

namespace Noof.Ledger.Persistence.Wallets;

internal sealed class EfWalletDirectory(LedgerDbContext db) : IWalletDirectory
{
    public async Task<IReadOnlyList<WalletOption>> ActiveAsync(CancellationToken cancellationToken)
    {
        var wallets = await db.Wallets.AsNoTracking()
            .Where(wallet => !wallet.Archived)
            .OrderBy(wallet => wallet.Name)
            .ThenBy(wallet => wallet.Id)
            .ToListAsync(cancellationToken);

        return [.. wallets.Select(wallet => new WalletOption(
            wallet.Id, wallet.Name, wallet.Currency, wallet.Aliases, wallet.IsDefaultForCurrency))];
    }
}
```

Run the Step 3 command. Expected: PASS.

- [ ] **Step 5: Watch the archived filter fail once**

Delete the line `.Where(wallet => !wallet.Archived)` and run the Step 3 command.
Expected: FAIL in `Active_offers_every_wallet_that_is_not_archived_ordered_by_name`: the names start with `"Alpha Bank (closed)"`. Put the line back and run again: PASS.

- [ ] **Step 6: Register it, test first**

In `tests/Noof.Ledger.Host.Tests/PersistenceRegistrationTests.cs` add `using Noof.Ledger.Application.Wallets;` after `using Noof.Ledger.Application.Secrets;`, and add after the `IJobQueue` line (line 45):

```csharp
        scope.ServiceProvider.GetRequiredService<IWalletDirectory>().Should().NotBeNull();
```

In `tests/Noof.Ledger.Host.Tests/CategorizationWiringTests.cs`, in `Every_scoped_categorization_port_resolves_without_touching_the_database`, add after the `IRecordEditor` line (line 35):

```csharp
        scope.ServiceProvider.GetRequiredService<Noof.Ledger.Application.Wallets.IWalletDirectory>();
```

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter PersistenceRegistrationTests`
Expected: FAIL: `InvalidOperationException: No service for type 'Noof.Ledger.Application.Wallets.IWalletDirectory' has been registered.`

In `src/Noof.Ledger.Persistence/PersistenceRegistration.cs` add `using Noof.Ledger.Application.Wallets;` after `using Noof.Ledger.Application.Transcription;` and `using Noof.Ledger.Persistence.Wallets;` after `using Noof.Ledger.Persistence.Transcription;`. Then add after `services.AddScoped<ISpendingReadModel, EfSpendingReadModel>();`:

```csharp
        services.AddScoped<IWalletDirectory, EfWalletDirectory>();
```

Merging with Task 5 (same wave): both tasks add `using Noof.Ledger.Application.Wallets;` and `using Noof.Ledger.Persistence.Wallets;` here and `using Noof.Ledger.Application.Wallets;` to `PersistenceRegistrationTests.cs`. Keep one copy of each directive; a duplicate is `CS0105`, an error under `TreatWarningsAsErrors`. If Task 12 has already reshaped `AddNoofPersistence` (a `connectionString` local), its shape wins and this `AddScoped` line goes back in after its `ISpendingReadModel` registration.

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter PersistenceRegistrationTests`
Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWiringTests`
Expected: both PASS.

- [ ] **Step 7: The subject says which wallet the record is in, test first**

In `tests/Noof.Ledger.Persistence.Tests/EfCategorizationStoreTests.cs`, in `GetSubjectAsync_returns_the_record_as_stored_with_its_lines`, add after `subject.WalletName.Should().Be("Main Wallet");`:

```csharp
        subject.WalletId.Should().Be(wallet.Id);
```

and add this test after `GetSubjectAsync_returns_null_when_the_transaction_is_gone`:

```csharp
    [Fact]
    public async Task GetSubjectAsync_names_no_wallet_for_a_capture_that_has_not_been_read_yet()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transaction = NewTransaction(SeededDefaultWalletId);
        transaction.WalletId = null;
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db, Clock);

        var subject = await store.GetSubjectAsync(transaction.Id, TestContext.Current.CancellationToken);

        subject!.WalletId.Should().BeNull("the wallet is chosen when the message is read, not when it is captured");
    }
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter EfCategorizationStoreTests`
Expected: FAIL in `GetSubjectAsync_returns_the_record_as_stored_with_its_lines`: `Expected subject.WalletId to be {guid}, but found <null>`. (The new test passes already. It pins the null case the next edit must keep.)

In `src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs`, `GetSubjectAsync`: in the anonymous `select new { ... }` of the header query, add `t.WalletId,` after `t.CaptureKind,`. In the `return new CategorizationSubject(...)` call, change the last argument line from

```csharp
            header.CaptureKind);
```

to

```csharp
            header.CaptureKind, WalletId: header.WalletId);
```

Run the same command. Expected: PASS.

- [ ] **Step 8: Rewrite the mapper tests (they fail)**

Replace `tests/Noof.Ledger.Persistence.Tests/ProposalMapperTests.cs` with:

```csharp
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Persistence.Tests;

public class ProposalMapperTests
{
    static readonly IProposalMapper Mapper = new ProposalMapper();
    static readonly string[] Slugs = ["groceries", "food-drink"];
    static readonly Guid KnownMerchant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    static readonly WalletOption MainRsd = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"), "Main Wallet", CurrencyCode.Rsd, [], IsDefaultForCurrency: true);
    static readonly WalletOption CashRsd = new(
        Guid.Parse("33333333-3333-3333-3333-333333333333"), "Cash", CurrencyCode.Rsd, ["налик"], IsDefaultForCurrency: false);
    static readonly WalletOption WiseEur = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "Wise EUR", CurrencyCode.Eur, ["wise"], IsDefaultForCurrency: true);
    static readonly WalletOption RevolutUsd = new(
        Guid.Parse("44444444-4444-4444-4444-444444444444"), "Revolut USD", CurrencyCode.Usd, [], IsDefaultForCurrency: false);
    static readonly IReadOnlyList<WalletOption> Wallets = [MainRsd, CashRsd, WiseEur, RevolutUsd];

    static ProposedLineItem Line(
        decimal amount, string? currency = "RSD", string slug = "groceries",
        Guid? knownMerchantId = null, string? merchantName = null, string description = "кофе") =>
        new(description, amount, currency, slug, knownMerchantId, merchantName);

    static bool Map(CategorizationProposal proposal, out MappedProposal mapped, out string failure) =>
        MapWith(Wallets, proposal, out mapped, out failure);

    static bool MapWith(
        IReadOnlyList<WalletOption> wallets, CategorizationProposal proposal, out MappedProposal mapped, out string failure) =>
        Mapper.TryMap(proposal, Slugs, [KnownMerchant], wallets, "RSD", out mapped, out failure);

    [Fact]
    public void An_amount_the_message_never_wrote_in_digits_is_taken_as_the_model_gives_it()
    {
        // "купил штуку евро" has no digits at all. Accepting the model's 1000 is the whole point of D1.
        // The model answers a JSON number, so there is nothing here to parse: it is the same decimal
        // System.Text.Json read off the response's "amount" token.
        Map(new([Line(1000m, "EUR")]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Amount.Should().Be(new Money(1000m, CurrencyCode.Eur));
    }

    [Theory]
    [InlineData(45.30)]
    [InlineData(0.5)]
    [InlineData(0.1)]
    [InlineData(250)]
    public void A_decimal_amount_maps_to_Money_with_no_precision_loss(decimal amount)
    {
        Map(new([Line(amount)]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Amount.Amount.Should().Be(amount);
    }

    [Fact]
    public void No_currency_and_no_wallet_named_means_the_default_wallet_and_its_currency()
    {
        Map(new([Line(250m, currency: null)]), out var mapped, out _).Should().BeTrue();

        mapped.WalletId.Should().Be(MainRsd.Id);
        mapped.Items.Single().Amount.Currency.Should().Be(CurrencyCode.Rsd);
    }

    [Fact]
    public void A_line_with_no_currency_takes_the_named_wallets_currency()
    {
        Map(new([Line(3.50m, currency: null)], WalletId: WiseEur.Id), out var mapped, out _).Should().BeTrue();

        mapped.WalletId.Should().Be(WiseEur.Id);
        mapped.Items.Single().Amount.Should().Be(new Money(3.50m, CurrencyCode.Eur));
    }

    [Fact]
    public void A_lower_case_currency_maps_to_the_supported_code()
    {
        Map(new([Line(2.50m, currency: "eur")]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Amount.Currency.Should().Be(CurrencyCode.Eur);
        mapped.WalletId.Should().Be(WiseEur.Id, "the wallet is matched on the currency whatever its case");
    }

    [Fact]
    public void A_currency_the_ledger_does_not_support_fails()
    {
        Map(new([Line(10m, currency: "GBP")]), out _, out var failure).Should().BeFalse();

        failure.Should().Contain("GBP");
    }

    [Fact]
    public void A_slug_that_was_not_offered_fails_and_a_differently_cased_one_maps_to_the_offered_spelling()
    {
        Map(new([Line(10m, slug: "rent")]), out _, out _).Should().BeFalse();

        Map(new([Line(10m, slug: "Groceries")]), out var mapped, out _).Should().BeTrue();
        mapped.Items.Single().CategorySlug.Should().Be("groceries");
    }

    [Fact]
    public void A_known_merchant_id_that_was_not_offered_fails()
    {
        Map(new([Line(10m, knownMerchantId: Guid.NewGuid())]), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_merchant_name_is_taken_as_given_whether_or_not_the_message_spells_it_that_way()
    {
        Map(new([Line(300m, merchantName: "Starbucks")]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().MerchantName.Should().Be("Starbucks");
    }

    [Fact]
    public void A_blank_merchant_name_is_no_merchant()
    {
        Map(new([Line(300m, merchantName: "  ")]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().MerchantName.Should().BeNull();
    }

    [Fact]
    public void Over_long_text_is_cut_to_the_column_widths_rather_than_failing_the_job()
    {
        var proposal = new CategorizationProposal([Line(1m, merchantName: new string('m', 300), description: new string('d', 600))]);

        Map(proposal, out var mapped, out _).Should().BeTrue();

        mapped.Items.Single().Description.Should().HaveLength(512);
        mapped.Items.Single().MerchantName.Should().HaveLength(256);
    }

    [Fact]
    public void Zero_items_is_a_real_answer()
    {
        Map(new([]), out var mapped, out _).Should().BeTrue();

        mapped.Items.Should().BeEmpty();
        mapped.WalletId.Should().Be(MainRsd.Id, "no line and no currency: the default wallet of the default currency");
    }

    [Fact]
    public void A_named_day_maps_to_that_date()
    {
        Map(new([Line(100m)], "2026-09-20"), out var mapped, out _).Should().BeTrue();

        mapped.OccurredOn.Should().Be(new DateOnly(2026, 9, 20));
    }

    [Fact]
    public void No_day_maps_to_no_date()
    {
        Map(new([Line(100m)]), out var mapped, out _).Should().BeTrue();

        mapped.OccurredOn.Should().BeNull();
    }

    [Theory]
    [InlineData("вчера")]
    [InlineData("20.09.2026")]
    [InlineData("2026-02-30")]
    public void A_day_that_is_not_an_ISO_date_fails(string occurredOn)
    {
        Map(new([Line(100m)], occurredOn), out _, out var failure).Should().BeFalse();

        failure.Should().Contain("occurred_on");
    }

    [Fact]
    public void A_wallet_the_model_named_from_the_offered_ones_is_the_wallet()
    {
        Map(new([Line(250m)], WalletId: CashRsd.Id), out var mapped, out _).Should().BeTrue();

        mapped.WalletId.Should().Be(CashRsd.Id, "a named wallet wins over the currency's default");
    }

    [Fact]
    public void A_wallet_that_was_not_offered_fails()
    {
        var stranger = Guid.Parse("99999999-9999-9999-9999-999999999999");

        Map(new([Line(250m)], WalletId: stranger), out _, out var failure).Should().BeFalse();

        failure.Should().Be($"wallet {stranger} was not offered");
    }

    [Fact]
    public void No_wallet_named_means_the_default_wallet_of_the_first_lines_currency()
    {
        Map(new([Line(3.50m, "EUR"), Line(250m, "RSD")]), out var mapped, out _).Should().BeTrue();

        mapped.WalletId.Should().Be(WiseEur.Id);
        mapped.Items.Select(item => item.Amount.Currency).Should().Equal([CurrencyCode.Eur, CurrencyCode.Rsd],
            "each line keeps its own currency; nothing is converted (M10)");
    }

    [Fact]
    public void A_currency_with_no_default_wallet_falls_back_to_the_default_wallet_of_the_default_currency()
    {
        // Revolut USD exists but is not the USD default, so there is no USD default at all.
        Map(new([Line(20m, "USD")]), out var mapped, out _).Should().BeTrue();

        mapped.WalletId.Should().Be(MainRsd.Id);
        mapped.Items.Single().Amount.Should().Be(new Money(20m, CurrencyCode.Usd), "the spending is not converted (M10)");
    }

    [Fact]
    public void With_no_default_wallet_to_fall_back_to_the_job_fails()
    {
        MapWith([], new([Line(250m)]), out _, out var noWallets).Should().BeFalse();
        MapWith([CashRsd, RevolutUsd], new([Line(250m)]), out _, out var noDefaults).Should().BeFalse();

        noWallets.Should().Be("no wallet to record into");
        noDefaults.Should().Be("no wallet to record into");
    }

    [Theory]
    [InlineData(ProposedKind.Expense, TransactionKind.Expense)]
    [InlineData(ProposedKind.Income, TransactionKind.Income)]
    [InlineData(ProposedKind.Balance, TransactionKind.BalanceCheck)]
    public void Each_kind_maps_to_its_transaction_kind(string kind, TransactionKind expected)
    {
        Map(new([], Kind: kind, BalanceAmount: 100m), out var mapped, out _).Should().BeTrue();

        mapped.Kind.Should().Be(expected);
    }

    [Fact]
    public void A_kind_that_is_not_one_of_the_three_fails()
    {
        Map(new([Line(250m)], Kind: "transfer"), out _, out var failure).Should().BeFalse();

        failure.Should().Contain("transfer");
    }

    [Fact]
    public void An_income_keeps_its_lines_and_carries_no_stated_balance()
    {
        Map(new([Line(2000m, "EUR", description: "зарплата")], Kind: ProposedKind.Income, BalanceAmount: 5m), out var mapped, out _)
            .Should().BeTrue();

        mapped.Kind.Should().Be(TransactionKind.Income);
        mapped.WalletId.Should().Be(WiseEur.Id);
        mapped.Items.Single().Amount.Should().Be(new Money(2000m, CurrencyCode.Eur));
        mapped.StatedBalance.Should().BeNull("only a balance statement states a balance");
    }

    [Fact]
    public void A_balance_statement_maps_its_amount_and_currency_and_picks_the_wallet_by_that_currency()
    {
        Map(new([], Kind: ProposedKind.Balance, BalanceAmount: 3200m, BalanceCurrency: "EUR"), out var mapped, out _)
            .Should().BeTrue();

        mapped.Kind.Should().Be(TransactionKind.BalanceCheck);
        mapped.WalletId.Should().Be(WiseEur.Id);
        mapped.StatedBalance.Should().Be(new Money(3200m, CurrencyCode.Eur));
        mapped.Items.Should().BeEmpty();
    }

    [Fact]
    public void A_balance_statement_ignores_any_lines_the_model_sent_with_it()
    {
        Map(new([Line(250m, "RSD")], Kind: ProposedKind.Balance, BalanceAmount: 3200m, BalanceCurrency: "EUR"), out var mapped, out _)
            .Should().BeTrue();

        mapped.Items.Should().BeEmpty();
        mapped.WalletId.Should().Be(WiseEur.Id, "a stray line's currency must not pick a statement's wallet");
    }

    [Fact]
    public void A_balance_statement_with_no_currency_is_in_its_wallets_currency()
    {
        Map(new([], Kind: ProposedKind.Balance, WalletId: WiseEur.Id, BalanceAmount: 45230.07m), out var mapped, out _)
            .Should().BeTrue();

        mapped.StatedBalance.Should().Be(new Money(45230.07m, CurrencyCode.Eur));
    }

    [Fact]
    public void A_balance_statement_with_no_amount_fails()
    {
        Map(new([], Kind: ProposedKind.Balance, BalanceCurrency: "RSD"), out _, out var failure).Should().BeFalse();

        failure.Should().Be("a balance statement with no amount");
    }

    [Fact]
    public void A_balance_currency_the_ledger_does_not_support_fails()
    {
        Map(new([], Kind: ProposedKind.Balance, WalletId: MainRsd.Id, BalanceAmount: 10m, BalanceCurrency: "GBP"), out _, out var failure)
            .Should().BeFalse();

        failure.Should().Contain("GBP");
    }
}
```

Change the mapper's signature only, so the tests compile and fail on behaviour. Replace `src/Noof.Ledger.Application/Categorization/IProposalMapper.cs` with:

```csharp
namespace Noof.Ledger.Application.Categorization;

public interface IProposalMapper
{
    bool TryMap(
        CategorizationProposal proposal,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        IReadOnlyList<WalletOption> wallets,
        string defaultCurrency,
        out MappedProposal mapped,
        out string failure);
}
```

In `ProposalMapper.cs` add the parameter `IReadOnlyList<WalletOption> wallets,` between `offeredMerchantIds` and `defaultCurrency` in `TryMap`, and nothing else.

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter ProposalMapperTests`
Expected: FAIL. Among others, `A_wallet_the_model_named_from_the_offered_ones_is_the_wallet` (`Expected mapped.WalletId to be {33333333-...}, but found {00000000-0000-0000-0000-000000000000}`), `Each_kind_maps_to_its_transaction_kind` for `income` and `balance`, `A_balance_statement_with_no_amount_fails`, and `With_no_default_wallet_to_fall_back_to_the_job_fails`. The fourteen pre-existing tests still pass.

`Noof.Ledger.Host` and `Noof.Ledger.Ai.Tests` do not build from here until Step 10 and Step 11. That is expected. Neither is part of the Persistence.Tests build.

- [ ] **Step 9: Implement the mapper**

Replace `src/Noof.Ledger.Application/Categorization/ProposalMapper.cs` with:

```csharp
using System.Globalization;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Categorization;

// Maps, never judges. Amount is already a decimal by the time it reaches here — the model answered a
// JSON number and System.Text.Json read it straight in, so there is no amount parsing left to do.
// Whether a figure is plausible, or appears in the message at all, is for the person to see in the
// echo and correct there (D1, docs/OPEN-QUESTIONS.md P2-1). Do not add a sanity bound or a verbatim
// check here. The same holds for the wallet and the kind: an offered wallet id and a known kind map,
// and whether income "looks like" income is not this class's question (M3, M9).
internal sealed class ProposalMapper : IProposalMapper
{
    const int MaxDescriptionLength = 512;
    const int MaxMerchantNameLength = 256;

    public bool TryMap(
        CategorizationProposal proposal,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        IReadOnlyList<WalletOption> wallets,
        string defaultCurrency,
        out MappedProposal mapped,
        out string failure)
    {
        mapped = new MappedProposal([], null);

        if (KindOf(proposal.Kind) is not { } kind)
        {
            failure = $"kind \"{proposal.Kind}\" is not expense, income or balance.";
            return false;
        }

        if (WalletFor(proposal, kind, wallets, defaultCurrency, out failure) is not { } wallet)
            return false;

        IReadOnlyList<ResolvedLineItem> items = [];
        Money? stated = null;

        if (kind == TransactionKind.BalanceCheck)
        {
            stated = StatementOf(proposal, wallet, out failure);
            if (stated is null)
                return false;
        }
        else
        {
            var resolved = MapItems(proposal.Items, offeredSlugs, offeredMerchantIds, wallet.Currency.Value, out failure);
            if (resolved is null)
                return false;

            items = resolved;
        }

        if (!TryParseDay(proposal.OccurredOn, out var occurredOn, out failure))
            return false;

        mapped = new MappedProposal(items, occurredOn, kind, wallet.Id, stated);
        failure = string.Empty;
        return true;
    }

    static TransactionKind? KindOf(string kind) => kind switch
    {
        ProposedKind.Expense => TransactionKind.Expense,
        ProposedKind.Income => TransactionKind.Income,
        ProposedKind.Balance => TransactionKind.BalanceCheck,
        _ => null,
    };

    static WalletOption? WalletFor(
        CategorizationProposal proposal, TransactionKind kind, IReadOnlyList<WalletOption> wallets, string defaultCurrency,
        out string failure)
    {
        if (proposal.WalletId is { } named)
        {
            var offered = wallets.FirstOrDefault(wallet => wallet.Id == named);
            failure = offered is null ? $"wallet {named} was not offered" : string.Empty;
            return offered;
        }

        var currency = StatedCurrency(proposal, kind) ?? defaultCurrency;
        var resolved = DefaultWalletOf(wallets, currency) ?? DefaultWalletOf(wallets, defaultCurrency);
        failure = resolved is null ? "no wallet to record into" : string.Empty;
        return resolved;
    }

    // A statement's lines are never read, so a stray line cannot pick a statement's wallet.
    static string? StatedCurrency(CategorizationProposal proposal, TransactionKind kind)
    {
        var lineCurrency = kind == TransactionKind.BalanceCheck ? null : proposal.Items.FirstOrDefault()?.CurrencyCode;
        return NonBlank(lineCurrency) ?? NonBlank(proposal.BalanceCurrency);
    }

    static WalletOption? DefaultWalletOf(IReadOnlyList<WalletOption> wallets, string currency) =>
        wallets.FirstOrDefault(wallet =>
            wallet.IsDefaultForCurrency && string.Equals(wallet.Currency.Value, currency, StringComparison.OrdinalIgnoreCase));

    static Money? StatementOf(CategorizationProposal proposal, WalletOption wallet, out string failure)
    {
        if (proposal.BalanceAmount is not { } amount)
        {
            failure = "a balance statement with no amount";
            return null;
        }

        var code = NonBlank(proposal.BalanceCurrency) ?? wallet.Currency.Value;
        if (Supported(code) is not { } currency)
        {
            failure = $"balance currency \"{code}\" is not one this ledger supports.";
            return null;
        }

        failure = string.Empty;
        return new Money(amount, currency);
    }

    static List<ResolvedLineItem>? MapItems(
        IReadOnlyList<ProposedLineItem> proposed,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        string walletCurrency,
        out string failure)
    {
        var items = new List<ResolvedLineItem>(proposed.Count);

        for (var index = 0; index < proposed.Count; index++)
        {
            var item = proposed[index];
            if (MapItem(item, offeredSlugs, offeredMerchantIds, walletCurrency, out var reason) is not { } resolved)
            {
                failure = $"Item {index + 1} (\"{item.Description}\"): {reason}";
                return null;
            }

            items.Add(resolved);
        }

        failure = string.Empty;
        return items;
    }

    static ResolvedLineItem? MapItem(
        ProposedLineItem item,
        IReadOnlyCollection<string> offeredSlugs,
        IReadOnlyCollection<Guid> offeredMerchantIds,
        string walletCurrency,
        out string reason)
    {
        var code = NonBlank(item.CurrencyCode) ?? walletCurrency;
        if (Supported(code) is not { } currency)
        {
            reason = $"currency \"{code}\" is not one this ledger supports.";
            return null;
        }

        var slug = offeredSlugs.FirstOrDefault(
            offered => string.Equals(offered, item.CategorySlug, StringComparison.OrdinalIgnoreCase));
        if (slug is null)
        {
            reason = $"category slug \"{item.CategorySlug}\" was not offered.";
            return null;
        }

        if (item.KnownMerchantId is { } merchantId && !offeredMerchantIds.Contains(merchantId))
        {
            reason = $"known merchant id {merchantId} was not offered.";
            return null;
        }

        reason = string.Empty;
        return new ResolvedLineItem(
            Truncate(item.Description, MaxDescriptionLength),
            new Money(item.Amount, currency),
            slug,
            item.KnownMerchantId,
            NonBlank(item.MerchantName) is { } merchantName ? Truncate(merchantName, MaxMerchantNameLength) : null);
    }

    static bool TryParseDay(string? text, out DateOnly? day, out string failure)
    {
        day = null;
        failure = string.Empty;

        if (NonBlank(text) is not { } trimmed)
            return true;

        if (!DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            failure = $"occurred_on \"{text}\" is not an ISO date (YYYY-MM-DD).";
            return false;
        }

        day = parsed;
        return true;
    }

    static CurrencyCode? Supported(string code)
    {
        var currency = CurrencyCode.Supported.FirstOrDefault(
            supported => string.Equals(supported.Value, code, StringComparison.OrdinalIgnoreCase));
        return currency.Value is null ? null : currency;
    }

    static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    static string Truncate(string text, int maxLength) => text.Length > maxLength ? text[..maxLength] : text;
}
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter ProposalMapperTests`
Expected: PASS (every test in the class).

- [ ] **Step 10: Watch the default-wallet rule fail once**

In `DefaultWalletOf`, delete `wallet.IsDefaultForCurrency && ` and run the Step 9 command.
Expected: FAIL in `A_currency_with_no_default_wallet_falls_back_to_the_default_wallet_of_the_default_currency` (it gets Revolut USD, `44444444-...`) and in `With_no_default_wallet_to_fall_back_to_the_job_fails`. Put it back and run again: PASS.

- [ ] **Step 11: Fix the live suite's one mapper call (compile only)**

`tests/Noof.Ledger.Ai.Tests/LiveModelTests.cs` is skipped unless `NOOF_LEDGER_LIVE_ANTHROPIC_KEY` is set. **Never set it in this task.** It still has to compile.

Add `using Noof.Ledger.Domain;` after `using Noof.Ledger.Application.Secrets;`. After the `OfferedSlugs` field (line 23), add:

```csharp
    static readonly IReadOnlyList<WalletOption> OfferedWallets =
        [new WalletOption(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Main Wallet", CurrencyCode.Rsd, [], true)];
```

In `Every_answer_the_model_returns_maps` (line 93) replace

```csharp
        var mapped = new ProposalMapper().TryMap(proposal, OfferedSlugs, offeredMerchantIds: [], defaultCurrency: "RSD", out var result, out var failure);
```

with

```csharp
        var mapped = new ProposalMapper().TryMap(
            proposal, OfferedSlugs, offeredMerchantIds: [], wallets: OfferedWallets, defaultCurrency: "RSD", out var result, out var failure);
```

Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Expected: PASS, with the `LiveModelTests` reported as skipped.

- [ ] **Step 12: Write the failing worker tests**

In `tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs`:

Add `using Noof.Ledger.Application.Wallets;` after `using Noof.Ledger.Application.Jobs;`.

After `static readonly IRecordEcho Echo = new RecordEcho();` (line 23) add:

```csharp
    static readonly WalletOption MainWallet = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"), "Main Wallet", CurrencyCode.Rsd, [], IsDefaultForCurrency: true);
    static readonly WalletOption CashRsd = new(
        Guid.Parse("33333333-3333-3333-3333-333333333333"), "Cash", CurrencyCode.Rsd, ["налик", "наличка"], IsDefaultForCurrency: false);
    static readonly WalletOption WiseEur = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "Wise EUR", CurrencyCode.Eur, ["wise", "вайз"], IsDefaultForCurrency: true);
```

Replace the `ScopeFactoryFor` signature and its fallback block. That covers the first lines of the method up to and including `var resolvedNotifier = notifier ?? Substitute.For<IChatNotifier>();`, and the `provider.GetService(typeof(IChatNotifier))` line. The result:

```csharp
    static IServiceScopeFactory ScopeFactoryFor(
        IJobQueue jobQueue, IModelProvider modelProvider, ICategorizationStore? store = null,
        ICategoryCatalog? categoryCatalog = null, IMerchantDirectory? merchantDirectory = null,
        ICategorizer? categorizer = null, IChatNotifier? notifier = null, IWalletDirectory? walletDirectory = null)
    {
        // The fallback substitutes are resolved into locals BEFORE any .Returns() call below.
        // Calling Substitute.For<T>() (or a helper that itself configures a substitute, like
        // DefaultCategoryCatalog) inline as a .Returns() argument creates/configures a second
        // substitute while the first substitute's "last call" is still pending on NSubstitute's
        // ThreadLocalContext - exactly the "mySub.SomeMethod().Returns(ConfigOtherSub())"
        // anti-pattern NSubstitute's own CouldNotSetReturnDueToNoLastCallException message warns
        // against - and clobbers it, so the outer .Returns() throws that exception at runtime.
        var resolvedStore = store ?? Substitute.For<ICategorizationStore>();
        var resolvedCategoryCatalog = categoryCatalog ?? DefaultCategoryCatalog();
        var resolvedMerchantDirectory = merchantDirectory ?? DefaultMerchantDirectory();
        var resolvedCategorizer = categorizer ?? Substitute.For<ICategorizer>();
        var resolvedNotifier = notifier ?? Substitute.For<IChatNotifier>();
        var resolvedWalletDirectory = walletDirectory ?? WalletDirectoryOf(MainWallet, CashRsd, WiseEur);

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IJobQueue)).Returns(jobQueue);
        provider.GetService(typeof(IModelProvider)).Returns(modelProvider);
        provider.GetService(typeof(ICategorizationStore)).Returns(resolvedStore);
        provider.GetService(typeof(ICategoryCatalog)).Returns(resolvedCategoryCatalog);
        provider.GetService(typeof(IMerchantDirectory)).Returns(resolvedMerchantDirectory);
        provider.GetService(typeof(ICategorizer)).Returns(resolvedCategorizer);
        provider.GetService(typeof(IChatNotifier)).Returns(resolvedNotifier);
        provider.GetService(typeof(IWalletDirectory)).Returns(resolvedWalletDirectory);
```

(the rest of the method, from `var scope = Substitute.For<IServiceScope>();`, is unchanged). After `DefaultMerchantDirectory()` add:

```csharp
    static IWalletDirectory WalletDirectoryOf(params WalletOption[] wallets)
    {
        var directory = Substitute.For<IWalletDirectory>();
        IReadOnlyList<WalletOption> active = wallets;
        directory.ActiveAsync(Arg.Any<CancellationToken>()).Returns(active);
        return directory;
    }
```

Replace the `Subject` helper with:

```csharp
    static CategorizationSubject Subject(
        int? botMessageId = 42, string rawText = "Bread 250 RSD", DateOnly? occurredOn = null,
        TransactionStatus status = TransactionStatus.Captured, IReadOnlyList<RecordedLine>? lines = null, Guid? walletId = null) =>
        new(TransactionId, rawText, 111L, botMessageId, "Cash", status, SentOn, occurredOn ?? SentOn, lines ?? [], WalletId: walletId);
```

Add these tests after `A_failed_correction_leaves_the_record_as_it_was`:

```csharp
    [Fact]
    public async Task The_model_is_offered_the_active_wallets()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await categorizer.Received(1).ProposeAsync(
            Arg.Is<CategorizationRequest>(request =>
                request.Wallets != null && request.Wallets.SequenceEqual(new[] { MainWallet, CashRsd, WiseEur })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_spending_that_names_no_wallet_goes_to_the_default_wallet_of_its_currency()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine(3.50m, "EUR"));
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome =>
                outcome.TransactionKind == TransactionKind.Expense
                && outcome.WalletId == WiseEur.Id
                && outcome.StatedBalance == null
                && outcome.Items.Single().Amount == new Money(3.50m, CurrencyCode.Eur)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_income_is_recorded_as_income_in_the_wallet_the_model_named()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(rawText: "пришла зарплата 2000 евро на Wise"));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneGroceryLine(2000m, "EUR") with { Kind = ProposedKind.Income, WalletId = WiseEur.Id });
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome =>
                outcome.TransactionKind == TransactionKind.Income && outcome.WalletId == WiseEur.Id && outcome.Items.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_balance_statement_reaches_the_store_with_its_stated_balance_and_no_lines()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(rawText: "на главном 45 тысяч"));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CategorizationProposal([], Kind: ProposedKind.Balance, BalanceAmount: 45000m));
        var worker = CreateWorker(ScopeFactoryFor(QueueWith(Job()), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome =>
                outcome.TransactionKind == TransactionKind.BalanceCheck
                && outcome.WalletId == MainWallet.Id
                && outcome.StatedBalance == new Money(45000m, CurrencyCode.Rsd)
                && outcome.Items.Count == 0
                && outcome.Kind == JobKind.Categorize),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_wallet_the_model_was_not_offered_fails_the_job_terminally()
    {
        var stranger = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var jobQueue = QueueWith(Job());
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject());
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneGroceryLine() with { WalletId = stranger });
        var worker = CreateWorker(ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).FailAsync(JobId, WorkerId, $"wallet {stranger} was not offered", Arg.Any<CancellationToken>());
        await store.DidNotReceive().ApplyAsync(Arg.Any<Guid>(), Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_correction_that_names_no_wallet_keeps_the_record_in_its_wallet()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed, lines: [StoredBread], walletId: CashRsd.Id));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine(1500m));
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job(kind: JobKind.Correct, instruction: "нет, 1500")), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == CashRsd.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_correction_that_names_a_wallet_moves_the_record_there()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed, lines: [StoredBread], walletId: MainWallet.Id));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(OneGroceryLine() with { WalletId = CashRsd.Id });
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job(kind: JobKind.Correct, instruction: "это было с налички")), KeyPresent(), store,
                categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == CashRsd.Id && outcome.Kind == JobKind.Correct),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_correction_whose_wallet_was_archived_falls_back_to_the_default()
    {
        var archived = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed, lines: [StoredBread], walletId: archived));
        CategorizationOutcome? applied = null;
        store.ApplyAsync(TransactionId, Arg.Do<CategorizationOutcome>(outcome => applied = outcome), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine(1500m, "RSD"));
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job(kind: JobKind.Correct, instruction: "нет, 1500")), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        (applied?.WalletId).Should().Be(MainWallet.Id,
            "the line is stated in RSD and Main Wallet is the RSD default; the archived wallet is not offered, so the correction cannot keep it");
    }

    [Fact]
    public async Task A_message_with_no_default_wallet_fails_terminally_naming_the_cause()
    {
        var jobQueue = QueueWith(Job());
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>()).Returns(Subject(rawText: "кофе 250"));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine(250m, "RSD"));
        var notifier = Substitute.For<IChatNotifier>();
        var worker = CreateWorker(
            ScopeFactoryFor(jobQueue, KeyPresent(), store, categorizer: categorizer, notifier: notifier,
                walletDirectory: WalletDirectoryOf(CashRsd)),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await jobQueue.Received(1).FailAsync(JobId, WorkerId, "no wallet to record into", Arg.Any<CancellationToken>());
        await jobQueue.DidNotReceive().RetryAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ApplyAsync(Arg.Any<Guid>(), Arg.Any<CategorizationOutcome>(), Arg.Any<CancellationToken>());
        await store.Received(1).MarkFailedAsync(TransactionId, Arg.Any<CancellationToken>());
        await notifier.Received(1).EditAsync(111L, 42, Arg.Any<EchoMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_reinterpretation_that_names_no_wallet_resolves_the_wallet_afresh()
    {
        var store = Substitute.For<ICategorizationStore>();
        store.GetSubjectAsync(TransactionId, Arg.Any<CancellationToken>())
            .Returns(Subject(status: TransactionStatus.Completed, lines: [StoredBread], walletId: CashRsd.Id));
        var categorizer = Substitute.For<ICategorizer>();
        categorizer.ProposeAsync(Arg.Any<CategorizationRequest>(), Arg.Any<CancellationToken>()).Returns(OneGroceryLine());
        var worker = CreateWorker(
            ScopeFactoryFor(QueueWith(Job(kind: JobKind.Reinterpret)), KeyPresent(), store, categorizer: categorizer),
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        await worker.RunTickAsync(TestContext.Current.CancellationToken);

        await store.Received(1).ApplyAsync(TransactionId,
            Arg.Is<CategorizationOutcome>(outcome => outcome.WalletId == MainWallet.Id),
            Arg.Any<CancellationToken>());
    }
```

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWorkerTests`
Expected: FAIL. The build breaks at the `proposalMapper.TryMap(` call in `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs` (`CS1503`/`CS7036`: the call still passes `options.DefaultCurrency` where the mapper's new signature from Step 8 expects `wallets`).

- [ ] **Step 13: Wire the worker**

In `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`:

Add `using Noof.Ledger.Application.Wallets;` after `using Noof.Ledger.Application.Jobs;`.

In `ProcessClaimedJobAsync`, after `var notifier = scope.ServiceProvider.GetRequiredService<IChatNotifier>();` (line 87) add:

```csharp
        var walletDirectory = scope.ServiceProvider.GetRequiredService<IWalletDirectory>();
```

Replace lines 109–134 (from `var allMerchants = ...` through the closing brace of the `TryMap` failure block) with:

```csharp
            var allMerchants = await merchantDirectory.MerchantsAsync(cancellationToken);
            var wallets = await walletDirectory.ActiveAsync(cancellationToken);

            var request = new CategorizationRequest(
                sub.RawText,
                TodayFor(job, sub),
                [.. categories.Select(category => new CategoryOption(category.Slug, category.NameEn, category.NameRu, category.ParentSlug))],
                hints,
                allMerchants,
                CorrectionFor(job, sub),
                wallets);

            var proposal = await categorizer.ProposeAsync(request, cancellationToken);

            var offeredSlugs = categories.Select(category => category.Slug).ToHashSet();

            // The full directory, NOT just the hints. A model that called list_merchants answers with
            // an id it learned there, and mapping against the short hint list would reject exactly
            // the answers that tool exists to produce - the tool would appear to work and every result
            // it influenced would fail to map.
            var offeredMerchantIds = allMerchants.Select(merchant => merchant.Id).ToHashSet();

            if (!proposalMapper.TryMap(
                KeepingTheRecordsWallet(job, sub, proposal, wallets), offeredSlugs, offeredMerchantIds, wallets,
                options.DefaultCurrency, out var mapped, out var failure))
            {
                await FailTerminallyAsync(jobQueue, store, notifier, job, subject, failure, cancellationToken);
                return;
            }
```

Replace lines 180–182:

```csharp
            var occurredOn = mapped.OccurredOn ?? DefaultDay(job, sub);
            await store.ApplyAsync(
                job.TransactionId, new CategorizationOutcome(categorizedItems, occurredOn, job.Kind, job.Instruction), cancellationToken);
```

with

```csharp
            var occurredOn = mapped.OccurredOn ?? DefaultDay(job, sub);
            await store.ApplyAsync(
                job.TransactionId,
                new CategorizationOutcome(
                    categorizedItems, occurredOn, job.Kind, job.Instruction, mapped.Kind, mapped.WalletId, mapped.StatedBalance),
                cancellationToken);
```

After the `DefaultDay` helper (line 250) add:

```csharp
    // The model is shown a correction's lines, not its wallet, so a correction that names no wallet means "leave it
    // where it is", not "the default": otherwise "нет, 300" would quietly move a Raiffeisen purchase into the RSD
    // default and both balances would drift (M1). A re-read starts from scratch and resolves the wallet afresh (M3).
    static CategorizationProposal KeepingTheRecordsWallet(
        CategorizationJob job, CategorizationSubject record, CategorizationProposal proposal, IReadOnlyList<WalletOption> wallets) =>
        job.Kind == JobKind.Correct
        && proposal.WalletId is null
        && record.WalletId is { } current
        && wallets.Any(wallet => wallet.Id == current)
            ? proposal with { WalletId = current }
            : proposal;
```

In `src/Noof.Ledger.Host/Workers/CategorizationWorkerOptions.cs`, replace the comment above `DefaultCurrency` (lines 44–47) with:

```csharp
    // The currency whose default wallet takes a record when neither the model nor the spending's own
    // currency picks one (M3). A line that states no currency takes its wallet's currency, not this. A
    // single hard default for now; see docs/BACKLOG.md for the deferred bot command that would set it.
```

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter CategorizationWorkerTests`
Expected: PASS. That covers the ten new tests, and every existing test with the default directory (Main Wallet RSD default, Cash, Wise EUR default) still passes. `An_answer_that_does_not_map_is_terminal_and_never_retried` still fails the job on its `GBP` line. The wallet resolves to Main first, then the line does not map.

- [ ] **Step 14: Watch the worker's new guards fail once**

1. In the `CategorizationOutcome` construction, replace `mapped.WalletId` with `null` and run the Step 13 command. Expected: FAIL in `A_spending_that_names_no_wallet_goes_to_the_default_wallet_of_its_currency`, `An_income_is_recorded_as_income_in_the_wallet_the_model_named`, `A_balance_statement_reaches_the_store_with_its_stated_balance_and_no_lines` and the four correction/reinterpretation tests. Revert.
2. In the `TryMap` call, replace `KeepingTheRecordsWallet(job, sub, proposal, wallets)` with `proposal` and run again. Expected: FAIL in `A_correction_that_names_no_wallet_keeps_the_record_in_its_wallet` only (it gets Main Wallet). Revert, run again: PASS.
3. In `A_message_with_no_default_wallet_fails_terminally_naming_the_cause`, replace `WalletDirectoryOf(CashRsd)` with `WalletDirectoryOf(MainWallet, CashRsd)` and run again. Expected: FAIL in that test only, on `FailAsync` receiving no call with `"no wallet to record into"` (the record now maps into Main Wallet). Revert, run again: PASS.

The operator-facing hint for this failure is not an echo string: the echo stays `RecordEcho.Failure`, and `/wallets` warns about a currency with no default wallet (Task 7). This test pins the failure text that the job's `last_error` carries.

- [ ] **Step 15: Run every affected project**

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS. It includes `Only_ProposalMapper_constructs_a_Money_inside_the_categorization_pipeline`, which scans `Application/Categorization` and `Host/Workers`. The only `new Money(` added is in `ProposalMapper.cs`.

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Run: `dotnet test --project tests/Noof.Ledger.Ai.Tests/Noof.Ledger.Ai.Tests.csproj`
Run: `dotnet test --project tests/Noof.Ledger.Telegram.Tests/Noof.Ledger.Telegram.Tests.csproj`
Expected: all PASS (live suites skipped). The Telegram tests build `CategorizationSubject` positionally, and the new trailing parameter leaves them compiling.

- [ ] **Step 16: Run the whole solution, holding the suite lock**

Take the lock: `mkdir C:\Users\noofs\AppData\Local\Temp\noof-suite.lock`. If it already exists, wait and retry. Do not delete someone else's lock.
Run: `dotnet test --solution NoofLedger.slnx`
Expected: every test passes except the skipped live suites.
Release the lock: `rmdir C:\Users\noofs\AppData\Local\Temp\noof-suite.lock`, including when the run fails.

- [ ] **Step 17: Commit**

```bash
git add src/Noof.Ledger.Application/Wallets/IWalletDirectory.cs \
        src/Noof.Ledger.Application/Categorization/CategorizationContract.cs \
        src/Noof.Ledger.Application/Categorization/IProposalMapper.cs \
        src/Noof.Ledger.Application/Categorization/ProposalMapper.cs \
        src/Noof.Ledger.Persistence/Wallets/EfWalletDirectory.cs \
        src/Noof.Ledger.Persistence/PersistenceRegistration.cs \
        src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs \
        src/Noof.Ledger.Host/Workers/CategorizationWorker.cs \
        src/Noof.Ledger.Host/Workers/CategorizationWorkerOptions.cs \
        tests/Noof.Ledger.Persistence.Tests/ProposalMapperTests.cs \
        tests/Noof.Ledger.Persistence.Tests/EfWalletDirectoryTests.cs \
        tests/Noof.Ledger.Persistence.Tests/EfCategorizationStoreTests.cs \
        tests/Noof.Ledger.Host.Tests/CategorizationWorkerTests.cs \
        tests/Noof.Ledger.Host.Tests/PersistenceRegistrationTests.cs \
        tests/Noof.Ledger.Host.Tests/CategorizationWiringTests.cs \
        tests/Noof.Ledger.Ai.Tests/LiveModelTests.cs \
        tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs
git commit -m "$(cat <<'EOF'
feat(capture): offer the active wallets and map kind, wallet and stated balance (M3, M9)

IWalletDirectory lists the wallets that are not archived. The worker offers them in the request.
The mapper resolves the wallet: the one named, else the default of the spending's currency, else
the default of Categorization:DefaultCurrency. A line with no currency takes its wallet's currency.
A correction that names no wallet keeps the record where it is. The outcome now carries the
transaction kind, the wallet and a balance statement's amount. Writing them is the next task.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 5: Wallet administration

The `/wallets` page (Task 7) manages wallets through `IWalletAdmin` (M2, M4). Creating a wallet also writes its opening balance as the wallet's first checkpoint (M7): a `BalanceCheck` transaction made on the dashboard, with no Telegram message. This task adds no migration, because Task 1 already created every column it writes. It also cannot read balances: the `wallet_balances` view arrives with Task 3 in this same wave. So its tests pin what the view will rely on instead: the checkpoint's `(occurred_on, occurred_at)` sorts before every record from its own day on.

**Decisions this task makes (the contract leaves them open):**

- **Unknown wallet id:** `RenameAsync`, `SetAliasesAsync`, `MakeDefaultForCurrencyAsync` and `ArchiveAsync` throw `KeyNotFoundException` and change nothing.
- **Blank name:** `CreateAsync` and `RenameAsync` trim the name. A name that is empty after trimming throws `ArgumentException` before anything is written.
- **Archived wallet made default:** `MakeDefaultForCurrencyAsync` throws `InvalidOperationException` and changes nothing. An archived wallet is hidden from capture, so it could never be the default capture resolves to.
- **The default switch** (in `MakeDefaultForCurrencyAsync`, and in `CreateAsync` with `IsDefaultForCurrency = true`) is two `ExecuteUpdateAsync` statements inside one database transaction: clear the currency's current default, then set the new one. `ix_wallets_one_default_per_currency` is an ordinary, non-deferrable unique index. PostgreSQL checks it row by row as each row is written, not at commit. A single `UPDATE … SET is_default_for_currency = (id = @new)` can therefore fail depending on which row it visits first. `SaveChanges` is free to batch and reorder two tracked updates of one table, and it knows nothing about a partial index built in raw SQL. `ExecuteUpdateAsync` runs each statement on the spot, in the order written, and bypasses the change tracker, so there is nothing to reorder.
- **All updates are set-based** (`ExecuteUpdateAsync`). The page's `LedgerDbContext` lives as long as its Blazor circuit. A tracked `Wallet` would keep the values it was loaded with after another statement changed the row, and `SaveChanges` writes only the columns it sees change. So a tracked approach could silently skip clearing a default that had been set since the wallet was loaded. The affected-row count doubles as the unknown-id check.
- **The opening revision's `StatusBefore` is `Completed`.** The record is created completed. There was no earlier state, and `Captured` would claim a pipeline this record never went through.
- **Aliases** are normalised the same way on create and on `SetAliasesAsync`: trimmed, blanks dropped, duplicates ignoring case removed, and the first spelling kept in its original order.
- **`ListAsync` order:** active wallets first, then by name (`StringComparer.OrdinalIgnoreCase`), then by id.
- **The capture zone:** `EfWalletAdmin` takes the `TimeZoneInfo` singleton the Host registers (`CaptureTimeZoneGuard.Resolve(Capture:TimeZone)`, Program.cs lines 33–34; `EfSpendingReadModel` already takes the same one). `TimeZoneId` is that zone's `Id`, which `CaptureTimeZoneGuard` guarantees is IANA (e.g. `Europe/Belgrade`). Telegram captures are stamped with the same configuration value (`TelegramPollingService`, line 77). `OccurredAt` is local midnight of `OpeningDate` in that zone, converted to UTC, because Npgsql writes only offset-zero values to `timestamptz`. The conversion lives in `ZonedClock.StartOfDay`.

**Files:**
- Create: `src/Noof.Ledger.Application/Wallets/IWalletAdmin.cs`
- Create: `src/Noof.Ledger.Persistence/Wallets/EfWalletAdmin.cs`
- Modify: `src/Noof.Ledger.Persistence/ZonedClock.cs` (add `StartOfDay`)
- Modify: `src/Noof.Ledger.Persistence/PersistenceRegistration.cs` (two `using`s among lines 5–20, a registration after line 46)
- Modify: `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs` (the `["Noof.Ledger.Application"]` array, lines 43–58)
- Modify: `tests/Noof.Ledger.Host.Tests/PersistenceRegistrationTests.cs` (a `using` among lines 4–10, a line after line 44)
- Modify: `tests/Noof.Ledger.Persistence.Tests/ZonedClockTests.cs`
- Test: `tests/Noof.Ledger.Persistence.Tests/EfWalletAdminTests.cs` (new)

**Interfaces:**
- Consumes (Task 1): `Wallet` with `Aliases` (`string[]`), `IsDefaultForCurrency`, `Archived` and `CreatedAt`. `Transaction` with `Guid? WalletId`, `TransactionKind Kind`, `long? TelegramChatId`, `int? TelegramMessageId`. `CaptureKind.Manual`, `TransactionKind.BalanceCheck` and `BalanceCheck`. `LedgerDbContext.BalanceChecks`. The partial unique index `ix_wallets_one_default_per_currency ON wallets (currency) WHERE is_default_for_currency`. The check constraints on `transactions` must accept a `Manual` capture with `raw_text` set and both Telegram ids null. The seeded *Main Wallet* (`00000000-0000-0000-0000-000000000001`, RSD, `IsDefaultForCurrency = true`).
- Consumes (today's code): `RevisionLog.AppendAsync(LedgerDbContext db, Transaction transaction, RevisionKind kind, string? instruction, TransactionStatus statusBefore, DateTimeOffset now, CancellationToken cancellationToken)` and `RevisionKind.Initial` (`src/Noof.Ledger.Persistence/Revisions/`). `ZonedClock` (`src/Noof.Ledger.Persistence/ZonedClock.cs`). The `TimeZoneInfo` and `TimeProvider` singletons the Host registers.
- Produces:
  - In namespace `Noof.Ledger.Application.Wallets`: `public sealed record WalletDetails(Guid Id, string Name, CurrencyCode Currency, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency, bool Archived)`, `public sealed record NewWallet(string Name, CurrencyCode Currency, decimal OpeningBalance, DateOnly OpeningDate, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency)`, and `public interface IWalletAdmin` exactly as the contract gives it. It is registered scoped in `AddNoofPersistence` as `EfWalletAdmin`.
  - `internal sealed class EfWalletAdmin(LedgerDbContext db, TimeProvider timeProvider, TimeZoneInfo captureZone) : IWalletAdmin` (namespace `Noof.Ledger.Persistence.Wallets`).
  - `ZonedClock.StartOfDay(DateOnly day, string timeZoneId) → DateTimeOffset`: local midnight of `day` in that zone, with offset zero.
  - The exceptions listed under Decisions. Task 7's page relies on them.

- [ ] **Step 1: Write the failing test for the start of a local day**

Append to the class in `tests/Noof.Ledger.Persistence.Tests/ZonedClockTests.cs`:

```csharp
    [Theory]
    [InlineData("2026-09-01", "2026-08-31T22:00:00Z")]
    [InlineData("2026-01-15", "2026-01-14T23:00:00Z")]
    public void The_start_of_a_Belgrade_day_is_its_local_midnight_as_a_UTC_instant(string day, string expected)
    {
        var start = ZonedClock.StartOfDay(
            DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture), "Europe/Belgrade");

        start.Should().Be(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        start.Offset.Should().Be(TimeSpan.Zero, "Npgsql writes only offset-zero values to timestamptz");
    }
```

The first row falls in summer time (+2), the second in winter time (+1).

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL to compile, because `ZonedClock` has no `StartOfDay`.

- [ ] **Step 2: Add `StartOfDay`, and watch its offset guard fail once**

Replace `src/Noof.Ledger.Persistence/ZonedClock.cs` with:

```csharp
namespace Noof.Ledger.Persistence;

internal static class ZonedClock
{
    public static DateTime LocalDateTime(DateTimeOffset instant, string timeZoneId) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).DateTime;

    public static DateOnly LocalDate(DateTimeOffset instant, string timeZoneId) =>
        DateOnly.FromDateTime(LocalDateTime(instant, timeZoneId));

    public static DateTimeOffset StartOfDay(DateOnly day, string timeZoneId)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        var offset = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId).GetUtcOffset(midnight);

        // UTC because Npgsql refuses a non-zero offset for timestamptz.
        return new DateTimeOffset(midnight, offset).ToUniversalTime();
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter ZonedClockTests`
Expected: PASS (4: the two existing rows of `The_local_day_in_Belgrade_is_not_the_UTC_day` plus the two new ones).

Delete `.ToUniversalTime()` and run again. Expected: both new rows FAIL on `start.Offset` ("Expected start.Offset to be 0s … but found 2h" for the summer row, 1h for the winter row). The instants still compare equal. Revert, and run again. Expected: PASS (4).

- [ ] **Step 3: Write the failing wallet-admin tests**

Create `tests/Noof.Ledger.Persistence.Tests/EfWalletAdminTests.cs`:

```csharp
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Revisions;
using Noof.Ledger.Persistence.Wallets;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class EfWalletAdminTests(PostgresFixture fixture)
{
    // Seeded by AddCaptureModel into every migrated database; Task 1 made it the RSD default.
    static readonly Guid MainWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly DateTimeOffset Now = new(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
    static readonly DateOnly OpeningDay = new(2026, 9, 1);
    static readonly TimeZoneInfo Belgrade = TimeZoneInfo.FindSystemTimeZoneById("Europe/Belgrade");
    static int nextMessageId = 70_000;

    async Task<LedgerDbContext> MigratedAsync()
    {
        var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return db;
    }

    static EfWalletAdmin Admin(LedgerDbContext db) => new(db, new FakeTimeProvider(Now), Belgrade);

    static NewWallet Raiffeisen(bool isDefault = false) =>
        new("Raiffeisen RSD", CurrencyCode.Rsd, 45_000.00m, OpeningDay, ["raif"], isDefault);

    static NewWallet Named(string name, CurrencyCode currency, bool isDefault = false) =>
        new(name, currency, 0m, OpeningDay, [], isDefault);

    static Task<Wallet> ReadAsync(LedgerDbContext db, Guid walletId) =>
        db.Wallets.AsNoTracking().SingleAsync(w => w.Id == walletId, TestContext.Current.CancellationToken);

    static Transaction Expense(Guid walletId, DateOnly on, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(),
        WalletId = walletId,
        Kind = TransactionKind.Expense,
        RawText = "кофе 250",
        Status = TransactionStatus.Completed,
        TimeZoneId = "Europe/Belgrade",
        OccurredAt = at,
        OccurredOn = on,
        TelegramChatId = 1,
        TelegramMessageId = Interlocked.Increment(ref nextMessageId),
        CreatedAt = at,
    };

    [Fact]
    public async Task CreateAsync_stores_the_wallet_with_a_trimmed_name_and_clean_aliases()
    {
        await using var db = await MigratedAsync();

        var walletId = await Admin(db).CreateAsync(
            new NewWallet("  Wise EUR  ", CurrencyCode.Eur, 0m, OpeningDay, [" wise ", "", "WISE", "вайз"], false),
            TestContext.Current.CancellationToken);

        var wallet = await ReadAsync(db, walletId);
        wallet.Name.Should().Be("Wise EUR");
        wallet.Currency.Should().Be(CurrencyCode.Eur);
        wallet.Aliases.Should().Equal("wise", "вайз");
        wallet.IsDefaultForCurrency.Should().BeFalse();
        wallet.Archived.Should().BeFalse();
        wallet.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public async Task CreateAsync_writes_the_opening_balance_as_the_wallets_first_checkpoint()
    {
        await using var db = await MigratedAsync();

        var walletId = await Admin(db).CreateAsync(Raiffeisen(), TestContext.Current.CancellationToken);

        var opening = await db.Transactions.AsNoTracking()
            .SingleAsync(t => t.WalletId == walletId, TestContext.Current.CancellationToken);
        opening.Kind.Should().Be(TransactionKind.BalanceCheck);
        opening.CaptureKind.Should().Be(CaptureKind.Manual);
        opening.RawText.Should().Be("Opening balance");
        opening.Status.Should().Be(TransactionStatus.Completed);
        opening.TelegramChatId.Should().BeNull();
        opening.TelegramMessageId.Should().BeNull();
        opening.TimeZoneId.Should().Be("Europe/Belgrade");
        opening.OccurredOn.Should().Be(OpeningDay);
        opening.OccurredAt.Should().Be(
            new DateTimeOffset(2026, 8, 31, 22, 0, 0, TimeSpan.Zero), "local midnight of 1 September in Belgrade, summer time");
        opening.CreatedAt.Should().Be(Now);

        var checkpoint = await db.BalanceChecks.AsNoTracking()
            .SingleAsync(c => c.TransactionId == opening.Id, TestContext.Current.CancellationToken);
        checkpoint.WalletId.Should().Be(walletId);
        checkpoint.Stated.Should().Be(new Money(45_000.00m, CurrencyCode.Rsd));
        checkpoint.ComputedBefore.Should().Be(0m);

        var revision = await db.TransactionRevisions.AsNoTracking()
            .SingleAsync(r => r.TransactionId == opening.Id, TestContext.Current.CancellationToken);
        revision.Kind.Should().Be(RevisionKind.Initial);
        revision.RevisionNumber.Should().Be(1);
        revision.StatusBefore.Should().Be(TransactionStatus.Completed);
        revision.StatusAfter.Should().Be(TransactionStatus.Completed);
        JsonDocument.Parse(revision.Snapshot).RootElement.GetProperty("raw_text").GetString().Should().Be("Opening balance");
    }

    // What wallet_balances would add on top of the opening checkpoint: the records that sort strictly after it by
    // (occurred_on, occurred_at). The view itself arrives with Task 3 in this same wave.
    static async Task<List<Guid>> RecordsAfterTheOpeningAsync(LedgerDbContext db, Guid walletId)
    {
        var openingId = await db.BalanceChecks.AsNoTracking()
            .Where(c => c.WalletId == walletId)
            .Select(c => c.TransactionId)
            .SingleAsync(TestContext.Current.CancellationToken);

        return await db.Database.SqlQuery<Guid>(
            $"""
            SELECT t.id AS "Value"
            FROM public.transactions t
            JOIN public.transactions opening ON opening.id = {openingId}
            WHERE t.id <> opening.id
              AND (t.occurred_on, t.occurred_at) > (opening.occurred_on, opening.occurred_at)
            """).ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_opening_balance_sorts_before_every_record_from_its_own_day_on()
    {
        await using var db = await MigratedAsync();
        var walletId = await Admin(db).CreateAsync(Raiffeisen(), TestContext.Current.CancellationToken);
        var dayBefore = Expense(walletId, new DateOnly(2026, 8, 31), new DateTimeOffset(2026, 8, 31, 20, 0, 0, TimeSpan.Zero));
        var firstSecondOfTheDay = Expense(walletId, OpeningDay, new DateTimeOffset(2026, 8, 31, 22, 0, 1, TimeSpan.Zero));
        var daysLater = Expense(walletId, new DateOnly(2026, 9, 5), new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero));
        db.Transactions.AddRange(dayBefore, firstSecondOfTheDay, daysLater);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await RecordsAfterTheOpeningAsync(db, walletId)).Should().BeEquivalentTo(
            new[] { firstSecondOfTheDay.Id, daysLater.Id },
            "wallet_balances adds only what sorts strictly after a checkpoint, and the opening one sits at local midnight");
    }

    [Fact]
    public async Task CreateAsync_when_the_opening_date_is_after_existing_records_anchors_them_all()
    {
        await using var db = await MigratedAsync();
        var walletId = await Admin(db).CreateAsync(Raiffeisen(), TestContext.Current.CancellationToken);
        // Spent weeks before the operator set the wallet up with 1 September as its opening day.
        var weeksBefore = Expense(walletId, new DateOnly(2026, 8, 20), new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        var dayAfter = Expense(walletId, new DateOnly(2026, 9, 2), new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero));
        db.Transactions.AddRange(weeksBefore, dayAfter);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        (await RecordsAfterTheOpeningAsync(db, walletId)).Should().ContainSingle().Which.Should().Be(
            dayAfter.Id,
            "the opening balance already contains what was spent before it; counting it again would take it out twice");
    }

    [Fact]
    public async Task CreateAsync_as_the_default_takes_over_from_the_previous_default_of_that_currency()
    {
        await using var db = await MigratedAsync();

        var walletId = await Admin(db).CreateAsync(Raiffeisen(isDefault: true), TestContext.Current.CancellationToken);

        (await ReadAsync(db, walletId)).IsDefaultForCurrency.Should().BeTrue();
        (await ReadAsync(db, MainWalletId)).IsDefaultForCurrency.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_not_as_the_default_leaves_the_current_default_alone()
    {
        await using var db = await MigratedAsync();

        var walletId = await Admin(db).CreateAsync(Raiffeisen(isDefault: false), TestContext.Current.CancellationToken);

        (await ReadAsync(db, walletId)).IsDefaultForCurrency.Should().BeFalse();
        (await ReadAsync(db, MainWalletId)).IsDefaultForCurrency.Should().BeTrue();
    }

    [Fact]
    public async Task A_create_that_fails_leaves_the_previous_default_in_place()
    {
        await using var db = await MigratedAsync();
        var tooLong = new NewWallet(new string('x', 129), CurrencyCode.Rsd, 0m, OpeningDay, [], IsDefaultForCurrency: true);

        var act = () => Admin(db).CreateAsync(tooLong, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<DbUpdateException>();
        (await ReadAsync(db, MainWalletId)).IsDefaultForCurrency.Should().BeTrue(
            "clearing the old default and inserting the new wallet are one database transaction");
    }

    [Fact]
    public async Task CreateAsync_refuses_a_blank_name_and_writes_nothing()
    {
        await using var db = await MigratedAsync();

        var act = () => Admin(db).CreateAsync(Named("   ", CurrencyCode.Eur), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
        (await db.Wallets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1, "only the seeded wallet exists");
    }

    [Fact]
    public async Task MakeDefaultForCurrencyAsync_moves_the_default_between_two_wallets_of_one_currency()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        var wiseEur = await admin.CreateAsync(Named("Wise EUR", CurrencyCode.Eur, isDefault: true), TestContext.Current.CancellationToken);
        var cashEur = await admin.CreateAsync(Named("Cash EUR", CurrencyCode.Eur), TestContext.Current.CancellationToken);

        await admin.MakeDefaultForCurrencyAsync(cashEur, TestContext.Current.CancellationToken);

        (await ReadAsync(db, cashEur)).IsDefaultForCurrency.Should().BeTrue();
        (await ReadAsync(db, wiseEur)).IsDefaultForCurrency.Should().BeFalse();
        (await ReadAsync(db, MainWalletId)).IsDefaultForCurrency.Should().BeTrue("RSD's default belongs to another currency");

        await admin.MakeDefaultForCurrencyAsync(wiseEur, TestContext.Current.CancellationToken);

        (await ReadAsync(db, wiseEur)).IsDefaultForCurrency.Should().BeTrue();
        (await ReadAsync(db, cashEur)).IsDefaultForCurrency.Should().BeFalse();
    }

    [Fact]
    public async Task MakeDefaultForCurrencyAsync_on_the_current_default_changes_nothing()
    {
        await using var db = await MigratedAsync();

        await Admin(db).MakeDefaultForCurrencyAsync(MainWalletId, TestContext.Current.CancellationToken);

        (await ReadAsync(db, MainWalletId)).IsDefaultForCurrency.Should().BeTrue();
    }

    [Fact]
    public async Task MakeDefaultForCurrencyAsync_refuses_an_archived_wallet()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        var walletId = await admin.CreateAsync(Raiffeisen(), TestContext.Current.CancellationToken);
        await admin.ArchiveAsync(walletId, TestContext.Current.CancellationToken);

        var act = () => admin.MakeDefaultForCurrencyAsync(walletId, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await ReadAsync(db, walletId)).IsDefaultForCurrency.Should().BeFalse();
        (await ReadAsync(db, MainWalletId)).IsDefaultForCurrency.Should().BeTrue();
    }

    [Fact]
    public async Task ArchiveAsync_hides_the_wallet_gives_up_its_default_and_keeps_its_history()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        var walletId = await admin.CreateAsync(Raiffeisen(isDefault: true), TestContext.Current.CancellationToken);

        await admin.ArchiveAsync(walletId, TestContext.Current.CancellationToken);

        var wallet = await ReadAsync(db, walletId);
        wallet.Archived.Should().BeTrue();
        wallet.IsDefaultForCurrency.Should().BeFalse();
        (await db.BalanceChecks.AsNoTracking().CountAsync(c => c.WalletId == walletId, TestContext.Current.CancellationToken))
            .Should().Be(1, "archiving hides a wallet, it does not erase what happened in it");
    }

    [Fact]
    public async Task RenameAsync_stores_the_trimmed_name()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        var walletId = await admin.CreateAsync(Raiffeisen(), TestContext.Current.CancellationToken);

        await admin.RenameAsync(walletId, "  Raiffeisen  ", TestContext.Current.CancellationToken);

        (await ReadAsync(db, walletId)).Name.Should().Be("Raiffeisen");
    }

    [Fact]
    public async Task RenameAsync_refuses_a_blank_name_and_keeps_the_old_one()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        var walletId = await admin.CreateAsync(Raiffeisen(), TestContext.Current.CancellationToken);

        var act = () => admin.RenameAsync(walletId, " ", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
        (await ReadAsync(db, walletId)).Name.Should().Be("Raiffeisen RSD");
    }

    [Fact]
    public async Task SetAliasesAsync_trims_drops_blanks_and_keeps_the_first_spelling_of_each_word()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        var walletId = await admin.CreateAsync(Raiffeisen(), TestContext.Current.CancellationToken);

        await admin.SetAliasesAsync(
            walletId, ["  Райф ", "", "raif", "РАЙФ", "   ", "Raiffeisen", "RAIF"], TestContext.Current.CancellationToken);

        (await ReadAsync(db, walletId)).Aliases.Should().Equal("Райф", "raif", "Raiffeisen");

        await admin.SetAliasesAsync(walletId, [" ", ""], TestContext.Current.CancellationToken);

        (await ReadAsync(db, walletId)).Aliases.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_lists_every_wallet_archived_last_then_by_name()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        await admin.CreateAsync(Named("wise", CurrencyCode.Eur), TestContext.Current.CancellationToken);
        var cash = await admin.CreateAsync(
            new NewWallet("Cash", CurrencyCode.Rsd, 0m, OpeningDay, ["наличка"], false), TestContext.Current.CancellationToken);
        var alpha = await admin.CreateAsync(Named("Alpha", CurrencyCode.Usd, isDefault: true), TestContext.Current.CancellationToken);
        await admin.ArchiveAsync(alpha, TestContext.Current.CancellationToken);

        var wallets = await admin.ListAsync(TestContext.Current.CancellationToken);

        wallets.Select(w => w.Name).Should().Equal("Cash", "Main Wallet", "wise", "Alpha");
        wallets.Single(w => w.Id == cash).Should().BeEquivalentTo(
            new WalletDetails(cash, "Cash", CurrencyCode.Rsd, ["наличка"], IsDefaultForCurrency: false, Archived: false));
        wallets.Single(w => w.Id == alpha).Archived.Should().BeTrue();
        wallets.Single(w => w.Id == MainWalletId).IsDefaultForCurrency.Should().BeTrue();
    }

    [Fact]
    public async Task Every_method_given_an_unknown_wallet_id_throws_and_changes_nothing()
    {
        await using var db = await MigratedAsync();
        var admin = Admin(db);
        var unknown = Guid.NewGuid();

        var rename = () => admin.RenameAsync(unknown, "Cash", TestContext.Current.CancellationToken);
        var setAliases = () => admin.SetAliasesAsync(unknown, ["cash"], TestContext.Current.CancellationToken);
        var makeDefault = () => admin.MakeDefaultForCurrencyAsync(unknown, TestContext.Current.CancellationToken);
        var archive = () => admin.ArchiveAsync(unknown, TestContext.Current.CancellationToken);

        await rename.Should().ThrowAsync<KeyNotFoundException>();
        await setAliases.Should().ThrowAsync<KeyNotFoundException>();
        await makeDefault.Should().ThrowAsync<KeyNotFoundException>();
        await archive.Should().ThrowAsync<KeyNotFoundException>();
        (await ReadAsync(db, MainWalletId)).IsDefaultForCurrency.Should().BeTrue();
    }
}
```

Run: `dotnet build NoofLedger.slnx`
Expected: FAIL to compile. `Noof.Ledger.Application.Wallets`, `NewWallet`, `WalletDetails` and `EfWalletAdmin` do not exist. That is the red here, because these tests cannot exist without the types they call.

- [ ] **Step 4: Declare the contract**

Create `src/Noof.Ledger.Application/Wallets/IWalletAdmin.cs`:

```csharp
using Noof.Ledger.Domain;

namespace Noof.Ledger.Application.Wallets;

public sealed record WalletDetails(
    Guid Id, string Name, CurrencyCode Currency, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency, bool Archived);

public sealed record NewWallet(
    string Name, CurrencyCode Currency, decimal OpeningBalance, DateOnly OpeningDate, IReadOnlyList<string> Aliases,
    bool IsDefaultForCurrency);

// A caller that lets these through has a bug, not an operator error, so the page checks before it calls: a name
// blank after trimming throws ArgumentException, an unknown wallet id KeyNotFoundException, and making an archived
// wallet a default InvalidOperationException. Nothing is written in any of those cases.
public interface IWalletAdmin
{
    Task<IReadOnlyList<WalletDetails>> ListAsync(CancellationToken cancellationToken);

    Task<Guid> CreateAsync(NewWallet wallet, CancellationToken cancellationToken);

    Task RenameAsync(Guid walletId, string name, CancellationToken cancellationToken);

    Task SetAliasesAsync(Guid walletId, IReadOnlyList<string> aliases, CancellationToken cancellationToken);

    Task MakeDefaultForCurrencyAsync(Guid walletId, CancellationToken cancellationToken);

    Task ArchiveAsync(Guid walletId, CancellationToken cancellationToken);
}
```

`Wallets/IWalletDirectory.cs` (Task 4, same wave) is a different file in the same folder and namespace, so the two do not collide.

In `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`, in the `["Noof.Ledger.Application"]` array, append the line `"WalletDetails", "NewWallet", "IWalletAdmin",` after the line `"ITranscriber", "ISpeechProvider", "CapturedVoice", "ITranscriptionStore", "IVoiceFileSource",`. Tasks 3, 4 and 12 append to the same array in this wave. When merging, keep every name from every side.

- [ ] **Step 5: Write `EfWalletAdmin`**

Create `src/Noof.Ledger.Persistence/Wallets/EfWalletAdmin.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Application.Wallets;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Revisions;

namespace Noof.Ledger.Persistence.Wallets;

// Every change is a set-based statement, never a tracked entity: the page's context lives as long as its circuit, and
// a tracked wallet keeps values another statement has since overwritten, so SaveChanges would skip a column it
// believes unchanged.
internal sealed class EfWalletAdmin(LedgerDbContext db, TimeProvider timeProvider, TimeZoneInfo captureZone) : IWalletAdmin
{
    const string OpeningBalanceText = "Opening balance";

    public async Task<IReadOnlyList<WalletDetails>> ListAsync(CancellationToken cancellationToken)
    {
        var wallets = await db.Wallets.AsNoTracking().ToListAsync(cancellationToken);

        return
        [
            .. wallets
                .OrderBy(wallet => wallet.Archived)
                .ThenBy(wallet => wallet.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(wallet => wallet.Id)
                .Select(wallet => new WalletDetails(
                    wallet.Id, wallet.Name, wallet.Currency, wallet.Aliases, wallet.IsDefaultForCurrency, wallet.Archived)),
        ];
    }

    public async Task<Guid> CreateAsync(NewWallet wallet, CancellationToken cancellationToken)
    {
        var name = RequireName(wallet.Name);
        var now = timeProvider.GetUtcNow();
        var walletId = Guid.NewGuid();
        var opening = new Transaction
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            Kind = TransactionKind.BalanceCheck,
            CaptureKind = CaptureKind.Manual,
            RawText = OpeningBalanceText,
            Status = TransactionStatus.Completed,
            TimeZoneId = captureZone.Id,
            OccurredOn = wallet.OpeningDate,
            // Midnight, so the opening balance precedes everything recorded on its own day.
            OccurredAt = ZonedClock.StartOfDay(wallet.OpeningDate, captureZone.Id),
            CreatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        if (wallet.IsDefaultForCurrency)
            await ClearDefaultAsync(wallet.Currency, walletId, cancellationToken);

        db.Wallets.Add(new Wallet
        {
            Id = walletId,
            Name = name,
            Currency = wallet.Currency,
            Aliases = Normalise(wallet.Aliases),
            IsDefaultForCurrency = wallet.IsDefaultForCurrency,
            CreatedAt = now,
        });
        db.Transactions.Add(opening);
        db.BalanceChecks.Add(new BalanceCheck
        {
            TransactionId = opening.Id,
            WalletId = walletId,
            Stated = new Money(wallet.OpeningBalance, wallet.Currency),
            ComputedBefore = 0m,
        });
        await db.SaveChangesAsync(cancellationToken);

        // Born completed: there was no earlier state, and Captured would claim a pipeline this record never went through.
        await RevisionLog.AppendAsync(db, opening, RevisionKind.Initial, null, TransactionStatus.Completed, now, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return walletId;
    }

    public async Task RenameAsync(Guid walletId, string name, CancellationToken cancellationToken)
    {
        var trimmed = RequireName(name);

        RequireFound(walletId, await WalletById(walletId)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.Name, trimmed), cancellationToken));
    }

    public async Task SetAliasesAsync(Guid walletId, IReadOnlyList<string> aliases, CancellationToken cancellationToken)
    {
        var normalised = Normalise(aliases);

        RequireFound(walletId, await WalletById(walletId)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.Aliases, normalised), cancellationToken));
    }

    public async Task MakeDefaultForCurrencyAsync(Guid walletId, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var wallet = await WalletById(walletId).AsNoTracking()
            .Select(w => new { w.Currency, w.Archived })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw Unknown(walletId);

        if (wallet.Archived)
            throw new InvalidOperationException(
                $"Wallet {walletId} is archived; an archived wallet cannot be the default for {wallet.Currency}.");

        // Clear, then set, as two statements executed on the spot. ix_wallets_one_default_per_currency is checked row by
        // row, so the new default written before the old one is cleared - an order SaveChanges' batching is free to
        // choose - would violate it.
        await ClearDefaultAsync(wallet.Currency, walletId, cancellationToken);
        await WalletById(walletId)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.IsDefaultForCurrency, true), cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public async Task ArchiveAsync(Guid walletId, CancellationToken cancellationToken)
    {
        // An archived wallet is hidden from capture, so it cannot stay the default capture resolves to.
        RequireFound(walletId, await WalletById(walletId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(w => w.Archived, true).SetProperty(w => w.IsDefaultForCurrency, false),
                cancellationToken));
    }

    IQueryable<Wallet> WalletById(Guid walletId) => db.Wallets.Where(w => w.Id == walletId);

    Task<int> ClearDefaultAsync(CurrencyCode currency, Guid exceptWalletId, CancellationToken cancellationToken) =>
        db.Wallets
            .Where(w => w.Currency == currency && w.IsDefaultForCurrency && w.Id != exceptWalletId)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.IsDefaultForCurrency, false), cancellationToken);

    static void RequireFound(Guid walletId, int updatedRows)
    {
        if (updatedRows == 0)
            throw Unknown(walletId);
    }

    static KeyNotFoundException Unknown(Guid walletId) => new($"There is no wallet {walletId}.");

    static string RequireName(string name) =>
        name.Trim() is { Length: > 0 } trimmed ? trimmed : throw new ArgumentException("A wallet needs a name.", nameof(name));

    static string[] Normalise(IEnumerable<string> aliases) =>
        [.. aliases.Select(alias => alias.Trim()).Where(alias => alias.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
}
```

`SetAliasesAsync` passes a `string[]` to `SetProperty` for a `text[]` column, and EF parameterises it with the column's own type mapping. `SetAliasesAsync_trims_drops_blanks_and_keeps_the_first_spelling_of_each_word` fails loudly if this version of EF refuses to translate that (an `InvalidOperationException` naming `SetProperty`). If it does, replace that one statement with `db.Database.ExecuteSqlAsync($"UPDATE public.wallets SET aliases = {normalised} WHERE id = {walletId}", cancellationToken)`. Npgsql sends a `string[]` parameter as `text[]`. Feed its row count to `RequireFound` exactly as before.

`ClearDefaultAsync` compares `w.Currency == currency`, and no query in `src` compares a `CurrencyCode` property yet. EF translates `==` on a value-converted record struct by comparing the converted column with the converted parameter. If this version refuses (an `InvalidOperationException` saying the `Where` "could not be translated", surfacing in `CreateAsync_as_the_default_takes_over_from_the_previous_default_of_that_currency` and `MakeDefaultForCurrencyAsync_moves_the_default_between_two_wallets_of_one_currency`), write the predicate as `w.Currency.Equals(currency) && w.IsDefaultForCurrency && w.Id != exceptWalletId`. EF maps `Equals` on converted types the same way.

- [ ] **Step 6: Run the tests and see them pass**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter EfWalletAdminTests`
Expected: PASS (17).

`CreateAsync_writes_the_opening_balance_as_the_wallets_first_checkpoint` may fail with `PostgresException 23514` naming `ck_transactions_capture_has_content`. If so, Task 1's check constraint does not yet accept `capture_kind = 2`. Stop and report it against Task 1; do not add a migration in this task. The same applies to a `23514` naming `ck_transactions_telegram_ids_match_capture_kind`.

- [ ] **Step 7: Watch each guard fail once**

Rebuild after each edit, and revert each edit before making the next one.

1. In `MakeDefaultForCurrencyAsync`, move `await ClearDefaultAsync(wallet.Currency, walletId, cancellationToken);` below the statement that sets `IsDefaultForCurrency` to `true`.
   Run `--filter EfWalletAdminTests`. Expected: `MakeDefaultForCurrencyAsync_moves_the_default_between_two_wallets_of_one_currency` FAILS with `Npgsql.PostgresException : 23505: duplicate key value violates unique constraint "ix_wallets_one_default_per_currency"`. Revert.
2. In `CreateAsync`, delete the line `await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);` and the line `await tx.CommitAsync(cancellationToken);`.
   Expected: `A_create_that_fails_leaves_the_previous_default_in_place` FAILS. The Main Wallet is no longer the default, because the clearing statement committed on its own before the insert failed. Revert.
3. In `ArchiveAsync`, delete `.SetProperty(w => w.IsDefaultForCurrency, false)`.
   Expected: `ArchiveAsync_hides_the_wallet_gives_up_its_default_and_keeps_its_history` FAILS. Revert.
4. In `Normalise`, delete `.Distinct(StringComparer.OrdinalIgnoreCase)`.
   Expected: `SetAliasesAsync_trims_drops_blanks_and_keeps_the_first_spelling_of_each_word` FAILS, and so does `CreateAsync_stores_the_wallet_with_a_trimmed_name_and_clean_aliases`. Revert.
5. In `CreateAsync`, replace `OccurredAt = ZonedClock.StartOfDay(wallet.OpeningDate, captureZone.Id),` with `OccurredAt = now,`.
   Expected: `The_opening_balance_sorts_before_every_record_from_its_own_day_on` FAILS, because the checkpoint's `occurred_at` is now 24 September's instant while its `occurred_on` stays 1 September, so the expense at 22:00:01 UTC on 31 August (the first second of 1 September in Belgrade) no longer sorts after it. `CreateAsync_writes_the_opening_balance_as_the_wallets_first_checkpoint` fails too. Revert.
6. In `CreateAsync`, replace `OccurredOn = wallet.OpeningDate,` with `OccurredOn = DateOnly.FromDateTime(now.UtcDateTime),`.
   Expected: `CreateAsync_when_the_opening_date_is_after_existing_records_anchors_them_all` FAILS on `ContainSingle`: the checkpoint now sorts on 24 September, so the 2 September expense no longer comes after it and the list is empty. `The_opening_balance_sorts_before_every_record_from_its_own_day_on` and `CreateAsync_writes_the_opening_balance_as_the_wallets_first_checkpoint` fail too. Revert.

Run `--filter EfWalletAdminTests` again. Expected: PASS (17).

- [ ] **Step 8: Register the service, test first**

In `tests/Noof.Ledger.Host.Tests/PersistenceRegistrationTests.cs`, add `using Noof.Ledger.Application.Wallets;` after `using Noof.Ledger.Application.Secrets;` (line 9), and after line 44 (`...GetRequiredService<ISpendingReadModel>()...`) add:

```csharp
        scope.ServiceProvider.GetRequiredService<IWalletAdmin>().Should().NotBeNull();
```

The test already registers `TimeProvider.System` and `TimeZoneInfo.Utc` (lines 20–21), which is everything `EfWalletAdmin`'s constructor asks for besides the context.

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj --filter PersistenceRegistrationTests`
Expected: FAIL with `No service for type 'Noof.Ledger.Application.Wallets.IWalletAdmin' has been registered.`

In `src/Noof.Ledger.Persistence/PersistenceRegistration.cs`, add `using Noof.Ledger.Application.Wallets;` after `using Noof.Ledger.Application.Transcription;` (line 12). Add `using Noof.Ledger.Persistence.Wallets;` after `using Noof.Ledger.Persistence.Transcription;` (line 20). After line 46 (`services.AddScoped<ISpendingReadModel, EfSpendingReadModel>();`), add:

```csharp
        services.AddScoped<IWalletAdmin, EfWalletAdmin>();
```

Run it again. Expected: PASS.

Tasks 3, 4 and 12 edit the same two files in this wave. When merging, keep one copy of each `using` directive: Task 4 adds the same `using Noof.Ledger.Application.Wallets;` and `using Noof.Ledger.Persistence.Wallets;` to `PersistenceRegistration.cs` and `using Noof.Ledger.Application.Wallets;` to `PersistenceRegistrationTests.cs`, and a duplicate is CS0105, an error under `TreatWarningsAsErrors`. Task 12's shape of `AddNoofPersistence` (a `connectionString` local) wins, and `services.AddScoped<IWalletAdmin, EfWalletAdmin>();` is re-added after `services.AddScoped<ISpendingReadModel, EfSpendingReadModel>();` in it. Keep every other task's registration and assertion lines.

- [ ] **Step 9: Run the affected projects**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: PASS: the whole project, with 19 new tests (`EfWalletAdminTests` 17, `ZonedClockTests` 2).

Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS. `WalletDetails`, `NewWallet` and `IWalletAdmin` are allowed. `EfWalletAdmin` is internal, so `No_public_concrete_service_crosses_an_infrastructure_boundary` still passes.

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Expected: PASS.

- [ ] **Step 10: Run the whole solution, holding the suite lock**

This task adds no migration, so the test template is not touched.

```powershell
$lock = 'C:\Users\noofs\AppData\Local\Temp\noof-suite.lock'
while (-not (New-Item -ItemType Directory $lock -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 30 }
try { dotnet test --solution NoofLedger.slnx } finally { Remove-Item $lock }
```

Expected: every test passes. The count is the branch's count before this task plus 19.

- [ ] **Step 11: Commit**

```bash
git add src/Noof.Ledger.Application/Wallets/IWalletAdmin.cs src/Noof.Ledger.Persistence/Wallets/EfWalletAdmin.cs src/Noof.Ledger.Persistence/ZonedClock.cs src/Noof.Ledger.Persistence/PersistenceRegistration.cs tests/Noof.Ledger.Persistence.Tests/EfWalletAdminTests.cs tests/Noof.Ledger.Persistence.Tests/ZonedClockTests.cs tests/Noof.Ledger.Host.Tests/PersistenceRegistrationTests.cs tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs
git commit -m "feat(persistence): wallet administration with the opening balance as the first checkpoint (M2, M4, M7)

The default moves as two ordered statements in one database transaction, so the
per-currency partial unique index never sees two defaults.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m"
```

`git status` must show a clean tree afterwards.

---

### Task 12: Backup core

**Files:**
- Create: `src/Noof.Ledger.Application/Backup/BackupContract.cs` — `BackupRunRecord`, `BackupStatus`, `DumpResult`.
- Create: `src/Noof.Ledger.Application/Backup/IBackupLog.cs`
- Create: `src/Noof.Ledger.Application/Backup/IDatabaseDumper.cs`
- Create: `src/Noof.Ledger.Persistence/Backup/EfBackupLog.cs`
- Create: `src/Noof.Ledger.Persistence/Backup/PgDumpDatabaseDumper.cs` — also holds `internal static class PgDumpArguments` (pure argument-list builder, split out so the "no password in arguments" guarantee is unit-testable without a real `pg_dump.exe`).
- Modify: `src/Noof.Ledger.Persistence/PersistenceRegistration.cs` — register `IBackupLog` and `IDatabaseDumper`.
- Modify: `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs` — add `"BackupRunRecord", "BackupStatus", "DumpResult", "IBackupLog", "IDatabaseDumper"` to `Allowed["Noof.Ledger.Application"]`.
- Test: `tests/Noof.Ledger.Persistence.Tests/PgDumpArgumentsTests.cs` (new), `tests/Noof.Ledger.Persistence.Tests/EfBackupLogTests.cs` (new), `tests/Noof.Ledger.Persistence.Tests/PgDumpDatabaseDumperTests.cs` (new).

> **Contract correction (consistent with the review's Task 12 finding 1 and the contract table's verdict):** `BackupRetention` does not belong to this task. It is `internal static class BackupRetention` in `Noof.Ledger.Host.Workers`, created by Task 13, which is its only caller (`Noof.Ledger.Host.Tests` already carries the `InternalsVisibleTo` this needs). This task neither creates it nor tests it — the review's rejection of a public Application-level `BackupRetention` stands: CLAUDE.md §3 names its public-helper exemptions "not a licence to invent more by analogy," and nothing outside `BackupWorker` calls it.
>
> **Also removed here (review finding 2):** `BackupRun.cs`, `BackupRunConfiguration.cs` and the `LedgerDbContext.BackupRuns` `DbSet` are Task 1's, not this task's — verified against `plan-10-task-01.md:1264-1319`, which already creates all three. This task only consumes `db.BackupRuns`, never declares it.

**Interfaces:**
- Consumes (Task 1 — read only, do not modify): the `backup_runs` table, created by Task 1's migration `AddMoneyModel` with exactly these columns (snake_case, per the contract's "Persistence internals" section): `id uuid PK`, `started_at timestamptz NOT NULL`, `finished_at timestamptz NOT NULL`, `succeeded boolean NOT NULL`, `file_name text NULL`, `size_bytes bigint NULL`, `error text NULL`. If the template clone this task's tests run against does not have this table (`relation "backup_runs" does not exist`), Task 1 has not landed yet or used different column names — stop and report the mismatch; do not guess a different shape.
- Consumes: `LedgerConnectionString.Resolve` (`src/Noof.Ledger.Persistence/LedgerConnectionString.cs`, unchanged), `PersistenceRegistration.AddNoofPersistence(IConfiguration, int)` (unchanged signature — this task only adds two lines to its body).
- Produces (namespace `Noof.Ledger.Application.Backup`, every member below is exactly what Task 13 (`BackupWorker`) and Task 9 (dashboard backup status) consume):
  ```csharp
  public sealed record BackupRunRecord(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool Succeeded, string? FileName, long? SizeBytes, string? Error);
  public sealed record BackupStatus(DateTimeOffset? LastSuccessAt, bool LastRunFailed, string? LastError);
  public interface IBackupLog
  {
      Task RecordAsync(BackupRunRecord run, CancellationToken cancellationToken);
      Task<BackupStatus> StatusAsync(CancellationToken cancellationToken);
  }
  public sealed record DumpResult(bool Succeeded, string? Error);
  public interface IDatabaseDumper { Task<DumpResult> DumpAsync(string targetPath, CancellationToken cancellationToken); }
  ```
  Both interfaces are registered scoped by `AddNoofPersistence` (`IBackupLog` → `EfBackupLog`, `IDatabaseDumper` → `PgDumpDatabaseDumper`), reached only through the interface — neither concrete class is public.

---

- [ ] **Step 1: Write the failing test for `PgDumpArguments` (the password-safety guard)**

  `tests/Noof.Ledger.Persistence.Tests/PgDumpArgumentsTests.cs`:

  ```csharp
  using AwesomeAssertions;
  using Noof.Ledger.Persistence.Backup;
  using Npgsql;

  namespace Noof.Ledger.Persistence.Tests;

  public class PgDumpArgumentsTests
  {
      [Fact]
      public void Names_host_port_user_database_and_the_target_file_but_never_the_password()
      {
          var builder = new NpgsqlConnectionStringBuilder(
              "Host=db.example.internal;Port=5433;Database=noof_ledger;Username=noof;Password=correct-horse-battery-staple");

          var args = PgDumpArguments.Build(builder, @"C:\backups\out.dump");

          args.Should().ContainInOrder("-h", "db.example.internal", "-p", "5433", "-U", "noof", "-d", "noof_ledger", "-f", @"C:\backups\out.dump");
          args.Should().Contain("-Fc");
          args.Should().NotContain(a => a.Contains("correct-horse-battery-staple", StringComparison.Ordinal));
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests --filter PgDumpArgumentsTests`
  Expected FAIL: compiler error — `Noof.Ledger.Persistence.Backup.PgDumpArguments` does not exist.

- [ ] **Step 2: Implement `PgDumpArguments` and `PgDumpDatabaseDumper`**

  `src/Noof.Ledger.Persistence/Backup/PgDumpDatabaseDumper.cs`:

  ```csharp
  using System.Diagnostics;
  using System.Globalization;
  using Noof.Ledger.Application.Backup;
  using Npgsql;

  namespace Noof.Ledger.Persistence.Backup;

  // A pure function so "the password never reaches an argument" is a unit test against a plain
  // string list, not something only provable by launching a real process (CLAUDE.md §4 "External
  // processes": ProcessStartInfo.Environment carries the password, ArgumentList never does).
  internal static class PgDumpArguments
  {
      public static IReadOnlyList<string> Build(NpgsqlConnectionStringBuilder connection, string targetPath) =>
      [
          "-Fc",
          "-h", connection.Host ?? "localhost",
          "-p", connection.Port.ToString(CultureInfo.InvariantCulture),
          "-U", connection.Username ?? string.Empty,
          "-d", connection.Database ?? string.Empty,
          "-f", targetPath,
      ];
  }

  internal sealed class PgDumpDatabaseDumper(string connectionString, string pgDumpPath) : IDatabaseDumper
  {
      public const string DefaultPath = @"C:\Program Files\PostgreSQL\18\bin\pg_dump.exe";

      public async Task<DumpResult> DumpAsync(string targetPath, CancellationToken cancellationToken)
      {
          var connection = new NpgsqlConnectionStringBuilder(connectionString);

          var start = new ProcessStartInfo
          {
              FileName = pgDumpPath,
              UseShellExecute = false,
              RedirectStandardOutput = true,
              RedirectStandardError = true,
          };
          foreach (var argument in PgDumpArguments.Build(connection, targetPath))
              start.ArgumentList.Add(argument);

          // The one and only place the password reaches the child process (CLAUDE.md §4).
          start.Environment["PGPASSWORD"] = connection.Password ?? string.Empty;

          using var process = new Process { StartInfo = start };
          process.Start();
          var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
          var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
          await process.WaitForExitAsync(cancellationToken);
          var stderr = Scrub(await stderrTask, connection.Password);
          await stdoutTask;

          return process.ExitCode == 0
              ? new DumpResult(true, null)
              : new DumpResult(false, string.IsNullOrWhiteSpace(stderr) ? $"pg_dump exited with code {process.ExitCode}" : stderr.Trim());
      }

      // Defence in depth, not the primary guarantee: pg_dump itself never echoes a working
      // password, but a wrong one can appear in a connection-refused message from some drivers,
      // and CLAUDE.md §4 says the password never reaches a recorded error, full stop.
      static string Scrub(string text, string? password) =>
          string.IsNullOrEmpty(password) ? text : text.Replace(password, "***", StringComparison.Ordinal);
  }
  ```

  `src/Noof.Ledger.Persistence/Backup/EfBackupLog.cs`:

  ```csharp
  using Microsoft.EntityFrameworkCore;
  using Noof.Ledger.Application.Backup;

  namespace Noof.Ledger.Persistence.Backup;

  internal sealed class EfBackupLog(LedgerDbContext db) : IBackupLog
  {
      public async Task RecordAsync(BackupRunRecord run, CancellationToken cancellationToken)
      {
          db.BackupRuns.Add(new BackupRun
          {
              Id = Guid.NewGuid(),
              StartedAt = run.StartedAt,
              FinishedAt = run.FinishedAt,
              Succeeded = run.Succeeded,
              FileName = run.FileName,
              SizeBytes = run.SizeBytes,
              Error = run.Error,
          });

          await db.SaveChangesAsync(cancellationToken);
      }

      public async Task<BackupStatus> StatusAsync(CancellationToken cancellationToken)
      {
          var lastSuccessAt = await db.BackupRuns.AsNoTracking()
              .Where(r => r.Succeeded)
              .OrderByDescending(r => r.FinishedAt)
              .Select(r => (DateTimeOffset?)r.FinishedAt)
              .FirstOrDefaultAsync(cancellationToken);

          var lastRun = await db.BackupRuns.AsNoTracking()
              .OrderByDescending(r => r.FinishedAt)
              .FirstOrDefaultAsync(cancellationToken);

          var lastRunFailed = lastRun is { Succeeded: false };
          return new BackupStatus(lastSuccessAt, lastRunFailed, lastRunFailed ? lastRun!.Error : null);
      }
  }
  ```

  `src/Noof.Ledger.Application/Backup/BackupContract.cs`:

  ```csharp
  namespace Noof.Ledger.Application.Backup;

  public sealed record BackupRunRecord(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool Succeeded, string? FileName, long? SizeBytes, string? Error);

  public sealed record BackupStatus(DateTimeOffset? LastSuccessAt, bool LastRunFailed, string? LastError);

  public sealed record DumpResult(bool Succeeded, string? Error);
  ```

  `src/Noof.Ledger.Application/Backup/IBackupLog.cs`:

  ```csharp
  namespace Noof.Ledger.Application.Backup;

  public interface IBackupLog
  {
      Task RecordAsync(BackupRunRecord run, CancellationToken cancellationToken);
      Task<BackupStatus> StatusAsync(CancellationToken cancellationToken);
  }
  ```

  `src/Noof.Ledger.Application/Backup/IDatabaseDumper.cs`:

  ```csharp
  namespace Noof.Ledger.Application.Backup;

  public interface IDatabaseDumper
  {
      Task<DumpResult> DumpAsync(string targetPath, CancellationToken cancellationToken);
  }
  ```

  Run `dotnet test --project tests/Noof.Ledger.Persistence.Tests --filter PgDumpArgumentsTests`. Expected PASS.

- [ ] **Step 3: Watch the password guard fail**

  In `PgDumpArguments.Build`, temporarily add `"--password", connection.Password ?? string.Empty` to the returned list, re-run `PgDumpArgumentsTests`. It must fail on `NotContain(a => a.Contains("correct-horse-battery-staple", ...))`. Confirms the assertion actually inspects every element rather than only the ones already named. Revert the temporary line.

- [ ] **Step 4: Wire `PersistenceRegistration`**

  `LedgerDbContext.BackupRuns` already exists — Task 1 adds it (`plan-10-task-01.md:1296-1301`). This step touches only `PersistenceRegistration.cs`.

  In `src/Noof.Ledger.Persistence/PersistenceRegistration.cs`, add `using Noof.Ledger.Application.Backup;` and `using Noof.Ledger.Persistence.Backup;`, then inside `AddNoofPersistence`, after the `connectionString` local already resolved for the `DbContext` registration (change the inline `LedgerConnectionString.Resolve(...)` call into a local variable used by both registrations):

  ```csharp
  var connectionString = LedgerConnectionString.Resolve(configuration.GetConnectionString("Ledger"));

  services.AddDbContext<LedgerDbContext>(options => options.UseNpgsql(connectionString));

  // ... existing AddScoped lines unchanged ...

  services.AddScoped<IBackupLog, EfBackupLog>();
  services.AddScoped<IDatabaseDumper>(_ => new PgDumpDatabaseDumper(
      connectionString, configuration["Backup:PgDumpPath"] ?? PgDumpDatabaseDumper.DefaultPath));
  ```

  This is `Program.cs`-adjacent wiring (CLAUDE.md §5's DI-registration exemption from TDD applies to the two `AddScoped` lines themselves, not to the classes they register, which are already tested above).

- [ ] **Step 5: Add the new public types to `PublicSurfaceTests`**

  In `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`, in `Allowed["Noof.Ledger.Application"]`, append after `"ITranscriber", "ISpeechProvider", "CapturedVoice", "ITranscriptionStore", "IVoiceFileSource",`:
  ```csharp
  "BackupRunRecord", "BackupStatus", "DumpResult", "IBackupLog", "IDatabaseDumper",
  ```
  `Allowed["Noof.Ledger.Persistence"]` is unchanged — `EfBackupLog`, `PgDumpDatabaseDumper`, `BackupRun`, `BackupRunConfiguration`, `PgDumpArguments` are all `internal`.

  Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests --filter PublicSurfaceTests`
  Expected: FAIL first (the new types are public and not yet in the allowlist) — confirms the test's subject set is not empty for this change — then PASS once the allowlist edit above is in place.

- [ ] **Step 6: Write the failing test for `EfBackupLog`**

  `tests/Noof.Ledger.Persistence.Tests/EfBackupLogTests.cs`:

  ```csharp
  using AwesomeAssertions;
  using Microsoft.EntityFrameworkCore;
  using Noof.Ledger.Application.Backup;
  using Noof.Ledger.Persistence.Backup;

  namespace Noof.Ledger.Persistence.Tests;

  [Collection("postgres")]
  public class EfBackupLogTests(PostgresFixture fixture)
  {
      static readonly DateTimeOffset T0 = new(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);

      [Fact]
      public async Task A_run_that_never_happened_reports_no_success_and_no_failure()
      {
          await using var db = await fixture.CreateContextAsync();
          await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
          var log = new EfBackupLog(db);

          var status = await log.StatusAsync(TestContext.Current.CancellationToken);

          status.Should().Be(new BackupStatus(null, false, null));
      }

      [Fact]
      public async Task A_successful_run_is_the_last_success_and_not_a_failure()
      {
          await using var db = await fixture.CreateContextAsync();
          await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
          var log = new EfBackupLog(db);

          await log.RecordAsync(new BackupRunRecord(T0, T0.AddMinutes(1), true, "noof_ledger-20260924-030000.dump", 4096, null),
              TestContext.Current.CancellationToken);

          var status = await log.StatusAsync(TestContext.Current.CancellationToken);
          status.Should().Be(new BackupStatus(T0.AddMinutes(1), false, null));
      }

      [Fact]
      public async Task A_failure_after_an_earlier_success_keeps_the_success_but_reports_the_failure()
      {
          await using var db = await fixture.CreateContextAsync();
          await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
          var log = new EfBackupLog(db);

          await log.RecordAsync(new BackupRunRecord(T0, T0.AddMinutes(1), true, "noof_ledger-20260924-030000.dump", 4096, null),
              TestContext.Current.CancellationToken);
          await log.RecordAsync(new BackupRunRecord(T0.AddDays(1), T0.AddDays(1).AddSeconds(5), false, null, null, "pg_dump exited with code 1"),
              TestContext.Current.CancellationToken);

          var status = await log.StatusAsync(TestContext.Current.CancellationToken);
          status.LastSuccessAt.Should().Be(T0.AddMinutes(1));
          status.LastRunFailed.Should().BeTrue();
          status.LastError.Should().Be("pg_dump exited with code 1");
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests --filter EfBackupLogTests`
  Expected FAIL: `relation "backup_runs" does not exist` if Task 1's migration is missing the table, or a compile error if `EfBackupLog`/`BackupRun*` are missing — since Step 2 already created `EfBackupLog` and Task 1 already ships `BackupRun`/`BackupRunConfiguration`, the expected failure here is purely the migration/table check.

- [ ] **Step 7: Confirm and fix**

  If Step 6 fails on a missing table, stop and report the Task 1 contract mismatch (see "Consumes" above) rather than improvising a migration here — Task 12 must not add its own migration (the header's "only one task per wave adds a migration" rule, and Task 1 already owns `AddMoneyModel`). If it fails only because `db.Database.MigrateAsync` has nothing to apply against an out-of-date `noof_ledger_test_template`, that means Task 1 has landed in the branch but the template was not refreshed after its migration — re-run the "Updating the test template" command from the plan header, then re-run this test. Once the table and mapping agree, all three tests in Step 6 pass.

- [ ] **Step 8: Write the failing test for `PgDumpDatabaseDumper` against a real clone**

  `tests/Noof.Ledger.Persistence.Tests/PgDumpDatabaseDumperTests.cs`:

  ```csharp
  using System.Diagnostics;
  using AwesomeAssertions;
  using Noof.Ledger.Persistence.Backup;
  using Noof.Ledger.TestKit;
  using Npgsql;

  namespace Noof.Ledger.Persistence.Tests;

  [Collection("postgres")]
  public class PgDumpDatabaseDumperTests(PostgresFixture fixture) : IAsyncLifetime
  {
      string? tempDump;

      public ValueTask InitializeAsync() => ValueTask.CompletedTask;

      public ValueTask DisposeAsync()
      {
          if (tempDump is not null && File.Exists(tempDump))
              File.Delete(tempDump);
          return ValueTask.CompletedTask;
      }

      static string PgDumpPath => Environment.GetEnvironmentVariable("NOOF_TEST_PGDUMP") ?? PgDumpDatabaseDumper.DefaultPath;

      [Fact]
      public async Task Dumps_a_template_clone_to_a_non_empty_file_pg_restore_accepts()
      {
          if (!File.Exists(PgDumpPath))
              Assert.Skip($"pg_dump.exe not found at {PgDumpPath} - install PostgreSQL 18 or set Backup:PgDumpPath (test override: NOOF_TEST_PGDUMP).");

          await using var clone = await fixture.CreateDatabaseAsync();
          tempDump = Path.Combine(Path.GetTempPath(), $"noof-backup-test-{Guid.NewGuid():N}.dump");
          var dumper = new PgDumpDatabaseDumper(clone.ConnectionString, PgDumpPath);

          var result = await dumper.DumpAsync(tempDump, TestContext.Current.CancellationToken);

          result.Succeeded.Should().BeTrue(result.Error);
          File.Exists(tempDump).Should().BeTrue();
          new FileInfo(tempDump).Length.Should().BeGreaterThan(0);

          var pgRestore = PgDumpPath.Replace("pg_dump.exe", "pg_restore.exe", StringComparison.Ordinal);
          var list = Process.Start(new ProcessStartInfo(pgRestore, ["--list", tempDump])
          {
              RedirectStandardOutput = true,
              UseShellExecute = false,
          })!;
          var output = await list.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
          await list.WaitForExitAsync(TestContext.Current.CancellationToken);

          list.ExitCode.Should().Be(0, output);
          output.Should().Contain("wallets");
      }

      [Fact]
      public async Task A_bad_host_fails_without_the_password_anywhere_in_the_error()
      {
          if (!File.Exists(PgDumpPath))
              Assert.Skip($"pg_dump.exe not found at {PgDumpPath} - install PostgreSQL 18 or set Backup:PgDumpPath (test override: NOOF_TEST_PGDUMP).");

          var admin = new NpgsqlConnectionStringBuilder(DatabaseSettings.AdminConnectionString)
          {
              Host = "noof-ledger-unreachable-host.invalid",
              Password = "marker-password-never-appears",
          };
          tempDump = Path.Combine(Path.GetTempPath(), $"noof-backup-test-{Guid.NewGuid():N}.dump");
          var dumper = new PgDumpDatabaseDumper(admin.ConnectionString, PgDumpPath);

          var result = await dumper.DumpAsync(tempDump, TestContext.Current.CancellationToken);

          result.Succeeded.Should().BeFalse();
          result.Error.Should().NotBeNull();
          result.Error.Should().NotContain("marker-password-never-appears");
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests --filter PgDumpDatabaseDumperTests`
  Expected: both PASS immediately if `pg_dump.exe`/`pg_restore.exe` are present at the default path (they were confirmed present during Phase 4 research at `C:\Program Files\PostgreSQL\18\bin\`) — `PgDumpDatabaseDumper` was already fully written in Step 2, so this step is verification, not new production code; if either binary is absent the tests report Skipped rather than failing the run.

- [ ] **Step 9: Watch the "no password in error" guard fail**

  In `PgDumpDatabaseDumper.DumpAsync`, temporarily change `Scrub` to `static string Scrub(string text, string? password) => text;` (stop scrubbing), re-run `A_bad_host_fails_without_the_password_anywhere_in_the_error`. It must go red only if the underlying `pg_dump` error text happens to echo the password back (it usually does not for a DNS failure, since the connection never reaches password negotiation) — so also add a temporary line at the top of `DumpAsync` that forces `return new DumpResult(false, connection.Password);` to prove the assertion itself catches a leak when one exists, confirming the test is not vacuously green. Revert both temporary changes with `git checkout -- src/Noof.Ledger.Persistence/Backup/PgDumpDatabaseDumper.cs` so a forgotten line cannot survive into the commit, then re-run to confirm PASS.

- [ ] **Step 10: Full-suite run for the affected project**

  Take the suite lock, then:
  ```powershell
  mkdir "$env:TEMP\noof-suite.lock" 2>$null
  dotnet test --project tests/Noof.Ledger.Persistence.Tests
  rmdir "$env:TEMP\noof-suite.lock"
  ```
  Expected: all tests in the project pass (the new ones plus everything Task 1/3's schema changes may have touched). If anything outside this task's files fails, stop and report rather than editing another task's files.

- [ ] **Step 11: Commit**

  ```powershell
  git add src/Noof.Ledger.Application/Backup/BackupContract.cs src/Noof.Ledger.Application/Backup/IBackupLog.cs src/Noof.Ledger.Application/Backup/IDatabaseDumper.cs src/Noof.Ledger.Persistence/Backup/EfBackupLog.cs src/Noof.Ledger.Persistence/Backup/PgDumpDatabaseDumper.cs src/Noof.Ledger.Persistence/PersistenceRegistration.cs tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs tests/Noof.Ledger.Persistence.Tests/PgDumpArgumentsTests.cs tests/Noof.Ledger.Persistence.Tests/EfBackupLogTests.cs tests/Noof.Ledger.Persistence.Tests/PgDumpDatabaseDumperTests.cs
  git commit -m "$(cat <<'EOF'
  Add the backup core: EF backup log and a pg_dump-based dumper (B1-B4)

  IBackupLog/BackupRunRecord/BackupStatus and IDatabaseDumper/DumpResult give the phase's backup
  track a place to record every run and a way to make one, with the password confined to
  ProcessStartInfo.Environment and never an argument, log line or recorded error.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

---

### Task 6: Write path: entries, checkpoints, revisions

**Files:**
- Create: `src/Noof.Ledger.Persistence/Balances/LedgerPostings.cs`
- Modify: `src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs` (`ApplyAsync`, the block from `transaction.OccurredOn = outcome.OccurredOn;` to `await RevisionLog.AppendAsync(`, lines 99–102 at `0852521`; plus one `using`)
- Modify: `src/Noof.Ledger.Persistence/Revisions/RevisionLog.cs` (`SnapshotAsync` and the `Snapshot` record, as Task 1 left them with `kind` and `wallet_id`)
- Test: `tests/Noof.Ledger.Persistence.Tests/LedgerWritePathTests.cs` (new), `tests/Noof.Ledger.Persistence.Tests/TransactionRevisionTests.cs`

**Interfaces:**
- Consumes:
  - Task 1:
    - `TransactionKind`, `EntryRole.Principal`, `Entry` and `BalanceCheck`.
    - `LedgerDbContext.Entries` and `LedgerDbContext.BalanceChecks`.
    - `Transaction.Kind` (settable) and `Transaction.WalletId` (`Guid?`, settable).
    - The tables `entries (id, transaction_id, wallet_id, amount numeric(19,4), currency varchar(3), role int)` and `balance_checks (transaction_id PK, wallet_id, stated_amount numeric(19,4), currency varchar(3), computed_before numeric(19,4))`.
    - The seeded Main Wallet `00000000-0000-0000-0000-000000000001` (RSD) and the seeded categories `groceries` (`00000000-0000-0000-0001-000000000001`) and `coffee` (`00000000-0000-0000-0001-000000000017`).
  - Task 3:
    - `internal static class BalanceSql` in `Noof.Ledger.Persistence.Balances`: `public static Task<decimal> AsOfAsync(LedgerDbContext db, Guid walletId, CurrencyCode currency, DateOnly occurredOn, DateTimeOffset occurredAt, Guid excludingTransactionId, CancellationToken ct)`.
    - The view `wallet_balances(wallet_id, currency, balance, checked_on)`. It counts only `status = 1`.
  - Task 4: `CategorizationOutcome(..., TransactionKind TransactionKind = TransactionKind.Expense, Guid? WalletId = null, Money? StatedBalance = null)`. The worker always sets all three.
- Produces:
  - `EfCategorizationStore.ApplyAsync` writes the record's kind and wallet, and its postings, in the same database transaction as its lines and its revision. Postings are the entries of an expense or an income, or the checkpoint of a balance statement.
  - `internal static class LedgerPostings` (`Noof.Ledger.Persistence.Balances`): `public static Task RewriteAsync(LedgerDbContext db, Transaction transaction, Money? stated, CancellationToken cancellationToken)`.
  - The revision snapshot JSON gains `"stated_balance"` (`{"amount": "<decimal string>", "currency": "<CUR>"}` or null). Task 1 already added `"kind"` (the `TransactionKind` member name) and `"wallet_id"`. Every existing key stays. Task 8's echo and Task 9's dashboard read balances from the view these rows feed.

**The rules this task implements** (spec M5, M6, M7, M10, M13):

- **Expense:** one `Principal` entry per currency, `-SUM(lines of that currency)`, in the record's wallet. **Income:** the same with `+SUM`. An entry keeps its line's currency even when it differs from the wallet's (M10: nothing is converted).
- **BalanceCheck:** no entries. Its lines are the model-authored ones the existing `DELETE` removes, and the mapper sends none. One `balance_checks` row is upserted with:
  - `stated_amount` and `currency` = the outcome's `StatedBalance`;
  - `wallet_id` = the record's wallet;
  - `computed_before = BalanceSql.AsOfAsync(db, walletId, stated currency, occurredOn, occurredAt, excluding this transaction)`: what the app would have said just before the statement, by M6's `(occurred_on, occurred_at)` order, from completed records only.
- Entries are **replaced whole** on every write (delete, then insert from the lines as they now stand). So:
  - a correction that changes the amount rewrites them;
  - a correction that changes the wallet moves them;
  - a correction that turns an expense into a statement removes them;
  - a correction that turns a statement back into an expense or income deletes its `balance_checks` row.
- **Cancel and Restore are not touched.** `EfRecordEditor.CancelAsync` (line 93) and `RestoreAsync` (line 108) only flip `Status` (lines 101 and 121) and append a revision; neither touches lines, entries or checkpoints. That is correct as it stands. `wallet_balances` and `BalanceSql` count only `status = 1` (Completed), so a cancelled record's postings drop out of every balance and come back on Restore, with nothing rewritten. The last test in Step 1 proves it through the view.
- **A correction to a cancelled record** keeps it cancelled (existing rule). Its postings are rewritten all the same and stay out of balances until Restore.
- Nothing here stores a balance (Global Constraints). `computed_before` is history for the echo, never read back as a current balance.

- [ ] **Step 1: Write the failing write-path tests**

Each test gets its own real PostgreSQL database: `PostgresFixture.CreateContextAsync()` then `MigrateAsync`, the pattern every EF-context test here uses. So `wallet_balances` is the view Task 3's migration created, not a stand-in. Timestamps are fixed literals.

Create `tests/Noof.Ledger.Persistence.Tests/LedgerWritePathTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence.Categorization;
using Noof.Ledger.Persistence.Editing;

namespace Noof.Ledger.Persistence.Tests;

[Collection("postgres")]
public class LedgerWritePathTests(PostgresFixture fixture)
{
    static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
    static readonly Guid MainWalletId = new("00000000-0000-0000-0000-000000000001");
    static readonly Guid GroceriesId = new("00000000-0000-0000-0001-000000000001");
    static readonly Guid CoffeeId = new("00000000-0000-0000-0001-000000000017");
    static int nextMessageId;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    async Task<LedgerDbContext> LedgerAsync()
    {
        var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(Ct);
        return db;
    }

    static LedgerDbContext Context(string connectionString, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).AddInterceptors(interceptors).Options);

    static async Task<Guid> AddWalletAsync(LedgerDbContext db, string name, CurrencyCode currency)
    {
        var wallet = new Wallet { Id = Guid.NewGuid(), Name = name, Currency = currency, CreatedAt = Clock.GetUtcNow() };
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync(Ct);
        return wallet.Id;
    }

    // A text capture as EfCaptureStore leaves it: no wallet yet, sent at 10:00 UTC on the given day.
    static async Task<Guid> CaptureAsync(LedgerDbContext db, DateOnly sentOn)
    {
        var sentAt = new DateTimeOffset(sentOn.Year, sentOn.Month, sentOn.Day, 10, 0, 0, TimeSpan.Zero);
        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            RawText = "test capture",
            Status = TransactionStatus.Captured,
            TimeZoneId = "Europe/Belgrade",
            OccurredAt = sentAt,
            OccurredOn = sentOn,
            TelegramChatId = 1,
            TelegramMessageId = Interlocked.Increment(ref nextMessageId),
            CreatedAt = sentAt,
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(Ct);
        return transaction.Id;
    }

    static CategorizedLineItem Line(decimal amount, CurrencyCode currency, Guid? categoryId = null) =>
        new("line", new Money(amount, currency), categoryId ?? GroceriesId, null);

    static CategorizationOutcome Expense(DateOnly day, Guid walletId, params CategorizedLineItem[] lines) =>
        new(lines, day, TransactionKind: TransactionKind.Expense, WalletId: walletId);

    static CategorizationOutcome Income(DateOnly day, Guid walletId, params CategorizedLineItem[] lines) =>
        new(lines, day, TransactionKind: TransactionKind.Income, WalletId: walletId);

    static CategorizationOutcome Statement(DateOnly day, Guid walletId, decimal amount, CurrencyCode currency) =>
        new([], day, TransactionKind: TransactionKind.BalanceCheck, WalletId: walletId, StatedBalance: new Money(amount, currency));

    static CategorizationOutcome AsCorrection(CategorizationOutcome outcome) =>
        outcome with { Kind = JobKind.Correct, Instruction = "correction" };

    static Task ApplyAsync(LedgerDbContext db, Guid transactionId, CategorizationOutcome outcome) =>
        new EfCategorizationStore(db, Clock).ApplyAsync(transactionId, outcome, Ct);

    static async Task<IReadOnlyList<(Guid WalletId, Money Amount, EntryRole Role)>> EntriesOfAsync(LedgerDbContext db, Guid transactionId)
    {
        var entries = await db.Entries.AsNoTracking().Where(entry => entry.TransactionId == transactionId).ToListAsync(Ct);
        return [.. entries.OrderBy(entry => entry.Amount.Currency.Value).Select(entry => (entry.WalletId, entry.Amount, entry.Role))];
    }

    static Task<BalanceCheck> CheckpointOfAsync(LedgerDbContext db, Guid transactionId) =>
        db.BalanceChecks.AsNoTracking().SingleAsync(check => check.TransactionId == transactionId, Ct);

    // Read through the view the echo and the dashboard read, never recomputed here. Null: the wallet has no
    // balance row in that currency at all.
    static async Task<decimal?> BalanceAsync(LedgerDbContext db, Guid walletId, CurrencyCode currency)
    {
        var rows = await db.Database.SqlQuery<decimal>(
            $"""SELECT balance AS "Value" FROM wallet_balances WHERE wallet_id = {walletId} AND currency = {currency.Value}""")
            .ToListAsync(Ct);
        return rows.Count == 0 ? null : rows.Single();
    }

    [Fact]
    public async Task An_expense_posts_one_negative_entry_per_currency_to_its_wallet()
    {
        await using var db = await LedgerAsync();
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);

        await ApplyAsync(db, id, Expense(day, MainWalletId,
            Line(250m, CurrencyCode.Rsd, CoffeeId), Line(1000m, CurrencyCode.Rsd), Line(3.50m, CurrencyCode.Eur, CoffeeId)));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, id)).Should().Equal(
            (MainWalletId, new Money(-3.50m, CurrencyCode.Eur), EntryRole.Principal),
            (MainWalletId, new Money(-1250m, CurrencyCode.Rsd), EntryRole.Principal));
        var stored = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id, Ct);
        stored.Kind.Should().Be(TransactionKind.Expense);
        stored.WalletId.Should().Be(MainWalletId, "the capture had no wallet; the outcome chose one");
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(-1250m);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Eur)).Should().Be(-3.50m, "an entry keeps its line's currency (M10)");
    }

    [Fact]
    public async Task An_income_posts_positive_entries()
    {
        await using var db = await LedgerAsync();
        var wise = await AddWalletAsync(db, "Wise EUR", CurrencyCode.Eur);
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);

        await ApplyAsync(db, id, Income(day, wise, Line(2000m, CurrencyCode.Eur), Line(150.25m, CurrencyCode.Eur)));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, id)).Should().Equal((wise, new Money(2150.25m, CurrencyCode.Eur), EntryRole.Principal));
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id, Ct)).Kind.Should().Be(TransactionKind.Income);
        (await BalanceAsync(db, wise, CurrencyCode.Eur)).Should().Be(2150.25m);
    }

    [Fact]
    public async Task A_balance_statement_is_a_checkpoint_holding_what_the_app_computed_just_before_it()
    {
        await using var db = await LedgerAsync();

        var opening = await CaptureAsync(db, new DateOnly(2026, 9, 10));
        await ApplyAsync(db, opening, Statement(new DateOnly(2026, 9, 10), MainWalletId, 10000m, CurrencyCode.Rsd));

        var bread = await CaptureAsync(db, new DateOnly(2026, 9, 12));
        await ApplyAsync(db, bread, Expense(new DateOnly(2026, 9, 12), MainWalletId, Line(1500m, CurrencyCode.Rsd)));
        var coffee = await CaptureAsync(db, new DateOnly(2026, 9, 14));
        await ApplyAsync(db, coffee, Expense(new DateOnly(2026, 9, 14), MainWalletId, Line(500m, CurrencyCode.Rsd, CoffeeId)));
        var cancelled = await CaptureAsync(db, new DateOnly(2026, 9, 15));
        await ApplyAsync(db, cancelled, Expense(new DateOnly(2026, 9, 15), MainWalletId, Line(700m, CurrencyCode.Rsd)));
        await new EfRecordEditor(db, Clock).CancelAsync(cancelled, Ct);
        var refund = await CaptureAsync(db, new DateOnly(2026, 9, 18));
        await ApplyAsync(db, refund, Income(new DateOnly(2026, 9, 18), MainWalletId, Line(300m, CurrencyCode.Rsd)));
        var later = await CaptureAsync(db, new DateOnly(2026, 9, 25));
        await ApplyAsync(db, later, Expense(new DateOnly(2026, 9, 25), MainWalletId, Line(999m, CurrencyCode.Rsd)));

        // Applied after a purchase dated later than it: processing order never changes a balance (M6).
        var statement = await CaptureAsync(db, new DateOnly(2026, 9, 21));
        await ApplyAsync(db, statement, Statement(new DateOnly(2026, 9, 21), MainWalletId, 9000m, CurrencyCode.Rsd));

        // Sent the day after the statement about the day before it: "вчера купил..." still falls before it.
        var yesterday = await CaptureAsync(db, new DateOnly(2026, 9, 22));
        await ApplyAsync(db, yesterday, Expense(new DateOnly(2026, 9, 20), MainWalletId, Line(4321m, CurrencyCode.Rsd)));

        db.ChangeTracker.Clear();
        var first = await CheckpointOfAsync(db, opening);
        first.Stated.Should().Be(new Money(10000m, CurrencyCode.Rsd));
        first.ComputedBefore.Should().Be(0m, "nothing came before the first statement");

        var checkpoint = await CheckpointOfAsync(db, statement);
        checkpoint.WalletId.Should().Be(MainWalletId);
        checkpoint.Stated.Should().Be(new Money(9000m, CurrencyCode.Rsd));
        checkpoint.ComputedBefore.Should().Be(8300m,
            "10000 stated, then -1500 and -500 and +300; the cancelled 700 and the later 999 do not count");

        (await EntriesOfAsync(db, statement)).Should().BeEmpty("a statement is a checkpoint, not an adjustment entry");
        (await db.LineItems.CountAsync(line => line.TransactionId == statement, Ct)).Should().Be(0);
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == statement, Ct)).Kind.Should().Be(TransactionKind.BalanceCheck);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(8001m,
            "the latest checkpoint's 9000 plus only what came after it: -999; the 4321 dated before it is absorbed");
    }

    [Fact]
    public async Task A_correction_that_moves_a_statements_day_recomputes_what_came_before_it()
    {
        await using var db = await LedgerAsync();
        var bread = await CaptureAsync(db, new DateOnly(2026, 9, 12));
        await ApplyAsync(db, bread, Expense(new DateOnly(2026, 9, 12), MainWalletId, Line(1500m, CurrencyCode.Rsd)));
        var later = await CaptureAsync(db, new DateOnly(2026, 9, 25));
        await ApplyAsync(db, later, Expense(new DateOnly(2026, 9, 25), MainWalletId, Line(999m, CurrencyCode.Rsd)));
        var statement = await CaptureAsync(db, new DateOnly(2026, 9, 21));
        await ApplyAsync(db, statement, Statement(new DateOnly(2026, 9, 21), MainWalletId, 9000m, CurrencyCode.Rsd));

        db.ChangeTracker.Clear();
        (await CheckpointOfAsync(db, statement)).ComputedBefore.Should().Be(-1500m);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(8001m);

        // "это было вчера" a few days later: only the day changes, the wallet and the amount stay.
        await ApplyAsync(db, statement, AsCorrection(Statement(new DateOnly(2026, 9, 26), MainWalletId, 9000m, CurrencyCode.Rsd)));

        db.ChangeTracker.Clear();
        (await CheckpointOfAsync(db, statement)).ComputedBefore.Should().Be(-2499m,
            "the 999 on Sept 25 now falls before the statement, so what the app computed just before it changes");
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(9000m, "nothing is dated after Sept 26");
    }

    [Fact]
    public async Task A_correction_from_an_expense_to_a_balance_statement_and_back_swaps_entries_for_a_checkpoint()
    {
        await using var db = await LedgerAsync();
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);

        await ApplyAsync(db, id, Expense(day, MainWalletId, Line(250m, CurrencyCode.Rsd)));
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(-250m);

        await ApplyAsync(db, id, AsCorrection(Statement(day, MainWalletId, 45000m, CurrencyCode.Rsd)));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, id)).Should().BeEmpty();
        (await db.LineItems.CountAsync(line => line.TransactionId == id, Ct)).Should().Be(0);
        var checkpoint = await CheckpointOfAsync(db, id);
        checkpoint.Stated.Should().Be(new Money(45000m, CurrencyCode.Rsd));
        checkpoint.ComputedBefore.Should().Be(0m, "the record's own former expense is excluded from what came before it");
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(45000m);

        await ApplyAsync(db, id, AsCorrection(Expense(day, MainWalletId, Line(300m, CurrencyCode.Rsd))));

        db.ChangeTracker.Clear();
        (await db.BalanceChecks.CountAsync(check => check.TransactionId == id, Ct)).Should().Be(0,
            "a record that is no longer a statement keeps no checkpoint");
        (await EntriesOfAsync(db, id)).Should().Equal((MainWalletId, new Money(-300m, CurrencyCode.Rsd), EntryRole.Principal));
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id, Ct)).Kind.Should().Be(TransactionKind.Expense);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(-300m);
    }

    [Fact]
    public async Task A_correction_that_changes_the_wallet_moves_the_entries()
    {
        await using var db = await LedgerAsync();
        var wise = await AddWalletAsync(db, "Wise EUR", CurrencyCode.Eur);
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);

        await ApplyAsync(db, id, Expense(day, MainWalletId, Line(3.50m, CurrencyCode.Eur, CoffeeId)));
        await ApplyAsync(db, id, AsCorrection(Expense(day, wise, Line(3.50m, CurrencyCode.Eur, CoffeeId))));

        db.ChangeTracker.Clear();
        (await EntriesOfAsync(db, id)).Should().Equal((wise, new Money(-3.50m, CurrencyCode.Eur), EntryRole.Principal));
        (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == id, Ct)).WalletId.Should().Be(wise);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Eur)).Should().BeNull("nothing is left in the wallet it moved out of");
        (await BalanceAsync(db, wise, CurrencyCode.Eur)).Should().Be(-3.50m);
    }

    [Fact]
    public async Task A_balance_statement_moved_to_another_wallet_moves_its_checkpoint()
    {
        await using var db = await LedgerAsync();
        var cash = await AddWalletAsync(db, "Cash", CurrencyCode.Rsd);
        var day = new DateOnly(2026, 9, 10);
        var id = await CaptureAsync(db, day);

        await ApplyAsync(db, id, Statement(day, MainWalletId, 45000m, CurrencyCode.Rsd));
        await ApplyAsync(db, id, AsCorrection(Statement(day, cash, 45000m, CurrencyCode.Rsd)));

        db.ChangeTracker.Clear();
        (await CheckpointOfAsync(db, id)).WalletId.Should().Be(cash);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().BeNull();
        (await BalanceAsync(db, cash, CurrencyCode.Rsd)).Should().Be(45000m);
    }

    [Fact]
    public async Task Cancel_and_restore_keep_the_postings_and_the_balance_follows_the_status()
    {
        await using var db = await LedgerAsync();
        var editor = new EfRecordEditor(db, Clock);
        var statement = await CaptureAsync(db, new DateOnly(2026, 9, 10));
        await ApplyAsync(db, statement, Statement(new DateOnly(2026, 9, 10), MainWalletId, 1000m, CurrencyCode.Rsd));
        var coffee = await CaptureAsync(db, new DateOnly(2026, 9, 12));
        await ApplyAsync(db, coffee, Expense(new DateOnly(2026, 9, 12), MainWalletId, Line(250m, CurrencyCode.Rsd, CoffeeId)));
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(750m);

        await editor.CancelAsync(coffee, Ct);
        (await EntriesOfAsync(db, coffee)).Should().ContainSingle("Cancel changes the status, not the postings");
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(1000m);

        await editor.RestoreAsync(coffee, Ct);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(750m);

        await editor.CancelAsync(statement, Ct);
        (await db.BalanceChecks.CountAsync(check => check.TransactionId == statement, Ct)).Should().Be(1);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(-250m,
            "with its only checkpoint cancelled, the wallet is the sum of its entries");

        await editor.RestoreAsync(statement, Ct);
        (await BalanceAsync(db, MainWalletId, CurrencyCode.Rsd)).Should().Be(750m);
    }

    [Fact]
    public async Task Entries_always_agree_with_the_lines_they_are_summed_from()
    {
        await using var db = await LedgerAsync();
        var wise = await AddWalletAsync(db, "Wise EUR", CurrencyCode.Eur);
        var day = new DateOnly(2026, 9, 10);
        var mixed = await CaptureAsync(db, day);
        await ApplyAsync(db, mixed, Expense(day, MainWalletId, Line(250m, CurrencyCode.Rsd), Line(3.50m, CurrencyCode.Eur)));
        var salary = await CaptureAsync(db, day);
        await ApplyAsync(db, salary, Income(day, wise, Line(2000m, CurrencyCode.Eur)));
        var statement = await CaptureAsync(db, day);
        await ApplyAsync(db, statement, Statement(day, MainWalletId, 45000m, CurrencyCode.Rsd));
        var refund = await CaptureAsync(db, day);
        await ApplyAsync(db, refund, Expense(day, MainWalletId, Line(100m, CurrencyCode.Rsd)));
        await ApplyAsync(db, refund, AsCorrection(Income(day, wise, Line(100m, CurrencyCode.Eur))));

        const string disagreeing = """
            SELECT count(*)::int AS "Value"
            FROM (
                SELECT t.id AS transaction_id, li.currency,
                       CASE t.kind WHEN 1 THEN 1 ELSE -1 END * SUM(li.amount) AS expected
                FROM transactions t
                JOIN line_items li ON li.transaction_id = t.id
                WHERE t.kind IN (0, 1)
                GROUP BY t.id, t.kind, li.currency
            ) lines
            FULL JOIN (
                SELECT transaction_id, currency, SUM(amount) AS actual
                FROM entries
                GROUP BY transaction_id, currency
            ) posted ON posted.transaction_id = lines.transaction_id AND posted.currency = lines.currency
            WHERE lines.expected IS DISTINCT FROM posted.actual
            """;
        const string misplaced = """
            SELECT count(*)::int AS "Value"
            FROM entries e
            JOIN transactions t ON t.id = e.transaction_id
            WHERE e.wallet_id IS DISTINCT FROM t.wallet_id
            """;

        (await db.Entries.CountAsync(Ct)).Should().BeGreaterThan(0, "a rule over no entries would prove nothing");
        (await db.Database.SqlQueryRaw<int>(disagreeing).ToListAsync(Ct)).Single()
            .Should().Be(0, "every expense and income has exactly minus/plus its lines per currency, and a statement has none (M5)");
        (await db.Database.SqlQueryRaw<int>(misplaced).ToListAsync(Ct)).Single()
            .Should().Be(0, "an entry is always in its record's wallet");
    }

    [Fact]
    public async Task Nothing_is_posted_when_the_write_fails_before_commit()
    {
        var connectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();
        var day = new DateOnly(2026, 9, 10);
        Guid expenseId, statementId;

        await using (var seed = Context(connectionString))
        {
            await seed.Database.MigrateAsync(Ct);
            expenseId = await CaptureAsync(seed, day);
            statementId = await CaptureAsync(seed, day);
        }

        await using (var breaking = Context(connectionString, new ThrowsBeforeCommitInterceptor()))
        {
            var expense = async () => await ApplyAsync(breaking, expenseId, Expense(day, MainWalletId, Line(250m, CurrencyCode.Rsd)));
            await expense.Should().ThrowAsync<InvalidOperationException>();
        }

        await using (var breaking = Context(connectionString, new ThrowsBeforeCommitInterceptor()))
        {
            var statement = async () => await ApplyAsync(breaking, statementId, Statement(day, MainWalletId, 45000m, CurrencyCode.Rsd));
            await statement.Should().ThrowAsync<InvalidOperationException>();
        }

        await using var verify = Context(connectionString);
        (await verify.Entries.CountAsync(Ct)).Should().Be(0, "the entries were written inside the rolled-back transaction");
        (await verify.BalanceChecks.CountAsync(Ct)).Should().Be(0, "so was the checkpoint");
        (await BalanceAsync(verify, MainWalletId, CurrencyCode.Rsd)).Should().BeNull();
    }
}
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter LedgerWritePathTests`
Expected: FAIL.
- `An_expense_posts_one_negative_entry_per_currency_to_its_wallet` fails because the entries collection is empty: `ApplyAsync` writes none yet.
- `A_balance_statement_is_a_checkpoint_...` and `A_correction_that_moves_a_statements_day_...` fail with `InvalidOperationException: Sequence contains no elements` from `CheckpointOfAsync`.
- `Cancel_and_restore_...` fails with `Expected ... to be 750M, but found <null>`.
- `Nothing_is_posted_when_the_write_fails_before_commit` already passes: nothing is posted at all yet. Step 4 sees it red.

- [ ] **Step 2: Implement the postings**

Create `src/Noof.Ledger.Persistence/Balances/LedgerPostings.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Noof.Ledger.Domain;
using Npgsql;

namespace Noof.Ledger.Persistence.Balances;

// What a record does to its wallet's balance, replaced whole every time the record is written: an expense's or
// an income's signed entries, or a balance statement's checkpoint (M5, M6). Called inside the caller's database
// transaction, after the record and its lines are saved and while the caller holds the record's row lock, so the
// entries can never disagree with the lines they are summed from. Nothing here stores a balance.
internal static class LedgerPostings
{
    // The rule the AddMoneyModel backfill applied to every expense that existed before entries did: one entry per
    // currency, the sum of the lines, negative for an expense and positive for an income. A record with lines and
    // no wallet cannot be posted; entries.wallet_id NOT NULL refuses it and the whole write rolls back.
    const string InsertEntriesSql = """
        INSERT INTO entries (id, transaction_id, wallet_id, amount, currency, role)
        SELECT gen_random_uuid(), t.id, t.wallet_id, @sign * SUM(li.amount), li.currency, @role
        FROM transactions t
        JOIN line_items li ON li.transaction_id = t.id
        WHERE t.id = @transactionId
        GROUP BY t.id, t.wallet_id, li.currency
        """;

    // SQL rather than a tracked entity, like the deletes: the row is replaced whole, and a BalanceCheck still
    // tracked from an earlier write in the same context would collide with a new instance under the same key.
    const string UpsertCheckpointSql = """
        INSERT INTO balance_checks (transaction_id, wallet_id, stated_amount, currency, computed_before)
        VALUES (@transactionId, @walletId, @statedAmount, @currency, @computedBefore)
        ON CONFLICT (transaction_id) DO UPDATE
        SET wallet_id = EXCLUDED.wallet_id,
            stated_amount = EXCLUDED.stated_amount,
            currency = EXCLUDED.currency,
            computed_before = EXCLUDED.computed_before
        """;

    public static async Task RewriteAsync(
        LedgerDbContext db, Transaction transaction, Money? stated, CancellationToken cancellationToken)
    {
        await DeleteAsync(db, "DELETE FROM entries WHERE transaction_id = @transactionId", transaction.Id, cancellationToken);

        if (transaction.Kind != TransactionKind.BalanceCheck)
        {
            await DeleteAsync(db, "DELETE FROM balance_checks WHERE transaction_id = @transactionId", transaction.Id, cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                InsertEntriesSql,
                [
                    new NpgsqlParameter("transactionId", transaction.Id),
                    new NpgsqlParameter("sign", transaction.Kind == TransactionKind.Income ? 1 : -1),
                    new NpgsqlParameter("role", (int)EntryRole.Principal),
                ],
                cancellationToken);
            return;
        }

        // Unreachable while ProposalMapper gives every statement both. If it ever fires, CategorizationWorker treats an
        // unmodelled exception as transient and retries it up to MaxAttempts before failing the job - it is not a
        // terminal mapping failure, and the retries cannot succeed.
        if (stated is not { } statement || transaction.WalletId is not { } walletId)
            throw new InvalidOperationException(
                $"Balance statement {transaction.Id} needs a wallet and a stated amount, and ProposalMapper always gives both.");

        var computedBefore = await BalanceSql.AsOfAsync(
            db, walletId, statement.Currency, transaction.OccurredOn, transaction.OccurredAt, transaction.Id, cancellationToken);

        await db.Database.ExecuteSqlRawAsync(
            UpsertCheckpointSql,
            [
                new NpgsqlParameter("transactionId", transaction.Id),
                new NpgsqlParameter("walletId", walletId),
                new NpgsqlParameter("statedAmount", statement.Amount),
                new NpgsqlParameter("currency", statement.Currency.Value),
                new NpgsqlParameter("computedBefore", computedBefore),
            ],
            cancellationToken);
    }

    static Task<int> DeleteAsync(LedgerDbContext db, string sql, Guid transactionId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(sql, [new NpgsqlParameter("transactionId", transactionId)], cancellationToken);
}
```

In `src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs` add `using Noof.Ledger.Persistence.Balances;` after `using Noof.Ledger.Domain;`, and in `ApplyAsync` replace

```csharp
        transaction.OccurredOn = outcome.OccurredOn;

        await db.SaveChangesAsync(cancellationToken);
        await RevisionLog.AppendAsync(db, transaction, RevisionKindFor(outcome.Kind), outcome.Instruction,
```

with

```csharp
        transaction.OccurredOn = outcome.OccurredOn;
        transaction.Kind = outcome.TransactionKind;
        transaction.WalletId = outcome.WalletId ?? transaction.WalletId;

        await db.SaveChangesAsync(cancellationToken);
        await LedgerPostings.RewriteAsync(db, transaction, outcome.StatedBalance, cancellationToken);
        await RevisionLog.AppendAsync(db, transaction, RevisionKindFor(outcome.Kind), outcome.Instruction,
```

The order matters. The record and its lines are saved first, because the entries are summed from the rows. The postings come before the revision, because the snapshot reads the checkpoint back. All of it stays inside the transaction `ApplyAsync` opened and commits at the end. `outcome.Kind` is still the `JobKind` that picks the revision kind. `outcome.TransactionKind` is what the record is.

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter LedgerWritePathTests`
Expected: PASS (all ten).

- [ ] **Step 3: Watch the sign and the checkpoint rule fail once**

1. In `RewriteAsync` swap the sign to `transaction.Kind == TransactionKind.Income ? -1 : 1` and run the Step 2 command. Expected: FAIL in `An_expense_posts_one_negative_entry_per_currency_to_its_wallet` (it finds `+1250`), `An_income_posts_positive_entries` and `Entries_always_agree_with_the_lines_they_are_summed_from` (`Expected ... to be 0 ..., but found 4`: every expense/income currency group disagrees). Revert.
2. Replace the `BalanceSql.AsOfAsync(...)` call with `0m` and run again. Expected: FAIL in `A_balance_statement_is_a_checkpoint_holding_what_the_app_computed_just_before_it` (`Expected checkpoint.ComputedBefore to be 8300M, but found 0M`). Revert.
3. In `UpsertCheckpointSql` replace `computed_before = EXCLUDED.computed_before` with `computed_before = balance_checks.computed_before` and run again. Expected: FAIL in `A_correction_that_moves_a_statements_day_recomputes_what_came_before_it` only: `Expected ... ComputedBefore to be -2499M because the 999 on Sept 25 now falls before the statement ..., but found -1500M`. A day-only correction must re-anchor the checkpoint. Revert.
4. Delete the `DeleteAsync(db, "DELETE FROM balance_checks ...")` line and run again. Expected: FAIL in `A_correction_from_an_expense_to_a_balance_statement_and_back_swaps_entries_for_a_checkpoint`: `Expected ... to be 0 because a record that is no longer a statement keeps no checkpoint, but found 1`. Left in place, that stale checkpoint would pin the wallet at 45000 whatever was spent. Revert and run again: PASS.

- [ ] **Step 4: Watch the rollback test see a posting once**

In `Nothing_is_posted_when_the_write_fails_before_commit`, change the first breaking context to `Context(connectionString)`, without the interceptor. Change its assertion line to `await expense();`. Run `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter Nothing_is_posted_when_the_write_fails_before_commit`.
Expected: FAIL at `verify.Entries.CountAsync`: `Expected ... to be 0 because the entries were written inside the rolled-back transaction, but found 1`. This proves the check reads the table the write fills. Revert both lines and run again: PASS.

- [ ] **Step 5: Write the failing revision-snapshot test**

M13: *"Revision snapshots gain `kind`, `wallet_id` and the stated balance."* Task 1 added `kind` and `wallet_id`. This step adds the stated balance, and the test pins all three against records this task's write path produces. In `tests/Noof.Ledger.Persistence.Tests/TransactionRevisionTests.cs`, add after `A_reinterpretation_is_recorded_as_an_edit`:

```csharp
    [Fact]
    public async Task A_revision_records_the_kind_the_wallet_and_the_stated_balance()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var transactionId = await SeedTransactionAsync(db);
        var store = new EfCategorizationStore(db, Clock);

        await store.ApplyAsync(transactionId,
            new CategorizationOutcome([Coffee(250m)], new DateOnly(2026, 9, 21), WalletId: DefaultWalletId),
            TestContext.Current.CancellationToken);
        await store.ApplyAsync(transactionId,
            new CategorizationOutcome([], new DateOnly(2026, 9, 21), JobKind.Correct, "на самом деле на главном 45 тысяч",
                TransactionKind.BalanceCheck, DefaultWalletId, new Money(45000m, CurrencyCode.Rsd)),
            TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var revisions = await db.TransactionRevisions.OrderBy(r => r.RevisionNumber).ToListAsync(TestContext.Current.CancellationToken);

        using var expense = JsonDocument.Parse(revisions[0].Snapshot);
        expense.RootElement.GetProperty("kind").GetString().Should().Be("Expense");
        expense.RootElement.GetProperty("wallet_id").GetGuid().Should().Be(DefaultWalletId);
        expense.RootElement.GetProperty("stated_balance").ValueKind.Should().Be(JsonValueKind.Null);

        using var statement = JsonDocument.Parse(revisions[1].Snapshot);
        statement.RootElement.GetProperty("kind").GetString().Should().Be("BalanceCheck");
        statement.RootElement.GetProperty("wallet_id").GetGuid().Should().Be(DefaultWalletId);
        statement.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        statement.RootElement.GetProperty("raw_text").GetString().Should().Be("кофе 250", "every existing key stays");
        var stated = statement.RootElement.GetProperty("stated_balance");
        stated.GetProperty("amount").ValueKind.Should().Be(JsonValueKind.String, "an amount is never a JSON number");
        decimal.Parse(stated.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture).Should().Be(45000m);
        stated.GetProperty("currency").GetString().Should().Be("RSD");
    }
```

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter TransactionRevisionTests`
Expected: FAIL in `A_revision_records_the_kind_the_wallet_and_the_stated_balance` with `KeyNotFoundException` at `GetProperty("stated_balance")`. `kind` and `wallet_id` are already there from Task 1. Every existing test in the class passes.

- [ ] **Step 6: Add the stated balance to the snapshot**

In `src/Noof.Ledger.Persistence/Revisions/RevisionLog.cs`, in `SnapshotAsync`, add after the `lines` query:

```csharp
        var checkpoint = await db.BalanceChecks.AsNoTracking()
            .Where(check => check.TransactionId == transaction.Id)
            .Select(check => new { check.Stated.Amount, check.Stated.Currency })
            .SingleOrDefaultAsync(cancellationToken);
```

and replace the `return JsonSerializer.Serialize(new Snapshot(...));` statement with:

```csharp
        return JsonSerializer.Serialize(new Snapshot(
            transaction.RawText,
            transaction.OccurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [.. lines.Select(line => new SnapshotLine(
                line.Description,
                line.Amount.Amount.ToString(CultureInfo.InvariantCulture),
                line.Amount.Currency.Value,
                line.CategorySlug,
                line.MerchantId,
                (int)line.CategorizedBy))],
            transaction.Kind.ToString(),
            transaction.WalletId,
            checkpoint is null
                ? null
                : new SnapshotBalance(checkpoint.Amount.ToString(CultureInfo.InvariantCulture), checkpoint.Currency.Value)));
```

The only differences from Task 1's version are the `checkpoint` query and the last argument. Replace the `Snapshot` record with the following, and add `SnapshotBalance` after `SnapshotLine`:

```csharp
    sealed record Snapshot(
        [property: JsonPropertyName("raw_text")] string? RawText,
        [property: JsonPropertyName("occurred_on")] string OccurredOn,
        [property: JsonPropertyName("items")] IReadOnlyList<SnapshotLine> Items,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("wallet_id")] Guid? WalletId,
        [property: JsonPropertyName("stated_balance")] SnapshotBalance? StatedBalance);
```

```csharp
    sealed record SnapshotBalance(
        [property: JsonPropertyName("amount")] string Amount,
        [property: JsonPropertyName("currency")] string Currency);
```

`RevisionLog.AppendAsync`'s signature does not change, so `EfRecordEditor`'s Cancel and Restore revisions carry `stated_balance` too, and so does Task 5's opening-balance revision. The snapshot is read back inside the caller's transaction, after `LedgerPostings.RewriteAsync`, so it shows the checkpoint as it will commit.

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj --filter TransactionRevisionTests`
Expected: PASS (every test in the class, including the three `The_history_cannot_be_rewritten` cases).

- [ ] **Step 7: Watch the stated balance key fail once**

Replace the `checkpoint is null ? null : new SnapshotBalance(...)` argument with `null` and run the Step 6 command.
Expected: FAIL in `A_revision_records_the_kind_the_wallet_and_the_stated_balance`: `The requested operation requires an element of type 'Object', but the target element has type 'Null'`. Revert, run again: PASS.

- [ ] **Step 8: Run every affected project**

Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests/Noof.Ledger.Persistence.Tests.csproj`
Expected: PASS. The existing `EfCategorizationStoreTests` build outcomes with only `(items, day)` or `(items, day, JobKind, instruction)`. They become an `Expense` in the transaction's own wallet (`WalletId` null keeps it) and now also post entries. None of them asserts on entries, and the concurrent-apply test still ends with one line: both callers delete and re-insert under the row lock.

Run: `dotnet test --project tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj`
Run: `dotnet test --project tests/Noof.Ledger.Architecture.Tests/Noof.Ledger.Architecture.Tests.csproj`
Expected: PASS. No public type was added. `LedgerPostings` is `internal`, and `PublicSurfaceTests` needs no edit.

- [ ] **Step 9: Run the whole solution, holding the suite lock**

Take the lock: `mkdir C:\Users\noofs\AppData\Local\Temp\noof-suite.lock`. If it already exists, wait and retry.
Run: `dotnet test --solution NoofLedger.slnx`
Expected: every test passes except the skipped live suites.
Release the lock: `rmdir C:\Users\noofs\AppData\Local\Temp\noof-suite.lock`, including when the run fails.

- [ ] **Step 10: Commit**

```bash
git add src/Noof.Ledger.Persistence/Balances/LedgerPostings.cs \
        src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs \
        src/Noof.Ledger.Persistence/Revisions/RevisionLog.cs \
        tests/Noof.Ledger.Persistence.Tests/LedgerWritePathTests.cs \
        tests/Noof.Ledger.Persistence.Tests/TransactionRevisionTests.cs
git commit -m "$(cat <<'EOF'
feat(ledger): write entries and checkpoints with every categorization (M5, M6, M13)

ApplyAsync now stores the record's kind and wallet. It replaces its postings in the same database
transaction as its lines and its revision. An expense or an income gets one signed entry per
currency, summed from its lines. A balance statement gets a checkpoint holding the stated amount
and the balance computed just before it. A kind or wallet correction moves or swaps them. Cancel
and Restore leave the postings alone; the balance view counts only completed records. Revision
snapshots gain stated_balance.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 7: The `/wallets` page

**Files:**
- Create: `src/Noof.Ledger.Web/Components/Pages/Wallets.razor`
- Modify: `src/Noof.Ledger.Web/Components/Layout/NavBar.razor`
- Create: `tests/Noof.Ledger.Architecture.Tests/WalletsPageSourceTests.cs`
- Create: `tests/Noof.Ledger.E2E.Tests/WalletsTests.cs`

**Interfaces:**
- Consumes (Task 5, `Noof.Ledger.Application.Wallets`, already registered scoped in `AddNoofPersistence` by Task 5):
  ```csharp
  public sealed record WalletDetails(Guid Id, string Name, CurrencyCode Currency, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency, bool Archived);
  public sealed record NewWallet(string Name, CurrencyCode Currency, decimal OpeningBalance, DateOnly OpeningDate, IReadOnlyList<string> Aliases, bool IsDefaultForCurrency);
  public interface IWalletAdmin
  {
      Task<IReadOnlyList<WalletDetails>> ListAsync(CancellationToken cancellationToken);
      Task<Guid> CreateAsync(NewWallet wallet, CancellationToken cancellationToken);
      Task RenameAsync(Guid walletId, string name, CancellationToken cancellationToken);
      Task SetAliasesAsync(Guid walletId, IReadOnlyList<string> aliases, CancellationToken cancellationToken);
      Task MakeDefaultForCurrencyAsync(Guid walletId, CancellationToken cancellationToken);
      Task ArchiveAsync(Guid walletId, CancellationToken cancellationToken);
  }
  ```
  Also `Noof.Ledger.Domain.CurrencyCode` (unchanged — `CurrencyCode.Supported` is `[Eur, Rsd, Usd, Rub, Kzt]`).
- Produces: nothing another task consumes. `Noof.Ledger.Web` is not in `PublicSurfaceTests.ScannedProjects`, so this task makes no change to `PublicSurfaceTests.Allowed`.

**Design notes, settled before writing code (read before Step 1):**

- CLAUDE.md §4 *Render modes* and spec M11 both forbid a popover, dialog, snackbar, tooltip or menu
  anywhere in this app. `MudSelect` and `MudDatePicker` both render their picker through
  `MudPopoverProvider`, and `MainLayout.razor` deliberately has no popover provider (verified in
  `facts-surfaces-ops.md` §3) — using either component here would silently render a picker that
  never opens. So currency is a plain native `<select>` element (five `<option>`s, `CurrencyCode.Supported`)
  and the opening date is a `MudTextField` with `InputType="InputType.Date"`, which renders a plain
  native `<input type="date">` — the browser's own OS-level date control, not a MudBlazor popover
  component — styled through MudBlazor's `MudTextField` chrome. `MudCheckBox` and `MudNumericField`
  render inline with no popover and are used as-is.
- Per-page render mode, same pattern as `Secrets.razor`: `@rendermode @(new InteractiveServerRenderMode(prerender: false))`. This page reads no secret, but every button here mutates data through a scoped service and needs a live circuit; prerendering it would run `OnInitializedAsync` twice for no benefit.
- `IWalletAdmin.ListAsync` returns immutable `WalletDetails`. The page keeps its own mutable `Row`
  wrapper (same idiom as `Secrets.razor`'s `Row`) so a name or alias edit in a text field does not
  need a separate "pending edit" dictionary.
- The contract's element ids are singular per row (`rename-{id}`, `save-aliases-{id}`, …) — the
  Rename button commits whatever is currently in the `wallet-name-{id}` field; there is no separate
  "edit mode" toggle.
- `wallets-error` / `wallets-saved` are single, page-level `MudAlert`s (not per-row), cleared at the
  start of every action and set at its end — exactly the `Secrets.razor` idiom of one `row.Error`
  per action, generalised to a single page-level slot because the contract names exactly one error
  id and one saved id for the whole page, not one pair per row.
- `IWalletAdmin`'s methods have no return value to report failure with (`Task`, not `Task<bool>`) —
  a violation (e.g. renaming a wallet that was archived and deleted concurrently, or a database
  error) surfaces as a thrown exception. Every button handler wraps its call in `try`/`catch
  (Exception)` and writes a short message into `error`; nothing here re-throws into the Blazor
  circuit's own error boundary, because a single failed action must not tear down the whole page.
- The opening-balance `MudNumericField<decimal>` and the opening-date `MudTextField<DateOnly?>` both
  pin `Culture="@CultureInfo.InvariantCulture"` — `MudFormComponent` otherwise parses typed input
  against the server's own `CurrentCulture`, and on the operator's ru-RU/sr-Latn-RS Windows a comma is
  the decimal separator, so "1500.50" would silently parse as invalid and the wallet would be created
  with an opening balance of 0.
- `wallets-warning` is a page-level `MudAlert` (`Severity.Warning`), separate from `wallets-error`/
  `wallets-saved`, naming every currency that has an active wallet but none marked default — the state
  the ledger is in right after the operator archives the only default wallet for a currency, which
  otherwise leaves messages naming no wallet with nowhere to record into and no visible sign why.

- [ ] **Step 1: The page exists, is authorised, lists wallets, and creates one**

`tests/Noof.Ledger.Architecture.Tests/WalletsPageSourceTests.cs`:

```csharp
using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

public class WalletsPageSourceTests
{
    static string SourceText() => File.ReadAllText(Path.Combine(
        RepoRoot.Find().FullName, "src", "Noof.Ledger.Web", "Components", "Pages", "Wallets.razor"));

    [Fact]
    public void Is_a_routable_authorised_page()
    {
        var source = SourceText();

        source.Should().Contain("@page \"/wallets\"");
        source.Should().Contain("[Authorize]");
    }

    [Fact]
    public void Never_uses_a_popover_backed_component()
    {
        // MainLayout deliberately has no MudPopoverProvider/MudDialogProvider/MudSnackbarProvider
        // (CLAUDE.md §4, verified in ShellSourceTests) - MudSelect, MudDatePicker, MudAutocomplete,
        // MudMenu, MudTooltip, MudDialog and MudSnackbar all render through one of those providers
        // and would silently never open from a page under this layout.
        var source = SourceText();

        source.Should().NotContain("MudSelect");
        source.Should().NotContain("MudDatePicker");
        source.Should().NotContain("MudAutocomplete");
        source.Should().NotContain("MudMenu");
        source.Should().NotContain("MudTooltip");
        source.Should().NotContain("MudDialog");
        source.Should().NotContain("MudSnackbar");
    }

    [Fact]
    public void Disposes_a_component_owned_cancellation_source()
    {
        var source = SourceText();

        source.Should().Contain("@implements IDisposable");
        source.Should().Contain("CancellationTokenSource");
        source.Should().NotContain("CancellationToken.None");
    }

    [Fact]
    public void Parses_numbers_and_dates_against_invariant_culture()
    {
        // MudNumericField and a MudTextField typed DateOnly? parse user input against the server's
        // own CurrentCulture unless told otherwise - on a ru-RU/sr-Latn-RS host, "1500.50" is not a
        // valid number (comma is the decimal separator there) and the field silently keeps 0.
        var source = SourceText();
        var occurrences = source.Split("Culture=\"@CultureInfo.InvariantCulture\"").Length - 1;

        occurrences.Should().BeGreaterThanOrEqualTo(2);
    }
}
```

Run it: `dotnet test tests/Noof.Ledger.Architecture.Tests --filter WalletsPageSourceTests`.
Expected FAIL: `System.IO.FileNotFoundException` inside `SourceText()` (`Wallets.razor` does not exist
yet) — all four tests error out with that exception.

Create `src/Noof.Ledger.Web/Components/Pages/Wallets.razor`:

```razor
@page "/wallets"
@attribute [Authorize]
@implements IDisposable
@using System.Globalization
@using Noof.Ledger.Application.Wallets
@using Noof.Ledger.Domain
@inject IWalletAdmin WalletAdmin
@rendermode @(new InteractiveServerRenderMode(prerender: false))

<PageTitle>Wallets</PageTitle>

<MudText HtmlTag="h1" Typo="Typo.h4" Class="mb-1">Wallets</MudText>
<MudText Typo="Typo.body2" Class="mud-text-secondary mb-6">
    Create, rename and archive wallets here. Income and balance statements are told to the bot.
</MudText>

@if (error is not null)
{
    <MudAlert id="wallets-error" Severity="Severity.Error" Variant="Variant.Outlined" Class="mb-4">@error</MudAlert>
}
@if (saved is not null)
{
    <MudAlert id="wallets-saved" Severity="Severity.Success" Variant="Variant.Outlined" Class="mb-4">@saved</MudAlert>
}
@if (missingDefaultCurrencies.Count > 0)
{
    <MudAlert id="wallets-warning" Severity="Severity.Warning" Variant="Variant.Outlined" Class="mb-4">
        @foreach (var currency in missingDefaultCurrencies)
        {
            <div>No default wallet for @currency — messages that name no wallet cannot be recorded until one is made default.</div>
        }
    </MudAlert>
}

@foreach (var wallet in wallets)
{
    <MudPaper id="@($"wallet-{wallet.Id}")" Outlined="true" Elevation="0" Class="pa-5 mb-4">
        <MudText Typo="Typo.caption" Class="mud-text-secondary">@wallet.Currency</MudText>
        @* noof-wallet-row/noof-native-select below are plain layout hooks, not yet defined in the
           app stylesheet - harmless if unstyled, and no test asserts on their CSS. *@
        <div class="noof-wallet-row">
            <MudTextField T="string" InputId="@($"wallet-name-{wallet.Id}")" @bind-Value="wallet.PendingName"
                          Variant="Variant.Outlined" Margin="Margin.Dense" Label="Name" />
            <MudButton id="@($"rename-{wallet.Id}")" Variant="Variant.Outlined"
                       OnClick="@(() => RenameAsync(wallet))">
                Rename
            </MudButton>
        </div>
        <div class="noof-wallet-row mt-2">
            <MudTextField T="string" InputId="@($"wallet-aliases-{wallet.Id}")" @bind-Value="wallet.PendingAliases"
                          Variant="Variant.Outlined" Margin="Margin.Dense" Label="Aliases (comma-separated)" />
            <MudButton id="@($"save-aliases-{wallet.Id}")" Variant="Variant.Outlined"
                       OnClick="@(() => SaveAliasesAsync(wallet))">
                Save aliases
            </MudButton>
        </div>
        <div class="noof-wallet-row mt-2">
            @if (wallet.IsDefaultForCurrency)
            {
                <MudChip T="string" Size="Size.Small" Color="Color.Primary" Variant="Variant.Outlined">
                    Default for @wallet.Currency
                </MudChip>
            }
            else
            {
                <MudButton id="@($"make-default-{wallet.Id}")" Variant="Variant.Text"
                           OnClick="@(() => MakeDefaultAsync(wallet))">
                    Make default for @wallet.Currency
                </MudButton>
            }

            @if (wallet.Archived)
            {
                <MudChip T="string" Size="Size.Small" Color="Color.Default" Variant="Variant.Outlined">Archived</MudChip>
            }
            else
            {
                <MudButton id="@($"archive-{wallet.Id}")" Variant="Variant.Text" Color="Color.Error"
                           OnClick="@(() => ArchiveAsync(wallet))">
                    Archive
                </MudButton>
            }
        </div>
    </MudPaper>
}

<MudPaper Outlined="true" Elevation="0" Class="pa-5">
    <MudText Typo="Typo.subtitle1" Class="mb-3">New wallet</MudText>

    <MudTextField T="string" InputId="new-wallet-name" @bind-Value="newName"
                  Variant="Variant.Outlined" Margin="Margin.Dense" Label="Name" Class="mb-3" />

    <MudText Typo="Typo.caption" Class="mud-text-secondary">Currency</MudText>
    <select id="new-wallet-currency" @bind="newCurrency" class="noof-native-select mb-3">
        @foreach (var currency in CurrencyCode.Supported)
        {
            <option value="@currency.Value">@currency.Value</option>
        }
    </select>

    <MudNumericField T="decimal" InputId="new-wallet-opening" @bind-Value="newOpeningBalance"
                     Culture="@CultureInfo.InvariantCulture"
                     Variant="Variant.Outlined" Margin="Margin.Dense" Label="Opening balance" Class="mb-3" />

    @* A native date input, not MudDatePicker - see the design note above the Steps. Culture is pinned
       to invariant so the server's own regional settings (ru-RU, sr-Latn-RS) never change how a typed
       "2026-09-01" parses - MudFormComponent parses text input against CurrentCulture unless told
       otherwise, same reasoning as the opening-balance field above. *@
    <MudTextField T="DateOnly?" InputId="new-wallet-date" @bind-Value="newOpeningDate" InputType="InputType.Date"
                  Culture="@CultureInfo.InvariantCulture" Format="yyyy-MM-dd"
                  Variant="Variant.Outlined" Margin="Margin.Dense" Label="Opening date" Class="mb-3" />

    <MudTextField T="string" InputId="new-wallet-aliases" @bind-Value="newAliases"
                  Variant="Variant.Outlined" Margin="Margin.Dense" Label="Aliases (comma-separated)" Class="mb-3" />

    @* MudCheckBox has no InputId parameter (only MudBaseInput-derived components do) - a plain id
       attribute goes to UserAttributes and lands on the root <label>, which Playwright can still click. *@
    <MudCheckBox T="bool" id="new-wallet-default" @bind-Value="newIsDefault"
                Label="Default for its currency" Class="mb-3" />

    <MudButton id="create-wallet" Variant="Variant.Filled" Color="Color.Primary" OnClick="CreateAsync">
        Create wallet
    </MudButton>
</MudPaper>

@code {
    sealed class Row
    {
        public required Guid Id { get; init; }
        public required CurrencyCode Currency { get; init; }
        public required bool IsDefaultForCurrency { get; set; }
        public required bool Archived { get; set; }
        public string PendingName { get; set; } = string.Empty;
        public string PendingAliases { get; set; } = string.Empty;
    }

    List<Row> wallets = [];
    List<string> missingDefaultCurrencies = [];
    string? error;
    string? saved;

    string newName = string.Empty;
    string newCurrency = CurrencyCode.Rsd.Value;
    decimal newOpeningBalance;
    DateOnly? newOpeningDate = DateOnly.FromDateTime(DateTime.Today);
    string newAliases = string.Empty;
    bool newIsDefault;

    readonly CancellationTokenSource cancellation = new();

    protected override async Task OnInitializedAsync() => await ReloadAsync();

    async Task ReloadAsync()
    {
        var details = await WalletAdmin.ListAsync(cancellation.Token);
        wallets =
        [
            .. details.Select(wallet => new Row
            {
                Id = wallet.Id,
                Currency = wallet.Currency,
                IsDefaultForCurrency = wallet.IsDefaultForCurrency,
                Archived = wallet.Archived,
                PendingName = wallet.Name,
                PendingAliases = string.Join(", ", wallet.Aliases),
            }),
        ];

        // A currency with an active wallet but no default leaves nothing for a message that names no
        // wallet to record into (the mapper falls back to the default for the message's currency;
        // CategorizationWorkerTests' A_message_with_no_default_wallet_fails_terminally_naming_the_cause
        // covers the failure this warns about - Task 4 finding 3).
        missingDefaultCurrencies =
        [
            .. details
                .Where(wallet => !wallet.Archived)
                .GroupBy(wallet => wallet.Currency.Value)
                .Where(group => !group.Any(wallet => wallet.IsDefaultForCurrency))
                .Select(group => group.Key)
                .OrderBy(currency => currency, StringComparer.Ordinal),
        ];
    }

    static IReadOnlyList<string> SplitAliases(string raw) =>
        [.. raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    async Task CreateAsync()
    {
        error = null;
        saved = null;

        var name = newName.Trim();
        if (name.Length == 0)
        {
            error = "Enter a wallet name.";
            return;
        }

        if (newOpeningDate is not { } openingDate)
        {
            error = "Enter an opening date.";
            return;
        }

        try
        {
            await WalletAdmin.CreateAsync(
                new NewWallet(name, new CurrencyCode(newCurrency), newOpeningBalance, openingDate,
                    SplitAliases(newAliases), newIsDefault),
                cancellation.Token);

            newName = string.Empty;
            newOpeningBalance = 0m;
            newAliases = string.Empty;
            newIsDefault = false;
            saved = $"Created {name}.";
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            error = $"Could not create the wallet: {exception.Message}";
        }
    }

    async Task RenameAsync(Row wallet)
    {
        error = null;
        saved = null;

        var name = wallet.PendingName.Trim();
        if (name.Length == 0)
        {
            error = "Enter a wallet name.";
            return;
        }

        try
        {
            await WalletAdmin.RenameAsync(wallet.Id, name, cancellation.Token);
            saved = "Renamed.";
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            error = $"Could not rename the wallet: {exception.Message}";
        }
    }

    async Task SaveAliasesAsync(Row wallet)
    {
        error = null;
        saved = null;

        try
        {
            await WalletAdmin.SetAliasesAsync(wallet.Id, SplitAliases(wallet.PendingAliases), cancellation.Token);
            saved = "Aliases saved.";
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            error = $"Could not save the aliases: {exception.Message}";
        }
    }

    async Task MakeDefaultAsync(Row wallet)
    {
        error = null;
        saved = null;

        try
        {
            await WalletAdmin.MakeDefaultForCurrencyAsync(wallet.Id, cancellation.Token);
            saved = "Made default.";
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            error = $"Could not make it the default: {exception.Message}";
        }
    }

    async Task ArchiveAsync(Row wallet)
    {
        error = null;
        saved = null;

        try
        {
            await WalletAdmin.ArchiveAsync(wallet.Id, cancellation.Token);
            saved = "Archived.";
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            error = $"Could not archive the wallet: {exception.Message}";
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
```

Run the guard tests again: `dotnet test tests/Noof.Ledger.Architecture.Tests --filter WalletsPageSourceTests`.
Expected PASS: 4 passed.

- [ ] **Step 2: Watch the popover guard fail, then put it back**

Before moving on, prove `Never_uses_a_popover_backed_component` actually enforces something: temporarily
add a line `<MudSelect T="string" />` anywhere in `Wallets.razor`, run
`dotnet test tests/Noof.Ledger.Architecture.Tests --filter Never_uses_a_popover_backed_component`,
confirm it fails with the `NotContain("MudSelect")` message, then remove the line and re-run to confirm
it passes again.

- [ ] **Step 3: E2E — creating a wallet with an opening balance**

`tests/Noof.Ledger.E2E.Tests/WalletsTests.cs`:

```csharp
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.E2E.Tests;

public sealed class WalletsTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task Creating_a_wallet_with_an_opening_balance_lists_it()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var name = $"Wise EUR {Guid.NewGuid():N}";

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/wallets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Page.FillAsync("#new-wallet-name", name);
        await Page.SelectOptionAsync("#new-wallet-currency", "EUR");
        await Page.FillAsync("#new-wallet-opening", "1500.50");
        await Page.FillAsync("#new-wallet-date", "2026-09-01");
        await Page.ClickAsync("#create-wallet");

        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Created");

        await using var db = OpenDb();
        var wallet = await db.Wallets.SingleAsync(w => w.Name == name, TestContext.Current.CancellationToken);
        wallet.Currency.Should().Be(CurrencyCode.Eur);

        // The wallet name lives in an <input value>, which Playwright's text-content locators
        // (HasText, ToContainTextAsync) never see - assert the value itself.
        await Expect(Page.Locator($"#wallet-name-{wallet.Id}")).ToHaveValueAsync(name);

        var checkpoint = await db.BalanceChecks.SingleAsync(
            bc => bc.WalletId == wallet.Id, TestContext.Current.CancellationToken);
        checkpoint.Stated.Should().Be(new Money(1500.50m, CurrencyCode.Eur),
            "the opening-balance field is culture-pinned to invariant, so a decimal point parses the same regardless of the server's own regional settings");
    }

    [Fact]
    public async Task Renaming_editing_aliases_making_default_and_archiving_a_wallet()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var originalName = $"Cash {Guid.NewGuid():N}";
        var renamedTo = $"Petty cash {Guid.NewGuid():N}";

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/wallets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Page.FillAsync("#new-wallet-name", originalName);
        await Page.SelectOptionAsync("#new-wallet-currency", "RSD");
        await Page.FillAsync("#new-wallet-opening", "0");
        await Page.FillAsync("#new-wallet-date", "2026-09-01");
        await Page.ClickAsync("#create-wallet");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Created");

        Guid walletId;
        await using (var db = OpenDb())
        {
            walletId = (await db.Wallets.SingleAsync(
                w => w.Name == originalName, TestContext.Current.CancellationToken)).Id;
        }

        var row = Page.Locator($"#wallet-{walletId}");
        await Expect(row).ToBeVisibleAsync();

        await Page.FillAsync($"#wallet-name-{walletId}", renamedTo);
        await Page.ClickAsync($"#rename-{walletId}");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Renamed");
        // The renamed value lives in an <input value>, not text content - ToContainTextAsync never sees it.
        await Expect(Page.Locator($"#wallet-name-{walletId}")).ToHaveValueAsync(renamedTo);

        await Page.FillAsync($"#wallet-aliases-{walletId}", "нал, cash, налик");
        await Page.ClickAsync($"#save-aliases-{walletId}");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Aliases saved");

        await Page.ClickAsync($"#make-default-{walletId}");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Made default");
        await Expect(row).ToContainTextAsync("Default for RSD");

        await Page.ClickAsync($"#archive-{walletId}");
        await Expect(Page.Locator("#wallets-saved")).ToContainTextAsync("Archived");
        await Expect(row).ToContainTextAsync("Archived");

        // This wallet was RSD's only default; archiving it leaves RSD with none, and the page must say
        // so (Task 4 finding 3 pairs the mapper's failure text with this warning).
        await Expect(Page.Locator("#wallets-warning")).ToContainTextAsync("RSD");

        await using var verify = OpenDb();
        var wallet = await verify.Wallets.AsNoTracking()
            .SingleAsync(w => w.Id == walletId, TestContext.Current.CancellationToken);
        wallet.Name.Should().Be(renamedTo);
        wallet.Aliases.Should().BeEquivalentTo(["нал", "cash", "налик"]);
        wallet.Archived.Should().BeTrue();
    }

    [Fact]
    public async Task An_empty_wallet_name_is_refused_inline()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/wallets");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Page.SelectOptionAsync("#new-wallet-currency", "USD");
        await Page.FillAsync("#new-wallet-opening", "10");
        await Page.FillAsync("#new-wallet-date", "2026-09-01");
        await Page.ClickAsync("#create-wallet");

        await Expect(Page.Locator("#wallets-error")).ToContainTextAsync("Enter a wallet name.");
        await Expect(Page.Locator("#wallets-saved")).Not.ToBeVisibleAsync();
    }

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

Run: `dotnet test tests/Noof.Ledger.E2E.Tests --filter WalletsTests`.
Expected: PASS once Task 5 is merged (this step adds coverage on top of an already-existing page — the
true red/green pair for `Wallets.razor` itself is Step 1's `WalletsPageSourceTests`, which failed on
`FileNotFoundException` before the page existed; these E2E tests instead exercise `IWalletAdmin`'s real
database behaviour through it). If `IWalletAdmin`'s Task 5 implementation is not yet merged into this
worktree, skip with `Assert.Skip("Task 5 (IWalletAdmin) not yet merged")` at the top of the class instead
of leaving it red for an unrelated reason.

- [ ] **Step 4: Add the `/wallets` nav link**

`src/Noof.Ledger.Web/Components/Layout/NavBar.razor`, in the `Links` array:

Before:
```csharp
    static readonly NavLinkSpec[] Links =
    [
        new("/", "Dashboard", Icons.Material.Outlined.Insights),
        new("/settings/secrets", "Secrets", Icons.Material.Outlined.Key),
    ];
```

After:
```csharp
    static readonly NavLinkSpec[] Links =
    [
        new("/", "Dashboard", Icons.Material.Outlined.Insights),
        new("/wallets", "Wallets", Icons.Material.Outlined.AccountBalanceWallet),
        new("/settings/secrets", "Secrets", Icons.Material.Outlined.Key),
    ];
```

There is no existing test asserting the exact contents of `Links`, so there is no red step for this
addition — it is covered end-to-end by every `WalletsTests` case, which navigates to `/wallets` directly
by URL rather than through the nav link. Confirm manually that the link appears by running the app (see
Step 5's full suite, which includes the Playwright suite that renders `NavBar` on every page).

- [ ] **Step 5: Full suite and commit**

```powershell
$lock = "C:\Users\noofs\AppData\Local\Temp\noof-suite.lock"
while (-not (New-Item -ItemType Directory -Path $lock -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2 }
try {
    dotnet test --solution NoofLedger.slnx
} finally {
    Remove-Item -Recurse -Force $lock
}
```

Expected PASS: every test green, including the four new `WalletsPageSourceTests` and the three new
`WalletsTests` E2E cases.

```
git add src/Noof.Ledger.Web/Components/Pages/Wallets.razor src/Noof.Ledger.Web/Components/Layout/NavBar.razor tests/Noof.Ledger.Architecture.Tests/WalletsPageSourceTests.cs tests/Noof.Ledger.E2E.Tests/WalletsTests.cs
git commit -m "$(cat <<'EOF'
Add the /wallets page: create, rename, alias, default and archive

Inline MudAlert feedback only, no popover-backed MudBlazor component
(MudSelect/MudDatePicker would silently never open under MainLayout's
provider-free static shell) - a native <select> and a native date
input stand in for them.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 13: `BackupWorker`

**Files:**
- Create: `src/Noof.Ledger.Host/Workers/BackupWorkerOptions.cs`
- Create: `src/Noof.Ledger.Host/Workers/BackupWorker.cs`
- Create: `src/Noof.Ledger.Host/Workers/BackupRetention.cs` — `internal static class BackupRetention` (moved here from Task 12's original draft per the review; `Noof.Ledger.Host.Tests` already carries the `InternalsVisibleTo` this needs, and `BackupWorker` is its only caller).
- Modify: `src/Noof.Ledger.Host/Workers/WorkerRegistration.cs`
- Modify: `src/Noof.Ledger.Host/Program.cs`
- Modify: `tests/Noof.Ledger.E2E.Tests/CookieModeHostFixture.cs` — add `Backup__Enabled = false` to the in-process host's configuration overrides.
- Modify: `tests/Noof.Ledger.E2E.Tests/UnreachableDatabaseHostFixture.cs` — same.
- Modify: every other `WebApplicationFactory<Program>` construction in `Noof.Ledger.Host.Tests` (`ThemeTests`, `CategorizationWiringTests`, `AuthenticationTests`, `BootTests`, `LoginEndpointTests`, `AnthropicHttpClientLoggingTests`, `TelegramHttpClientLoggingTests`, `DataProtectionWiringTests`) — add `builder.UseSetting("Backup:Enabled", "false")`.
- Test: `tests/Noof.Ledger.Host.Tests/BackupWorkerTests.cs` (new), `tests/Noof.Ledger.Host.Tests/BackupWorkerWiringTests.cs` (new), `tests/Noof.Ledger.Host.Tests/BackupRetentionTests.cs` (new).

> **Contract correction (review, contract finding 6 and Task 13 finding 1 — blocker):** `BackupWorkerOptions` gains `public bool Enabled { get; init; } = true;`, and `WorkerRegistration.AddNoofWorkers` registers `BackupWorker` only when it is true. Without this, `BackupWorker` runs inside every E2E host (`CookieModeHostFixture` starts the real published host with only `Database__MigrateOnStartup` and `ConnectionStrings__Ledger` set) and would `pg_dump` every E2E clone into the operator's own `%LOCALAPPDATA%\NoofLedger\backups`, and — because test dumps carry the newest timestamps — `Prune()` would delete the operator's real backups to stay at 14. Every `WebApplicationFactory<Program>` in `Noof.Ledger.Host.Tests` disables it too, since those point at an unreachable database and the worker would otherwise log a failed tick after a 2-second timeout on every test run.
>
> **Also moved here (review, Task 12 finding 1 and the contract table's rejection of a public `BackupRetention`):** `BackupRetention` is `internal static class BackupRetention` in `Noof.Ledger.Host.Workers`, not a public `Noof.Ledger.Application.Backup` type. This task creates and tests it, since it is the only caller.

**Interfaces:**
- Consumes (Task 12, exactly as declared there):
  ```csharp
  namespace Noof.Ledger.Application.Backup;
  public sealed record BackupRunRecord(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool Succeeded, string? FileName, long? SizeBytes, string? Error);
  public sealed record BackupStatus(DateTimeOffset? LastSuccessAt, bool LastRunFailed, string? LastError);
  public interface IBackupLog { Task RecordAsync(BackupRunRecord run, CancellationToken cancellationToken); Task<BackupStatus> StatusAsync(CancellationToken cancellationToken); }
  public sealed record DumpResult(bool Succeeded, string? Error);
  public interface IDatabaseDumper { Task<DumpResult> DumpAsync(string targetPath, CancellationToken cancellationToken); }
  ```
  Both `IBackupLog` and `IDatabaseDumper` are already registered scoped by `AddNoofPersistence` (Task 12) — this task only resolves them from a DI scope, never constructs an implementation.
- Consumes (existing, unchanged): `WorkerRegistration.AddNoofWorkers(this IServiceCollection, CategorizationWorkerOptions options)` (`src/Noof.Ledger.Host/Workers/WorkerRegistration.cs`) — this task widens its signature (see Step 5); `Program.cs`'s existing `categorizationOptions` binding block (`src/Noof.Ledger.Host/Program.cs`, around the `builder.Services.AddNoofPersistence(...)` call) is the pattern this task's `Backup` section binding copies.
- Produces: `internal sealed class BackupWorker : BackgroundService` with `public Task<BackupTickResult> RunTickAsync(CancellationToken cancellationToken)` (namespace `Noof.Ledger.Host.Workers`) — a single tick's outcome, tested directly the way `CategorizationWorkerTests` drives `CategorizationWorker.RunTickAsync`. `internal enum BackupTickResult { BackedUp, Skipped, Failed }`. Neither is public — no `PublicSurfaceTests` change (`Noof.Ledger.Host` scans to an empty allowed set today and stays empty).
- Produces: `internal static class BackupRetention { public static IReadOnlyList<string> ToDelete(IReadOnlyList<string> fileNames, int keep = 14); }` (namespace `Noof.Ledger.Host.Workers`) — this task's own helper, not consumed anywhere else.

---

- [ ] **Step 1: Write the failing tests for a tick's decision and outcome**

  `tests/Noof.Ledger.Host.Tests/BackupWorkerTests.cs`:

  ```csharp
  using AwesomeAssertions;
  using Microsoft.Extensions.DependencyInjection;
  using Microsoft.Extensions.Logging.Abstractions;
  using Microsoft.Extensions.Time.Testing;
  using NSubstitute;
  using NSubstitute.ExceptionExtensions;
  using Noof.Ledger.Application.Backup;
  using Noof.Ledger.Host.Workers;

  namespace Noof.Ledger.Host.Tests;

  public class BackupWorkerTests : IDisposable
  {
      readonly string backupDirectory = Path.Combine(Path.GetTempPath(), $"noof-backup-worker-tests-{Guid.NewGuid():N}");

      public void Dispose()
      {
          if (Directory.Exists(backupDirectory))
              Directory.Delete(backupDirectory, recursive: true);
      }

      BackupWorkerOptions Options(int keepCount = 14, TimeSpan? retryInterval = null) => new()
      {
          BackupDirectory = backupDirectory,
          Interval = TimeSpan.FromHours(24),
          KeepCount = keepCount,
          RetryInterval = retryInterval ?? TimeSpan.FromHours(1),
      };

      static IServiceScopeFactory ScopeFactoryFor(IBackupLog backupLog, IDatabaseDumper dumper)
      {
          var provider = Substitute.For<IServiceProvider>();
          provider.GetService(typeof(IBackupLog)).Returns(backupLog);
          provider.GetService(typeof(IDatabaseDumper)).Returns(dumper);

          var scope = Substitute.For<IServiceScope>();
          scope.ServiceProvider.Returns(provider);

          var factory = Substitute.For<IServiceScopeFactory>();
          factory.CreateScope().Returns(scope);
          return factory;
      }

      static IBackupLog LogWithStatus(BackupStatus status)
      {
          var log = Substitute.For<IBackupLog>();
          log.StatusAsync(Arg.Any<CancellationToken>()).Returns(status);
          return log;
      }

      static BackupWorker CreateWorker(IServiceScopeFactory scopeFactory, FakeTimeProvider time, BackupWorkerOptions options) =>
          new(scopeFactory, time, options, NullLogger<BackupWorker>.Instance);

      [Fact]
      public async Task A_backup_that_never_ran_before_is_taken_immediately()
      {
          var now = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
          var time = new FakeTimeProvider(now);
          var log = LogWithStatus(new BackupStatus(null, false, null));
          var dumper = Substitute.For<IDatabaseDumper>();
          dumper.DumpAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(ci => WriteFakeDumpAsync(ci.Arg<string>(), new DumpResult(true, null)));
          var worker = CreateWorker(ScopeFactoryFor(log, dumper), time, Options());

          var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

          result.Should().Be(BackupTickResult.BackedUp);
          var files = Directory.GetFiles(backupDirectory, "noof_ledger-*.dump");
          files.Should().ContainSingle();
          Path.GetFileName(files[0]).Should().Be("noof_ledger-20260924-030000.dump");
          await log.Received(1).RecordAsync(
              Arg.Is<BackupRunRecord>(r => r.Succeeded && r.FileName == "noof_ledger-20260924-030000.dump" && r.SizeBytes > 0),
              Arg.Any<CancellationToken>());
      }

      [Fact]
      public async Task A_backup_less_than_a_day_old_is_skipped_without_calling_the_dumper()
      {
          var now = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
          var time = new FakeTimeProvider(now);
          var log = LogWithStatus(new BackupStatus(now.AddHours(-2), false, null));
          var dumper = Substitute.For<IDatabaseDumper>();
          var worker = CreateWorker(ScopeFactoryFor(log, dumper), time, Options());

          var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

          result.Should().Be(BackupTickResult.Skipped);
          await dumper.DidNotReceive().DumpAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
          await log.DidNotReceive().RecordAsync(Arg.Any<BackupRunRecord>(), Arg.Any<CancellationToken>());
      }

      [Fact]
      public async Task A_backup_more_than_a_day_stale_is_taken_again()
      {
          var now = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
          var time = new FakeTimeProvider(now);
          var log = LogWithStatus(new BackupStatus(now.AddHours(-25), false, null));
          var dumper = Substitute.For<IDatabaseDumper>();
          dumper.DumpAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(ci => WriteFakeDumpAsync(ci.Arg<string>(), new DumpResult(true, null)));
          var worker = CreateWorker(ScopeFactoryFor(log, dumper), time, Options());

          var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

          result.Should().Be(BackupTickResult.BackedUp);
      }

      [Fact]
      public async Task A_failed_dump_is_recorded_leaves_no_temp_file_and_does_not_throw()
      {
          var now = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
          var time = new FakeTimeProvider(now);
          var log = LogWithStatus(new BackupStatus(null, false, null));
          var dumper = Substitute.For<IDatabaseDumper>();
          dumper.DumpAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new DumpResult(false, "pg_dump exited with code 1"));
          var worker = CreateWorker(ScopeFactoryFor(log, dumper), time, Options());

          var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

          result.Should().Be(BackupTickResult.Failed);
          Directory.Exists(backupDirectory).Should().BeTrue();
          Directory.GetFiles(backupDirectory).Should().BeEmpty("a failed dump must leave no half-written file behind");
          await log.Received(1).RecordAsync(
              Arg.Is<BackupRunRecord>(r => !r.Succeeded && r.FileName == null && r.Error == "pg_dump exited with code 1"),
              Arg.Any<CancellationToken>());
      }

      [Fact]
      public async Task A_dumper_that_throws_is_recorded_as_a_failure_not_a_crash()
      {
          var now = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
          var time = new FakeTimeProvider(now);
          var log = LogWithStatus(new BackupStatus(null, false, null));
          var dumper = Substitute.For<IDatabaseDumper>();
          dumper.DumpAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk full"));
          var worker = CreateWorker(ScopeFactoryFor(log, dumper), time, Options());

          var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

          result.Should().Be(BackupTickResult.Failed);
          await log.Received(1).RecordAsync(Arg.Is<BackupRunRecord>(r => !r.Succeeded && r.Error == "disk full"), Arg.Any<CancellationToken>());
      }

      [Fact]
      public async Task A_scope_that_cannot_be_created_fails_the_tick_without_throwing()
      {
          var factory = Substitute.For<IServiceScopeFactory>();
          factory.CreateScope().Throws(new InvalidOperationException("no scope"));
          var worker = CreateWorker(factory, new FakeTimeProvider(DateTimeOffset.UtcNow), Options());

          var result = await worker.RunTickAsync(TestContext.Current.CancellationToken);

          result.Should().Be(BackupTickResult.Failed);
      }

      [Fact]
      public async Task A_failed_tick_is_retried_after_the_retry_interval_not_a_day_later()
      {
          var now = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
          var time = new FakeTimeProvider(now);
          var log = Substitute.For<IBackupLog>();
          log.StatusAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("database unreachable"));
          var dumper = Substitute.For<IDatabaseDumper>();
          var worker = CreateWorker(ScopeFactoryFor(log, dumper), time, Options(retryInterval: TimeSpan.FromHours(1)));

          await worker.StartAsync(TestContext.Current.CancellationToken);
          try
          {
              await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
              await log.Received(1).StatusAsync(Arg.Any<CancellationToken>());

              time.Advance(TimeSpan.FromHours(1));
              await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

              await log.Received(2).StatusAsync(Arg.Any<CancellationToken>());
          }
          finally
          {
              await worker.StopAsync(TestContext.Current.CancellationToken);
          }
      }

      [Fact]
      public async Task A_successful_backup_prunes_down_to_the_configured_count()
      {
          Directory.CreateDirectory(backupDirectory);
          for (var day = 1; day <= 3; day++)
              File.WriteAllText(Path.Combine(backupDirectory, $"noof_ledger-202609{day:D2}-030000.dump"), "old");

          var now = new DateTimeOffset(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
          var time = new FakeTimeProvider(now);
          var log = LogWithStatus(new BackupStatus(null, false, null));
          var dumper = Substitute.For<IDatabaseDumper>();
          dumper.DumpAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(ci => WriteFakeDumpAsync(ci.Arg<string>(), new DumpResult(true, null)));
          var worker = CreateWorker(ScopeFactoryFor(log, dumper), time, Options(keepCount: 2));

          await worker.RunTickAsync(TestContext.Current.CancellationToken);

          var remaining = Directory.GetFiles(backupDirectory, "noof_ledger-*.dump").Select(Path.GetFileName).ToArray();
          remaining.Should().BeEquivalentTo(["noof_ledger-20260903-030000.dump", "noof_ledger-20260924-030000.dump"]);
      }

      static Task<DumpResult> WriteFakeDumpAsync(string targetPath, DumpResult result)
      {
          if (result.Succeeded)
              File.WriteAllText(targetPath, "fake dump contents");
          return Task.FromResult(result);
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests --filter BackupWorkerTests`
  Expected FAIL: compiler errors — `BackupWorker`, `BackupWorkerOptions`, `BackupTickResult` do not exist yet.

- [ ] **Step 2: Write the failing test for the retention helper**

  `tests/Noof.Ledger.Host.Tests/BackupRetentionTests.cs`:

  ```csharp
  using AwesomeAssertions;
  using Noof.Ledger.Host.Workers;

  namespace Noof.Ledger.Host.Tests;

  public class BackupRetentionTests
  {
      [Fact]
      public void Keeps_nothing_to_delete_when_at_or_under_the_limit()
      {
          string[] files = ["noof_ledger-20260101-000000.dump", "noof_ledger-20260102-000000.dump"];

          BackupRetention.ToDelete(files, keep: 14).Should().BeEmpty();
      }

      [Fact]
      public void Deletes_every_file_past_the_newest_fourteen_by_name_order()
      {
          // File names embed a sortable UTC timestamp (yyyyMMdd-HHmmss), so the newest 14 are the
          // last 14 in ordinal order - no parsing needed, and no ambiguity from a file's mtime,
          // which a copy or a restore of the backups folder would not preserve.
          var files = Enumerable.Range(1, 16)
              .Select(day => $"noof_ledger-202601{day:D2}-000000.dump")
              .ToArray();

          var toDelete = BackupRetention.ToDelete(files, keep: 14);

          toDelete.Should().BeEquivalentTo(
          [
              "noof_ledger-20260101-000000.dump",
              "noof_ledger-20260102-000000.dump",
          ]);
      }

      [Fact]
      public void An_unordered_input_is_sorted_before_pruning()
      {
          string[] files =
          [
              "noof_ledger-20260103-000000.dump",
              "noof_ledger-20260101-000000.dump",
              "noof_ledger-20260102-000000.dump",
          ];

          BackupRetention.ToDelete(files, keep: 2).Should().BeEquivalentTo(["noof_ledger-20260101-000000.dump"]);
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests --filter BackupRetentionTests`
  Expected FAIL: compiler error — `Noof.Ledger.Host.Workers.BackupRetention` does not exist.

- [ ] **Step 3: Implement `BackupRetention` and `BackupWorkerOptions`**

  `src/Noof.Ledger.Host/Workers/BackupRetention.cs`:

  ```csharp
  namespace Noof.Ledger.Host.Workers;

  // Pure and stateless, nothing to substitute — same exemption as MerchantName.Fold (CLAUDE.md §3).
  // Internal, not public: BackupWorker is its only caller, and CLAUDE.md §3 names the public-helper
  // exemptions "not a licence to invent more by analogy."
  internal static class BackupRetention
  {
      public static IReadOnlyList<string> ToDelete(IReadOnlyList<string> fileNames, int keep = 14) =>
          [.. fileNames
              .OrderBy(name => name, StringComparer.Ordinal)
              .SkipLast(Math.Min(keep, fileNames.Count))];
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests --filter BackupRetentionTests`
  Expected PASS.

  `src/Noof.Ledger.Host/Workers/BackupWorkerOptions.cs`:

  ```csharp
  namespace Noof.Ledger.Host.Workers;

  internal sealed class BackupWorkerOptions
  {
      public bool Enabled { get; init; } = true;

      public string BackupDirectory { get; init; } = Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NoofLedger", "backups");

      public TimeSpan Interval { get; init; } = TimeSpan.FromHours(24);

      public TimeSpan RetryInterval { get; init; } = TimeSpan.FromHours(1);

      public int KeepCount { get; init; } = 14;
  }
  ```

- [ ] **Step 4: Implement `BackupWorker`**

  `src/Noof.Ledger.Host/Workers/BackupWorker.cs`:

  ```csharp
  using Noof.Ledger.Application.Backup;

  namespace Noof.Ledger.Host.Workers;

  internal enum BackupTickResult { BackedUp, Skipped, Failed }

  // TimeProvider-driven throughout (CLAUDE.md "seed timestamps from fixed literals"): the started/
  // finished stamps recorded in backup_runs and the file name both come from timeProvider, never
  // DateTimeOffset.UtcNow, so a test can assert an exact file name and an exact recorded interval.
  internal sealed class BackupWorker(
      IServiceScopeFactory scopeFactory,
      TimeProvider timeProvider,
      BackupWorkerOptions options,
      ILogger<BackupWorker> logger)
      : BackgroundService
  {
      protected override async Task ExecuteAsync(CancellationToken stoppingToken)
      {
          while (!stoppingToken.IsCancellationRequested)
          {
              var result = await RunTickAsync(stoppingToken);
              var delay = result == BackupTickResult.Failed ? options.RetryInterval : options.Interval;
              await Task.Delay(delay, timeProvider, stoppingToken);
          }
      }

      // Never throws (B1/B2: "a failure is logged and recorded, never crashes the host") - every
      // failure path below, including one this method cannot foresee, is caught and turned into a
      // recorded run instead of an unhandled exception on the host's background-service thread.
      public async Task<BackupTickResult> RunTickAsync(CancellationToken cancellationToken)
      {
          try
          {
              using var scope = scopeFactory.CreateScope();
              var backupLog = scope.ServiceProvider.GetRequiredService<IBackupLog>();

              var status = await backupLog.StatusAsync(cancellationToken);
              var now = timeProvider.GetUtcNow();
              if (status.LastSuccessAt is { } lastSuccess && now - lastSuccess < options.Interval)
                  return BackupTickResult.Skipped;

              var dumper = scope.ServiceProvider.GetRequiredService<IDatabaseDumper>();
              return await RunBackupAsync(backupLog, dumper, now, cancellationToken);
          }
          catch (Exception ex) when (ex is not OperationCanceledException)
          {
              logger.LogError(ex, "Backup worker tick failed");
              return BackupTickResult.Failed;
          }
      }

      async Task<BackupTickResult> RunBackupAsync(
          IBackupLog backupLog, IDatabaseDumper dumper, DateTimeOffset started, CancellationToken cancellationToken)
      {
          Directory.CreateDirectory(options.BackupDirectory);
          var fileName = $"noof_ledger-{started:yyyyMMdd-HHmmss}.dump";
          var finalPath = Path.Combine(options.BackupDirectory, fileName);

          // Dumped under a temporary name and renamed only on success, so a half-written dump - the
          // process killed mid-write, the disk filling up - never looks like a finished one to
          // anything (restore-check, a future prune) that lists this directory (B1).
          var tempPath = finalPath + ".tmp";

          DumpResult result;
          try
          {
              result = await dumper.DumpAsync(tempPath, cancellationToken);
          }
          catch (Exception ex) when (ex is not OperationCanceledException)
          {
              result = new DumpResult(false, ex.Message);
          }

          var finished = timeProvider.GetUtcNow();
          long? size = null;

          if (result.Succeeded && File.Exists(tempPath))
          {
              File.Move(tempPath, finalPath, overwrite: true);
              size = new FileInfo(finalPath).Length;
              Prune();
          }
          else if (File.Exists(tempPath))
          {
              File.Delete(tempPath);
          }

          await backupLog.RecordAsync(
              new BackupRunRecord(started, finished, result.Succeeded, result.Succeeded ? fileName : null, size, result.Error),
              cancellationToken);

          if (!result.Succeeded)
              logger.LogError("Backup failed: {Error}", result.Error);

          return result.Succeeded ? BackupTickResult.BackedUp : BackupTickResult.Failed;
      }

      void Prune()
      {
          var fileNames = Directory.EnumerateFiles(options.BackupDirectory, "noof_ledger-*.dump")
              .Select(Path.GetFileName)
              .Where(name => name is not null)
              .Select(name => name!)
              .ToArray();

          foreach (var name in BackupRetention.ToDelete(fileNames, options.KeepCount))
              File.Delete(Path.Combine(options.BackupDirectory, name));
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests --filter BackupWorkerTests`
  Expected PASS: every test in the file.

- [ ] **Step 5: Watch the "no crash" guard fail**

  In `RunTickAsync`'s `catch` clause, temporarily change the filter from `when (ex is not OperationCanceledException)` to `when (false)` (leaving the `catch` block itself in place). Re-run `A_scope_that_cannot_be_created_fails_the_tick_without_throwing` and `A_dumper_that_throws_is_recorded_as_a_failure_not_a_crash` — both must now fail with an unhandled exception escaping the test rather than a clean `Assert.Equal` failure, confirming the catch is what the test actually depends on. Revert `when (false)` back to `when (ex is not OperationCanceledException)`.

- [ ] **Step 6: Wire `WorkerRegistration`, `Program.cs`, and the `Enabled` gate**

  In `src/Noof.Ledger.Host/Workers/WorkerRegistration.cs`, widen the signature and add the third hosted service, registered only when enabled:

  ```csharp
  public static IServiceCollection AddNoofWorkers(
      this IServiceCollection services, CategorizationWorkerOptions options, BackupWorkerOptions backupOptions)
  {
      services.AddSingleton(options);
      services.AddSingleton(backupOptions);
      services.AddHostedService(sp => new CategorizationWorker(
          sp.GetRequiredService<IServiceScopeFactory>(),
          sp.GetRequiredService<TimeProvider>(),
          options,
          CategorizationWorker.CreateWorkerId(),
          sp.GetRequiredService<IProposalMapper>(),
          sp.GetRequiredService<IMerchantScan>(),
          sp.GetRequiredService<IRecordEcho>(),
          sp.GetRequiredService<ILogger<CategorizationWorker>>()));

      services.AddHostedService(sp => new TranscriptionWorker(
          sp.GetRequiredService<IServiceScopeFactory>(),
          sp.GetRequiredService<TimeProvider>(),
          options,
          CategorizationWorker.CreateWorkerId(),
          sp.GetRequiredService<IRecordEcho>(),
          sp.GetRequiredService<ILogger<TranscriptionWorker>>()));

      if (backupOptions.Enabled)
      {
          services.AddHostedService(sp => new BackupWorker(
              sp.GetRequiredService<IServiceScopeFactory>(),
              sp.GetRequiredService<TimeProvider>(),
              backupOptions,
              sp.GetRequiredService<ILogger<BackupWorker>>()));
      }

      return services;
  }
  ```

  This is the guard the contract's finding 6 and this task's finding 1 require: without it, `BackupWorker` would run inside every E2E host and every Host.Tests `WebApplicationFactory`, `pg_dump`-ing E2E clones into the operator's real `%LOCALAPPDATA%\NoofLedger\backups` and pruning the operator's own dumps to stay at 14. The remaining edits in this step turn it off everywhere a factory or fixture does not explicitly need it.

  In `tests/Noof.Ledger.E2E.Tests/CookieModeHostFixture.cs`, find the dictionary of configuration overrides the in-process host is built with (around `Database__MigrateOnStartup` and `ConnectionStrings__Ledger`) and add:
  ```csharp
  ["Backup__Enabled"] = "false",
  ```

  In `tests/Noof.Ledger.E2E.Tests/UnreachableDatabaseHostFixture.cs`, add the same entry to its configuration overrides.

  In every other `Noof.Ledger.Host.Tests` file that builds a `WebApplicationFactory<Program>` (grep `new WebApplicationFactory<Program>()` under `tests/Noof.Ledger.Host.Tests/` — this is `ThemeTests`, `CategorizationWiringTests`, `AuthenticationTests`, `BootTests`, `LoginEndpointTests`, `AnthropicHttpClientLoggingTests`, `TelegramHttpClientLoggingTests` and `DataProtectionWiringTests`, eight files in total), add inside every `.WithWebHostBuilder(builder => { ... })` block it builds:
  ```csharp
  builder.UseSetting("Backup:Enabled", "false");
  ```
  `BackupWorkerWiringTests` itself is the one file that deliberately leaves the default `Factory()` enabled (Step 8), so the registration can be asserted; Step 8 adds a second, explicitly-disabled factory for the negative case.

  In `src/Noof.Ledger.Host/Program.cs`, immediately after the existing block:
  ```csharp
  var categorizationOptions = new CategorizationWorkerOptions();
  builder.Configuration.GetSection("Categorization").Bind(categorizationOptions);
  ```
  add:
  ```csharp
  var backupOptions = new BackupWorkerOptions();
  builder.Configuration.GetSection("Backup").Bind(backupOptions);
  ```
  and change the existing call
  ```csharp
  builder.Services.AddNoofWorkers(categorizationOptions);
  ```
  to
  ```csharp
  builder.Services.AddNoofWorkers(categorizationOptions, backupOptions);
  ```
  This is `Program.cs` wiring plus a DTO (`BackupWorkerOptions`), both exempt from TDD (CLAUDE.md §4 *Testing*); its correctness is exercised by the wiring tests in Step 7, not by a unit test of these lines themselves.

- [ ] **Step 7: Write the failing wiring tests**

  `tests/Noof.Ledger.Host.Tests/BackupWorkerWiringTests.cs`:

  ```csharp
  using AwesomeAssertions;
  using Microsoft.AspNetCore.Mvc.Testing;
  using Microsoft.Extensions.DependencyInjection;
  using Microsoft.Extensions.Hosting;
  using Noof.Ledger.Application.Backup;
  using Noof.Ledger.Host.Workers;

  namespace Noof.Ledger.Host.Tests;

  public class BackupWorkerWiringTests
  {
      static WebApplicationFactory<Program> Factory() =>
          new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
          {
              builder.UseSetting("Database:MigrateOnStartup", "false");
              builder.UseSetting("ConnectionStrings:Ledger",
                  "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
          });

      [Fact]
      public void BackupWorker_is_registered_as_a_hosted_service()
      {
          using var factory = Factory();

          factory.Services.GetServices<IHostedService>().Should().Contain(service => service is BackupWorker);
      }

      [Fact]
      public void BackupWorkerOptions_is_a_singleton_with_its_documented_defaults()
      {
          using var factory = Factory();

          var options = factory.Services.GetRequiredService<BackupWorkerOptions>();

          options.Enabled.Should().BeTrue();
          options.Interval.Should().Be(TimeSpan.FromHours(24));
          options.RetryInterval.Should().Be(TimeSpan.FromHours(1));
          options.KeepCount.Should().Be(14);
          options.BackupDirectory.Should().EndWith(Path.Combine("NoofLedger", "backups"));
      }

      [Fact]
      public void Every_scoped_backup_port_resolves_without_touching_the_database()
      {
          using var factory = Factory();
          using var scope = factory.Services.CreateScope();

          scope.ServiceProvider.GetRequiredService<IBackupLog>();
          scope.ServiceProvider.GetRequiredService<IDatabaseDumper>();
      }

      [Fact]
      public void A_disabled_backup_worker_is_not_registered()
      {
          using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
          {
              builder.UseSetting("Database:MigrateOnStartup", "false");
              builder.UseSetting("ConnectionStrings:Ledger",
                  "Host=127.0.0.1;Port=59999;Database=never_dialled;Username=none;Timeout=2");
              builder.UseSetting("Backup:Enabled", "false");
          });

          factory.Services.GetServices<IHostedService>().Should().NotContain(service => service is BackupWorker);
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests --filter BackupWorkerWiringTests`
  Expected FAIL first (before Step 6's edits are in place) with a compile error or a missing-registration assertion failure, then PASS once Step 6 is applied.

- [ ] **Step 8: Full-suite run for the affected project**

  ```powershell
  mkdir "$env:TEMP\noof-suite.lock" 2>$null
  dotnet test --project tests/Noof.Ledger.Host.Tests
  rmdir "$env:TEMP\noof-suite.lock"
  ```
  Expected: every test in the project passes, including the pre-existing `CategorizationWiringTests` and `ThemeTests`/`LoginEndpointTests` (each now disables `Backup:Enabled` per Step 6, and their own assertions are otherwise unaffected).

- [ ] **Step 9: Commit**

  ```powershell
  git add src/Noof.Ledger.Host/Workers/BackupWorkerOptions.cs src/Noof.Ledger.Host/Workers/BackupWorker.cs src/Noof.Ledger.Host/Workers/BackupRetention.cs src/Noof.Ledger.Host/Workers/WorkerRegistration.cs src/Noof.Ledger.Host/Program.cs tests/Noof.Ledger.Host.Tests/BackupWorkerTests.cs tests/Noof.Ledger.Host.Tests/BackupWorkerWiringTests.cs tests/Noof.Ledger.Host.Tests/BackupRetentionTests.cs tests/Noof.Ledger.Host.Tests/ThemeTests.cs tests/Noof.Ledger.Host.Tests/CategorizationWiringTests.cs tests/Noof.Ledger.Host.Tests/AuthenticationTests.cs tests/Noof.Ledger.Host.Tests/BootTests.cs tests/Noof.Ledger.Host.Tests/LoginEndpointTests.cs tests/Noof.Ledger.Host.Tests/AnthropicHttpClientLoggingTests.cs tests/Noof.Ledger.Host.Tests/TelegramHttpClientLoggingTests.cs tests/Noof.Ledger.Host.Tests/DataProtectionWiringTests.cs tests/Noof.Ledger.E2E.Tests/CookieModeHostFixture.cs tests/Noof.Ledger.E2E.Tests/UnreachableDatabaseHostFixture.cs
  git commit -m "$(cat <<'EOF'
  Add BackupWorker: a daily pg_dump, kept 14 deep, that never crashes the host (B1)

  Runs on start when the last success is missing or older than 24 hours, then once a day, retrying
  after an hour rather than a full day when a tick fails (Manual-start PostgreSQL on the operator's
  machine); writes under a temporary name and renames only on success, prunes with BackupRetention,
  and records every run - success or failure - through IBackupLog, all TimeProvider-driven for the
  tests. Disabled by default in every test host (Backup:Enabled=false) so it never touches an E2E
  clone's real backup directory or the operator's own.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

---

### Task 8: The echo shows balances

**Files:**
- Modify: `src/Noof.Ledger.Application/Categorization/CategorizationContract.cs`
- Modify: `src/Noof.Ledger.Application/Chat/RecordEcho.cs`
- Modify: `src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs`
- Modify: `tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`
- Modify (full rewrite): `tests/Noof.Ledger.Host.Tests/RecordEchoTests.cs`
- Modify (extend one existing test, append two new ones): `tests/Noof.Ledger.Persistence.Tests/EfCategorizationStoreTests.cs`

**Interfaces:**
- Consumes (Task 1, already merged): `Transaction.Kind` (`TransactionKind`), `Entry`, `EntryRole`,
  `BalanceCheck` (`TransactionId`, `WalletId`, `Stated`, `ComputedBefore`), `LedgerDbContext.BalanceChecks`
  (`DbSet<BalanceCheck>`). Consumes (Task 4, already merged): `CategorizationSubject`'s trailing
  `Guid? WalletId = null` and `EfCategorizationStore.GetSubjectAsync`'s left-joined header query that
  already projects `t.WalletId`. Consumes (Task 3, already merged):
  `internal sealed class EfBalanceReadModel(LedgerDbContext db) : IBalanceReadModel` in
  `Noof.Ledger.Persistence.Balances`, specifically `Task<IReadOnlyList<Money>> BalanceOfAsync(Guid walletId, CancellationToken cancellationToken)`
  — one `Money` per currency the wallet has ever had a completed checkpoint or completed entry in,
  wallet's own currency first then ordinal, `[]` for an unknown or untouched wallet. This task calls
  it rather than querying the `wallet_balances` view directly, so the ordering and empty-case rules
  live in one place.
- Produces:
  - `public sealed record BalanceStatement(Money Stated, decimal ComputedBefore)` in
    `Noof.Ledger.Application.Categorization` (`CategorizationContract.cs`).
  - `CategorizationSubject` gains four optional parameters — `TransactionKind Kind = TransactionKind.Expense,
    CurrencyCode? WalletCurrency = null, IReadOnlyList<Money>? WalletBalances = null, BalanceStatement? Statement = null` —
    inserted between the existing `CaptureKind CaptureKind = CaptureKind.Text` and Task 4's trailing
    `Guid? WalletId = null`, which stays last. `EfCategorizationStore.GetSubjectAsync` is the only
    production constructor call and already passes `WalletId` by name (Task 4's edit), so it keeps
    compiling once this task's four fields are filled positionally ahead of that named argument.
  - `EfCategorizationStore.GetSubjectAsync` fills `Kind`, `WalletCurrency`, `WalletBalances`,
    `Statement` from the database, read after any write that changed them, so the echo shows exactly
    what is stored (same guarantee `RecordEcho`'s own file comment already states for line items).
  - `IRecordEcho.Compose`/`ComposeHeardNothing`/`ComposeCorrectionFailure` render the exact strings
    below. No signature change to `IRecordEcho` itself — `RecordActionHandler`, `CategorizationWorker`
    and `TranscriptionWorker` (all resolve `IRecordEcho` via DI and call `Compose(record)` with a
    `CategorizationSubject` they got from `store.GetSubjectAsync`) need no code change at all: the
    balance now travels inside the subject they already pass, which is exactly what "Cancel and
    Restore re-render the balance" (M11) needs, since `RecordActionHandler.HandleAsync`'s Cancel and
    Restore branches already re-fetch the subject and re-`Compose` it unconditionally.

**Echo strings (from the contract, exact):**

`B` = the wallet's balances, each `"{amount:0.00} {CUR}"`, wallet currency first then ordinal, joined
`", "`; when the wallet has no balance row yet, `"0.00 {WalletCurrency}"`.

- Expense, completed, lines > 0: `Recorded — {WalletName} · balance {B}` + `\n` + body.
- Income, completed, lines > 0: `Income — {WalletName} · balance {B}` + `\n` + body.
- Expense, completed, no lines: unchanged — `{WalletName}: found no spending here — nothing recorded.`, `[Edit]`.
- Income, completed, no lines: `{WalletName}: found no income here — nothing recorded.`, `[Edit]`.
- BalanceCheck, completed: `{WalletName}: balance was {before} {CUR}, you said {stated} {CUR} — adjusted {sign}{|diff|} {CUR}`
  (`diff = stated − before`, `sign` `+`/`-`), or `— matches` when `diff == 0`. Actions `[Cancel, Edit]`.
- Cancelled: `Cancelled — {WalletName} · balance {B}` + `\n` + body (a `BalanceCheck`'s body is
  `Statement: {stated} {CUR}`). Actions `[Restore]`.
- Any line whose currency differs from `WalletCurrency` adds the final body line
  `Not in the wallet's currency — no conversion yet.`
- Captured / Failed / voice-in-progress lines: unchanged from Phase 3.

- [ ] **Step 1: `BalanceStatement` and the widened `CategorizationSubject`**

`src/Noof.Ledger.Application/Categorization/CategorizationContract.cs` — by the time this task runs,
Task 1 and Task 4 have already landed `CategorizationSubject` with a trailing `Guid? WalletId = null`
(Task 4's own contract correction — see this plan's header). Modify the record so this task's four
new fields land **before** `WalletId`, keeping it last, and add `BalanceStatement` immediately after:

Before (the shape Task 4 leaves):
```csharp
public sealed record CategorizationSubject(
    Guid TransactionId,
    string RawText,
    long TelegramChatId,
    int? BotMessageId,
    string WalletName,
    TransactionStatus Status,
    DateOnly SentOn,
    DateOnly OccurredOn,
    IReadOnlyList<RecordedLine> Lines,
    CaptureKind CaptureKind = CaptureKind.Text,
    Guid? WalletId = null);
```

After:
```csharp
public sealed record CategorizationSubject(
    Guid TransactionId,
    string RawText,
    long TelegramChatId,
    int? BotMessageId,
    string WalletName,
    TransactionStatus Status,
    DateOnly SentOn,
    DateOnly OccurredOn,
    IReadOnlyList<RecordedLine> Lines,
    CaptureKind CaptureKind = CaptureKind.Text,
    TransactionKind Kind = TransactionKind.Expense,
    CurrencyCode? WalletCurrency = null,
    IReadOnlyList<Money>? WalletBalances = null,
    BalanceStatement? Statement = null,
    Guid? WalletId = null);

// The stated amount of a balance-check transaction, and what the app had computed for that wallet
// and currency just before it - history for the echo (M6), never a figure anything reads back as
// the current balance.
public sealed record BalanceStatement(Money Stated, decimal ComputedBefore);
```

`TransactionKind` is already in scope in this file through `using Noof.Ledger.Domain;` at its top
(unchanged from today).

`tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs` — add `"BalanceStatement"` to the
`Noof.Ledger.Application` array:

Before:
```csharp
            "CategorizationSubject", "RecordedLine", "CategorizationOutcome", "CategoryEntry", "MerchantAliasEntry", "ICategorizationStore",
```

After:
```csharp
            "CategorizationSubject", "RecordedLine", "CategorizationOutcome", "CategoryEntry", "MerchantAliasEntry", "ICategorizationStore",
            "BalanceStatement",
```

Run: `dotnet test tests/Noof.Ledger.Architecture.Tests --filter Public_types_are_exactly_the_allowed_set`.
Expected FAIL, before this edit: fails naming `BalanceStatement` as an unexpected extra public type
(compile only succeeds once `BalanceStatement` exists from the code change above, at which point the
test fails on content, not on a build error — do the code change first, run the test, see it fail
on the missing allowlist entry, then add the entry).
Expected PASS, after: green.

- [ ] **Step 2: `GetSubjectAsync` fills the four new fields**

`tests/Noof.Ledger.Persistence.Tests/EfCategorizationStoreTests.cs` — first, extend Task 1's existing
`GetSubjectAsync_reads_a_capture_that_has_no_wallet_yet_with_an_empty_wallet_name` by appending two
lines right after `subject.TelegramChatId.Should().Be(777);`:

```csharp
        subject.WalletCurrency.Should().BeNull("no wallet has been chosen yet, the same reason WalletName reads empty");
        subject.WalletBalances.Should().BeEmpty();
```

That exercises the null-wallet branch of the `WalletCurrency` projection (`w == null ? (CurrencyCode?)null : w.Currency`),
which nothing else in this task's new tests reaches. Then append these two new tests inside the
`EfCategorizationStoreTests` class:

```csharp
    [Fact]
    public async Task GetSubjectAsync_fills_kind_wallet_currency_balance_and_the_statement_for_a_balance_check()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            Name = "Raiffeisen RSD",
            Currency = CurrencyCode.Rsd,
            Aliases = [],
            IsDefaultForCurrency = false,
            Archived = false,
            CreatedAt = Clock.GetUtcNow(),
        };
        var transaction = NewTransaction(wallet.Id);
        transaction.Kind = TransactionKind.BalanceCheck;
        transaction.Status = TransactionStatus.Completed;
        db.AddRange(wallet, transaction);
        db.BalanceChecks.Add(new BalanceCheck
        {
            TransactionId = transaction.Id,
            WalletId = wallet.Id,
            Stated = new Money(45000m, CurrencyCode.Rsd),
            ComputedBefore = 44800m,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db, Clock);

        var subject = await store.GetSubjectAsync(transaction.Id, TestContext.Current.CancellationToken);

        subject.Should().NotBeNull();
        subject!.Kind.Should().Be(TransactionKind.BalanceCheck);
        subject.WalletCurrency.Should().Be(CurrencyCode.Rsd);
        subject.Statement.Should().Be(new BalanceStatement(new Money(45000m, CurrencyCode.Rsd), 44800m));
        subject.WalletBalances.Should().Equal(new Money(45000m, CurrencyCode.Rsd));
        subject.WalletId.Should().Be(wallet.Id, "Task 4's keep-the-wallet correction rule reads this field back for every correction, and a regression here would silently move money between wallets");
    }

    [Fact]
    public async Task GetSubjectAsync_reports_no_balance_row_as_an_empty_list()
    {
        await using var db = await fixture.CreateContextAsync();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            Name = "Fresh EUR wallet",
            Currency = CurrencyCode.Eur,
            Aliases = [],
            IsDefaultForCurrency = false,
            Archived = false,
            CreatedAt = Clock.GetUtcNow(),
        };
        var transaction = NewTransaction(wallet.Id);
        db.AddRange(wallet, transaction);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfCategorizationStore(db, Clock);

        var subject = await store.GetSubjectAsync(transaction.Id, TestContext.Current.CancellationToken);

        subject.Should().NotBeNull();
        subject!.Kind.Should().Be(TransactionKind.Expense);
        subject.WalletCurrency.Should().Be(CurrencyCode.Eur);
        subject.WalletBalances.Should().BeEmpty("no checkpoint or entry has ever touched this wallet yet");
        subject.Statement.Should().BeNull();
    }
```

Run: `dotnet test tests/Noof.Ledger.Persistence.Tests --filter EfCategorizationStoreTests`.
Expected: the two new tests compile (Step 1 already added the four members to the record with
defaults) but FAIL on their assertions, because `GetSubjectAsync` never sets them yet — they read
back as the record's own defaults: `subject.Kind.Should().Be(TransactionKind.BalanceCheck)` fails
because it is actually `TransactionKind.Expense`. The two lines just appended to
`GetSubjectAsync_reads_a_capture_that_has_no_wallet_yet_with_an_empty_wallet_name` PASS already —
`WalletCurrency`/`WalletBalances` default to `null`/empty regardless of whether `GetSubjectAsync` fills
them, since this transaction has no wallet either way — they exist to pin that branch once the method
below does fill them for good.

`src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs` — replace `GetSubjectAsync`
in full. Add `using Noof.Ledger.Persistence.Balances;` to this file's usings (Task 3's
`EfBalanceReadModel` lives there and is `internal`, visible within this assembly).

Before (the method exactly as Task 1 and Task 4 leave it — a left-joined `Wallets`,
`TelegramChatId ?? 0`, and Task 4's `WalletId: header.WalletId`; open the real file first and confirm
this matches before pasting over it):
```csharp
    public async Task<CategorizationSubject?> GetSubjectAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var header = await (
            from t in db.Transactions.AsNoTracking()
            where t.Id == transactionId
            join w in db.Wallets.AsNoTracking() on t.WalletId equals (Guid?)w.Id into walletJoin
            from w in walletJoin.DefaultIfEmpty()
            select new
            {
                t.Id, t.RawText, t.TelegramChatId, t.BotMessageId, WalletName = w == null ? string.Empty : w.Name,
                t.Status, t.OccurredAt, t.TimeZoneId, t.OccurredOn, t.CaptureKind, t.WalletId,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (header is null)
            return null;

        // line_items has no ordinal column, so the order is made deterministic rather than left to the heap.
        var lines = await (
            from li in db.LineItems.AsNoTracking()
            where li.TransactionId == transactionId
            join c in db.Categories.AsNoTracking() on li.CategoryId equals c.Id into categoryJoin
            from c in categoryJoin.DefaultIfEmpty()
            join m in db.Merchants.AsNoTracking() on li.MerchantId equals m.Id into merchantJoin
            from m in merchantJoin.DefaultIfEmpty()
            orderby li.Description
            select new RecordedLine(
                li.Description,
                li.Amount,
                c == null ? null : c.Slug,
                c == null ? null : c.NameEn,
                m == null ? null : m.DisplayName))
            .ToListAsync(cancellationToken);

        // A voice capture has no text until its transcript arrives, and none at all when nothing was heard;
        // the pipeline and the echo read that as empty, which is what it is. Only a Manual record has no chat,
        // and nothing categorises or echoes one, so 0 stands in for it.
        return new CategorizationSubject(
            header.Id, header.RawText ?? string.Empty, header.TelegramChatId ?? 0, header.BotMessageId, header.WalletName,
            header.Status, ZonedClock.LocalDate(header.OccurredAt, header.TimeZoneId), header.OccurredOn, lines,
            header.CaptureKind, WalletId: header.WalletId);
    }
```

After (adds `WalletCurrency` and `Kind` to the header projection, and fills the four new fields —
balances come from Task 3's `EfBalanceReadModel.BalanceOfAsync`, which already orders the wallet's own
currency first and returns `[]` for a wallet with no row, so nothing here reimplements that query):
```csharp
    public async Task<CategorizationSubject?> GetSubjectAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var header = await (
            from t in db.Transactions.AsNoTracking()
            where t.Id == transactionId
            join w in db.Wallets.AsNoTracking() on t.WalletId equals (Guid?)w.Id into walletJoin
            from w in walletJoin.DefaultIfEmpty()
            select new
            {
                t.Id,
                t.RawText,
                t.TelegramChatId,
                t.BotMessageId,
                WalletName = w == null ? string.Empty : w.Name,
                WalletCurrency = w == null ? (CurrencyCode?)null : w.Currency,
                t.Status,
                t.OccurredAt,
                t.TimeZoneId,
                t.OccurredOn,
                t.CaptureKind,
                t.Kind,
                t.WalletId,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (header is null)
            return null;

        // line_items has no ordinal column, so the order is made deterministic rather than left to the heap.
        var lines = await (
            from li in db.LineItems.AsNoTracking()
            where li.TransactionId == transactionId
            join c in db.Categories.AsNoTracking() on li.CategoryId equals c.Id into categoryJoin
            from c in categoryJoin.DefaultIfEmpty()
            join m in db.Merchants.AsNoTracking() on li.MerchantId equals m.Id into merchantJoin
            from m in merchantJoin.DefaultIfEmpty()
            orderby li.Description
            select new RecordedLine(
                li.Description,
                li.Amount,
                c == null ? null : c.Slug,
                c == null ? null : c.NameEn,
                m == null ? null : m.DisplayName))
            .ToListAsync(cancellationToken);

        var balances = header.WalletId is { } walletId
            ? await new EfBalanceReadModel(db).BalanceOfAsync(walletId, cancellationToken)
            : [];

        var statement = header.Kind == TransactionKind.BalanceCheck
            ? await db.BalanceChecks.AsNoTracking()
                .Where(bc => bc.TransactionId == transactionId)
                .Select(bc => new BalanceStatement(bc.Stated, bc.ComputedBefore))
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        // A voice capture has no text until its transcript arrives, and none at all when nothing was heard;
        // the pipeline and the echo read that as empty, which is what it is. Only a Manual record has no chat,
        // and nothing categorises or echoes one, so 0 stands in for it.
        return new CategorizationSubject(
            header.Id, header.RawText ?? string.Empty, header.TelegramChatId ?? 0, header.BotMessageId, header.WalletName,
            header.Status, ZonedClock.LocalDate(header.OccurredAt, header.TimeZoneId), header.OccurredOn, lines,
            header.CaptureKind, header.Kind, header.WalletCurrency, balances, statement, WalletId: header.WalletId);
    }
```

Run: `dotnet test tests/Noof.Ledger.Persistence.Tests --filter EfCategorizationStoreTests`.
Expected PASS: every test in the class, including the two new ones, the two lines just added to
`GetSubjectAsync_reads_a_capture_that_has_no_wallet_yet_with_an_empty_wallet_name`, and the
pre-existing `GetSubjectAsync_returns_the_record_as_stored_with_its_lines` (whose seeded wallet has no
checkpoint or entry, so it now also carries `WalletBalances: []`, `Kind: TransactionKind.Expense`,
`WalletCurrency: CurrencyCode.Eur`, `Statement: null` — none of which that test asserts on, so it
keeps passing unchanged).

- [ ] **Step 3: `RecordEcho` renders the balance**

Replace `tests/Noof.Ledger.Host.Tests/RecordEchoTests.cs` in full — nearly every existing assertion
changes because every `Recorded —`/`Income —`/`Cancelled —` line now carries a balance segment:

```csharp
using System.Globalization;
using AwesomeAssertions;
using Noof.Ledger.Application.Categorization;
using Noof.Ledger.Application.Chat;
using Noof.Ledger.Domain;

namespace Noof.Ledger.Host.Tests;

public class RecordEchoTests
{
    static readonly IRecordEcho Echo = new RecordEcho();
    static readonly DateOnly Sent = new(2026, 9, 22);

    static RecordedLine Coffee => new("кофе", new Money(250m, CurrencyCode.Rsd), "food-drink", "Food & Drink", null);

    static CategorizationSubject Record(
        TransactionStatus status = TransactionStatus.Completed,
        DateOnly? occurredOn = null,
        IReadOnlyList<RecordedLine>? lines = null,
        TransactionKind kind = TransactionKind.Expense,
        CurrencyCode? walletCurrency = null,
        IReadOnlyList<Money>? walletBalances = null,
        BalanceStatement? statement = null) =>
        new(Guid.NewGuid(), "raw", 111L, 42, "Cash", status, Sent, occurredOn ?? Sent, lines ?? [Coffee],
            CaptureKind.Text, kind, walletCurrency ?? CurrencyCode.Rsd, walletBalances, statement);

    [Fact]
    public void A_recorded_line_is_echoed_with_its_balance_total_and_the_cancel_and_edit_buttons()
    {
        var echo = Echo.Compose(Record());

        echo.Text.Should().Be("Recorded — Cash · balance 0.00 RSD\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void The_balance_line_reads_every_currency_the_wallet_holds_wallet_currency_first()
    {
        var balances = new[] { new Money(45230.50m, CurrencyCode.Rsd), new Money(20m, CurrencyCode.Eur) };

        Echo.Compose(Record(walletBalances: balances)).Text
            .Should().StartWith("Recorded — Cash · balance 45230.50 RSD, 20.00 EUR\n");
    }

    [Fact]
    public void A_merchant_follows_the_category()
    {
        var line = new RecordedLine("продукты", new Money(1000m, CurrencyCode.Eur), "groceries", "Groceries", "Lidl");

        Echo.Compose(Record(lines: [line])).Text.Should().Contain("• продукты — 1000.00 EUR · Groceries · Lidl");
    }

    [Fact]
    public void Totals_are_per_currency_and_ordered_by_code()
    {
        var lines = new[]
        {
            Coffee,
            new RecordedLine("такси", new Money(1000m, CurrencyCode.Eur), "transport", "Transport", null),
            new RecordedLine("хлеб", new Money(100m, CurrencyCode.Rsd), "groceries", "Groceries", null),
        };

        Echo.Compose(Record(lines: lines)).Text.Should().EndWith("Total: 1000.00 EUR, 350.00 RSD");
    }

    [Fact]
    public void A_record_dated_to_another_day_says_which_day()
    {
        Echo.Compose(Record(occurredOn: new DateOnly(2026, 9, 21))).Text
            .Should().StartWith("Recorded — Cash · balance 0.00 RSD\nDate: 21.09.2026\n• кофе");
    }

    [Fact]
    public void A_record_dated_to_the_send_day_names_no_date()
    {
        Echo.Compose(Record()).Text.Should().NotContain("Date:");
    }

    [Fact]
    public void A_cancelled_record_shows_its_balance_and_offers_only_restore()
    {
        var echo = Echo.Compose(Record(TransactionStatus.Cancelled, walletBalances: [new Money(45230m, CurrencyCode.Rsd)]));

        echo.Text.Should().Be("Cancelled — Cash · balance 45230.00 RSD\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Restore);
    }

    [Fact]
    public void A_read_that_found_no_spending_says_so_and_offers_only_edit()
    {
        var echo = Echo.Compose(Record(lines: []));

        echo.Text.Should().Be("Cash: found no spending here — nothing recorded.");
        echo.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void An_income_record_is_echoed_with_the_income_prefix_and_its_balance()
    {
        var line = new RecordedLine("зарплата", new Money(2000m, CurrencyCode.Eur), "salary", "Salary", null);
        var echo = Echo.Compose(Record(
            kind: TransactionKind.Income, lines: [line], walletCurrency: CurrencyCode.Eur,
            walletBalances: [new Money(3200m, CurrencyCode.Eur)]));

        echo.Text.Should().Be("Income — Cash · balance 3200.00 EUR\n• зарплата — 2000.00 EUR · Salary\n\nTotal: 2000.00 EUR");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void An_income_record_with_no_lines_says_so_and_offers_only_edit()
    {
        var echo = Echo.Compose(Record(kind: TransactionKind.Income, lines: []));

        echo.Text.Should().Be("Cash: found no income here — nothing recorded.");
        echo.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void A_balance_statement_that_matches_says_so()
    {
        var statement = new BalanceStatement(new Money(44800.00m, CurrencyCode.Rsd), 44800.00m);
        var echo = Echo.Compose(Record(kind: TransactionKind.BalanceCheck, lines: [], statement: statement));

        echo.Text.Should().Be("Cash: balance was 44800.00 RSD, you said 44800.00 RSD — matches");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_balance_statement_above_the_computed_balance_says_adjusted_up()
    {
        var statement = new BalanceStatement(new Money(45000.00m, CurrencyCode.Rsd), 44800.00m);
        var echo = Echo.Compose(Record(kind: TransactionKind.BalanceCheck, lines: [], statement: statement));

        echo.Text.Should().Be("Cash: balance was 44800.00 RSD, you said 45000.00 RSD — adjusted +200.00 RSD");
    }

    [Fact]
    public void A_balance_statement_below_the_computed_balance_says_adjusted_down()
    {
        var statement = new BalanceStatement(new Money(44500.00m, CurrencyCode.Rsd), 44800.00m);
        var echo = Echo.Compose(Record(kind: TransactionKind.BalanceCheck, lines: [], statement: statement));

        echo.Text.Should().Be("Cash: balance was 44800.00 RSD, you said 44500.00 RSD — adjusted -300.00 RSD");
    }

    [Fact]
    public void A_cancelled_balance_statement_shows_the_stated_amount_as_its_body()
    {
        var statement = new BalanceStatement(new Money(45000.00m, CurrencyCode.Rsd), 44800.00m);
        var echo = Echo.Compose(Record(
            TransactionStatus.Cancelled, kind: TransactionKind.BalanceCheck, lines: [], statement: statement));

        echo.Text.Should().Be("Cancelled — Cash · balance 0.00 RSD\nStatement: 45000.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Restore);
    }

    [Fact]
    public void A_line_in_a_currency_other_than_the_wallets_warns_that_it_is_not_converted()
    {
        var line = new RecordedLine("такси", new Money(20m, CurrencyCode.Eur), "transport", "Transport", null);

        Echo.Compose(Record(lines: [Coffee, line])).Text
            .Should().EndWith("Not in the wallet's currency — no conversion yet.");
    }

    [Fact]
    public void A_line_in_the_wallets_own_currency_gets_no_conversion_warning()
    {
        Echo.Compose(Record()).Text.Should().NotContain("no conversion yet");
    }

    [Fact]
    public void A_failed_record_is_the_failure_echo()
    {
        Echo.Compose(Record(TransactionStatus.Failed, lines: [])).Should().Be(Echo.Failure);
        Echo.Failure.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void A_record_still_being_read_is_the_acknowledgement_without_buttons()
    {
        var echo = Echo.Compose(Record(TransactionStatus.Captured, lines: []));

        echo.Text.Should().Be(Echo.Acknowledgement);
        echo.Actions.Should().BeEmpty();
    }

    [Fact]
    public void A_failed_correction_says_so_above_the_unchanged_record()
    {
        var echo = Echo.ComposeCorrectionFailure(Record());

        echo.Text.Should().Be(
            "Could not apply that correction — the record is unchanged.\n\n"
            + "Recorded — Cash · balance 0.00 RSD\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_line_with_no_category_says_so()
    {
        var line = new RecordedLine("штраф", new Money(5m, CurrencyCode.Eur), null, null, null);

        Echo.Compose(Record(lines: [line])).Text.Should().Contain("• штраф — 5.00 EUR · uncategorised");
    }

    [Fact]
    public void Amounts_render_the_same_under_a_Russian_machine_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            Echo.Compose(Record()).Text.Should().Contain("250.00 RSD");
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    static CategorizationSubject Voice(
        string heard, TransactionStatus status = TransactionStatus.Completed, IReadOnlyList<RecordedLine>? lines = null) =>
        new(Guid.NewGuid(), heard, 111L, 42, "Cash", status, Sent, Sent, lines ?? [Coffee], CaptureKind.Voice,
            TransactionKind.Expense, CurrencyCode.Rsd, WalletBalances: null, Statement: null);

    [Fact]
    public void Transcribing_is_the_voice_notes_acknowledgement()
    {
        Echo.Transcribing.Should().Be("🎤 Transcribing…");
    }

    [Fact]
    public void A_voice_record_starts_with_what_was_heard()
    {
        var echo = Echo.Compose(Voice("кофе двести пятьдесят"));

        echo.Text.Should().Be(
            "🎤 \"кофе двести пятьдесят\"\nRecorded — Cash · balance 0.00 RSD\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }

    [Fact]
    public void A_typed_record_has_no_heard_line()
    {
        Echo.Compose(Record()).Text.Should().StartWith("Recorded — Cash");
    }

    [Fact]
    public void A_voice_record_with_no_transcript_shows_no_heard_line()
    {
        Echo.Compose(Voice(heard: "")).Text.Should().StartWith("Recorded — Cash");
    }

    [Fact]
    public void A_voice_note_waiting_for_its_transcript_says_it_is_transcribing()
    {
        var echo = Echo.Compose(Voice(heard: "", status: TransactionStatus.Captured, lines: []));

        echo.Text.Should().Be("🎤 Transcribing…");
        echo.Actions.Should().BeEmpty();
    }

    [Fact]
    public void A_voice_note_being_read_shows_what_was_heard_above_the_acknowledgement()
    {
        Echo.Compose(Voice("кофе 250", status: TransactionStatus.Captured, lines: [])).Text
            .Should().Be("🎤 \"кофе 250\"\nRecording…");
    }

    [Fact]
    public void A_cancelled_voice_record_keeps_what_was_heard()
    {
        Echo.Compose(Voice("кофе 250", status: TransactionStatus.Cancelled)).Text
            .Should().StartWith("🎤 \"кофе 250\"\nCancelled — Cash");
    }

    [Fact]
    public void Heard_nothing_says_so_and_offers_edit()
    {
        Echo.HeardNothing.Text.Should().Be("Heard nothing in that voice note.");
        Echo.HeardNothing.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void A_note_that_could_not_be_transcribed_says_so_and_offers_edit()
    {
        Echo.TranscriptionFailure.Text.Should().Be("Couldn't transcribe that voice note.");
        Echo.TranscriptionFailure.Actions.Should().Equal(RecordAction.Edit);
    }

    [Fact]
    public void Hearing_nothing_in_a_spoken_correction_shows_the_record_unchanged_below()
    {
        var echo = Echo.ComposeHeardNothing(Record());

        echo.Text.Should().Be(
            "Heard nothing in that voice note.\n\nRecorded — Cash · balance 0.00 RSD\n• кофе — 250.00 RSD · Food & Drink\n\nTotal: 250.00 RSD");
        echo.Actions.Should().Equal(RecordAction.Cancel, RecordAction.Edit);
    }
}
```

Run: `dotnet test tests/Noof.Ledger.Host.Tests --filter RecordEchoTests`.
Expected FAIL: every test whose text includes a `Recorded —`, `Income —` or `Cancelled —` prefix
fails, because today's `RecordEcho.Compose` never renders a balance segment and has no `Income —`
prefix or `BalanceCheck` branch at all (`An_income_record_is_echoed_with_the_income_prefix_and_its_balance`
and every `A_balance_statement_*` test additionally fail because `record.Kind` is read but `Compose`'s
switch never matches on it, falling through to the default expense branch and producing the wrong
text entirely, e.g. `Cash: found no spending here — nothing recorded.` for a `BalanceCheck` with
empty lines instead of the statement line).

`src/Noof.Ledger.Application/Chat/RecordEcho.cs` — replace `Compose` and `Body`, and add the three
new private helpers:

Before:
```csharp
    public EchoMessage Compose(CategorizationSubject record) => WithWhatWasHeard(record, record switch
    {
        { Status: TransactionStatus.Cancelled } =>
            new($"Cancelled — {record.WalletName}\n{Body(record)}".TrimEnd(), [RecordAction.Restore]),
        { Status: TransactionStatus.Failed } => Failure,
        { Status: TransactionStatus.Captured } => new(Waiting(record), []),
        { Lines.Count: 0 } =>
            new($"{record.WalletName}: found no spending here — nothing recorded.", [RecordAction.Edit]),
        _ => new($"Recorded — {record.WalletName}\n{Body(record)}", [RecordAction.Cancel, RecordAction.Edit]),
    });
```

After:
```csharp
    public EchoMessage Compose(CategorizationSubject record) => WithWhatWasHeard(record, record switch
    {
        { Status: TransactionStatus.Cancelled } =>
            new($"Cancelled — {record.WalletName} · balance {Balances(record)}\n{CancelledBody(record)}".TrimEnd(),
                [RecordAction.Restore]),
        { Status: TransactionStatus.Failed } => Failure,
        { Status: TransactionStatus.Captured } => new(Waiting(record), []),
        { Kind: TransactionKind.BalanceCheck, Status: TransactionStatus.Completed } =>
            new(StatementLine(record), [RecordAction.Cancel, RecordAction.Edit]),
        { Kind: TransactionKind.Income, Lines.Count: 0 } =>
            new($"{record.WalletName}: found no income here — nothing recorded.", [RecordAction.Edit]),
        { Lines.Count: 0 } =>
            new($"{record.WalletName}: found no spending here — nothing recorded.", [RecordAction.Edit]),
        { Kind: TransactionKind.Income } =>
            new($"Income — {record.WalletName} · balance {Balances(record)}\n{Body(record)}", [RecordAction.Cancel, RecordAction.Edit]),
        _ => new($"Recorded — {record.WalletName} · balance {Balances(record)}\n{Body(record)}", [RecordAction.Cancel, RecordAction.Edit]),
    });
```

Before:
```csharp
    static string Body(CategorizationSubject record)
    {
        List<string> lines = [];

        if (record.OccurredOn != record.SentOn)
            lines.Add($"Date: {record.OccurredOn.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}");

        lines.AddRange(record.Lines.Select(FormatLine));

        if (record.Lines.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"Total: {Totals(record.Lines)}");
        }

        return string.Join('\n', lines);
    }
```

After:
```csharp
    static string Body(CategorizationSubject record)
    {
        List<string> lines = [];

        if (record.OccurredOn != record.SentOn)
            lines.Add($"Date: {record.OccurredOn.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}");

        lines.AddRange(record.Lines.Select(FormatLine));

        if (record.Lines.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"Total: {Totals(record.Lines)}");
        }

        // M10: a spending in a currency other than its wallet's is not converted - visible as a
        // separate currency line in the balance, and flagged here so it never looks like an
        // oversight.
        if (record.WalletCurrency is { } currency && record.Lines.Any(line => line.Amount.Currency != currency))
            lines.Add("Not in the wallet's currency — no conversion yet.");

        return string.Join('\n', lines);
    }

    // A BalanceCheck's own body is the statement it recorded, not a line-item body - it has no lines
    // (the mapper discards a balance statement's Items, per the contract).
    static string CancelledBody(CategorizationSubject record) =>
        record is { Kind: TransactionKind.BalanceCheck, Statement: { } statement }
            ? $"Statement: {FormatAmount(statement.Stated.Amount)} {statement.Stated.Currency}"
            : Body(record);

    static string StatementLine(CategorizationSubject record)
    {
        // A Completed BalanceCheck always has a balance_checks row - the mapper and RewriteAsync both
        // guarantee it - but a null-forgiving `!` would turn a bug into a crashed Telegram edit rather
        // than a wrong-looking message, so a missing row degrades instead of throwing.
        if (record.Statement is not { } statement)
            return $"{record.WalletName}: balance statement recorded.";

        var currency = statement.Stated.Currency;
        var before = statement.ComputedBefore;
        var stated = statement.Stated.Amount;
        var diff = stated - before;
        var tail = diff == 0m
            ? "matches"
            : $"adjusted {(diff > 0 ? "+" : "-")}{FormatAmount(Math.Abs(diff))} {currency}";

        return $"{record.WalletName}: balance was {FormatAmount(before)} {currency}, you said {FormatAmount(stated)} {currency} — {tail}";
    }

    static string Balances(CategorizationSubject record)
    {
        if (record.WalletBalances is not { Count: > 0 } balances)
            return record.WalletCurrency is { } currency ? $"0.00 {currency}" : "0.00";

        return string.Join(", ", balances.Select(money => $"{FormatAmount(money.Amount)} {money.Currency}"));
    }
```

`RecordEcho.cs` already has `using Noof.Ledger.Domain;` at its top, which brings `TransactionKind`
into scope — no new `using` needed.

Run: `dotnet test tests/Noof.Ledger.Host.Tests --filter RecordEchoTests`.
Expected PASS: every test in the file, including the rewritten ones.

- [ ] **Step 4: Watch the currency-mismatch guard fail, then confirm it again**

Temporarily change the mismatch condition's `!=` to `==` in `Body`, run
`dotnet test tests/Noof.Ledger.Host.Tests --filter A_line_in_a_currency_other_than_the_wallets_warns_that_it_is_not_converted`,
confirm it fails (the warning line disappears because every line now has to *equal* the wallet
currency to trigger it, which the RSD `Coffee` line does, so the warning appears on the *matching*
test instead and the mismatch test's `EndWith` assertion fails). Revert to `!=` and re-run both this
test and `A_line_in_the_wallets_own_currency_gets_no_conversion_warning` to confirm both pass again.

- [ ] **Step 5: Full suite and commit**

```powershell
$lock = "C:\Users\noofs\AppData\Local\Temp\noof-suite.lock"
while (-not (New-Item -ItemType Directory -Path $lock -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2 }
try {
    dotnet test --solution NoofLedger.slnx
} finally {
    Remove-Item -Recurse -Force $lock
}
```

Expected PASS: every test green, including the rewritten `RecordEchoTests`, the two new
`EfCategorizationStoreTests` cases, and `PublicSurfaceTests`.

```
git add src/Noof.Ledger.Application/Categorization/CategorizationContract.cs src/Noof.Ledger.Application/Chat/RecordEcho.cs src/Noof.Ledger.Persistence/Categorization/EfCategorizationStore.cs tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs tests/Noof.Ledger.Host.Tests/RecordEchoTests.cs tests/Noof.Ledger.Persistence.Tests/EfCategorizationStoreTests.cs
git commit -m "$(cat <<'EOF'
The echo shows the wallet's balance after every recorded, cancelled
or restored transaction

CategorizationSubject now carries Kind, WalletCurrency, WalletBalances
and a BalanceStatement, filled by GetSubjectAsync from the
wallet_balances view and balance_checks - RecordActionHandler and the
workers need no change, since the balance travels inside the subject
they already re-fetch and re-Compose on Cancel and Restore.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 9: The dashboard shows balances and backup status

**Files:**
- Modify: `src/Noof.Ledger.Web/Components/Pages/Home.razor`
- Create: `tests/Noof.Ledger.E2E.Tests/DashboardBalancesTests.cs`

**Interfaces:**
- Consumes (Task 3, `Noof.Ledger.Application.Reporting`, already registered scoped in
  `AddNoofPersistence`):
  ```csharp
  public sealed record WalletBalance(Guid WalletId, string WalletName, CurrencyCode WalletCurrency, bool Archived, IReadOnlyList<Money> Balances, DateOnly? LastCheckedOn);
  public interface IBalanceReadModel
  {
      Task<IReadOnlyList<WalletBalance>> BalancesAsync(CancellationToken cancellationToken);
      Task<IReadOnlyList<Money>> BalanceOfAsync(Guid walletId, CancellationToken cancellationToken);
  }
  ```
- Consumes (Task 12, `Noof.Ledger.Application.Backup`, already registered scoped in
  `AddNoofPersistence`):
  ```csharp
  public sealed record BackupStatus(DateTimeOffset? LastSuccessAt, bool LastRunFailed, string? LastError);
  public interface IBackupLog
  {
      Task RecordAsync(BackupRunRecord run, CancellationToken cancellationToken);
      Task<BackupStatus> StatusAsync(CancellationToken cancellationToken);
  }
  ```
- Consumes (unchanged, already injected on `Home.razor`): `ISpendingReadModel`, `TimeProvider`
  (registered `AddSingleton(TimeProvider.System)` in `Program.cs`).
- Produces: nothing another task consumes. `Noof.Ledger.Web` is not in
  `PublicSurfaceTests.ScannedProjects`, so this task makes no change to `PublicSurfaceTests.Allowed`.

**Design decisions settled here (the contract leaves both open; read before Step 1):**

- **Archived wallets are hidden from the Balances card.** M11 says "Dashboard home: a Balances card
  — each *active* wallet…" — `IBalanceReadModel.BalancesAsync` deliberately returns every wallet
  including archived ones (its own contract says "every wallet"), so this page is the one place that
  filters `WalletBalance.Archived` out, both from the per-wallet rows and from the per-currency
  totals below them. An archived wallet's balance stays visible on `/wallets` (Task 7) and in its own
  transaction history — only the dashboard summary drops it, matching "hidden from capture, history
  kept" (M4).
- **Backup status text**, since the contract fixes only the element id (`backup-status`), not its
  exact wording — use the spec's own phrasing (B3: *"Last backup: 3 h ago"*), not an invented one:
  `"Last backup: never"` when `LastSuccessAt` is null and the last run did not fail;
  `"Last backup: failed"` when `LastRunFailed` is true (this takes priority over a stale
  `LastSuccessAt` — a failing daily job is worse news than an old success); otherwise
  `"Last backup: {N} {unit} ago"` with `{unit}` one of `min`/`h`/`d` depending on how long ago
  `LastSuccessAt` was, computed against `TimeProvider.GetUtcNow()` (never `DateTimeOffset.UtcNow`
  directly, so the Host.Tests fake clock and the live server use the same code path). The text
  colours amber (`Color.Warning`, which MudBlazor renders as CSS class `mud-warning-text`) when the
  last run failed, when nothing has ever succeeded, or when the last success is older than 36 hours —
  B3's own threshold.
- Both new queries are wrapped in the *same* `try`/`catch` as the existing `RecentAsync`/
  `ThisMonthAsync` calls in `OnInitializedAsync`, so `databaseUnavailable` gates the whole page
  exactly as it does today — a database that is down for spending is down for balances and backups
  too, and showing one section while hiding the other would say more than is actually known.

- [ ] **Step 1: The Balances card and per-currency totals**

`tests/Noof.Ledger.E2E.Tests/DashboardBalancesTests.cs`:

```csharp
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Noof.Ledger.Domain;
using Noof.Ledger.Persistence;

namespace Noof.Ledger.E2E.Tests;

public sealed class DashboardBalancesTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
{
    [Fact]
    public async Task The_balances_card_shows_a_wallets_balance_and_the_per_currency_total()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var walletId = Guid.NewGuid();
        var walletName = $"Wise EUR {Guid.NewGuid():N}";
        var transactionId = Guid.NewGuid();

        await using (var db = OpenDb())
        {
            db.Wallets.Add(new Wallet
            {
                Id = walletId,
                Name = walletName,
                Currency = CurrencyCode.Eur,
                Aliases = [],
                IsDefaultForCurrency = false,
                Archived = false,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Transactions.Add(new Transaction
            {
                Id = transactionId,
                WalletId = walletId,
                RawText = "Opening balance",
                CaptureKind = CaptureKind.Manual,
                Kind = TransactionKind.BalanceCheck,
                Status = TransactionStatus.Completed,
                TimeZoneId = "Europe/Belgrade",
                OccurredAt = DateTimeOffset.UtcNow,
                OccurredOn = ZonedClock.LocalDate(DateTimeOffset.UtcNow, "Europe/Belgrade"),
                TelegramChatId = null,
                TelegramMessageId = null,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.BalanceChecks.Add(new BalanceCheck
            {
                TransactionId = transactionId,
                WalletId = walletId,
                Stated = new Money(3200.00m, CurrencyCode.Eur),
                ComputedBefore = 0m,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var card = Page.Locator("#balances");
        await Expect(card).ToBeVisibleAsync();

        var row = Page.Locator($"#balance-{walletId}");
        await Expect(row).ToContainTextAsync(walletName);
        // FormatAmount uses "N2" + InvariantCulture (Global Constraints), which groups thousands.
        await Expect(row).ToContainTextAsync("3,200.00 EUR");

        var total = Page.Locator("#balance-total-EUR");
        await Expect(total).ToContainTextAsync("3,200.00");
    }

    [Fact]
    public async Task An_archived_wallet_is_left_out_of_the_balances_card_and_its_total()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var walletId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        await using (var db = OpenDb())
        {
            db.Wallets.Add(new Wallet
            {
                Id = walletId,
                Name = $"Retired KZT {Guid.NewGuid():N}",
                Currency = CurrencyCode.Kzt,
                Aliases = [],
                IsDefaultForCurrency = false,
                Archived = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Transactions.Add(new Transaction
            {
                Id = transactionId,
                WalletId = walletId,
                RawText = "Opening balance",
                CaptureKind = CaptureKind.Manual,
                Kind = TransactionKind.BalanceCheck,
                Status = TransactionStatus.Completed,
                TimeZoneId = "Europe/Belgrade",
                OccurredAt = DateTimeOffset.UtcNow,
                OccurredOn = ZonedClock.LocalDate(DateTimeOffset.UtcNow, "Europe/Belgrade"),
                TelegramChatId = null,
                TelegramMessageId = null,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.BalanceChecks.Add(new BalanceCheck
            {
                TransactionId = transactionId,
                WalletId = walletId,
                Stated = new Money(500000m, CurrencyCode.Kzt),
                ComputedBefore = 0m,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator($"#balance-{walletId}")).Not.ToBeVisibleAsync();
        await Expect(Page.Locator("#balance-total-KZT")).Not.ToBeVisibleAsync();
    }

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

Run: `dotnet test tests/Noof.Ledger.E2E.Tests --filter DashboardBalancesTests`.
Expected FAIL: both tests fail — `Page.Locator("#balances")` never appears (`ToBeVisibleAsync` times
out), because `Home.razor` has no Balances card yet.

`src/Noof.Ledger.Web/Components/Pages/Home.razor` — add two injections, right after the existing
`@inject ISpendingReadModel ReadModel`:

Before:
```razor
@using Noof.Ledger.Application.Reporting
@using Noof.Ledger.Domain
@inject ISpendingReadModel ReadModel
```

After:
```razor
@using Noof.Ledger.Application.Backup
@using Noof.Ledger.Application.Reporting
@using Noof.Ledger.Domain
@inject ISpendingReadModel ReadModel
@inject IBalanceReadModel BalanceReadModel
@inject IBackupLog BackupLog
@inject TimeProvider TimeProvider
```

Insert the Balances card right after the page's `<MudText HtmlTag="h1" ...>Ledger</MudText>` line and
before the `@if (databaseUnavailable)` block — the card sits inside the same `else` branch as the
month summary, so it only renders once the database has answered:

Before:
```razor
else
{
    <MudText Typo="Typo.body2" Class="mud-text-secondary mb-6">
        This month, from @summaryFirstDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    </MudText>
```

After:
```razor
else
{
    <MudPaper id="balances" Outlined="true" Elevation="0" Class="pa-5 mb-8">
        <MudText HtmlTag="h2" Typo="Typo.h6" Class="mb-3">Balances</MudText>

        @if (activeBalances.Count == 0)
        {
            <MudText Typo="Typo.body2" Class="mud-text-secondary">No active wallets yet.</MudText>
        }
        else
        {
            @* noof-balance-row is a plain layout hook, not yet defined in the app stylesheet -
               harmless if unstyled, and no test asserts on its CSS. *@
            @foreach (var wallet in activeBalances)
            {
                <div id="@($"balance-{wallet.WalletId}")" class="noof-balance-row mb-2">
                    <MudText Typo="Typo.body1">@wallet.WalletName</MudText>
                    <MudText Typo="Typo.body1" Class="noof-amount">@FormatBalances(wallet.Balances)</MudText>
                    <MudText Typo="Typo.caption" Class="mud-text-secondary">
                        @(wallet.LastCheckedOn is { } checkedOn
                            ? $"Reconciled {checkedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"
                            : "Not yet reconciled")
                    </MudText>
                </div>
            }

            @if (currencyTotals.Count > 0)
            {
                <MudDivider Class="my-3" />

                @foreach (var total in currencyTotals)
                {
                    <MudText id="@($"balance-total-{total.Currency}")" Typo="Typo.subtitle2">
                        Total @total.Currency: @FormatAmount(total.Amount)
                    </MudText>
                }
            }
        }
    </MudPaper>

    <MudText id="backup-status" Typo="Typo.caption" Class="mb-8"
             Color="@(BackupIsStale() ? Color.Warning : Color.Default)">
        @DescribeBackup()
    </MudText>

    <MudText Typo="Typo.body2" Class="mud-text-secondary mb-6">
        This month, from @summaryFirstDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    </MudText>
```

Add the new state, the `CurrencyTotal` record and the load in `OnInitializedAsync`:

Before:
```csharp
@code {
    IReadOnlyList<RecentTransaction> recent = [];
    IReadOnlyList<CurrencyGroup> currencyGroups = [];
    DateOnly summaryFirstDay;
    bool databaseUnavailable;

    readonly CancellationTokenSource cancellation = new();
```

After:
```csharp
@code {
    IReadOnlyList<RecentTransaction> recent = [];
    IReadOnlyList<CurrencyGroup> currencyGroups = [];
    IReadOnlyList<WalletBalance> activeBalances = [];
    IReadOnlyList<CurrencyTotal> currencyTotals = [];
    BackupStatus? backupStatus;
    DateOnly summaryFirstDay;
    bool databaseUnavailable;

    readonly CancellationTokenSource cancellation = new();

    sealed record CurrencyTotal(string Currency, decimal Amount);
```

Before:
```csharp
    protected override async Task OnInitializedAsync()
    {
        try
        {
            recent = await ReadModel.RecentAsync(20, cancellation.Token);

            var summary = await ReadModel.ThisMonthAsync(cancellation.Token);
            summaryFirstDay = summary.FirstDay;
            currencyGroups =
            [
                .. summary.Totals
                    .GroupBy(total => total.Currency.Value)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new CurrencyGroup(group.Key,
                    [
                        .. group
                            .OrderByDescending(total => total.Amount)
                            .ThenBy(total => total.CategoryName, StringComparer.Ordinal),
                    ])),
            ];
        }
```

After:
```csharp
    protected override async Task OnInitializedAsync()
    {
        try
        {
            recent = await ReadModel.RecentAsync(20, cancellation.Token);

            var summary = await ReadModel.ThisMonthAsync(cancellation.Token);
            summaryFirstDay = summary.FirstDay;
            currencyGroups =
            [
                .. summary.Totals
                    .GroupBy(total => total.Currency.Value)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new CurrencyGroup(group.Key,
                    [
                        .. group
                            .OrderByDescending(total => total.Amount)
                            .ThenBy(total => total.CategoryName, StringComparer.Ordinal),
                    ])),
            ];

            var allBalances = await BalanceReadModel.BalancesAsync(cancellation.Token);
            activeBalances =
            [
                .. allBalances
                    .Where(wallet => !wallet.Archived)
                    .OrderBy(wallet => wallet.WalletName, StringComparer.Ordinal),
            ];
            currencyTotals =
            [
                .. activeBalances
                    .SelectMany(wallet => wallet.Balances)
                    .GroupBy(money => money.Currency.Value)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new CurrencyTotal(group.Key, group.Sum(money => money.Amount))),
            ];

            backupStatus = await BackupLog.StatusAsync(cancellation.Token);
        }
```

Add the four new helpers, next to the existing `FormatAmount`/`FormatMoney`:

Before:
```csharp
    static string FormatAmount(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture);

    static string FormatMoney(Money money) => $"{FormatAmount(money.Amount)} {money.Currency}";
```

After:
```csharp
    static string FormatAmount(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture);

    static string FormatMoney(Money money) => $"{FormatAmount(money.Amount)} {money.Currency}";

    static string FormatBalances(IReadOnlyList<Money> balances) =>
        balances.Count == 0 ? "—" : string.Join(", ", balances.Select(FormatMoney));

    string DescribeBackup() => backupStatus switch
    {
        null => "Last backup: unavailable",
        { LastRunFailed: true } => "Last backup: failed",
        { LastSuccessAt: null } => "Last backup: never",
        { LastSuccessAt: { } at } => $"Last backup: {FormatAgo(TimeProvider.GetUtcNow() - at)} ago",
    };

    static string FormatAgo(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes} min",
        { TotalDays: < 1 } => $"{(int)elapsed.TotalHours} h",
        _ => $"{(int)elapsed.TotalDays} d",
    };

    // B3's own staleness threshold: amber past 36 hours since the last success, or on any failed run,
    // or when nothing has ever succeeded.
    bool BackupIsStale() => backupStatus is null or { LastRunFailed: true } or { LastSuccessAt: null }
        || backupStatus.LastSuccessAt is { } at && TimeProvider.GetUtcNow() - at > TimeSpan.FromHours(36);
```

Run: `dotnet test tests/Noof.Ledger.E2E.Tests --filter DashboardBalancesTests`.
Expected PASS: both tests.

- [ ] **Step 2: The backup status line**

`tests/Noof.Ledger.E2E.Tests/DashboardBalancesTests.cs` — append inside the same class:

```csharp
    [Fact]
    public async Task Backup_status_says_never_run_when_no_backup_has_ever_completed()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        // The three backup-status tests share one clone database and xunit does not guarantee their
        // order, so each one clears backup_runs first rather than assuming it starts empty.
        await using (var db = OpenDb())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM backup_runs", TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(Page.Locator("#backup-status")).ToContainTextAsync("Last backup: never");
    }

    [Fact]
    public async Task Backup_status_reports_how_long_ago_the_last_success_was()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var startedAt = DateTimeOffset.UtcNow.AddHours(-3).AddMinutes(-5);
        var finishedAt = DateTimeOffset.UtcNow.AddHours(-3);

        await using (var db = OpenDb())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM backup_runs", TestContext.Current.CancellationToken);
            db.BackupRuns.Add(new BackupRun
            {
                Id = Guid.NewGuid(),
                StartedAt = startedAt,
                FinishedAt = finishedAt,
                Succeeded = true,
                FileName = "noof_ledger-20260921-030000.dump",
                SizeBytes = 12_345,
                Error = null,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var status = Page.Locator("#backup-status");
        await Expect(status).ToContainTextAsync("Last backup: 3 h ago");
        // 3 hours is under B3's 36-hour staleness threshold - the text stays the default colour.
        await Expect(status).Not.ToHaveClassAsync(new Regex("mud-warning-text"));
    }

    [Fact]
    public async Task Backup_status_reports_a_failed_run_even_after_an_older_success()
    {
        if (fixture.DatabaseUnavailable)
            Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

        var oldSuccess = DateTimeOffset.UtcNow.AddDays(-2);
        var recentFailure = DateTimeOffset.UtcNow.AddMinutes(-10);

        await using (var db = OpenDb())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM backup_runs", TestContext.Current.CancellationToken);
            db.BackupRuns.Add(new BackupRun
            {
                Id = Guid.NewGuid(),
                StartedAt = oldSuccess.AddMinutes(-1),
                FinishedAt = oldSuccess,
                Succeeded = true,
                FileName = "noof_ledger-old.dump",
                SizeBytes = 1000,
                Error = null,
            });
            db.BackupRuns.Add(new BackupRun
            {
                Id = Guid.NewGuid(),
                StartedAt = recentFailure.AddMinutes(-1),
                FinishedAt = recentFailure,
                Succeeded = false,
                FileName = null,
                SizeBytes = null,
                Error = "pg_dump exited with code 1",
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await SignInAsync();
        await Page.GotoAsync(fixture.BaseUrl + "/");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var status = Page.Locator("#backup-status");
        await Expect(status).ToContainTextAsync("Last backup: failed");
        await Expect(status).ToHaveClassAsync(new Regex("mud-warning-text"));
    }
```

Run: `dotnet test tests/Noof.Ledger.E2E.Tests --filter DashboardBalancesTests`.
Expected FAIL, before Step 1's `Home.razor` edit exists: all three time out on `#backup-status` never
appearing. Since Step 1 above already added `#backup-status` and its `DescribeBackup()`/`BackupIsStale()`
logic in the same edit, these three should already pass once Step 1 is applied — this step exists to
add the missing-data (`never`), success-ago-with-colour and failure-priority-with-colour coverage that
Step 1's own test did not: run them immediately after Step 1's implementation lands and confirm all
three PASS with no further code change. This also depends on Task 13's `Backup:Enabled=false` landing
in `CookieModeHostFixture` (Task 13 finding 1) — without it, the real `BackupWorker` may have already
written its own row into `backup_runs` by the time these tests run, which the `DELETE FROM backup_runs`
at the top of each test now neutralises regardless. If any of the three fails on text or colour, the
fix belongs in `DescribeBackup`/`FormatAgo`/`BackupIsStale`, not in a new implementation path — re-read
Step 1's helper code before changing anything.

- [ ] **Step 3: Watch the archived-wallet guard fail, then confirm it again**

Temporarily remove the `.Where(wallet => !wallet.Archived)` call from `OnInitializedAsync` in
`Home.razor`, run
`dotnet test tests/Noof.Ledger.E2E.Tests --filter An_archived_wallet_is_left_out_of_the_balances_card_and_its_total`,
confirm it fails (the archived KZT wallet's row and total now appear). Put the filter back and re-run
to confirm it passes again.

- [ ] **Step 4: Full suite and commit**

```powershell
$lock = "C:\Users\noofs\AppData\Local\Temp\noof-suite.lock"
while (-not (New-Item -ItemType Directory -Path $lock -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2 }
try {
    dotnet test --solution NoofLedger.slnx
} finally {
    Remove-Item -Recurse -Force $lock
}
```

Expected PASS: every test green, including all five new `DashboardBalancesTests` cases and the
pre-existing `DashboardTests` (unaffected — the new card and status line add markup, they do not
change any locator `DashboardTests` already depends on).

```
git add src/Noof.Ledger.Web/Components/Pages/Home.razor tests/Noof.Ledger.E2E.Tests/DashboardBalancesTests.cs
git commit -m "$(cat <<'EOF'
The dashboard shows each active wallet's balance, per-currency
totals, and the backup's status

Archived wallets are dropped from the Balances card and its totals
(M4's "hidden from capture, history kept" - /wallets still shows
them). Backup status text is amber past B3's 36-hour threshold or on
any failed run, computed from TimeProvider rather than
DateTimeOffset.UtcNow so it is testable against a fake clock.

Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
EOF
)"
```

---

### Task 10: Exactness under two cultures, and `ops/restore-check.ps1`

**Files:**
- Create: `tests/Noof.Ledger.TestKit/CultureScope.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/MoneyExactnessTests.cs`
- Create: `tests/Noof.Ledger.Persistence.Tests/RestoreRoundTripTests.cs` — the automated dump/restore round trip spec B4 asks for (review finding 4).
- Create: `tests/Noof.Ledger.Host.Tests/DashboardCultureTests.cs` — reads the dashboard HTML the host itself serves under a non-invariant culture, in-process (review finding 3; the Playwright test alone only proves element ids, never the host process's own culture).
- Create: `tests/Noof.Ledger.E2E.Tests/MoneyExactnessDashboardTests.cs`
- Create: `ops/restore-check.ps1`
- Modify: `tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj` — add a `ProjectReference` to `Noof.Ledger.TestKit` if it does not already have one (`DashboardCultureTests` needs `DatabaseSettings`/`PostgresFixture`-adjacent helpers from it).

**Interfaces:**
- Consumes (Task 1): `Wallet`, `Transaction` (`WalletId` nullable, `Kind`, `CaptureKind.Manual`, nullable Telegram ids), `Entry`, `BalanceCheck`, `Money`, `CurrencyCode.Supported` (`[Eur, Rsd, Usd, Rub, Kzt]`) — all exactly as declared in `plan-01-contract.md`'s Domain section.
- Consumes (Task 3): `IBalanceReadModel` (`Task<IReadOnlyList<Money>> BalanceOfAsync(Guid walletId, CancellationToken)`) registered scoped by `AddNoofPersistence` — **resolved through DI, never constructed directly**, because Task 3 owns the concrete class name and this task must not guess it.
- Consumes (Task 8): `IRecordEcho.Compose(CategorizationSubject)` and the exact `CategorizationSubject` shape (`Kind`, `WalletCurrency`, `WalletBalances`, `Statement`) from `plan-01-contract.md`, and the exact echo strings from that same file's "Echo strings (Task 8, exact)" section — this task asserts against those literal strings, it does not invent new ones.
- Consumes (Task 9): the Balances card's dashboard element ids `balances`, `balance-{walletId}` (`plan-01-contract.md`, "Dashboard element ids"), and the `N2`/`InvariantCulture` formatting rule (contract finding 9: `3200` renders `3,200.00`) — this task's literal assertions use that comma-grouped form.
- Consumes (Task 12): `PgDumpDatabaseDumper`, `PgDumpDatabaseDumper.DefaultPath` (`src/Noof.Ledger.Persistence/Backup/PgDumpDatabaseDumper.cs`) — used directly by `RestoreRoundTripTests`, the same way `PgDumpDatabaseDumperTests` does.
- Consumes (Task 13, for backup context only): none directly — `ops/restore-check.ps1` reads a dump file `BackupWorker` produced, but this task never runs the worker; it seeds a dump with `pg_dump` itself (Step 9).
- Produces: `public sealed class CultureScope : IDisposable` (namespace `Noof.Ledger.TestKit`) — sets `CultureInfo.CurrentCulture`/`CurrentUICulture` for the scope's lifetime and restores them on `Dispose`, reusable by any later phase's culture-sensitive test.

---

- [ ] **Step 1: `CultureScope`**

  `tests/Noof.Ledger.TestKit/CultureScope.cs`:

  ```csharp
  using System.Globalization;

  namespace Noof.Ledger.TestKit;

  // .NET Core flows CultureInfo.CurrentCulture through ExecutionContext, so setting it here is
  // visible to every awaited call inside the scope, not just synchronous code on this thread.
  public sealed class CultureScope : IDisposable
  {
      readonly CultureInfo previousCulture = CultureInfo.CurrentCulture;
      readonly CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;

      public CultureScope(string cultureName)
      {
          var culture = new CultureInfo(cultureName);
          CultureInfo.CurrentCulture = culture;
          CultureInfo.CurrentUICulture = culture;
      }

      public void Dispose()
      {
          CultureInfo.CurrentCulture = previousCulture;
          CultureInfo.CurrentUICulture = previousUiCulture;
      }
  }
  ```

  This is a test-infrastructure DTO-like helper with no branching logic — exempt from TDD under CLAUDE.md §4 *Testing* the same way `DatabaseSettings` and `PostgresFixture` are. Its correctness is proven by every test that uses it below actually going red under a culture it doesn't expect (Step 6).

- [ ] **Step 2: Write the failing exactness tests (balances)**

  `tests/Noof.Ledger.Persistence.Tests/MoneyExactnessTests.cs`:

  ```csharp
  using AwesomeAssertions;
  using Microsoft.EntityFrameworkCore;
  using Microsoft.Extensions.Configuration;
  using Microsoft.Extensions.DependencyInjection;
  using Noof.Ledger.Application.Categorization;
  using Noof.Ledger.Application.Chat;
  using Noof.Ledger.Application.Reporting;
  using Noof.Ledger.Domain;
  using Noof.Ledger.TestKit;

  namespace Noof.Ledger.Persistence.Tests;

  // M12: sums are numeric(19,4) in SQL and decimal in C#, and everything shown is
  // InvariantCulture-formatted regardless of the ambient thread culture. A missing explicit culture
  // argument on a ToString/decimal.Parse call is exactly the class of bug this catches - it would
  // pass silently under en-US (the default CI/dev locale) and only misbehave under a comma-decimal
  // culture, which is why the assertions below run wrapped in CultureScope rather than trusting the
  // test host's own locale to already be "wrong" by luck.
  [Collection("postgres")]
  public class MoneyExactnessTests(PostgresFixture fixture)
  {
      static readonly Guid EurWalletId = Guid.NewGuid();
      static readonly Guid RsdWalletId = Guid.NewGuid();
      static readonly Guid UsdWalletId = Guid.NewGuid();
      static readonly Guid RubWalletId = Guid.NewGuid();
      static readonly Guid KztWalletId = Guid.NewGuid();

      const string TimeZone = "Europe/Belgrade";
      static readonly DateOnly Day1 = new(2026, 9, 1);
      static readonly DateOnly Day2 = new(2026, 9, 2);
      static readonly DateTimeOffset At1 = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
      static readonly DateTimeOffset At2 = new(2026, 9, 2, 8, 0, 0, TimeSpan.Zero);

      static async Task SeedAsync(LedgerDbContext db)
      {
          await db.Database.MigrateAsync(TestContext.Current.CancellationToken);

          db.Wallets.AddRange(
              Wallet(EurWalletId, "Wise EUR", CurrencyCode.Eur),
              Wallet(RsdWalletId, "Raiffeisen RSD", CurrencyCode.Rsd),
              Wallet(UsdWalletId, "Payoneer USD", CurrencyCode.Usd),
              Wallet(RubWalletId, "Cash RUB", CurrencyCode.Rub),
              Wallet(KztWalletId, "Halyk KZT", CurrencyCode.Kzt));

          // EUR: the canonical 0.1 + 0.2 case. Decimal keeps this exact; double would not.
          Opening(db, EurWalletId, CurrencyCode.Eur, 1000.00m, Day1, At1);
          Expense(db, EurWalletId, CurrencyCode.Eur, 0.10m, Day2, At2);
          Expense(db, EurWalletId, CurrencyCode.Eur, 0.20m, Day2, At2);

          // RSD: the spec's own worked example (M11) - opening, then a statement that adjusts +200.
          Opening(db, RsdWalletId, CurrencyCode.Rsd, 44_800.00m, Day1, At1);
          Statement(db, RsdWalletId, CurrencyCode.Rsd, 45_000.00m, 44_800.00m, Day2, At2);

          // USD: a 4-decimal input. The view keeps all four; only display truncates to two.
          Opening(db, UsdWalletId, CurrencyCode.Usd, 500.0001m, Day1, At1);
          Expense(db, UsdWalletId, CurrencyCode.Usd, 0.0001m, Day2, At2);

          // RUB: three expenses that must sum to exactly 100.00, not 99.99999999999999.
          Opening(db, RubWalletId, CurrencyCode.Rub, 0.00m, Day1, At1);
          Expense(db, RubWalletId, CurrencyCode.Rub, 33.33m, Day2, At2);
          Expense(db, RubWalletId, CurrencyCode.Rub, 33.33m, Day2, At2);
          Expense(db, RubWalletId, CurrencyCode.Rub, 33.34m, Day2, At2);

          // KZT: a large value (M12 explicitly names "large KZT values").
          Opening(db, KztWalletId, CurrencyCode.Kzt, 50_000_000.00m, Day1, At1);
          Income(db, KztWalletId, CurrencyCode.Kzt, 1_234_567.89m, Day2, At2);

          await db.SaveChangesAsync(TestContext.Current.CancellationToken);
      }

      static Wallet Wallet(Guid id, string name, CurrencyCode currency) => new()
      {
          Id = id, Name = name, Currency = currency, Aliases = [], IsDefaultForCurrency = false,
          Archived = false, CreatedAt = At1,
      };

      static void Opening(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal amount, DateOnly occurredOn, DateTimeOffset occurredAt) =>
          AddCheckpoint(db, walletId, currency, amount, computedBefore: 0m, occurredOn, occurredAt);

      static void Statement(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal stated, decimal computedBefore, DateOnly occurredOn, DateTimeOffset occurredAt) =>
          AddCheckpoint(db, walletId, currency, stated, computedBefore, occurredOn, occurredAt);

      static void AddCheckpoint(
          LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal amount, decimal computedBefore, DateOnly occurredOn, DateTimeOffset occurredAt)
      {
          var transactionId = Guid.NewGuid();
          db.Transactions.Add(new Transaction
          {
              Id = transactionId, WalletId = walletId, Kind = TransactionKind.BalanceCheck,
              CaptureKind = CaptureKind.Manual, RawText = "Balance statement", Status = TransactionStatus.Completed,
              TimeZoneId = TimeZone, OccurredAt = occurredAt, OccurredOn = occurredOn,
              TelegramChatId = null, TelegramMessageId = null, CreatedAt = occurredAt,
          });
          db.BalanceChecks.Add(new BalanceCheck
          {
              TransactionId = transactionId, WalletId = walletId, Stated = new Money(amount, currency), ComputedBefore = computedBefore,
          });
      }

      static void Expense(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal amount, DateOnly occurredOn, DateTimeOffset occurredAt) =>
          AddEntry(db, walletId, currency, -amount, TransactionKind.Expense, occurredOn, occurredAt);

      static void Income(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal amount, DateOnly occurredOn, DateTimeOffset occurredAt) =>
          AddEntry(db, walletId, currency, amount, TransactionKind.Income, occurredOn, occurredAt);

      static void AddEntry(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal signedAmount, TransactionKind kind, DateOnly occurredOn, DateTimeOffset occurredAt)
      {
          var transactionId = Guid.NewGuid();
          db.Transactions.Add(new Transaction
          {
              Id = transactionId, WalletId = walletId, Kind = kind, CaptureKind = CaptureKind.Manual,
              RawText = kind == TransactionKind.Expense ? "seeded expense" : "seeded income", Status = TransactionStatus.Completed,
              TimeZoneId = TimeZone, OccurredAt = occurredAt, OccurredOn = occurredOn,
              TelegramChatId = null, TelegramMessageId = null, CreatedAt = occurredAt,
          });
          db.Entries.Add(new Entry
          {
              Id = Guid.NewGuid(), TransactionId = transactionId, WalletId = walletId,
              Amount = new Money(signedAmount, currency), Role = EntryRole.Principal,
          });
      }

      static (IBalanceReadModel Balances, ServiceProvider Provider) BuildBalanceReadModel(string connectionString)
      {
          var services = new ServiceCollection();
          services.AddSingleton(TimeProvider.System);
          var configuration = new ConfigurationBuilder()
              .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ledger"] = connectionString })
              .Build();
          services.AddNoofPersistence(configuration, maxJobAttempts: 8);

          // Ownership stays with the caller (review finding 8: the provider was never disposed
          // before). Resolved straight from the root provider - IBalanceReadModel becomes part of
          // the root's own implicit scope, so disposing the returned ServiceProvider once the
          // test's assertions are done with it disposes the scoped DbContext underneath it too.
          var provider = services.BuildServiceProvider();
          return (provider.GetRequiredService<IBalanceReadModel>(), provider);
      }

      [Theory]
      [InlineData("ru-RU")]
      [InlineData("sr-Latn-RS")]
      public async Task Balances_are_exact_in_all_five_currencies_under_a_non_invariant_culture(string cultureName)
      {
          using var culture = new CultureScope(cultureName);
          var connectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();
          await using (var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(connectionString).Options))
              await SeedAsync(db);

          var (balances, provider) = BuildBalanceReadModel(connectionString);
          await using var _ = provider;

          (await balances.BalanceOfAsync(EurWalletId, TestContext.Current.CancellationToken))
              .Should().BeEquivalentTo([new Money(999.70m, CurrencyCode.Eur)]);
          (await balances.BalanceOfAsync(RsdWalletId, TestContext.Current.CancellationToken))
              .Should().BeEquivalentTo([new Money(45_000.00m, CurrencyCode.Rsd)]);
          (await balances.BalanceOfAsync(UsdWalletId, TestContext.Current.CancellationToken))
              .Should().BeEquivalentTo([new Money(500.0000m, CurrencyCode.Usd)]);
          (await balances.BalanceOfAsync(RubWalletId, TestContext.Current.CancellationToken))
              .Should().BeEquivalentTo([new Money(-100.00m, CurrencyCode.Rub)]);
          (await balances.BalanceOfAsync(KztWalletId, TestContext.Current.CancellationToken))
              .Should().BeEquivalentTo([new Money(51_234_567.89m, CurrencyCode.Kzt)]);
      }
  }
  ```

  Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests --filter MoneyExactnessTests`
  Expected FAIL: compile errors (`IBalanceReadModel`, `Entry`, `BalanceCheck`, `TransactionKind`, `db.Entries`, `db.BalanceChecks` do not exist) if Tasks 1/3 have not landed on this branch yet — if they have, the expected failure is a real assertion mismatch only if Task 3's view/read-model disagrees with the contract, which this task must then report rather than "fix" by changing its own expected values.

- [ ] **Step 3: Confirm against the real Task 1/3 code**

  This step has no code of its own — it is the gate before Step 4. Run the test from Step 2 once Tasks 1 and 3 are merged into the branch. It must PASS with no changes to the test file. If it does not, stop: either the `wallet_balances` view's ordering rule, `BalanceOfAsync`'s currency ordering, or the `Entry`/`BalanceCheck` column shapes disagree with `plan-01-contract.md`, and that is a defect in Task 1 or Task 3 against the binding contract, not something this task works around.

- [ ] **Step 4: Write the failing exactness tests (echo strings)**

  Append to `tests/Noof.Ledger.Persistence.Tests/MoneyExactnessTests.cs`:

  ```csharp
      [Theory]
      [InlineData("ru-RU")]
      [InlineData("sr-Latn-RS")]
      public void The_expense_echo_renders_cents_exactly_regardless_of_culture(string cultureName)
      {
          using var culture = new CultureScope(cultureName);
          var echo = new RecordEcho();
          var subject = new CategorizationSubject(
              Guid.NewGuid(), "coffee 0.10 EUR", 111L, 42, "Wise EUR", TransactionStatus.Completed, Day2, Day2,
              [new RecordedLine("Coffee", new Money(0.10m, CurrencyCode.Eur), "coffee", "Coffee", null)],
              CaptureKind.Text, TransactionKind.Expense, CurrencyCode.Eur,
              [new Money(999.70m, CurrencyCode.Eur)]);

          var message = echo.Compose(subject);

          message.Text.Should().StartWith("Recorded — Wise EUR · balance 999.70 EUR\n");
          message.Text.Should().Contain("0.10 EUR");
      }

      [Theory]
      [InlineData("ru-RU")]
      [InlineData("sr-Latn-RS")]
      public void The_balance_statement_echo_renders_the_adjustment_exactly(string cultureName)
      {
          using var culture = new CultureScope(cultureName);
          var echo = new RecordEcho();
          var subject = new CategorizationSubject(
              Guid.NewGuid(), "на райфе 45000", 111L, 43, "Raiffeisen RSD", TransactionStatus.Completed, Day2, Day2,
              [], CaptureKind.Text, TransactionKind.BalanceCheck, CurrencyCode.Rsd, [new Money(45_000.00m, CurrencyCode.Rsd)],
              new BalanceStatement(new Money(45_000.00m, CurrencyCode.Rsd), 44_800.00m));

          var message = echo.Compose(subject);

          message.Text.Should().Be("Raiffeisen RSD: balance was 44800.00 RSD, you said 45000.00 RSD — adjusted +200.00 RSD");
      }
  ```

  Add `using Noof.Ledger.Application.Categorization;` if not already present via the earlier `using` block.

  Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests --filter MoneyExactnessTests`
  Expected FAIL until Task 8's `RecordEcho`/`CategorizationSubject` land with exactly the contract's shape and strings; once they do, PASS with no edits here — if it does not, the mismatch is Task 8's against the binding contract, reported rather than patched around.

- [ ] **Step 5: Full run for the Persistence project**

  ```powershell
  mkdir "$env:TEMP\noof-suite.lock" 2>$null
  dotnet test --project tests/Noof.Ledger.Persistence.Tests
  rmdir "$env:TEMP\noof-suite.lock"
  ```
  Expected: PASS.

- [ ] **Step 6: Watch the culture guard fail**

  In `Balances_are_exact_in_all_five_currencies_under_a_non_invariant_culture`, immediately after constructing `culture`, temporarily add `System.Globalization.CultureInfo.CurrentCulture.Name.Should().Be("en-US");` (fully qualified so no new `using` is needed for a line that is about to be reverted) and re-run. It must fail (actual is `ru-RU`/`sr-Latn-RS`), confirming `CultureScope` is actually changing the ambient culture and not silently doing nothing. Remove the temporary line.

- [ ] **Step 7: Write the dashboard exactness E2E test**

  `tests/Noof.Ledger.E2E.Tests/MoneyExactnessDashboardTests.cs`:

  ```csharp
  using AwesomeAssertions;
  using Microsoft.EntityFrameworkCore;
  using Microsoft.Playwright;
  using Microsoft.Playwright.Xunit.v3;
  using Noof.Ledger.Domain;
  using Noof.Ledger.Persistence;
  using Noof.Ledger.TestKit;

  namespace Noof.Ledger.E2E.Tests;

  public sealed class MoneyExactnessDashboardTests(CookieModeHostFixture fixture) : PageTest, IClassFixture<CookieModeHostFixture>
  {
      static readonly Guid EurWalletId = Guid.NewGuid();
      static readonly Guid RsdWalletId = Guid.NewGuid();
      const string TimeZone = "Europe/Belgrade";
      static readonly DateOnly Day1 = new(2026, 9, 1);
      static readonly DateTimeOffset At1 = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

      [Theory]
      [InlineData("ru-RU")]
      [InlineData("sr-Latn-RS")]
      public async Task The_dashboard_serves_exact_invariant_formatted_balances(string cultureName)
      {
          if (fixture.DatabaseUnavailable)
              Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

          // The host process itself always formats with InvariantCulture (CLAUDE.md §4), so this
          // scope's job is the same one it has in MoneyExactnessTests: catching a test-side
          // comparison that forgot to be culture-explicit, not simulating a Serbian Windows install.
          using var culture = new CultureScope(cultureName);

          await using (var db = OpenDb())
          {
              db.Wallets.AddRange(
                  new Wallet { Id = EurWalletId, Name = $"Wise EUR {cultureName}", Currency = CurrencyCode.Eur, Aliases = [], IsDefaultForCurrency = false, Archived = false, CreatedAt = At1 },
                  new Wallet { Id = RsdWalletId, Name = $"Raiffeisen RSD {cultureName}", Currency = CurrencyCode.Rsd, Aliases = [], IsDefaultForCurrency = false, Archived = false, CreatedAt = At1 });

              AddCheckpoint(db, EurWalletId, CurrencyCode.Eur, 1000.00m, 0m);
              AddEntry(db, EurWalletId, CurrencyCode.Eur, -0.10m, TransactionKind.Expense);
              AddEntry(db, EurWalletId, CurrencyCode.Eur, -0.20m, TransactionKind.Expense);

              AddCheckpoint(db, RsdWalletId, CurrencyCode.Rsd, 44_800.00m, 0m);

              await db.SaveChangesAsync(TestContext.Current.CancellationToken);
          }

          await SignInAsync();
          await Page.GotoAsync(fixture.BaseUrl + "/");
          await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

          await Expect(Page.Locator($"#balance-{EurWalletId}")).ToContainTextAsync("999.70 EUR");
          await Expect(Page.Locator($"#balance-{RsdWalletId}")).ToContainTextAsync("44,800.00 RSD");
      }

      void AddCheckpoint(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal amount, decimal computedBefore)
      {
          var transactionId = Guid.NewGuid();
          db.Transactions.Add(new Transaction
          {
              Id = transactionId, WalletId = walletId, Kind = TransactionKind.BalanceCheck, CaptureKind = CaptureKind.Manual,
              RawText = "Opening balance", Status = TransactionStatus.Completed, TimeZoneId = TimeZone,
              OccurredAt = At1, OccurredOn = Day1, TelegramChatId = null, TelegramMessageId = null, CreatedAt = At1,
          });
          db.BalanceChecks.Add(new BalanceCheck { TransactionId = transactionId, WalletId = walletId, Stated = new Money(amount, currency), ComputedBefore = computedBefore });
      }

      void AddEntry(LedgerDbContext db, Guid walletId, CurrencyCode currency, decimal signedAmount, TransactionKind kind)
      {
          var transactionId = Guid.NewGuid();
          db.Transactions.Add(new Transaction
          {
              Id = transactionId, WalletId = walletId, Kind = kind, CaptureKind = CaptureKind.Manual,
              RawText = "seeded", Status = TransactionStatus.Completed, TimeZoneId = TimeZone,
              OccurredAt = At1.AddDays(1), OccurredOn = Day1.AddDays(1), TelegramChatId = null, TelegramMessageId = null, CreatedAt = At1.AddDays(1),
          });
          db.Entries.Add(new Entry { Id = Guid.NewGuid(), TransactionId = transactionId, WalletId = walletId, Amount = new Money(signedAmount, currency), Role = EntryRole.Principal });
      }

      LedgerDbContext OpenDb() => new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(fixture.ConnectionString).Options);

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

  Run: `dotnet test --project tests/Noof.Ledger.E2E.Tests --filter MoneyExactnessDashboardTests`
  Expected FAIL until Task 9's Balances card exists with `balance-{walletId}` element ids exactly as `plan-01-contract.md` names them; PASS with no edits here once Task 9 lands correctly, else report the Task 9 mismatch.

- [ ] **Step 8: Write the failing in-process culture test (spec M12's real subject)**

  The Playwright test above proves the page's element ids exist and contain the right text, but it runs in a separate process whose own culture the test never controls — Playwright drives a real browser talking to the real published host, and nothing in that path is `ru-RU`/`sr-Latn-RS`. The test that actually exercises "the served HTML is culture-independent" has to set the *host's* culture and read the HTML the host serves, in the same process. Add it:

  `tests/Noof.Ledger.Host.Tests/DashboardCultureTests.cs`:

  ```csharp
  using System.Globalization;
  using AwesomeAssertions;
  using Microsoft.AspNetCore.Mvc.Testing;
  using Microsoft.EntityFrameworkCore;
  using Microsoft.Extensions.DependencyInjection;
  using Noof.Ledger.Domain;
  using Noof.Ledger.Persistence;
  using Noof.Ledger.TestKit;
  using Npgsql;

  namespace Noof.Ledger.Host.Tests;

  [Collection("culture")]
  public sealed class DashboardCultureTests
  {
      static readonly Guid EurWalletId = Guid.NewGuid();
      static readonly Guid RsdWalletId = Guid.NewGuid();
      const string TimeZone = "Europe/Belgrade";
      static readonly DateOnly Day1 = new(2026, 9, 1);
      static readonly DateTimeOffset At1 = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

      [Theory]
      [InlineData("ru-RU")]
      [InlineData("sr-Latn-RS")]
      public async Task The_served_dashboard_html_is_culture_independent(string cultureName)
      {
          if (!await DatabaseIsReachableAsync(TestContext.Current.CancellationToken))
              Assert.Skip("No reachable PostgreSQL database - set NOOF_TEST_PG or run ops/reset-database-auth.ps1.");

          var databaseName = $"noof_dashboard_culture_{Guid.NewGuid():N}";
          await CreateCloneAsync(databaseName, TestContext.Current.CancellationToken);

          var cloneBuilder = new NpgsqlConnectionStringBuilder(DatabaseSettings.AdminConnectionString) { Database = databaseName };

          var previousCulture = CultureInfo.CurrentCulture;
          var previousUiCulture = CultureInfo.CurrentUICulture;
          try
          {
              var culture = new CultureInfo(cultureName);
              CultureInfo.DefaultThreadCurrentCulture = culture;
              CultureInfo.DefaultThreadCurrentUICulture = culture;
              CultureInfo.CurrentCulture = culture;
              CultureInfo.CurrentUICulture = culture;

              await using (var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(cloneBuilder.ConnectionString).Options))
              {
                  db.Wallets.AddRange(
                      new Wallet { Id = EurWalletId, Name = "Wise EUR", Currency = CurrencyCode.Eur, Aliases = [], IsDefaultForCurrency = false, Archived = false, CreatedAt = At1 },
                      new Wallet { Id = RsdWalletId, Name = "Raiffeisen RSD", Currency = CurrencyCode.Rsd, Aliases = [], IsDefaultForCurrency = false, Archived = false, CreatedAt = At1 });

                  var eurTransactionId = Guid.NewGuid();
                  db.Transactions.Add(new Transaction
                  {
                      Id = eurTransactionId, WalletId = EurWalletId, Kind = TransactionKind.BalanceCheck, CaptureKind = CaptureKind.Manual,
                      RawText = "Opening balance", Status = TransactionStatus.Completed, TimeZoneId = TimeZone,
                      OccurredAt = At1, OccurredOn = Day1, TelegramChatId = null, TelegramMessageId = null, CreatedAt = At1,
                  });
                  db.BalanceChecks.Add(new BalanceCheck { TransactionId = eurTransactionId, WalletId = EurWalletId, Stated = new Money(1000.00m, CurrencyCode.Eur), ComputedBefore = 0m });

                  var expenseTransactionId = Guid.NewGuid();
                  db.Transactions.Add(new Transaction
                  {
                      Id = expenseTransactionId, WalletId = EurWalletId, Kind = TransactionKind.Expense, CaptureKind = CaptureKind.Manual,
                      RawText = "seeded", Status = TransactionStatus.Completed, TimeZoneId = TimeZone,
                      OccurredAt = At1.AddDays(1), OccurredOn = Day1.AddDays(1), TelegramChatId = null, TelegramMessageId = null, CreatedAt = At1.AddDays(1),
                  });
                  db.Entries.Add(new Entry { Id = Guid.NewGuid(), TransactionId = expenseTransactionId, WalletId = EurWalletId, Amount = new Money(-0.30m, CurrencyCode.Eur), Role = EntryRole.Principal });

                  var rsdTransactionId = Guid.NewGuid();
                  db.Transactions.Add(new Transaction
                  {
                      Id = rsdTransactionId, WalletId = RsdWalletId, Kind = TransactionKind.BalanceCheck, CaptureKind = CaptureKind.Manual,
                      RawText = "Opening balance", Status = TransactionStatus.Completed, TimeZoneId = TimeZone,
                      OccurredAt = At1, OccurredOn = Day1, TelegramChatId = null, TelegramMessageId = null, CreatedAt = At1,
                  });
                  db.BalanceChecks.Add(new BalanceCheck { TransactionId = rsdTransactionId, WalletId = RsdWalletId, Stated = new Money(44_800.00m, CurrencyCode.Rsd), ComputedBefore = 0m });

                  await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
                  await db.SaveChangesAsync(TestContext.Current.CancellationToken);
              }

              await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
              {
                  builder.UseSetting("ConnectionStrings:Ledger", cloneBuilder.ConnectionString);
                  builder.UseSetting("Database:MigrateOnStartup", "false");
                  builder.UseSetting("Backup:Enabled", "false");
                  builder.ConfigureTestServices(services => FakeUserStore.Register(services));
              });

              using var client = factory.CreateClient();
              await LoginHelper.PostWithTokenAsync(client, FakeUserStore.Username, FakeUserStore.Password, TestContext.Current.CancellationToken);
              var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

              html.Should().Contain("44,800.00 RSD");
              html.Should().Contain("999.70 EUR");
              html.Should().NotContain("44 800,00");
              html.Should().NotContain("999,70");
          }
          finally
          {
              CultureInfo.DefaultThreadCurrentCulture = null;
              CultureInfo.DefaultThreadCurrentUICulture = null;
              CultureInfo.CurrentCulture = previousCulture;
              CultureInfo.CurrentUICulture = previousUiCulture;

              await DropCloneAsync(databaseName);
          }
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
          await using var create = new NpgsqlCommand(
              $"CREATE DATABASE \"{name}\" TEMPLATE {DatabaseSettings.TemplateDatabase}", admin);
          await create.ExecuteNonQueryAsync(cancellationToken);
      }

      static async Task DropCloneAsync(string name)
      {
          NpgsqlConnection.ClearAllPools();

          await using var admin = new NpgsqlConnection(DatabaseSettings.AdminConnectionString);
          await admin.OpenAsync(CancellationToken.None);
          // DROP DATABASE waits on a Postgres checkpoint before it can remove the files, which can
          // exceed Npgsql's default 30s command timeout under load - same reason PostgresFixture and
          // CookieModeHostFixture both raise it.
          await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin)
          {
              CommandTimeout = 120,
          };
          await drop.ExecuteNonQueryAsync(CancellationToken.None);
      }
  }
  ```

  The reachability check, clone creation and clone teardown above are the same pattern
  `CookieModeHostFixture.DatabaseIsReachableAsync`/`CreateCloneAsync` and `PostgresFixture` already use
  elsewhere in this solution — a plain `NpgsqlConnection` + `NpgsqlCommand.ExecuteNonQueryAsync`
  against `DatabaseSettings.AdminConnectionString`/`TemplateDatabase`, no placeholder helper.
  `[Collection("culture")]` keeps this test from running in parallel with anything else that reads
  `CultureInfo.CurrentCulture` on the same process.

  Run: `dotnet test --project tests/Noof.Ledger.Host.Tests --filter DashboardCultureTests`
  Expected FAIL until Task 9's dashboard renders the balances in `N2`/`InvariantCulture`; PASS once Task 9 is correct.

- [ ] **Step 9: Write the failing backup-restore round trip (spec B4)**

  `MoneyExactnessTests.cs` already seeds all five currencies; this test proves a `pg_dump`/`pg_restore` round trip preserves them exactly, closing spec B4's "an automated test does the same round trip on a clone of the test template." Append a new file rather than growing `MoneyExactnessTests.cs` further, since it needs `pg_dump.exe`/`pg_restore.exe` and its own skip guard:

  `tests/Noof.Ledger.Persistence.Tests/RestoreRoundTripTests.cs`:

  ```csharp
  using System.Diagnostics;
  using AwesomeAssertions;
  using Microsoft.EntityFrameworkCore;
  using Noof.Ledger.Domain;
  using Noof.Ledger.Persistence.Backup;
  using Noof.Ledger.TestKit;

  namespace Noof.Ledger.Persistence.Tests;

  [Collection("postgres")]
  public class RestoreRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
  {
      string? tempDump;

      static string PgDumpPath => Environment.GetEnvironmentVariable("NOOF_TEST_PGDUMP") ?? PgDumpDatabaseDumper.DefaultPath;
      static string PgRestorePath => PgDumpPath.Replace("pg_dump.exe", "pg_restore.exe", StringComparison.Ordinal);

      public ValueTask InitializeAsync() => ValueTask.CompletedTask;

      public ValueTask DisposeAsync()
      {
          if (tempDump is not null && File.Exists(tempDump))
              File.Delete(tempDump);
          return ValueTask.CompletedTask;
      }

      [Fact]
      public async Task A_restored_dump_has_the_same_balances_and_row_counts_as_the_source()
      {
          if (!File.Exists(PgDumpPath))
              Assert.Skip($"pg_dump.exe not found at {PgDumpPath} - install PostgreSQL 18 or set Backup:PgDumpPath (test override: NOOF_TEST_PGDUMP).");

          var sourceConnectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();
          await using (var db = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(sourceConnectionString).Options))
          {
              await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
              var eurWallet = BalanceSeed.AddWallet(db, CurrencyCode.Eur, "Wise EUR");
              var rsdWallet = BalanceSeed.AddWallet(db, CurrencyCode.Rsd, "Raiffeisen RSD");
              BalanceSeed.State(db, eurWallet, 1000.00m, new DateOnly(2026, 9, 1), new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
              BalanceSeed.Spend(db, eurWallet, 0.30m, new DateOnly(2026, 9, 2), new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero));
              BalanceSeed.State(db, rsdWallet, 44_800.00m, new DateOnly(2026, 9, 1), new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
              BalanceSeed.Earn(db, rsdWallet, 200.00m, new DateOnly(2026, 9, 2), new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero));
              await db.SaveChangesAsync(TestContext.Current.CancellationToken);
          }

          tempDump = Path.Combine(Path.GetTempPath(), $"noof-restore-roundtrip-{Guid.NewGuid():N}.dump");
          var dumper = new PgDumpDatabaseDumper(sourceConnectionString, PgDumpPath);
          var dumpResult = await dumper.DumpAsync(tempDump, TestContext.Current.CancellationToken);
          dumpResult.Succeeded.Should().BeTrue(dumpResult.Error);

          var restoredConnectionString = await fixture.CreateEmptyDatabaseConnectionStringAsync();
          var restoredBuilder = new Npgsql.NpgsqlConnectionStringBuilder(restoredConnectionString);
          var restore = Process.Start(new ProcessStartInfo(PgRestorePath,
              ["--no-owner", "--no-privileges", "-h", restoredBuilder.Host!, "-p", restoredBuilder.Port.ToString(), "-U", restoredBuilder.Username!, "-d", restoredBuilder.Database!, tempDump])
          {
              UseShellExecute = false,
              RedirectStandardError = true,
              Environment = { ["PGPASSWORD"] = restoredBuilder.Password ?? string.Empty },
          })!;
          var restoreError = await restore.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
          await restore.WaitForExitAsync(TestContext.Current.CancellationToken);
          restore.ExitCode.Should().Be(0, restoreError);

          await using var source = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(sourceConnectionString).Options);
          await using var restored = new LedgerDbContext(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(restoredConnectionString).Options);

          var sourceBalances = await source.Database.SqlQuery<string>(
              $"SELECT wallet_id::text || ':' || currency || ':' || balance::text FROM wallet_balances ORDER BY wallet_id, currency")
              .ToListAsync(TestContext.Current.CancellationToken);
          var restoredBalances = await restored.Database.SqlQuery<string>(
              $"SELECT wallet_id::text || ':' || currency || ':' || balance::text FROM wallet_balances ORDER BY wallet_id, currency")
              .ToListAsync(TestContext.Current.CancellationToken);
          restoredBalances.Should().BeEquivalentTo(sourceBalances);

          (await restored.Wallets.CountAsync(TestContext.Current.CancellationToken))
              .Should().Be(await source.Wallets.CountAsync(TestContext.Current.CancellationToken));
          (await restored.Transactions.CountAsync(TestContext.Current.CancellationToken))
              .Should().Be(await source.Transactions.CountAsync(TestContext.Current.CancellationToken));
          (await restored.Entries.CountAsync(TestContext.Current.CancellationToken))
              .Should().Be(await source.Entries.CountAsync(TestContext.Current.CancellationToken));
          (await restored.BalanceChecks.CountAsync(TestContext.Current.CancellationToken))
              .Should().Be(await source.BalanceChecks.CountAsync(TestContext.Current.CancellationToken));
      }
  }
  ```

  This test uses the real `BalanceSeed` helper Task 3 defines in `Noof.Ledger.Persistence.Tests` (`AddWallet`, `Spend`, `Earn`, `State`) — not `Noof.Ledger.TestKit`, and not a `SeedTwoWalletsWithAStatementAndTwoCurrenciesAsync` method, which does not exist anywhere. The seeding above is inline, built directly from those helpers: two wallets, an opening/statement checkpoint on each, then an entry in each wallet's own currency.

  Run: `dotnet test --project tests/Noof.Ledger.Persistence.Tests --filter RestoreRoundTripTests`
  Expected: Skipped if `pg_dump.exe`/`pg_restore.exe` are absent, else PASS (both dump and restore go through the same schema, so nothing here should legitimately fail once Task 12's dumper and Task 1/3's schema are in place).

- [ ] **Step 10: `ops/restore-check.ps1`**

  `ops/restore-check.ps1`:

  ```powershell
  #Requires -Version 7
  [CmdletBinding(SupportsShouldProcess)]
  param(
      [string]$DumpPath,
      [string]$BackupDirectory = "$env:LOCALAPPDATA\NoofLedger\backups",
      [string]$SourceDatabase = "noof_ledger_test_template",
      [string]$PgRoot = "C:\Program Files\PostgreSQL\18"
  )

  $ErrorActionPreference = 'Stop'

  # This is the guard that matters (B4/B5): the SCRATCH target this script creates and drops must
  # never be able to land on a real database, no matter what -SourceDatabase names for comparison
  # (SourceDatabase legitimately IS noof_ledger for the operator's real run in B5 - this script only
  # ever reads it). A pattern is a filter; a refusal is a guarantee - same idiom as
  # ops/clean-test-databases.ps1's $Protected array.
  $Protected = @('noof_ledger', 'noof_ledger_test_template', 'postgres', 'template0', 'template1')
  $scratch = "noof_restore_check_$([guid]::NewGuid().ToString('N'))"
  if ($scratch -in $Protected) { throw "Refusing: generated scratch name collided with a protected database name - re-run." }

  $connectionFile = $env:NOOF_TEST_PG
  if (-not $connectionFile) { $connectionFile = (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection" -Raw).Trim() }
  $c = $connectionFile
  $p = @{}; foreach ($x in $c.Split(';')) { if ($x -match '^\s*([^=]+)=(.*)$') { $p[$Matches[1].Trim()] = $Matches[2].Trim() } }
  $env:PGPASSWORD = $p['Password']
  $psql = Join-Path $PgRoot 'bin\psql.exe'
  $pgRestore = Join-Path $PgRoot 'bin\pg_restore.exe'

  if (-not $DumpPath) {
      $DumpPath = Get-ChildItem $BackupDirectory -Filter 'noof_ledger-*.dump' -ErrorAction SilentlyContinue |
          Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
      if (-not $DumpPath) { throw "No dump found in $BackupDirectory and none given via -DumpPath." }
  }
  if (-not (Test-Path $DumpPath)) { throw "Dump not found: $DumpPath" }

  function Invoke-Psql([string]$Database, [string]$Sql) {
      $rows = & $psql -h $p['Host'] -p $p['Port'] -U $p['Username'] -d $Database -A -t -F',' -c $Sql
      if ($LASTEXITCODE -ne 0) { throw "psql against '$Database' failed with exit code $LASTEXITCODE" }
      return @($rows)
  }

  function Get-Comparable([string]$Database) {
      $tables = Invoke-Psql $Database "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE' ORDER BY table_name"
      $counts = foreach ($table in $tables) { Invoke-Psql $Database "SELECT '$table', count(*) FROM `"$table`"" }
      [PSCustomObject]@{
          Balances = Invoke-Psql $Database "SELECT wallet_id, currency, balance, checked_on FROM wallet_balances ORDER BY wallet_id, currency"
          Counts   = $counts
      }
  }

  Write-Host "Restoring '$DumpPath' into scratch database '$scratch'..."
  if ($PSCmdlet.ShouldProcess($scratch, 'CREATE DATABASE')) {
      & $psql -h $p['Host'] -p $p['Port'] -U $p['Username'] -d postgres -c "CREATE DATABASE `"$scratch`"" | Out-Null
      if ($LASTEXITCODE -ne 0) { throw "CREATE DATABASE failed with exit code $LASTEXITCODE" }
  }

  try {
      if ($PSCmdlet.ShouldProcess($DumpPath, "pg_restore into $scratch")) {
          & $pgRestore -h $p['Host'] -p $p['Port'] -U $p['Username'] -d $scratch --no-owner --no-privileges $DumpPath
          if ($LASTEXITCODE -ne 0) { throw "pg_restore failed with exit code $LASTEXITCODE" }
      }

      $source = Get-Comparable $SourceDatabase
      $restored = Get-Comparable $scratch

      # Compare-Object throws "Cannot bind argument to parameter 'ReferenceObject' because it is
      # null" when either side is empty (e.g. an empty wallet_balances in a freshly migrated
      # template) - wrapping both sides in @() keeps it an empty array instead of $null.
      $balanceDiff = Compare-Object @($source.Balances) @($restored.Balances)
      $countDiff = Compare-Object @($source.Counts) @($restored.Counts)

      if ($balanceDiff -or $countDiff) {
          Write-Host "MISMATCH between '$SourceDatabase' and the restored dump:"
          if ($balanceDiff) { Write-Host "wallet_balances differs:"; $balanceDiff | Format-Table | Out-String | Write-Host }
          if ($countDiff) { Write-Host "row counts differ:"; $countDiff | Format-Table | Out-String | Write-Host }
          $script:failed = $true
      }
      else {
          Write-Host "restore-check OK: '$DumpPath' matches '$SourceDatabase' (wallet_balances and every table's row count)."
          $script:failed = $false
      }
  }
  finally {
      if ($PSCmdlet.ShouldProcess($scratch, 'DROP DATABASE')) {
          & $psql -h $p['Host'] -p $p['Port'] -U $p['Username'] -d postgres -c "DROP DATABASE IF EXISTS `"$scratch`" WITH (FORCE)" | Out-Null
      }
  }

  if ($failed) { exit 1 }
  exit 0
  ```

  Note on `-WhatIf`: with `SupportsShouldProcess`, `-WhatIf` skips the `CREATE`/`pg_restore`/`DROP` blocks (their `ShouldProcess` guards return false) and the comparison then runs against a scratch database that was never created, which fails loudly rather than reporting a false OK — this mirrors `-WhatIf`'s existing meaning in `ops/clean-test-databases.ps1` ("list/describe, do nothing"), not a claim that `-WhatIf` here safely reports a real comparison.

- [ ] **Step 11: Manual verification — restore-check against a test-template clone**

  This is a script Task 12/13's own tests already exercise the underlying `pg_dump`/`pg_restore` calls for; `restore-check.ps1` itself is verified by hand once per phase, the same way `ops/reset-database-auth.ps1` and `ops/clean-test-databases.ps1` are — no xUnit test wraps a PowerShell script in this codebase.

  ```powershell
  # 1. Produce one real dump of the test template (never noof_ledger):
  $c = (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection" -Raw).Trim()
  $p = @{}; foreach ($x in $c.Split(';')) { if ($x -match '^\s*([^=]+)=(.*)$') { $p[$Matches[1].Trim()] = $Matches[2].Trim() } }
  $env:PGPASSWORD = $p['Password']
  New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\NoofLedger\backups" | Out-Null
  & 'C:\Program Files\PostgreSQL\18\bin\pg_dump.exe' -Fc -h $p['Host'] -p $p['Port'] -U $p['Username'] -d noof_ledger_test_template `
      -f "$env:LOCALAPPDATA\NoofLedger\backups\noof_ledger-manual-check.dump"

  # 2. Run the check against that dump, comparing to the same template it came from:
  pwsh -File ops/restore-check.ps1 -DumpPath "$env:LOCALAPPDATA\NoofLedger\backups\noof_ledger-manual-check.dump" -SourceDatabase noof_ledger_test_template
  ```
  Expected output ends `restore-check OK: '...' matches 'noof_ledger_test_template' ...` and the script exits `0` (`$LASTEXITCODE -eq 0`). Confirm no scratch `noof_restore_check_*` database is left behind: `psql -h ... -d postgres -c "SELECT datname FROM pg_database WHERE datname LIKE 'noof_restore_check_%'"` returns no rows. Delete the manual dump file afterward — it is not one of the 14 `BackupWorker` manages, it is this verification's own throwaway.

  **Never point `-SourceDatabase` or the dump itself at `noof_ledger` in this phase.** The one real run against `noof_ledger` is the operator's, later, with explicit permission (spec B5) — Task 11 documents that procedure in `ops/RUNBOOK.md`; this task does not run it.

- [ ] **Step 12: Full-suite run for the affected projects**

  ```powershell
  mkdir "$env:TEMP\noof-suite.lock" 2>$null
  dotnet test --project tests/Noof.Ledger.Persistence.Tests
  dotnet test --project tests/Noof.Ledger.Host.Tests --filter DashboardCultureTests
  dotnet test --project tests/Noof.Ledger.E2E.Tests --filter MoneyExactnessDashboardTests
  rmdir "$env:TEMP\noof-suite.lock"
  ```
  Expected: all PASS.

- [ ] **Step 13: Commit**

  ```powershell
  git add tests/Noof.Ledger.TestKit/CultureScope.cs tests/Noof.Ledger.Persistence.Tests/MoneyExactnessTests.cs tests/Noof.Ledger.Persistence.Tests/RestoreRoundTripTests.cs tests/Noof.Ledger.Host.Tests/DashboardCultureTests.cs tests/Noof.Ledger.Host.Tests/Noof.Ledger.Host.Tests.csproj tests/Noof.Ledger.E2E.Tests/MoneyExactnessDashboardTests.cs ops/restore-check.ps1
  git commit -m "$(cat <<'EOF'
  Prove balances exact under ru-RU and sr-Latn-RS, and check a restore (M12, B4)

  MoneyExactnessTests seeds all five currencies' entries and checkpoints straight into the schema
  and reads them back through IBalanceReadModel and the echo, wrapped in CultureScope so a missing
  explicit InvariantCulture argument would fail loudly instead of only under a Serbian locale.
  DashboardCultureTests proves the same for the HTML the host itself serves, in-process, under a
  culture the Playwright process alone cannot set; MoneyExactnessDashboardTests covers the same
  ground for a real browser reading the same page. RestoreRoundTripTests closes B4's automated
  round trip: dump a seeded clone, restore it, and diff wallet_balances and every table's row count
  against the source. ops/restore-check.ps1 does the same by hand against any dump, restores into a
  scratch database, compares wallet_balances and every public table's row count against the source,
  and refuses to let that scratch target ever be a protected database name.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

---

### Task 11: Documentation and status

**Files:**
- Modify: `CLAUDE.md` (status block; "The model" section; two new short rules)
- Modify: `README.md` (status block; "still missing" list)
- Modify: `docs/OPEN-QUESTIONS.md` (new `P4-1` entry)
- Modify: `docs/BACKLOG.md` (new entries)
- Modify: `ops/RUNBOOK.md` (new `## Backups` section)
- Test: none — documentation only, exempt from TDD (CLAUDE.md §4 *Testing*: "Exempt: migrations, DTOs, `Program.cs` wiring" plus the standing exemption every earlier phase's own closing-documentation task has used). Step 1 below is a verification run, not a test-writing step.

**Interfaces:**
- Consumes: everything Tasks 1–10, 12 and 13 produced — this task names real, already-merged types and files (`record_transaction`, `IBalanceReadModel`, `BackupWorker`, `ops/restore-check.ps1`, `wallet_balances`) and must not invent a name none of them actually used. If a name below does not match what an earlier task actually shipped, fix the name here to match the real code, not the other way around, and note the mismatch in this task's own commit message.
- Produces: nothing another task depends on — this is the phase's last task (wave 6, depends on all others).

---

- [ ] **Step 1: Get the real test count**

  ```powershell
  mkdir "$env:TEMP\noof-suite.lock" 2>$null
  dotnet test --solution NoofLedger.slnx
  rmdir "$env:TEMP\noof-suite.lock"
  ```
  Read the final summary line (`Passed! - Failed: 0, Passed: <N>, ...` or the Microsoft.Testing.Platform
  equivalent). **`<N>` below is a placeholder for that real number — write the number this run actually
  reports, not `677` unchanged and not a guess.** If anything fails, stop and fix it (or report which
  earlier task's work is broken) before touching any documentation — a status block that claims a green
  suite while the suite is red is exactly the kind of stale claim CLAUDE.md §6 exists to prevent.

- [ ] **Step 2: `CLAUDE.md` — status block**

  Replace the blockquote status paragraph (the `> **Status:** ...` block at the top of the file) with:

  ```markdown
  > **Status:** spec approved (`docs/superpowers/specs/2026-09-24-money-model.md`); **Phases 0, 0b, 1A, 1B, 1C, 1D, 2, 3 and 4 complete** — solution, EF Core model and migrations, PostgreSQL money-storage gate, cookie authentication as the sole mode, the `user set-password` verb, the loopback interlock, a Blazor Server shell, Telegram capture with a durable queue, natural-language capture, voice notes transcribed by Groq's whisper-large-v3, and now the money model: every wallet's balance — opening balance, minus spending, plus income, re-anchored by the operator's own balance statements — is exact in all five currencies (EUR, RSD, USD, RUB, KZT) under `ru-RU` and `sr-Latn-RS`. Transactions carry a `Kind` (`Expense`/`Income`/`BalanceCheck`); expense and income transactions own signed double-entry-lite `entries`; a balance statement is a `balance_checks` checkpoint; a wallet's balance is computed by the `wallet_balances` SQL view and read back by `IBalanceReadModel`, never stored. The model's answer tool is `record_transaction` (was `record_spending`) and now names a wallet and, for a balance statement, the stated amount. `/wallets` manages wallets on the dashboard; income and balance statements are ordinary messages to the bot. The app also backs itself up daily — `pg_dump -Fc` into `%LOCALAPPDATA%\NoofLedger\backups`, the newest 14 kept, every run logged to `backup_runs` — and `ops/restore-check.ps1` proves a dump restores to the same balances. `<N>` solution tests, all green — the Playwright browser tests are in the solution now, so `dotnet test --solution` runs them too and needs Chromium present. Live suites stay skipped unless `NOOF_LEDGER_LIVE_ANTHROPIC_KEY` / `NOOF_LEDGER_LIVE_GROQ_KEY` + `NOOF_LEDGER_LIVE_VOICE_FILE` are set; `ops/publish.ps1` produces a runnable host. Cross-currency conversion, transfers and receipt photos remain future phases. Rules below marked *(settled)* are direct user decisions and are not up for re-litigation.
  >
  > Deferred **decisions** live in `docs/OPEN-QUESTIONS.md`; deferred **work** lives in `docs/BACKLOG.md`. Check both before proposing something as missing.
  >
  > **`noof_ledger` holds the operator's real credentials now.** Never run tests, experiments or manual checks against it, or call the live model, without an explicit request. Tests use the `noof_ledger_test_template` clones only.
  ```

  Replace `<N>` with the number Step 1 actually printed.

- [ ] **Step 3: `CLAUDE.md` — rename `record_spending` to `record_transaction`**

  In the "The model" *(settled)* section, two edits:

  Change:
  ```
  - **The answer is a forced tool call with `strict: true`, not structured outputs** *(settled
    2026-09-23, operator's preference)*. `record_spending`'s arguments are the answer; strictness is a
  ```
  to:
  ```
  - **The answer is a forced tool call with `strict: true`, not structured outputs** *(settled
    2026-09-23, operator's preference)*. `record_transaction`'s arguments are the answer (renamed from
    `record_spending` in Phase 4: it now records income and balance statements too, and names a `kind`,
    an optional `wallet`, and — for `kind = balance` — the stated `balance`); strictness is a
  ```

  Change:
  ```
    `DelegatingChatClient` below it that re-forces `record_spending` on the follow-up request: FICC
  ```
  to:
  ```
    `DelegatingChatClient` below it that re-forces `record_transaction` on the follow-up request: FICC
  ```

- [ ] **Step 4: `CLAUDE.md` — the checkpoint rule**

  In the "Money" *(settled)* subsection, immediately after the existing "Capture is the exception" bullet, add:

  ```markdown
  - **A wallet's balance is derived, never stored** *(settled 2026-09-24, Phase 4)*. No column anywhere
    holds a running balance. It is the latest `balance_checks` checkpoint for that wallet and currency
    plus the sum of `entries` after it (no checkpoint → the sum of all entries), computed by the
    `wallet_balances` SQL view and read back through `IBalanceReadModel`. A checkpoint's recorded
    `computed_before` is history for the echo, not a balance anything reads back as current — never add
    a cached-balance column "for speed" without re-deriving it from entries on every write; that is
    exactly the drift this model exists to prevent.
  ```

- [ ] **Step 5: `CLAUDE.md` — the external-process rule**

  In the "Database" *(settled)* subsection, after the "Tests run against a real database" bullet, add:

  ```markdown
  - **An external process never receives a secret as an argument** *(settled 2026-09-24, Phase 4)*.
    `BackupWorker`'s `pg_dump` is the first production code in this repo to shell out to another
    process; its connection password goes through `ProcessStartInfo.Environment["PGPASSWORD"]` only —
    never `ArgumentList`, a log line, or a recorded `backup_runs.error`. Any future external process
    (another database tool, a future export) follows the same rule: `UseShellExecute = false`,
    `ArgumentList` for arguments, environment variables for anything that must not appear in a process
    list or a log.
  ```

- [ ] **Step 5a: `CLAUDE.md` — the view rule**

  In the "Database" *(settled)* subsection, immediately after the rule added in Step 5, add:

  ```markdown
  - **`wallet_balances` reads `transactions`, `entries` and `balance_checks`.** A migration that alters
    or drops a column any of those three still expose to the view must `DROP VIEW wallet_balances`
    first and re-create it in the same migration, or the migration fails on the dependency.
    `schema.expected.sql` never shows views or triggers — `WalletBalancesViewTests` is their detector.
  ```

- [ ] **Step 6: `README.md` — status block**

  Replace the blockquote status paragraph with:

  ```markdown
  > **Status: capture, balances and a checked backup all work end to end.** A message typed to the
  > Telegram bot becomes a categorised expense, income, or balance statement on the dashboard — say it
  > the way you would say it, and the model reads the amount, the date, the kind and which wallet from
  > how you speak it. Every wallet's balance — opening balance, minus spending, plus income, corrected
  > by your own balance statements — is exact in all five supported currencies (EUR, RSD, USD, RUB,
  > KZT), proven under both `ru-RU` and `sr-Latn-RS`. Wallets are created and managed on a `/wallets`
  > page; income and balance statements are ordinary messages to the bot, typed or spoken. The app
  > backs its own database up daily and a restore has been checked against it — `ops/restore-check.ps1`
  > restores a dump into a scratch database and compares every wallet's balance and row counts against
  > the source.
  > `<N>` tests, all green — browser tests included.
  >
  > Still missing: **receipt photos** and **currency exchange** (a spend in a currency other than its
  > wallet's own is recorded as-is, in its own currency, not converted).
  ```

  Replace `<N>` with Step 1's number.

- [ ] **Step 7: `docs/OPEN-QUESTIONS.md` — record the phase's decisions**

  Append a new section at the end of the file:

  ```markdown
  ### P4-1 — Phase 4 money model decisions (2026-09-24)

  The operator made these decisions during Phase 4 spec discussion; the rules themselves live in
  `CLAUDE.md` and `docs/superpowers/specs/2026-09-24-money-model.md` (M1–M13, B1–B5). Recorded here for
  the reasoning.

  | # | Decision | Reasoning |
  |---|---|---|
  | M1 | A wallet's balance is derived from entries and checkpoints, never stored as a column | A cached balance drifts from what produced it the moment anything writes around it instead of through it; deriving it is the only way "exact" stays true after the code that computed it once is forgotten |
  | M3/M4 | `transactions.wallet_id` becomes nullable; the wallet is chosen when the record is read (the model names it, or the currency's default), not at capture | Capture no longer needs a wallet at all, and "no default wallet" stops being a capture-time failure — it only matters once the model has actually named a currency |
  | M6 | A balance statement is a checkpoint (`balance_checks`), not an adjustment entry | An adjustment entry would hide the operator's own correction inside the same ledger as ordinary spending; a checkpoint keeps it as what it is — a fact about reality overriding the app's own running total — and gives the dashboard a "last reconciled" date for free |
  | M10 | Cross-currency spending is not converted; it is recorded in its own currency, shown as a separate balance line | *"курсы зависят от банка — отложи"* (operator). Inventing a rate the bank would not actually use is worse than an honest, visible second currency line — `docs/BACKLOG.md` records the deferred conversion work |
  | B2 | An external process's secret goes through its environment only, never an argument | `Process.Start` arguments are visible to anything that can list processes (Task Manager, `Get-Process`, a crash dump); an environment variable set via `ProcessStartInfo.Environment` is not |
  | B5 | `restore-check.ps1` runs against a real database only once, by the operator's own hand, with explicit permission | The script legitimately reads `noof_ledger` to compare against a restored scratch copy — never writes to or drops it — but "this script only ever reads the real ledger" is a claim worth the operator's own eyes once, not something Phase 4's automated tests should assert on his behalf against his real data |
  ```

- [ ] **Step 7a: `docs/OPEN-QUESTIONS.md` / `docs/BACKLOG.md` — fix the stale `record_spending` mentions**

  `docs/OPEN-QUESTIONS.md:428-429` and the one `record_spending` hit in `docs/BACKLOG.md` (grep to find its exact line) still name the tool `record_spending`. Since Step 3 above renamed it to `record_transaction` in CLAUDE.md, append `(renamed to \`record_transaction\` in Phase 4)` immediately after each of those two mentions rather than rewriting the historical decision text around them — they are records of what was decided at the time, not a live spec.

- [ ] **Step 7b: `docs/BACKLOG.md` — fix the stale `EfCaptureStoreTests` sentence**

  `docs/BACKLOG.md:502` says `EfCaptureStoreTests` deletes the seeded default wallet that `WalletDefaultTests` depends on. That is no longer true after Task 1 — the helper it refers to is deleted (`plan-10-task-01.md:571` deletes the default-wallet lookup and seeding this sentence was warning about). Verify the exact current wording at that line and either delete the sentence outright or replace it with one line noting it no longer applies after Phase 4's Task 1 removed the default-wallet capture path it described.

- [ ] **Step 8: `docs/BACKLOG.md` — deferred work**

  Append a new section at the end of the file:

  ```markdown
  ---

  ## Deferred from Phase 4 (money model and backup)

  **Cross-currency conversion.** A spend in a currency other than its wallet's own (M10) is recorded as
  a separate currency line on that wallet's balance, not converted. Building this needs a rate source
  decision (Q4 in the original design's open questions) the operator has not made, and a rate is a
  moving target that would need its own history to stay honest in a re-read old transaction. Not
  scheduled until a rate source is chosen.

  **Transfers between wallets (Phase 7).** `TransactionKind.Transfer = 3` and `EntryRole.Fee` are
  reserved in the enum but not implemented — a transfer becomes two entries (one per wallet) with no
  schema change needed when that phase arrives. Moving cash between wallets today is two separate
  manual transactions (an expense from one, an income to the other), which loses the "this was the same
  money" relationship a real transfer would keep.

  **The same-day checkpoint ordering edge.** A purchase dated to the same local day as a balance
  statement, but sent to the bot after the statement, is ordered after it (M6's `(occurred_on,
  occurred_at)` rule) — so the *next* statement absorbs it instead of the one it was dated alongside.
  This is a known, accepted approximation (recorded in the spec's "Known limits"), not a bug: the
  alternative (ordering by `occurred_on` alone, ties broken arbitrarily) would make a statement's
  "adjustment" figure depend on transcription order rather than anything the operator said.

  **Encrypted backups.** `BackupWorker`'s dumps sit unencrypted under `%LOCALAPPDATA%\NoofLedger\backups`,
  protected only by the user profile's own permissions — the same trust boundary the credential file
  already relies on. OneDrive sync (Q8 in the original design) is Phase 10 and would want this decided
  first, since syncing an unencrypted financial dump to the cloud is a different risk than a dump that
  never leaves the machine.
  ```

- [ ] **Step 9: `ops/RUNBOOK.md` — the `## Backups` section**

  Insert a new `## Backups` section immediately after `## Leftover test databases` and before `## Start
  the published app from its own directory` (i.e., as the new heading 7, pushing the two sections after
  it down by one in the numbering — the file has no literal numbers, only heading order, so no other
  text needs to change):

  ```markdown
  ## Backups

  `BackupWorker` runs inside the host, not as a separate process. On start it checks
  `backup_runs` for the newest successful run; if there is none, or it is older than 24 hours, it
  backs up immediately. It then checks again 24 hours after each success, or 1 hour after a failed
  attempt rather than waiting a full day, for as long as the host keeps running.

  **Where:** `%LOCALAPPDATA%\NoofLedger\backups\noof_ledger-yyyyMMdd-HHmmss.dump` (UTC timestamp in
  the file name). Written under a `.tmp` name first and renamed only on success, so a half-written
  dump never looks finished to anything that lists the directory.

  **How many:** the newest 14. Older ones are deleted right after a successful backup, by file name
  order (the timestamp in the name sorts the same as time, so no file needs to be opened to prune).

  **Checking status:** every run — success or failure — is a row in `backup_runs`. The dashboard's
  home page shows *Last backup: never* until the first success, then *Last backup: N min/h/d ago*
  after one, or *Last backup: failed* whenever the most recent run failed (even after an earlier
  success) — amber whenever nothing has ever succeeded, the last run failed, or the last success is
  older than 36 hours. From a database connection directly:
  ```sql
  SELECT started_at, finished_at, succeeded, file_name, size_bytes, error
  FROM backup_runs ORDER BY started_at DESC LIMIT 5;
  ```

  **Restoring a dump by hand** (into a *new* database — never over `noof_ledger` directly):
  ```powershell
  $c = (Get-Content "$env:LOCALAPPDATA\NoofLedger\db.connection" -Raw).Trim()
  $p = @{}; foreach ($x in $c.Split(';')) { if ($x -match '^\s*([^=]+)=(.*)$') { $p[$Matches[1].Trim()] = $Matches[2].Trim() } }
  $env:PGPASSWORD = $p['Password']
  & 'C:\Program Files\PostgreSQL\18\bin\psql.exe' -h $p['Host'] -p $p['Port'] -U $p['Username'] -d postgres -c "CREATE DATABASE noof_ledger_restored"
  & 'C:\Program Files\PostgreSQL\18\bin\pg_restore.exe' -h $p['Host'] -p $p['Port'] -U $p['Username'] -d noof_ledger_restored --no-owner --no-privileges `
      "$env:LOCALAPPDATA\NoofLedger\backups\<the .dump file>"
  ```
  Drop `noof_ledger_restored` when you are done inspecting it — `psql ... -c "DROP DATABASE noof_ledger_restored"`.

  **`ops/restore-check.ps1`** automates the check above and compares the result against a source
  database instead of leaving that to your own eyes: it restores a dump (the newest one by default, or
  `-DumpPath` for a specific one) into a throwaway scratch database, compares `wallet_balances` and
  every public table's row count against `-SourceDatabase` (default `noof_ledger_test_template`),
  prints the result, and drops the scratch database either way. It refuses to let the scratch target
  ever be `noof_ledger`, `noof_ledger_test_template`, `postgres`, or either template database, by exact
  name.

  `Backup:Enabled=false` disables the worker (the test fixtures set it); `Backup:PgDumpPath` overrides
  the `pg_dump.exe`/`pg_restore.exe` binary location, if it is not at the default
  `C:\Program Files\PostgreSQL\18\bin\`.

  **The one real run against `noof_ledger`, per the operator's decision (B5):** this must be run by the
  operator, or with the operator's explicit permission, since it reads the real ledger:
  ```powershell
  pwsh -File ops/restore-check.ps1 -SourceDatabase noof_ledger
  ```
  Record the result here once it has been run:

  > _Not yet run. When it is: date, dump file name, and OK/MISMATCH go here._
  ```

- [ ] **Step 10: Read back every edited file**

  ```powershell
  git diff -- CLAUDE.md README.md docs/OPEN-QUESTIONS.md docs/BACKLOG.md ops/RUNBOOK.md
  ```
  Confirm no `<N>` placeholder survived (both occurrences replaced with Step 1's real number), no
  Markdown heading collided with an existing one, and the RUNBOOK insertion landed between the two
  named sections rather than at the end of the file.

- [ ] **Step 11: Commit**

  ```powershell
  git add CLAUDE.md README.md docs/OPEN-QUESTIONS.md docs/BACKLOG.md ops/RUNBOOK.md
  git commit -m "$(cat <<'EOF'
  Close Phase 4: status, the record_transaction rename, backups, and what's deferred

  CLAUDE.md and README.md now say the money model and the checked backup are real; three new settled
  rules (balances are derived, never stored; a secret never reaches an external process's arguments;
  a migration touching a column wallet_balances reads must drop and re-create the view) bind future
  phases the way the phase actually built them. docs/OPEN-QUESTIONS.md records why M1,
  M3/M4, M6, M10, B2 and B5 were decided the way they were; docs/BACKLOG.md records cross-currency
  conversion, transfers, the same-day checkpoint edge and dump encryption as deliberately deferred,
  not forgotten. ops/RUNBOOK.md documents where backups live, how to check and restore one, and the
  one real restore-check run against noof_ledger the operator still owns.

  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01NXaWFvgT296G5WzxGuuN3m
  EOF
  )"
  ```

---

