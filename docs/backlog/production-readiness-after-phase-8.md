---
title: Production readiness after Phase 8
status: deferred
area: ops
since: 2026-10-03
related: [deferred-from-phase-4-money-model-and-backup, cancelling-a-transaction-in-the-dashboard]
---
**Wanted.** Once Phase 8 is done, make `noof_ledger` the database the operator actually records
spending in. Three parts, as the operator asked for them (2026-10-03):

**1. Clean the database.** Delete everything in `noof_ledger` except the stored secrets — every record
captured while trying the app out goes, so real use starts from an empty ledger without re-entering
the model, speech and Telegram credentials. Reference data the migrations seed stays with the schema.

**2. Production on its own PostgreSQL server, out of agents' reach.** Today `noof_ledger` shares a
server with every test clone and the test templates, so an agent running tests is one connection
string away from the operator's real data. Production moves to a server the tests never connect to.
This settles the "separate PostgreSQL instance for tests" item in
`deferred-from-phase-4-money-model-and-backup`, which the operator had left open. A separate server
alone is not the boundary: agents run as the same Windows user that can read
`%LOCALAPPDATA%\NoofLedger\db.connection`, so how the production credential is kept from them is
part of this work.

**3. Backups.** `BackupWorker` already dumps `noof_ledger` locally. What production needs beyond that —
a copy off the machine (Q8, `docs/decisions/q8-onedrive-backup-scope.md`), encrypted dumps, a tested
restore — is left for the operator to decide when this is scheduled.

## Triage of the backlog for real use — proposed 2026-10-08, awaiting the operator

Every open backlog entry, sorted by what real use needs. Phase 8b is in progress elsewhere and is not
triaged here. Bug reports filed with `/bug` live in `noof_ledger` and are triaged only through `/bugs`,
so they are not covered here either.

### Must have before real use

What is missing today, beyond the three parts above, turns into lost messages, real data within
agents' reach, or a ledger that cannot be recovered:

