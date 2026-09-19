# noof-finance — Design (revision 2)

**Status:** awaiting your review · **Date:** 2026-09-19

> **How to review:** questions are marked **❓ Q1 … Q8** inline. Annotate your answer next to any of them, or anywhere you want something changed. **Approve** to lock the spec and move to the implementation plan. No product code until then.

> **What changed since you last read this:** database is now **PostgreSQL** (your suggestion — it won on measured evidence). The scaled-integer money encoding is **deleted** — Q2 and Q3 from the last round are gone, resolved the way you wanted. Containers researched and **declined**, with reasons. All ten of your annotations are folded in.

---

## 1. Your annotations — what happened to each

| You said | Outcome |
|---|---|
| *"Maybe postgres then?"* | **Adopted.** Won decisively on measured evidence. §3 |
| *"Base currency is EUR"* | Adopted everywhere — storage, reports, net worth, spread. §7 |
| *"just haiku without adviser"* | One model, one config property. The schema **cannot express** an adviser tier. §8 |
| *"all secrets encrypted right into the database and put through UI"* | Adopted, with one narrow carve-out you should sign off on. §9 · **❓Q1** |
| *"good logging… self diagnostics… on the screen… retention"* | Whole new subsystem. §10 |
| *"store rate when changing currencies… receipt for buying money"* | Adopted, and it yields a feature no other tracker gives you. §7 |
| *"recategorize manually by user or llm"* | One precedence order, enforced in SQL. Revertible batches. §11 |
| *"[merchant normaliser] should be processed with llm"* | Adopted — without reintroducing the silent-split risk. §11 |
| *"defered and abstract… write temp recording in db in a queue"* | Adopted. Mandatory now means *guaranteed*, not *synchronous*. §8 |
| *"En + Ru"* · *"OneDrive next phase"* | Locked. §12 · **❓Q3** |
| *"whole app with postgres to container"* | **Researched and declined.** §4 |

---

## 2. What this is

A personal expense tracker for one person. You capture spending through a Telegram bot — typed, spoken, or photographed — an LLM categorises it per line item, and a local Blazor dashboard shows where the money went across several wallets and five currencies.

**Success:** you log an expense in under five seconds from your phone without thinking about it, and at month's end you get an answer you trust enough to act on.

**Constraints:** .NET 10 · strict TDD · local hosting · public GitHub repo · cloud LLM, cheap · UI English, LLM output Russian · RSD/RUB/USD/EUR/KZT · your PC is only on when you're using it.

---

## 3. PostgreSQL — you were right

**Measured on your machine, reproduced independently by two agents.** EF Core 10 on SQLite emits `"Amount" TEXT` for `decimal`:

| Culture | `OrderBy(w => w.Amount)` on SQLite |
|---|---|
| `en-US` | correct |
| **`ru-RU`** | **throws `FormatException`** |
| **`sr-Latn-RS`** | **silently returns the wrong order** — swaps 9.99 and 10.00 |

And on a *raw* connection in every culture — DB Browser, any script you ever write:

```
ORDER BY : 0.1, 0.2, 10.0, 100.0, 2.5, 9.99     <- lexicographic
MAX      : 999.99                                <- over a set containing 100.00
```

Reproduced in this repository on 2026-09-19; see git history for `SqliteCounterfactualTests`.

`MAX` over money returning `9.99` when `100.00` is present isn't a rough edge. It's a wrong answer with no warning. Postgres, same data, same three cultures: `numeric(19,4)`, correct ordering, `SUM -1111.7778`, `MAX 100.0000`, every time.

**The operational objection is already paid.** PostgreSQL 17.5 was already on your PC (`C:\Program Files\PostgreSQL\17`, with `pg_trgm 1.6` and `unaccent 1.1`), and **you are now installing 18 via choco** — so the target is **PostgreSQL 18**, service `postgresql-x64-18`. (`dotnet-ef 10.0.12` is installed too.) Pin that major for the life of the app; there's no reason to chase releases on a single-user tracker.

**Money stays `decimal` + `Currency` exactly as you instructed** and maps straight to `numeric(19,4)`. The ValueConverter is deleted. Rates get `numeric(24,12)` — a 300,000-case fuzz showed scale 8 produces `0.01` errors and scale 10 produces none, so 12 has real margin.

