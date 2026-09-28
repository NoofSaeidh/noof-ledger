# Backlog — future improvements

Work that is wanted but deliberately not scheduled. Distinct from `docs/decisions/`, which holds
deferred *decisions*; this holds deferred *work* whose decision is already made.

Nothing here blocks any phase. An item leaves this folder only by being written into a phase plan.

## Convention

One file per backlog entry, named `<slug>.md`. This replaced a single `docs/BACKLOG.md` — every
parallel agent that appended to it collided on the same tail of the same file, every time. The
folder is the index — do not add an index file here; that would just reintroduce the same
conflict point one level up.

Each file starts with YAML front matter:

```
---
title: <the entry's heading text>
status: deferred        # deferred | ready | done
area: <web | telegram | ai | persistence | host | ops | tests | docs | hosting | security | other>
since: <date or phase, when the text states one>
related: [<other slugs this entry explicitly refers to>]
---
```

`since` and `related` are omitted when the entry's text does not give one.

**To list entries:** `ls docs/backlog` (or `Glob docs/backlog/*.md`). Skip `README.md` itself.

**A finished item is either deleted (git keeps its history) or marked `status: done`** — this
mirrors how the original file already treated closed items: some were removed once superseded,
others were kept in place with "fixed" or "closed" noted directly in their text. Prefer deleting
an item whose full story is captured in the commit or PR that closed it; keep it as `status: done`
when the text itself is the record worth preserving (a lesson, a measurement, a decision).