- **Agents' reach covers more than the credential.** The production host's whole data directory —
  `backups\` (full dumps of the ledger), `logs\`, `dp-keys\` and
  `db.connection` — sits in the profile agents run as, and CLAUDE.md §7 lets them search it. Most
  `WebApplicationFactory` fixtures also use that real key ring (`loose-ends-phase-5-observability`,
  "Copilot review items parked"). The boundary that covers all of it is a separate Windows account
  for production: its own `%LOCALAPPDATA%`, its own PostgreSQL cluster on its own port, and a
  credential only that account can read.
- **The host runs only while someone starts it.** `.\run.ps1 start-published` is manual, and Telegram
  drops updates after 24 hours (P1-5). Production starts with the machine and restarts after a crash —
  a service or a scheduled task under the production account. It runs from its own deploy folder, not
  the repository's `publish\`, which any agent's `.\run.ps1 publish` overwrites.
- **A host can still stop silently with exit code 0** when a dependency throws an
  `OperationCanceledException` that is not the host's own token
  (`dependency-operationcanceledexception-not-our-token`). A restart-on-failure policy does not see
  exit code 0. Settle it before relying on auto-restart: the filter becomes
  `ex is OperationCanceledException && stoppingToken.IsCancellationRequested`.
- **A restore that has been run.** `ops/restore-check.ps1` exists. One run against the production
  server's dump is the acceptance test for part 3, whatever the operator decides beyond that.

**Consequences for part 1.** DPAPI protects the key ring per Windows user, so secrets written under
the operator's account cannot be read under a production account. Copying `noof_ledger` minus the
records would carry secrets that no longer decrypt. A fresh database on the production server — re-enter
the three secrets, `user set-password`, claim the bot — is then simpler than cleaning. Either way, run
`/bugs` (or `.\run.ps1 bugs export`) first: open bug reports live in `noof_ledger` and go with it.
After a fresh start the bot is claimed after start-up and has no command menu until one restart
(`bot-commands-for-a-bot-claimed-later`).

**Cheaper before real data than after.** `zero-value-for-existing-enums` shifts every stored kind and
status value in a data migration. On an empty ledger that costs nothing to get wrong; after launch it
rewrites real records. Do it before launch or decide to leave those enums as they are.

### Wanted at or right after launch

- `cancelling-a-transaction-in-the-dashboard` — ranked high by the operator. Telegram's Cancel covers
  it until then.
- `correcting-vision-read-receipts`, with "a fiscal link in a photo's caption" from
  `deferred-from-phase-6-receipts` — most real receipts go through vision, and a wrong amount there
  cannot be corrected today, only cancelled and told again. The largest data-quality gap for real use;
  its own phase.
- Knowing the app is down: proactive alerts (`loose-ends-phase-5-observability`,
  `proactive-integrity-alerts-in-telegram`) and the capture-gap marker from
  `wake-the-machine-on-a-schedule`. Telegram alerts don't help when the machine itself is off, so
  the gap marker is the part that catches that.
- `test-button-for-telegram-bot-token` — small, and the token is re-entered at launch.
- The trace page: `trace-page-failure-reason`, `trace-page-exchange-slip`,
  `trace-page-fee-in-another-currency` (a finding's Trace link can fail to render).

### Small bugs — one batch, any time after launch

None loses money or data; each is a rare window or a cosmetic gap, mostly already visible on the
integrity page: `atomic-job-and-record-failure`, `failed-job-on-a-cancelled-record` (needs a design
choice), `edited-original-re-reading-fails-no-revision`, `pressing-izmenit-twice-forgets-first-prompt`,
`loose-ends-phase-2-closing-review` (`AskAsync` to a deleted echo, a reply to a stale prompt captured as
new), `loose-ends-phase-3-closing-review`, `fiscal-link-redaction-forms`, the `PgDumpDatabaseDumper`
kill race in `deferred-from-phase-4-money-model-and-backup`, the voice download size cap and the trace
summary's line order in `deferred-from-phase-6-receipts`, M-10's live database probe in
`loose-ends-phase-5-observability`.

### Features — later, by the operator's choice

Editing a record (`editing-a-transaction-in-the-dashboard`, `per-line-item-category-correction`,
`rolling-back-to-an-earlier-revision`, `category-management-screen`); capture
(`multi-item-capture-from-one-message`, `choosing-default-currency-from-telegram`,
`currencies-outside-the-five-known-codes`, `voice-bug-reports`, `filing-an-improvement-like-a-bug-report`);
money model (`reconciling-with-bank-statements`, `transfer-from-a-bank-screenshot`,
`transfers-in-transit`, `several-fees-on-one-transfer`, `transfer-amount-provenance`,
`foreign-currency-spending-on-a-fiscal-receipt`, `converting-earlier-expense-income-pairs-into-transfers`,
loans and foreign income in `deferred-from-phase-4-money-model-and-backup`); insight
(`free-form-questions-about-spending` after 8b, `duplicate-looking-transactions`,
`merchant-merge-inbox-near-duplicate-aliases`, `background-services-dashboard`); look
(`light-dark-toggle`, `dashboard-reading-width`, `trace-receipt-lines-overflow-on-phone`,
`multi-language-bot`, `mudblazor-features-needing-render-mode-decision`).

### Remote access — one decision, not for launch

`reaching-dashboard-from-phone-away-from-home` gates the rest: `tunnel-for-phone-access`,
`telegram-mini-app-for-the-dashboards`, `hosting-the-whole-application`, `capture-relay-lambda-dynamodb`,
and with whichever comes first `useforwardedheaders-missing-cookiesecurepolicy`,
`revalidating-auth-state-long-lived-circuits` and `security-review-before-hosting-rework`.

### Calibration — needs weeks of real use first

`whispers-inventions-on-silence`, `vocabulary-hints-for-transcriber`, `a-second-speech-provider`,
`keeping-the-audio`, the stronger `read_receipt` model and the product → category cache in
`deferred-from-phase-6-receipts`, `integrity-checks-at-scale` (waits for a measured slow run).

### Not about production — development hygiene

Worth doing for agents' throughput, independent of launch: `run-test-db-filter-reaches-only-persistence`
and the `DatabaseLogLevelDbTests` race in `loose-ends-phase-5-observability` (together they stop every
local `.\run.ps1 test db`); `per-run-template-for-e2e-and-host-tests`, `test-template-can-silently-drift-fixed`,
`test-suite-per-test-database-strategy-wont-scale` (a separate production server removes their risk to
real data, not their cost); code style (`explicit-access-modifiers`,
`provider-specific-code-in-its-own-project`, `classifying-job-errors-to-keep-bugs-red`); guards and
sweeps (`publicsurfacetests-regex-missing-delegate`, `read-model-current-zone-singleton-test-gap`,
`resharper-42-warning-findings-not-triaged`, `publicapianalyzers-considered-and-deferred`); the demo
(`demo-review-leftovers`, `demo-cut-from-first-design`).

## Operator's answers, 2026-10-08

- **Isolation boundary: research first.** A production Windows account, another machine or VM, or a
  separate PostgreSQL server alone — to be researched before the spec.
- **The database is cleaned, keeping the secrets** — not a fresh one. This constrains the isolation
  research: if production moves to another Windows account, the DPAPI-protected key ring has to move
  with it (re-protected for that account, or protected another way), or the kept secrets will not
  decrypt.
- **Backups before launch include an encrypted copy off the machine**, beside the local dumps and a
  tested restore. Q8 (dump only, or the key ring too) is decided in the spec.
- **`zero-value-for-existing-enums` goes in before launch.**

## Isolation research, 2026-10-08 — desk research, not yet proven

**Recommended: a separate standard local Windows account, running the host as a Windows service, with
its own PostgreSQL cluster that accepts SSPI logins only.**

**Compared and set aside:**
- **A separate cluster under the same user** is no boundary: an agent can still read `db.connection`
  and decrypt `dp-keys` through the same user's DPAPI.
- **A VM on this PC** is no boundary either. Windows 11 Home has no Hyper-V; a VirtualBox or VMware
  disk image belongs to the operator; and a WSL2 distro is the operator's (`wsl -u root`).
- **A separate always-on machine** is the strongest boundary and also closes the 24-hour Telegram
  gap, but it needs the Linux port. It is a later step, not a launch requirement.

**Why the separate account holds.** An agent's unelevated token has the administrator SIDs filtered
out, so it cannot read the production account's profile, data directory or key ring. With SSPI-only
`pg_hba` there is no password to steal. What stays reachable is the loopback ports and an operator
who clicks through a UAC prompt by reflex: Microsoft does not treat same-desktop UAC as a security
boundary.

**Shape.**
- **Accounts and PostgreSQL.**
  - A standard local user `noofledger` runs the host service.
  - The production cluster has its own data directory, ACL'd to SYSTEM, Administrators and its own
    `NT SERVICE` account, and listens on its own port on localhost only.
  - `pg_hba` admits only `sspi` from loopback, and `pg_ident` maps `noofledger` to the database
    role. The operator's account is never mapped: SSPI names the Windows user, not whether its
    token is elevated.
  - The production connection string then holds no secret, which also settles Q1 as SSPI.
- **Host.**
  - Releases go under `Program Files`, writable only when elevated. Deploys use an elevated
    `ops/deploy-production.ps1`: copy, stop, switch, start, `/healthz`, and keep the previous release
    for rollback.
  - The service uses recovery actions plus `sc failureflag`.
  - A scheduled task was rejected: its restart-on-failure does not fire on an exit code.
- **Exit code 0 is a code change.** `WindowsServiceLifetime` reports a stop the app starts itself as
  exit code 0, so the service manager runs no recovery. The host must report a non-zero exit when the
  service manager did not ask it to stop. This ships with the `OperationCanceledException` filter.
- **Key ring, with the secrets kept.**
  - Create a long-lived certificate under `noofledger`. Its PFX goes into the operator's password
    manager and never stays on disk.
  - A one-off command run as the operator decrypts each key's DPAPI-protected secret in memory and
    re-encrypts it to the certificate.
  - The host loads the certificate itself and calls `ProtectKeysWithCertificate(X509Certificate2)`.
    The thumbprint overload accepts only valid certificates, so it rejects a self-signed one.
  - The cleaned `noof_ledger` moves with `pg_dump`/`pg_restore` and is checked with `restore-check`.
  - The old database, the old key ring and the old dumps are deleted afterwards, because together they
    decrypt the kept secrets.
  - This corrects `hosting-the-whole-application`: a DPAPI key ring can move by re-wrapping its keys.
- **Off-machine backup.** Each dump is encrypted to the same certificate (CMS `EnvelopedCms`, in the
  shared framework). The encrypted dump and the re-wrapped key ring go to an outbox that feeds the
  off-machine copy. That answers Q8 with both, encrypted. A restore elsewhere needs only the PFX.

**To prove in a spike:**
- the SSPI user name a local account presents;
- re-wrapped keys decrypting old payloads;
- CNG versus CAPI keys for certificate decryption on .NET 10;
- granting the service logon right by script on Windows 11 Home;
- the production account writing into a folder that feeds OneDrive.

**Operator's questions still open:**
- Whether the daily account is a local administrator, and if so how the UAC prompt is gated.
- Where the PFX lives, and whether the encrypted key ring may go off the machine.
- The off-machine target.
- `/bugs` and `user set-password` becoming run-as-production commands.
- Whether to rotate the three secrets at launch anyway.
- Production's ports.

## Next steps

1. **Phase 8b merges** (another session owns it).
2. **The operator answers the isolation questions above**; a spike proves the unverified points.
3. **A spec for this item** (`docs/specs/`), from those answers, reviewed per CLAUDE.md §1 — it changes
   ops, Host start-up and the CLAUDE.md rules about `noof_ledger` and `%LOCALAPPDATA%\NoofLedger\`.
4. **Code first:** the `OperationCanceledException` filter; test hosts off the real key ring, with an
   architecture guard; the enum migration; `cancelling-a-transaction-in-the-dashboard`.
5. **Ops:** scripts that provision the production account, its PostgreSQL cluster and credential, the
   deploy folder and the auto-start; encrypted off-machine backup copies; a `RUNBOOK` section for
   deploying a new version and for restoring.
6. **Launch:** `/bugs` against `noof_ledger`; provision; move `noof_ledger` to the production server
   cleaned of everything but its secrets, with the key ring that decrypts them; deploy; a
   `restore-check` against the first production dump; then drop the old copy and point the CLAUDE.md
   rules at the new boundary.
7. **After launch,** in the order above: vision-receipt correction, knowing the app is down, the trace
   page, the small-bug batch.
