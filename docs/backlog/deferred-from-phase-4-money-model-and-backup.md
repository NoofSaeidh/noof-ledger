---
title: Deferred from Phase 4 (money model and backup)
status: deferred
area: persistence
since: 2026-09-24
---
**Income in a foreign currency is still not converted (M10, narrowed by Phase 7).** Phase 7 converts a
spending in a currency other than its wallet's at the wallet's own terms set on `/wallets` (T-6,
`docs/decisions/p7-1-transfers-and-exchange-decisions.md`), so M10 survives only where no charge is
computed: income in another currency, and a spending in a currency the wallet has no terms for (whose
echo says how to set them). Both keep a separate currency line on the wallet's balance. Converting income
the same way would mean charges on `Income` records and an income-side reading of the terms; the
transfers spec left Income's posting unchanged (§1), and the operator has not asked for it.

**Loans are recorded as other-income; there is no liability kind.** A loan received
("заняла у Маши 5000 рсд") is recorded as kind `income` under the `other-income` category
(`CategorizationPrompt`'s I-3 fix, Phase 4 final review) so the wallet matches the bank — but a loan
is a liability, not earned income. Phase 7 added the `Transfer` kind for money moving between the
operator's own wallets, but a loan is money owed to someone else, and no liability is modelled (the
transfers spec's "Out of scope"), so the dashboard's income totals still include money that was
borrowed, not earned.

**A correction to a record whose wallet was archived no longer falls back to the default (resolved
2026-09-27, PR #3).** M-7 (Phase 4 final review) is reversed: a correction that names no wallet now
keeps the record's existing wallet even after that wallet is archived — archiving must not rewrite a
historical record's wallet out from under it. See `KeepingTheRecordsWallet` and
`WalletsIncludingKept` in `src/Noof.Ledger.Host/Workers/CategorizationWorker.cs`.

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

**A separate PostgreSQL instance for tests** (own port, `fsync` off, no real data on it). Phase 4's
subagents spent most of their time in database test runs and in waiting on the shared suite lock:
every worktree's `DROP DATABASE` waits on the one server's checkpoints, and under parallel load the
fixtures' cleanup timed out and failed tests that were not broken. A test-only cluster pointed at
through the existing `NOOF_TEST_PG` variable would make clones cheap, retire the lock, and keep test
clones off the server that holds `noof_ledger`. Costs a second cluster to start after a reboot and a
small ops script. The operator has seen the trade-offs (2026-09-24) and not decided; the Phase 4
rule of running database and E2E tests filtered, and in full once per phase, removed most of the
contention in the meantime, and Phase 6's `FILE_COPY` clones removed the long checkpoint waits
themselves (the 2026-09-27 entry below), which weakens the case for a second cluster.

**A cancelled dump can be recorded as a failed run.** `PgDumpDatabaseDumper` kills `pg_dump` on
cancellation with `if (!process.HasExited) process.Kill(entireProcessTree: true)`; if `pg_dump` exits
between the check and the kill, `Kill` throws `InvalidOperationException`, which replaces the pending
cancellation, and `BackupWorker` records an ordinary failed run. A microsecond window at host
shutdown, no data lost — wrap the `Kill` in a `catch (InvalidOperationException)` when next in the
file (Phase 4 fix-wave re-review).
