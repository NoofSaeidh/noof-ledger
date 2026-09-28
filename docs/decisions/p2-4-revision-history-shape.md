---
id: P2-4
title: "The revision history's shape"
status: decided
date: 2026-09-22
phase: Phase 2
---

One `transaction_revisions` row per state, with `status_before` and `status_after`, the instruction,
and a jsonb snapshot whose amounts are decimal strings. `status_before` is what Вернуть restores.
Append-only by trigger; the FK is RESTRICT, so a revised transaction can never be deleted.
