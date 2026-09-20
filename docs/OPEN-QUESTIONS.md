# Open questions — deferred decisions

Parked from the approved design (`docs/superpowers/specs/2026-09-19-noof-finance-design.md`).
The spec was approved without answering these, so **each has taken its stated default**. None blocks the current phase. Each names the phase where it becomes real — revisit it there, or earlier if you want to change the default.

| # | Question | Default taken | Decide by |
|---|---|---|---|
| Q1 | Postgres credential: Windows-integrated auth (SSPI) or a DPAPI-protected password file? | **Neither** — a generated password in a plaintext file outside the repo | Phase 1 |
| Q3 | Write `%UserProfile%\.wslconfig`? WSL is running uncapped — 50% of RAM (~32 GB) + ~16 GB swap, and `ext4.vhdx` grows but never shrinks | **Not written** | Any time — unrelated to this project now containers are out |
| Q4 | Canonical mid-rate source for RSD: `open.er-api.com` for all five currencies, or add NBS *srednji kurs* for RSD? | **`open.er-api.com`** for all five | Phase 5 |
| Q5 | Accept the larger Phase 1, or split it and accept rework? | **Larger Phase 1** — build the capture-path contracts once | Settled by approval |
| Q6 | Move voice and receipts earlier than Phase 4? | **Keep at Phase 4** | Phase 2 |
| Q7 | Send the top ~50 canonical merchant names as a prompt hint? Best single lever for canonicalisation consistency, but puts a slice of the shopping profile in each request | **No** | Phase 4 |
| Q8 | OneDrive backup: dump only, or dump + Data Protection key ring? | **Dump only** — a restore means re-entering two secrets | Phase 8 |

## Why each default is safe to defer

**Q1** — SSPI was never spiked. What shipped instead is a generated password written to a plaintext file at `%LOCALAPPDATA%\NoofLedger\db.connection`, created by `ops/reset-database-auth.ps1` — outside the repo and never in `appsettings.json`. That is acceptable for a single-user local dev machine, but it is neither of the two options on the table, so the SSPI/DPAPI upgrade is re-parked here, decide by Phase 1.

**Q3** — Only matters while WSL runs. Nothing in the design touches it now.

**Q4** — The two providers disagree on RSD by **0.09%** while the spread being measured is 0.5–0.6%, so provider choice is roughly a sixth of the signal. `Source` is stamped on every rate row and the archive is append-only, so adding NBS later is a new source, not a migration.

**Q5** — Settled by approving the spec.

**Q6** — Dependency-driven: receipts need per-item categorisation (P1) and wallets/currencies (P2). Moving them earlier means building them twice.

**Q7** — Reversible at zero cost; it is one prompt field. Worth revisiting once real canonicalisation quality is visible.

**Q8** — Phase 8 by your own instruction. The real fork is whether a restore should be complete (key ring in the cloud) or safe (re-enter two secrets). A third option exists: `ProtectKeysWithCertificate` with the PFX in a password manager.

## Answered, kept for the record

| Question | Answer |
|---|---|
| Database engine | **PostgreSQL 18** — installed via choco |
| Project name | **Ledger** — `Noof.Ledger.*`, solution `NoofLedger.slnx`, database `noof_ledger` |
| `Money` ordering across currencies | **Throws.** Comparing 10 EUR to 10 USD has no correct answer. Callers sort mixed lists explicitly: `OrderBy(m => m.Currency).ThenBy(m => m.Amount)` |
| `CurrencyCode` ordering | **Ordinal, never cultural** — `string.CompareOrdinal`. Serbian Latin treats `LJ` as one collating letter and would invert pairs |
| `Microsoft.AspNetCore.Components.Authorization` version | **10.0.8**, lockstep with `Components.Web`. Verified to restore and build clean under CPM + `TreatWarningsAsErrors` |
| Containers (Docker/Podman) | **No**, for now — reasoning in §4 of the design |
| Money representation | `decimal` + `Currency`, native `numeric(19,4)` |
| Base currency | **EUR** |
| LLM | `claude-haiku-4-5` only, no adviser tier |
| Bilingual category names | `NameEn` + `NameRu` |

---

## Auth addendum questions

From §15 of the design, added 2026-09-19. All defaulted; none blocks any phase.

| # | Question | Default taken | Decide by |
|---|---|---|---|
| A1 | Does anyone else have a Windows account on this PC? | **No** | Any time — a yes means flipping `Auth:Mode=Cookie` immediately, since that is the single scenario where auth is load-bearing today |
| A2 | Will you reach the dashboard from your phone, and when? | **Not yet** | Enforced automatically: the startup guard refuses to boot on a non-loopback binding while `Auth:Mode=Off` |
| A3 | Re-prompt for the password on `/settings/secrets` after ~10 minutes? | **Policy attached, handler lenient** | Whenever auth is switched on |
| A4 | Set up trusted local HTTPS once, to unlock Windows Hello passkeys? | **No** — password stays the permanent mechanism | Any time |

**Why A2 needs no discipline from you:** the guard makes the config key and the Kestrel binding physically inseparable. The day you widen the binding for phone access, the app will not start until auth is on. That is what stops "optional now" from becoming "forgotten forever".

---

## Phase 0b questions — raised by the auth/data research

| # | Question | Default taken | Decide by |
|---|---|---|---|
| B1 | Does `user set-password` CREATE the `app_user` row, or require it to exist? There is no `/register` and no `/setup` page, so nothing else can create it | **Upsert** — it creates the row if absent. Otherwise the app has no path to a first user at all | Phase 0b, task 13 |
| B2 | Which entities beyond `AppUser` ship before Phase 1? | **None.** `MoneyProbeEntity` stays as the Money-mapping regression fixture and is dropped in Phase 1 when a real Money-bearing entity exists | Phase 1 |
| B3 | Should the loopback guard also reject a non-loopback *configured* URL pre-bind, as an early check? | **No** — the post-bind `IServerAddressesFeature` check is authoritative. Re-deriving Kestrel's URL precedence by hand is a bug source | Phase 0b, task 9 |

**Note on A1/A2.** `Auth:Mode` still defaults to `Off`, so the login screen is not in the way.
The startup guard makes the config key and the Kestrel binding inseparable: widening the binding
for phone access will refuse to start until auth is on. That is what stops "optional now" from
becoming "forgotten forever" — no discipline required from you.