**TDD cost, measured not argued:** transaction+rollback per test = **1.0 ms**, identical to SQLite in-memory. `CREATE DATABASE … TEMPLATE` per class = 272 ms. The only new cost is a one-off ~5 s fixture per run. (One trap: `NpgsqlConnection.ClearAllPools()` *before* `DROP DATABASE … WITH (FORCE)`, or teardown hangs forever.) And `initdb` + `pg_ctl` build a throwaway cluster **with no admin rights** in 4.5 s — so CI and clean-machine rebuilds need nothing installed.

**What Postgres buys that your annotations now require:** `jsonb` for raw LLM responses, log properties and queue payloads · `pg_trgm.word_similarity` for the merchant merge suggester · `SELECT … FOR UPDATE SKIP LOCKED`, the documented queue primitive SQLite cannot express · MVCC so the log sink flushes while the dashboard reads and capture writes · `CHECK` constraints for ledger invariants.

**No provider abstraction.** "SQLite now, Postgres later" is a fantasy that costs more than it saves — migrations are provider-specific, and the data migration is an export/import with a text→numeric parse in the middle, which is the exact parse that already throws under `ru-RU`.

> **❓ Q1 — The one thing that cannot live in the database.**
> The Postgres password can't be stored encrypted *inside the database it opens*. Two options: **(a) Windows-integrated auth (SSPI)** — no password exists at all, which eliminates the exception rather than managing it; **(b) a DPAPI-protected file** under `%LOCALAPPDATA%`. I lean (a), with a 30-minute spike in Phase 0 to confirm. Either way it is never in `appsettings.json`.

> **✅ Q2 — ANSWERED: PostgreSQL 18**, which you're installing via choco. Settled, no action needed.

---

## 4. Containers — researched, declined ✅ *(you agreed: "let's do without container for now")*

You asked about putting the whole app plus Postgres in a container. **The honest answer is no**, and the research overruled four of its own five researchers to say so. Recorded here so the reasoning survives if you revisit it later.

**The app in a container: no.** `ProtectedData` (DPAPI) **throws `PlatformNotSupportedException` on Linux** — your encrypted-secrets instruction currently rests on it, and every replacement relocates the problem rather than solving it. It also contradicts *"the app must live in a publish folder"*, and replaces a working one-link autostart with a four-link chain.

**Postgres in a container: also no**, and this is the part that surprised me:

