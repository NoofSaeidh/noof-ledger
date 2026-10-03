---
title: Explicit access modifiers everywhere
status: deferred
area: code style
since: Phase 8a (2026-10-03, operator's review of the bot PR)
---
**What.** Members and types rely on C#'s implicit defaults (a class member with no modifier is `private`, a top-level
type `internal`). The operator wants every declaration to state its access modifier explicitly, enforced by the
analyzers (`IDE0040`, "accessibility modifiers required") and applied to the existing code.

**Why it is not done.** It touches nearly every file — a mechanical change for its own PR, separate from behaviour.
