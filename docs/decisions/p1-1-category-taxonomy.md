---
id: P1-1
title: "Category taxonomy — there was none anywhere in the repo or the spec"
status: decided
date: 2026-09-21
phase: Phase 1
---

One of four questions put to the user before Phase 1 planning (see `docs/decisions/README.md`).

**Decision:** Dynamic hierarchy. Guid keys, self-referencing parent, sub-categories, bilingual
`NameEn`/`NameRu`, renameable.

## Categories are data, not an enum

Guid primary keys, `ParentId` for sub-categories, `NameEn` + `NameRu`, and renaming must not break
anything. That last requirement is the one with teeth: **a renameable display name cannot be the key
the model answers with.** Every category therefore carries an immutable `Slug` that is minted once and
never changes; the model's structured-output enum is built from current slugs at call time, and
`category.NameRu` can be rewritten freely without invalidating a single stored categorisation.

Consequence for the request schema: the enum is per-request data, not a compile-time constant. There
is no `CategoryKind` enum in the codebase and there must not be one.

Seed: a starting tree, not a fixed taxonomy — the user renames and extends from there. A management
UI is NOT a Phase 1 deliverable; the schema supports renaming from the first migration, which is the
half that is expensive to retrofit.
