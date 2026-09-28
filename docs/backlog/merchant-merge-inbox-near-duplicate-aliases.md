---
title: Merchant merge inbox for near-duplicate aliases
status: deferred
area: web
---
**Wanted.** Spec §11: "A `pg_trgm word_similarity` sweep surfaces near-duplicates in a merge inbox
- one click merges retroactively and revertibly, and rejected pairs are remembered permanently."

**Why it is not in Phase 1B.** The alias table this needs - write-once, `Fold()`-keyed, the sole
authority on merchant identity - ships this phase (`IMerchantDirectory`). The merge inbox is a
read/write UI over rows that table already produces correctly; nothing about it changes the
schema. It is explicitly Phase 7 (Governance) work in the spec's phase table, alongside the
recategorization batch UI it shares page furniture with.

**Until then.** A wrong canonicalisation on first sighting is permanent under write-once (a
named, accepted trade-off - see spec §13 risk 4), with no UI yet to correct it short of a manual
database edit.

**Prerequisite when this is built — create the extensions in a migration** (found 2026-09-28).
`pg_trgm` and `unaccent` are installed today only by `ops/reset-database-auth.ps1` and the demo
tool (`tools/Noof.Ledger.Demo`); no migration creates them and no code uses them yet. The first
migration whose SQL relies on them must `CREATE EXTENSION IF NOT EXISTS` both, or a database
created without that script (a fresh install, a test database migrated from `template0`) fails.
Both are trusted extensions since PostgreSQL 13, so the database owner can create them without
superuser rights.