1. **Startup ordering.** Task Scheduler "At log on" guarantees nothing about Docker Desktop's tray app (start-at-login is **off by default**) → WSL VM boot → engine ready → healthcheck. `MigrateAsync()` would throw on cold boot, succeed on warm. The Windows Service Control Manager solves this for free.
2. **Your ledger would live inside `ext4.vhdx`.** `wsl --unregister` or Docker's "Reset to factory defaults" deletes it instantly — and those are the buttons people press *while already troubleshooting*.
3. **Sleep.** WSL2 guest clock drift across sleep/hibernate is Microsoft-confirmed (WSL#5324, #10006, closed *"waiting on backport"*) and nobody could confirm which build has the fix. In a finance app a stale clock **misdates expenses** and breaks TLS. Silent corruption is the worst failure class available.
4. **Backups get worse.** `docker compose exec -T db pg_dump -Fc > file.dump` through PowerShell re-encodes the binary stream and produces a corrupt dump that fails only at restore.

**If you ever want a runtime anyway** — for Testcontainers or CI parity — it's **Docker Desktop, not Podman**: Testcontainers' Podman guidance covers macOS and Linux only, Windows is absent, and rootless Podman needs `TESTCONTAINERS_RYUK_DISABLED=true`, which kills the cleanup reaper. Note Docker Desktop's own requirements page doesn't list Windows Home and contradicts itself about it. Scope any runtime as a **dev-time tool**, never as the host.

Your WSL install isn't wasted — it's what makes that option cheap later. And `dotnet publish -t:PublishContainer` already produced an **80 MB OCI image in 3 seconds with no runtime installed**, so this door never closes.

> **⚠️ Unrelated but act on it: `%UserProfile%\.wslconfig` does not exist** and WSL is running uncapped. Defaults are **50% of RAM (~32 GB)** and **25% swap (~16 GB)**. Also `sparseVhd=true` — `ext4.vhdx` grows and never shrinks. **❓ Q3 — want me to write it?**

---

## 5. Solution layout

**One process, nine projects. No new projects in this revision** — every addition lands where the layering already says it belongs.

```
src/
  Noof.Domain        refs: NONE       Money, Ledger, Fold(), CategorizationAuthority, FX invariants
  Noof.Application   refs: Domain     use cases + ports  ("all services")
  Noof.Persistence   refs: App,Dom    DbContext, migrations, sinks, queue, integrity SQL
  Noof.Ai            refs: App,Dom    categorizer, canonicalizer, explainer
  Noof.Fx            refs: App,Dom    providers, archive, triangulation
  Noof.Receipts      refs: App,Dom    fiscal QR, vision extraction
  Noof.Telegram      refs: App,Dom    poller, router, presenter
  Noof.Web           refs: App,Dom    RCL — UI ONLY. No DbContext, no EF type, no Program.cs
  Noof.Host          refs: all        the only .exe, ~40-line Program.cs
publish/   gitignored, code only, wiped every deploy
```

**"Web is UI only" is enforced three ways**, because project references are **transitive at compile time** — a convention alone will not hold: `DisableTransitiveProjectReferences` (turns a violation into `CS0234`), a test parsing each `.csproj` for its exact reference set, and ArchUnitNET rules for type-level leaks.

**New Blazor pages:** `/settings/secrets` · `/diagnostics` · `/diagnostics/logs` · `/diagnostics/capture/{captureId}` · `/merchants` · `/recategorize`.

---

## 6. Ledger and wallets

- **Account** = the institution (Wise, Revolut, Kaspi, Raiffeisen, Cash). **Wallet** = `Account × Currency` — "Wise EUR" and "Wise USD" are two Wallets under one Account, because real multi-currency wallets hold several balances.
- **Transaction** (`Kind = Expense | Income | Transfer`) owns signed **Entry** rows. A fee is a **third Entry** with `Role=Fee`. **LineItem** is a real table with its own `CategoryId`.
- **Balances are derived**, never stored — a `WalletBalances` view summing entries. A stored balance drifts after any bug or manual fix; a derived one cannot.
- **Spending is one predicate:** `Kind == Expense || Role == Fee`.
- **Timestamps are `DateTimeOffset` → `timestamptz`, pinned in `ConfigureConventions` before the first migration.** Npgsql maps `DateTime(Kind=Utc)` to `timestamptz` but `Local`/`Unspecified` to `timestamp` — mixing kinds silently produces different column types. Changing this later is a data migration.

---

## 7. Currency exchange — and the feature worth having

A currency exchange is `Transaction(Kind=Transfer)` with an owned **`FxConversion`** on its own table: both captured amounts, the realised rate, the venue, a mid-rate snapshot, and the cost.

**The two amounts are truth; the rate is a persisted projection** — stored so it can be indexed and charted, never used to reconstruct an amount.

**The insight you get for free:** the app knows the mid-market rate for that date and the rate you actually got. So it can tell you *"Over mid-market: 70.55 RSD (0.60%)"* on every exchange — and compare venues: *"Wise averages 0.6%, the menjačnica on Knez Mihailova averages 2.1%."* Over a year that's a real number, and no other tracker gives it to you.

**The implicit spread is never an Entry.** Fabricating one would make balances disagree with your real wallet and break every reconciliation. Instead there are two clearly-labelled bases: **Ledger** (unchanged, reconcilable) and **Economic** = Ledger + implicit spread, rendered as a separate line and never merged into a category total. The unifying metric `total_cost_eur = implicit spread + explicit fees` puts a fee-transparent neobank and an opaque cash window on one axis.

**Capture is deterministic, zero model calls:** a grammar handling *"поменял 100 евро на 11700 динар"*, *"bought 500 eur wise 58600 rsd"*, *"100 eur -> 11700 rsd cash"*, *"обменял 50000 тенге на 95 евро"*, plus a one-tap confirmation card that doubles as disambiguation for `11.700,50` vs `11,700.50`.

**Exchange receipts:** NBS *Odluka* član 23 legally fixes the field set, so the vision schema is **specified by law rather than guessed** — and because the slip must carry amounts *and* rate *and* commission, `dinars ÷ foreign_amount` must reproduce the stated rate, giving a free correctness oracle. Wise/Revolut screenshots first (easy, high confidence), menjačnica photos second.

**FX archive:** one `fx_rates` table storing **`UnitsPerEur numeric(24,12)`** per `(Currency, AsOfDate, Source, Quality)`. `RSD 117.394506` means 117.394506 RSD per €1 — matching both the provider payload and the menjačnica's own board, removing a whole class of inversion bug. `open.er-api.com/v6/latest/EUR` pinned as the single canonical mid.

> **❓ Q4 — Canonical mid source for RSD.** The two candidate providers disagree on RSD by **0.09%**, while the spread you're measuring is only 0.5–0.6% — so provider choice is a sixth of the signal. Add the **NBS srednji kurs** as the preferred source for RSD specifically? It's the authoritative Serbian mid, but its machine-readable endpoint couldn't be verified without web search.

---

## 8. LLM: mandatory, but never blocking

**`AlwaysCallModel = true` now means: no bill is ever accepted without a categorization job enqueued in the same transaction.** Guaranteed, not synchronous. That atomicity *is* the guarantee — there is no instant where a bill exists without a pending job. Nothing is ever "done uncategorized"; only "not yet".

- **Queue:** `categorization_jobs` with lease, exponential backoff with jitter, and a terminal `Failed` state. Drained by a `BackgroundService` at DOP 1 using `FOR UPDATE SKIP LOCKED`, reclaiming expired leases on startup — the common case, because your PC was off.
- **Write order:** save + enqueue in one transaction → reply to Telegram immediately with total, wallet and running balance (model-free numbers) → categorise → `editMessageText` in place.
- **Two implementations from day one:** `LlmCategorizer` (claude-haiku-4-5) and `DeterministicOfflineCategorizer`, so *"the LLM can be changed"* is real rather than nominal. Offline rows carry `CategorizedBy = Offline`, are visibly provisional, and auto-re-enqueue when the network returns.
- **One model, one property.** `AnthropicOptions` has exactly one `Model`. No `AdviserModel`, no `EscalationModel`, no `FallbackModel` — making the schema *incapable* of expressing a tier is stronger than choosing not to configure one.
- **Amounts never originate in a model.** Quote-and-verify: the model returns the **substring** it believes is an amount, never a number; C# asserts that substring occurs verbatim in the input and re-parses it. A hallucinated figure becomes structurally impossible.

---

## 9. Secrets

`app_secret` holds **ciphertext only**, produced by ASP.NET Core Data Protection with a per-secret purpose chain — so the Telegram protector cannot decrypt the Anthropic payload even inside one process. The key ring lives at `%LOCALAPPDATA%\NoofFinance\dp-keys` wrapped with `ProtectKeysWithDpapi()`.

**Nothing is in `appsettings.json`**, so there is nothing to mis-gitignore in a public repo, and a leaked database dump is inert.

- **The app boots cleanly with no secrets configured.** A `TelegramPollerSupervisor` owns the poller's lifetime and reconciles on a change signal, so saving the token in the UI starts the bot with no restart.
- **`ISecretStore.GetAsync` returns `Present | Missing | Unreadable` and never throws.** That third state is what turns a Windows reinstall into an amber *"enter it again"* banner instead of a crash loop.
- **Settings page:** masked inputs, never renders a stored value back to the browser, per-secret Test buttons (Telegram `getMe`; Anthropic `GET /v1/models`, which costs zero tokens).

> **⚠️ Two traps, both reproduced on your machine, not theorised:**
> - **Default `IHttpClientFactory` logging writes your full bot token TWICE at Information level per request**, because Telegram puts it in the URL path. With logs stored in the database and shown on screen, an unfixed leak would render your bot token on a web page next to a dutifully-encrypted column. A `DelegatingHandler` **cannot** fix it — those loggers wrap around yours. Three layers: category `System.Net.Http.HttpClient.telegram` → `None`, a `SecretRedactor` filter, and a full-pipeline sentinel test plus an on-disk grep.
> - **Silent plaintext key ring:** calling `PersistKeysTo*` without an explicit `ProtectKeysWith*` **stops encrypting keys at rest** — silently.
> - **CVE-2026-40372** (CVSS 9.1) affects `Microsoft.AspNetCore.DataProtection` 10.0.0–10.0.6, patched 10.0.7. `NuGetAuditMode=all` + `NU1903;NU1904` as errors.

---

## 10. Logging and self-diagnostics

**Logging — Serilog, two-stage, file-first.** A rolling file sink comes up in the bootstrap logger *before* configuration, DI or EF, so a hidden-window autostart failure still logs somewhere. Then a ~40-line batched sink writes to `log.app_log` **on its own connection, never the app DbContext** (reentrancy and transaction pollution). Log tables are created by an idempotent `CREATE TABLE IF NOT EXISTS` *before* `MigrateAsync`, so logging survives a hung migration lock.

**Retention, grounded not guessed:** your volume is ~1,982 rows/day at ~670 bytes — a full year with *no* retention is 463 MB, nothing on a 64 GB machine. So be generous: Debug 7 days, Information 90, Warning/Error 730. Cleanup at startup+60s then every 6h, because **a nightly cron never fires on a PC that's off at night**.

**Viewer:** `/diagnostics/logs`, paged QuickGrid with level/time/`CaptureId`/text filters. **Not `Virtualize`** — it requires uniform row heights by documentation, and log rows with wrapped messages and stack traces are not uniform. The payoff feature is the **capture drill-down**: a timeline (`+0 ms / +312 ms / +1.4 s`) with the stage strip *Received ▸ Transcribed ▸ Extracted ▸ Categorized ▸ Persisted ▸ Replied*. That's what answers *"what happened to the message I sent at 2am"*.

**Self-diagnostics — three tiers, and the boundary is a build rule, not a promise:**

| Tier | What decides | Example |
|---|---|---|
| 1 · Health | `IHealthCheck` | DB reachable, migrations current, token valid, last poll recent, FX freshness, disk |
| 2 · Correctness | **SQL returning rows** | transfer legs net · line items sum to bill · uncategorized expense line · missing FX snapshot · balance vs reconciliation · duplicate-looking transaction |
| 3 · Explanation | the **LLM**, in Russian | renders prose over facts C# already computed |

**The LLM never returns a health status, never a number, never a branch of control flow** — enforced by an ArchUnitNET rule forbidding health and integrity checks from referencing `Noof.Ai`. The realistic failure is someone adding *"if the explainer says it's fine, auto-resolve"* to save a click, so it has to be a build failure.

**Severity mapping matters more than it sounds:** a socket-level network failure is **amber**, with the text *"Offline — this is normal"*. Only a 401, pending migrations, missing secrets or low disk go **red**. A tile that goes red every time the wifi drops trains you to ignore the dashboard within a week — at which point a real fault goes unnoticed.

---

## 11. Merchants and recategorization

**Merchant identity — the LLM proposes, a table decides.** `LookupKeyNormalizer` is deleted, as you asked. Replaced by:

- `Fold()` = trim + collapse whitespace + uppercase, **nothing else** — used only to build a lookup key.
- **`merchant_aliases`, write-once**, keyed on `Fold(raw)`, always consulted first, and the **sole authority** on merchant identity.
- The LLM canonicalises **only on a miss**, in the same batched call as categorisation.

**So the model sees any given raw string exactly once, ever. The second sighting is a primary-key lookup.** Two different canonicalisations on two different days is impossible because there is no second day. A `pg_trgm word_similarity` sweep surfaces near-duplicates in a merge inbox — one click merges retroactively and revertibly, and **rejected pairs are remembered permanently**, or the same false positive reappears nightly and you stop reading the inbox, which is how the original normaliser failed.

*(Exchange venues are merchants with a `MerchantKind`, not a separate table — otherwise a shop that's also an exchange point exists twice, with two normalisers and two merge stories.)*

**Recategorization — one precedence order, enforced in one place:**

```
User(4) > PinnedRule(3) > Rule(2) > Llm(1) > Offline(0) > None(-1)
```

Applied as a **SQL predicate inside every `ExecuteUpdateAsync`**, not an in-memory `if` a future caller can forget. A model write physically cannot land on a line you set by hand — and `rows-affected == 0` doubles as the idempotency signal for a duplicated queue delivery.

Every bulk operation writes a **`RecategorizationBatch`** with full before/after provenance per line, is **dry-run by default**, and the **diff viewer is built before the apply path** — otherwise dry-run-by-default becomes a flag nobody reads. **Revert is conditional:** a line is restored only if it still matches what the batch wrote, so undo can never destroy an edit you made since.

---

## 12. Phases

| # | Phase | Delivers |
|---|---|---|
| **0** | Prove the database | The culture regression test passes on Postgres and fails three ways on SQLite. Service survives a reboot. The three irreversible conventions written down. ~½ day. |
| **1** | Working end-to-end slice | Boot with **zero secrets** → paste tokens into the settings page → send `кофе 250 рсд` → categorized expense on the dashboard. Pull the network cable → still saves → reconnect → it categorizes itself and the Telegram message updates. |
| **2** | Money model | Balances exact across all five currencies under `ru-RU` and `sr-Latn-RS`. **A backup restored successfully at least once.** |
| **3** | Observability | Stop Postgres and start the app: it says *"waiting for database"*, logs the whole thing to file, doesn't crash. Kill the DB sink: viewer degrades to a file tail, tile goes amber. |
| **4** | Rich capture | A photographed Serbian receipt yields per-line categories and one merchant row; the same shop next week is a primary-key hit costing zero merchant tokens. |
| **5** | Currency exchange | *"поменял 100 евро на 11700 динар"* → Transfer with realised rate, *"Over mid-market: 70.55 RSD (0.60%)"*, spending and income both read 0, net worth falls by exactly the spread. |
| **6** | Integrity + explainer | Seed a violation → exactly one finding. A fabricated number in a mocked response is **rejected, not displayed**. |
| **7** | Governance | Edit one merchant rule → a year of history corrects itself in one revertible batch. |
| **8** | Operations | Restore performed twice from two different dumps. OneDrive sync. Weekly Russian error digest over real data with no fabricated figures. |

> **❓ Q5 — Phase 1 is bigger than a normal vertical slice, and I won't pretend otherwise.** Three of your annotations — secrets in the DB, the deferred queue, merchant identity — are all **capture-path contracts**: each is a rewrite rather than an addition if deferred. I'd rather build them once. Accept the larger Phase 1, or split it and accept rework?

> **❓ Q6 — Voice and receipts are at Phase 4.** You originally called them "most important". They're there because they depend on per-item categorisation (P1) and wallets/currencies (P2). Move earlier and they get built twice. Your call.

> **❓ Q7 — Merchant prompt hint.** Sending the top ~50 canonical merchant names with new-merchant requests is the single highest-leverage change for canonicalisation consistency (~300 extra tokens, only on misses) — but it puts a slice of your shopping profile in those requests. You already accept cloud categorisation of line items, so it's a small step. Conscious choice, not a default.

> **❓ Q8 — OneDrive backup (Phase 8).** Sync the **dump only** (safest; a restore means re-entering two secrets) or **also the Data Protection key ring** (complete restore, but your key ring is in the cloud)? Third option: `ProtectKeysWithCertificate` with the PFX in a password manager.

---

## 13. Risks I'm not hiding

1. **The Postgres service is `Manual` and currently stopped.** Left alone, the app fails at next logon with an opaque connection error. Set it to Automatic, add a bounded 60-second retry gate, and make "waiting for database" a real UI state.
2. **Backup regresses from "copy one file" to `pg_dump`** — the single strongest argument for SQLite, and I won't pretend it away. Mitigation is a 12-line script *and* an actually-performed restore in Phase 2, not Phase 8. **An untested backup is a hypothesis.**
3. **Two retry layers multiply.** The Anthropic SDK retries twice with a 10-minute default timeout; the queue then retries eight times. Composed naively, a bad API key buys **two hours of silence** and `attempt_count` lies about how many calls happened.
4. **The alias table makes a bad first canonicalisation permanent.** Write-once is what buys determinism, and it also freezes a wrong verdict. Mitigated by confidence and provenance on alias rows plus a review screen — but it's a real trade.
5. **Integrity findings become wallpaper if they ship wrong.** Eight checks with recurring false positives and you stop reading the tile in a fortnight — at which point the suite is *worse than nothing* because it creates false confidence. Ship **three**, each with two tests.
6. **The provisional-forever trap.** Offline-categorised rows are meant to be temporary, but nothing forces a re-run; a month of bad connectivity could quietly become the permanent record.
7. **Four API surfaces are still unverified**, each potentially a day: whether the Anthropic C# SDK accepts a programmatic API key rather than the env var (**load-bearing** — the key comes from the database), and three others. Phase 0 spikes them.
8. **Losing the Data Protection key ring** makes both secrets permanently unreadable — a new PC, a Windows reinstall, an admin password reset. Correct behaviour against the leaked-dump threat; the recovery is re-typing two secrets, provided the app surfaces it as amber instead of crashing.

---

## 14. Questions in one place

| # | Question | Default if you say nothing |
|---|---|---|
| Q1 | Postgres credential: SSPI or DPAPI file? | SSPI, spiked in Phase 0 |
| ~~Q2~~ | ~~Postgres 17 or 18?~~ | ✅ **Answered: 18** |
| Q3 | Write `.wslconfig` for you? | Not written |
| Q4 | NBS as the RSD mid source? | `open.er-api.com` for all five |
| Q5 | Accept the larger Phase 1? | Yes, build contracts once |
| Q6 | Move voice/receipts earlier than Phase 4? | Keep at Phase 4 |
| Q7 | Send merchant-name hints to the model? | No |
| Q8 | OneDrive: dump only, or dump + key ring? | Dump only |

---

*Approve to lock this spec and move to the implementation plan. Annotate anything you want changed and I'll revise and reopen.*
