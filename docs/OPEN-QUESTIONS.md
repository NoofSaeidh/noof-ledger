# Open questions — deferred decisions

Parked from the approved design (`docs/superpowers/specs/2026-09-19-noof-finance-design.md`).
The spec was approved without answering these, so **each has taken its stated default**. None blocks the current phase. Each names the phase where it becomes real — revisit it there, or earlier if you want to change the default.

| # | Question | Default taken | Decide by |
|---|---|---|---|
| Q1 | Postgres credential: Windows-integrated auth (SSPI) or a DPAPI-protected password file? | **SSPI**, spiked first | Phase 0 |
| Q3 | Write `%UserProfile%\.wslconfig`? WSL is running uncapped — 50% of RAM (~32 GB) + ~16 GB swap, and `ext4.vhdx` grows but never shrinks | **Not written** | Any time — unrelated to this project now containers are out |
| Q4 | Canonical mid-rate source for RSD: `open.er-api.com` for all five currencies, or add NBS *srednji kurs* for RSD? | **`open.er-api.com`** for all five | Phase 5 |
| Q5 | Accept the larger Phase 1, or split it and accept rework? | **Larger Phase 1** — build the capture-path contracts once | Settled by approval |
| Q6 | Move voice and receipts earlier than Phase 4? | **Keep at Phase 4** | Phase 2 |
| Q7 | Send the top ~50 canonical merchant names as a prompt hint? Best single lever for canonicalisation consistency, but puts a slice of the shopping profile in each request | **No** | Phase 4 |
| Q8 | OneDrive backup: dump only, or dump + Data Protection key ring? | **Dump only** — a restore means re-entering two secrets | Phase 8 |

## Why each default is safe to defer

**Q1** — SSPI removes the one secret that cannot live encrypted in the database it opens. If the Phase 0 spike fails, the DPAPI file is a drop-in fallback; either way it is never in `appsettings.json`.

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
| Containers (Docker/Podman) | **No**, for now — reasoning in §4 of the design |
| Money representation | `decimal` + `Currency`, native `numeric(19,4)` |
| Base currency | **EUR** |
| LLM | `claude-haiku-4-5` only, no adviser tier |
| Bilingual category names | `NameEn` + `NameRu` |
