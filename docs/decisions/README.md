# Decisions — deferred and settled questions

One file per decision or open question, in the spirit of an Architecture Decision Record. This
replaced a single `docs/OPEN-QUESTIONS.md` — every parallel agent that touched it collided on the
same file, 20 commits since 2026-09-20. Distinct from `docs/backlog/`, which holds deferred *work*
whose decision is already made; this holds deferred and settled *decisions and questions*.

## Convention

The folder is the index — there is no index file here, for the same reason `docs/backlog/` has
none: an index would reintroduce the one conflict point this split exists to remove.

Each file starts with YAML front matter:

```
---
id: <ID, e.g. P2-1, A1 — omitted when the original had none>
title: <heading or question text, without the ID>
status: decided        # open | decided | deferred | superseded
date: <decision date, when the text states one>
phase: <phase, when the text states one>
superseded_by: <slug>   # only when a later file supersedes this one
supersedes: <slug>      # only when this file supersedes an earlier one
related: [<ids/slugs the text explicitly refers to>]
---
```

**To find open questions:** `git grep -n "^status: open" docs/decisions`.

**A new decision is a new file.** **A changed decision gets a new file** that `supersedes` the old
one, which is in turn marked `status: superseded` and points `superseded_by` at the new file — never
rewrite a decision's own file to say something different than what was actually decided at the time.

Why: the file is the record of what was decided and why, with what was known then. Agents read the
file, not its `git log`, so a rewritten file loses the reasoning behind the first choice — and with it
the answer to "we tried that, here is why we stopped", which is what keeps a settled question from
being re-litigated. CLAUDE.md, code comments and PRs also cite these files by name; a rewrite would
silently change what an old citation means.

Where the boundary sits:

- **Edited in place** — anything that leaves the decision itself as it was: typos, wording, broken
  links, a clarification of what was already meant, `related` entries. So is an `open` or `deferred`
  question receiving its answer: nothing had been decided yet, so nothing is rewritten.
- **A new file** — anything that changes the decision: reversing it, choosing another option,
  widening or narrowing what it covers.
- **The superseded file** keeps its text. Beyond the front matter it gets one line at the top of its
  body — ``Superseded <date> — see `<slug>.md`: <the reason in a sentence>``.
- **One item in a file that holds several** (a table of `O-*` decisions, a numbered list) is
  superseded the same way, by a new file; the old item keeps its text behind a leading
  `*(Superseded <date> by <ref>.)*`. The file's own `status` changes only once every item in it is
  superseded.

## Preserved from `docs/OPEN-QUESTIONS.md`

The original file's own preamble, and a few section headings that only grouped several items without
being a decision themselves, are kept here verbatim so nothing from the original is lost:

> Parked from the approved design (`docs/specs/2026-09-19-noof-finance-design.md`).
> The spec was approved without answering these, so **each has taken its stated default**. None
> blocks the current phase. Each names the phase where it becomes real — revisit it there, or
> earlier if you want to change the default.

The section headings below only introduced a table or a run of items; their own content is now
each item's own file (listed by directory, not repeated here):

- "Why each default is safe to defer" — the reasoning for each `q*` file's default lives in that
  file.
- "Answered, kept for the record" — each row is now its own `status: decided` file.
- "Auth addendum questions" — *"From §15 of the design, added 2026-09-19. All defaulted; none
  blocks any phase."* — items `a1`..`a4`.
- "Phase 0b questions — raised by the auth/data research" — items `b1`..`b3`.
- "Phase 1 decisions — taken 2026-09-21" — *"Four questions were put to the user before Phase 1
  planning. All four are answered; two of them changed the design that the readiness audit had
  recommended."* — items `p1-1`..`p1-4`.
- "Phase 2 decisions — taken 2026-09-22" — the original file kept every later item (P3-1 through
  P6-2) nested under this one heading, even across later phases; each now has its own file under
  its own phase-specific title, so the heading text itself carries no further information.
